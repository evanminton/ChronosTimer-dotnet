using System.Globalization;
using System.Net;
using System.Text;
using ChronosTimer.Control;
using ChronosTimer.Link;
using ChronosTimer.Settings;

namespace ChronosTimer.Show;

/// <summary>
/// Show features of a <see cref="TimerHost"/>: the scheduled start/end with the hold button, the cue light, messages,
/// and the link that makes one timer the master and others followers mirroring its time.
/// Thread-safe.
/// </summary>
public sealed class ShowController : IDisposable
{
    private const int MaxMessages = 200;

    private readonly TimerHost _host;
    private readonly object _gate = new();
    private readonly ShowSchedule _schedule = new();
    private readonly List<ShowMessage> _messages = [];
    private CueLight _cue;
    private bool _cueAuto = true;

    // schedule → engine
    private ShowPhase _lastPhase = ShowPhase.Idle;
    private bool _lastStarted;
    private TimerPhase _lastTimerPhase;

    // link
    private LinkRole _role;
    private string _name = "auto", _bind = NetworkBinding.Apipa, _masterAddress = "auto", _key = "", _lastNode = "";
    private int _port = LinkProtocol.DefaultPort;
    private LinkMaster? _master;
    private LinkFollower? _follower;
    private ShowView? _remote;
    private string _linkStatus = "off";
    private bool _started;
    private Timer? _timer;
    private int _ticking;

    /// <summary>Timecode preroll a new controller starts with.</summary>
    public static readonly TimeSpan DefaultPreroll = TimeSpan.FromSeconds(5);

    public ShowController(TimerHost host)
    {
        _host = host;
        _schedule.Preroll = DefaultPreroll;
    }

    /// <summary>Wall clock for the schedule. Replaceable for tests.</summary>
    public Func<DateTimeOffset> WallClock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>Messages, cue light, link or schedule changed. Any thread.</summary>
    public event Action? Changed;

    /// <summary>A message was sent or received. Any thread.</summary>
    public event Action<ShowMessage>? MessageAdded;

    private TimerEngine Engine => _host.Engine;

    // ═════════════════════════════ lifecycle ═════════════════════════════

    /// <summary>Starts the link (per the settings) and the schedule clock.</summary>
    public void Start()
    {
        _started = true;
        ApplyLink();
        _timer ??= new Timer(_ => SafeTick(), null, 50, 50);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
        _started = false;
        StopLink();
    }

    private void SafeTick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1) return;
        try { Tick(); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { _host.Log("show: " + ex.Message); }
        finally { Volatile.Write(ref _ticking, 0); }
    }

    /// <summary>Drives the timer from the schedule, applies the automatic cue light and pushes the view to followers.</summary>
    public void Tick()
    {
        ShowState st;
        bool enter;
        lock (_gate)
        {
            st = _schedule.Evaluate(WallClock());
            enter = st.Phase != _lastPhase || st.Started != _lastStarted;
            var (prevPhase, prevStarted) = (_lastPhase, _lastStarted);
            _lastPhase = st.Phase;
            _lastStarted = st.Started;
            if (enter) enter = Transition(st, prevPhase, prevStarted);
        }
        if (enter) Changed?.Invoke();
        AutoCue();
        if (_master is { } m) m.SendState(View);
    }

    /// <summary>Moves the timer when the show changes phase. Returns true when anything changed.</summary>
    private bool Transition(ShowState st, ShowPhase prev, bool prevStarted)
    {
        switch (st.Phase)
        {
            case ShowPhase.Waiting:
                if (prev is ShowPhase.Idle or ShowPhase.Over or ShowPhase.Preroll || prevStarted) Prepare(st);
                return true;
            case ShowPhase.Preroll:
                // Roll the timecode up to the start: the count sits the rest of the preroll before zero.
                Prepare(st);
                Engine.SeekElapsed(st.Elapsed!.Value);
                Engine.Play();
                _host.Log("Show preroll: timecode rolling, starts " + ShowTime.Short(st.EffectiveStart!.Value, WallClock()));
                return true;
            case ShowPhase.Holding when st.Started:
                Engine.Pause();
                _host.Log("Show held");
                return true;
            case ShowPhase.Holding:
                if (prev is ShowPhase.Idle or ShowPhase.Preroll) Prepare(st); // a hold during the preroll stops the code; it rolls again on release
                _host.Log("Show start held");
                return true;
            case ShowPhase.Running when prev == ShowPhase.Preroll:
                Engine.Play(); // already rolling: no locate, so the code runs on without a jump
                _host.Log("Show started");
                return true;
            case ShowPhase.Running:
                if (!prevStarted && prev != ShowPhase.Holding) Prepare(st);
                if (st.Remaining is { } rem) Engine.Locate(Seconds(rem));
                else if (st.Elapsed is { } el) Engine.Locate(Seconds(el));
                Engine.Play();
                _host.Log(prev == ShowPhase.Holding ? "Show released" + (st.Delay > TimeSpan.Zero ? ", end moved to " + ShowTime.Short(st.EffectiveEnd ?? st.EffectiveStart!.Value, WallClock()) : "") : "Show started");
                return true;
            default:
                return prev != st.Phase;
        }
    }

    private static TimeInput Seconds(TimeSpan t) => TimeInput.Parse(Math.Max(0, t.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture));

    /// <summary>Sets the timer up for the show: a countdown of the show's length (or a count-up without an end), stopped at the start.</summary>
    private void Prepare(ShowState st)
    {
        if (st.Length is { } len)
        {
            Engine.Mode = TimerMode.CountDown;
            Engine.Duration = len;
        }
        else Engine.Mode = TimerMode.CountUp;
        Engine.Stop();
        Engine.Reset();
    }

    private void AutoCue()
    {
        if (!_cueAuto || _role == LinkRole.Follower) return;
        var s = Engine.GetStatus();
        var phase = s.Phase;
        var last = _lastTimerPhase;
        _lastTimerPhase = phase;
        if (phase == last || s.Length is null) return;
        CueLight? next = phase switch
        {
            TimerPhase.Warning or TimerPhase.Critical when last == TimerPhase.Normal && s.State == TransportState.Running => CueLight.Warning,
            TimerPhase.Overrun or TimerPhase.Ended => CueLight.End,
            _ => null,
        };
        if (next is { } c && Cue is CueLight.Off or CueLight.Go or CueLight.Warning && Cue != c) SetCue(c, "auto");
    }

    // ═════════════════════════════ schedule ═════════════════════════════

    public DateTimeOffset? ScheduledStart { get { lock (_gate) return _schedule.Start; } }
    public DateTimeOffset? ScheduledEnd { get { lock (_gate) return _schedule.End; } }

    /// <summary>How long the timer (and its LTC) runs before the scheduled start, so receivers are locked by the start. Holds delay it too.</summary>
    public TimeSpan Preroll
    {
        get { lock (_gate) return _schedule.Preroll; }
        set
        {
            lock (_gate) _schedule.Preroll = value;
            Tick();
        }
    }

    /// <summary>The schedule now.</summary>
    public ShowState State { get { lock (_gate) return _schedule.Evaluate(WallClock()); } }

    /// <summary>Schedules the show (null start clears it). Throws <see cref="ArgumentException"/> when the end isn't after the start.</summary>
    public void Schedule(DateTimeOffset? start, DateTimeOffset? end)
    {
        lock (_gate)
        {
            _schedule.Set(start, end);
            _lastPhase = ShowPhase.Idle; // prepare the timer again
            _lastStarted = false;
        }
        _host.Log(start is null ? "Show schedule cleared" : $"Show scheduled {ShowTime.Short(start.Value, WallClock())}" + (end is { } e ? " – " + ShowTime.Short(e, WallClock()) : ""));
        Tick();
    }

    /// <summary>Hold (the start waits, or the running show pauses) or release it.</summary>
    public void SetHold(bool hold)
    {
        lock (_gate)
        {
            if (hold) _schedule.Hold(WallClock());
            else _schedule.Release(WallClock());
        }
        Tick();
    }

    public bool Holding { get { lock (_gate) return _schedule.Holding; } }

    // ═════════════════════════════ cue light ═════════════════════════════

    public CueLight Cue
    {
        get
        {
            if (_role == LinkRole.Follower) return _remote?.Cue ?? CueLight.Off;
            lock (_gate) return _cue;
        }
    }

    /// <summary>Automatic warning/end cue from the countdown.</summary>
    public bool CueAuto
    {
        get { lock (_gate) return _cueAuto; }
        set { lock (_gate) _cueAuto = value; }
    }

    /// <summary>Sets the cue light (master or unlinked timer). Throws on a follower.</summary>
    public void SetCue(CueLight cue, string? source = null)
    {
        if (_role == LinkRole.Follower) throw new InvalidOperationException("Only the master sets the cue light. A follower can 'cue ack'.");
        lock (_gate)
        {
            if (_cue == cue) return;
            _cue = cue;
        }
        _master?.ResetAcks();
        _host.Log("Cue " + Setting.Token(cue) + (source is null ? "" : $" ({source})"));
        if (_master is { } m) m.SendState(View);
        Changed?.Invoke();
    }

    /// <summary>A follower acknowledges the cue light to the master.</summary>
    public void Acknowledge()
    {
        if (_role != LinkRole.Follower) throw new InvalidOperationException("'cue ack' is for followers; the master sees the acknowledgements.");
        if (_follower?.Acknowledge() != true) throw new InvalidOperationException("Not connected to a master.");
    }

    // ═════════════════════════════ messages ═════════════════════════════

    public IReadOnlyList<ShowMessage> Messages { get { lock (_gate) return [.. _messages]; } }

    /// <summary>Sends a message to every linked timer, or to one with "@name text". Throws when it can't be sent.</summary>
    public ShowMessage Send(string text)
    {
        string t = (text ?? "").Trim();
        string? to = null;
        if (t.StartsWith('@'))
        {
            int sp = t.IndexOf(' ');
            if (sp < 0) throw new FormatException("Write the message after the name, e.g. message @ChronosTimer02 standby for the top.");
            to = t[1..sp].TrimEnd(':', ',');
            t = t[(sp + 1)..];
        }
        t = ShowMessage.Clean(t);
        if (t.Length == 0) throw new FormatException("The message is empty.");

        var msg = new ShowMessage(WallClock(), NodeName, to, t);
        switch (_role)
        {
            case LinkRole.Master when _master is { } m:
                if (!m.SendMessage(msg)) throw new InvalidOperationException($"No timer named '{to}' is linked.");
                break;
            case LinkRole.Follower when _follower is { } f:
                if (!f.SendMessage(t, to)) throw new InvalidOperationException("Not connected to a master.");
                break;
            default:
                throw new InvalidOperationException("This timer isn't linked (set link-role to master or follower).");
        }
        AddMessage(msg);
        return msg;
    }

    private void AddMessage(ShowMessage m)
    {
        lock (_gate)
        {
            _messages.Add(m);
            if (_messages.Count > MaxMessages) _messages.RemoveRange(0, _messages.Count - MaxMessages);
        }
        MessageAdded?.Invoke(m);
        Changed?.Invoke();
    }

    // ═════════════════════════════ view ═════════════════════════════

    /// <summary>What the Show display shows: the master's time (this timer's, unless following), schedule line and cue.</summary>
    public ShowView View => _role == LinkRole.Follower ? RemoteView() : LocalView();

    private ShowView RemoteView()
    {
        var f = _follower;
        var r = _remote;
        bool live = f?.Connected == true && r is not null;
        if (r is null) return new ShowView { Live = false, Display = "--:--", Detail = f?.Status ?? _linkStatus };
        return r with { Live = live, Detail = live ? r.Detail : (f?.Status ?? "not linked") };
    }

    private ShowView LocalView()
    {
        var s = Engine.GetStatus();
        var now = WallClock();
        ShowState st;
        CueLight cue;
        lock (_gate) { st = _schedule.Evaluate(now); cue = _cue; }

        string display = s.Display, detail = s.Detail;
        var phase = s.Phase;
        double? progress = s.Progress;
        TimeSpan? remaining = s.Remaining;
        string Span(TimeSpan t, bool up = false) => Engine.FormatSpan(t.TotalSeconds, DisplayFormat.Compact, ceiling: up);
        string Delay() => st.Delay > TimeSpan.Zero ? " · delayed " + Span(st.Delay) : "";
        string Ends() => st.EffectiveEnd is { } e ? " · ends " + ShowTime.Short(e, now) : "";

        switch (st.Phase)
        {
            case ShowPhase.Waiting or ShowPhase.Preroll:
                display = "−" + Span(st.UntilStart!.Value, up: true);
                detail = (st.Phase == ShowPhase.Preroll ? "Preroll · starts " : "Starts ") + ShowTime.Short(st.EffectiveStart!.Value, now) + Ends() + (st.Length is { } l ? " (" + Span(l) + ")" : "") + Delay();
                phase = TimerPhase.Normal;
                progress = null;
                remaining = st.UntilStart;
                break;
            case ShowPhase.Holding when !st.Started:
                display = "HOLD";
                detail = "Held " + Span(st.HeldFor) + (st.Preroll > TimeSpan.Zero ? " · prerolls " + Span(st.Preroll) + " on release" : " · starts on release") + (st.Length is { } hl ? " · runs " + Span(hl) : "");
                phase = TimerPhase.Warning;
                progress = null;
                remaining = null;
                break;
            case ShowPhase.Holding:
                detail = "HOLD " + Span(st.HeldFor) + Ends() + Delay();
                phase = TimerPhase.Warning;
                break;
            case ShowPhase.Running:
                detail = "Started " + ShowTime.Short(st.EffectiveStart!.Value, now) + Ends() + Delay();
                break;
            case ShowPhase.Over:
                detail = "Ended " + ShowTime.Short(st.EffectiveEnd!.Value, now) + Delay();
                break;
        }

        return new ShowView
        {
            Source = NodeName,
            Live = true,
            Display = display,
            Detail = detail,
            Phase = phase,
            State = s.State,
            ShowPhase = st.Phase,
            Cue = cue,
            Progress = progress,
            Remaining = remaining,
        };
    }

    // ═════════════════════════════ link ═════════════════════════════

    public LinkRole Role
    {
        get => _role;
        set { _role = value; ApplyLink(); }
    }

    /// <summary>This timer's node name setting ("auto" = ChronosTimer## numbered by the master).</summary>
    public string Name
    {
        get => _name;
        set
        {
            string v = ShowMessage.Clean(value);
            if (v.Length == 0) v = "auto";
            if (v.Length > 40) throw new FormatException("The name must be 1–40 characters.");
            _name = v;
            ApplyLink();
        }
    }

    /// <summary>The name the master gave this timer last time (asked for again on reconnect).</summary>
    public string LastNode
    {
        get => _lastNode;
        set => _lastNode = value.Trim().Length == 0 || value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : value.Trim();
    }

    public int Port
    {
        get => _port;
        set { _port = value; ApplyLink(); }
    }

    public string Bind
    {
        get => _bind;
        set { _bind = NetworkBinding.Normalize(value); ApplyLink(); }
    }

    public string MasterAddress
    {
        get => _masterAddress;
        set
        {
            string v = value.Trim();
            if (LinkFollower.IsAuto(v)) v = "auto";
            else LinkProtocol.ParseEndpoint(v, _port);
            _masterAddress = v;
            ApplyLink();
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            string t = value.Trim();
            if (t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("none", StringComparison.OrdinalIgnoreCase)) t = "";
            if (t.Length > 128 || t.Any(char.IsWhiteSpace)) throw new FormatException("The link key must be up to 128 characters without spaces ('off' for none).");
            _key = t;
            ApplyLink();
        }
    }

    /// <summary>The name this timer goes by on the link: the set name, or ChronosTimer01 as master / the number the master gave.</summary>
    public string NodeName
    {
        get
        {
            if (!_name.Equals("auto", StringComparison.OrdinalIgnoreCase)) return _name;
            if (_role == LinkRole.Follower)
            {
                string assigned = _follower?.AssignedName ?? "";
                return assigned.Length > 0 ? assigned : _lastNode.Length > 0 ? _lastNode : LinkProtocol.AppName + "??";
            }
            return LinkProtocol.NodeName(1);
        }
    }

    /// <summary>Link state for displays: "master on 169.254.3.4:8490 · 2 followers", "connected to ChronosTimer01 …", "off".</summary>
    public string LinkStatus
    {
        get
        {
            if (_master is { } m)
            {
                int n = m.Followers.Count;
                return $"master {NodeName} on {m.BindAddress}:{m.Port} · {n} follower{(n == 1 ? "" : "s")}";
            }
            return _follower?.Status ?? _linkStatus;
        }
    }

    public IReadOnlyList<LinkFollowerInfo> Followers => _master?.Followers ?? [];

    private void StopLink()
    {
        var m = _master; var f = _follower;
        _master = null; _follower = null; _remote = null;
        m?.Dispose();
        f?.Dispose();
    }

    private void ApplyLink()
    {
        if (!_started || _host.ApplySuspended) return;
        StopLink();
        _linkStatus = "off";
        try
        {
            switch (_role)
            {
                case LinkRole.Master:
                    {
                        IPAddress ip;
                        if (NetworkBinding.Resolve(_bind) is { } r) ip = r;
                        else
                        {
                            ip = IPAddress.Any;
                            _host.Log(_bind == NetworkBinding.Apipa
                                ? "Link: this computer has no 169.254.x.x (APIPA) address; listening on every network."
                                : $"Link: network interface '{_bind}' not found or down; listening on every network.");
                        }
                        var m = new LinkMaster(ip, _port, NodeName, _key, NetworkBinding.Broadcasts(ip));
                        m.Log += _host.Log;
                        m.FollowersChanged += () => Changed?.Invoke();
                        m.Acknowledged += n => { _host.Log($"Cue acknowledged by {n}"); Changed?.Invoke(); };
                        m.MessageReceived += OnFollowerMessage;
                        m.Start();
                        _master = m;
                        _host.Log($"Link: master {NodeName} on {ip}:{m.Port}" + (_key.Length == 0 ? " (no link key: any timer on the network can join)" : ""));
                        break;
                    }
                case LinkRole.Follower:
                    {
                        var f = new LinkFollower(_masterAddress, _port, _name, _lastNode, _key);
                        f.StatusChanged += () =>
                        {
                            if (f.AssignedName.Length > 0 && LinkProtocol.NodeNumber(f.AssignedName) is not null) _lastNode = f.AssignedName;
                            _host.Log("Link: " + f.Status);
                            Changed?.Invoke();
                        };
                        f.StateReceived += v => _remote = v;
                        f.MessageReceived += AddMessage;
                        _follower = f;
                        f.Start();
                        break;
                    }
            }
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or FormatException or ArgumentException)
        {
            _linkStatus = "failed: " + ex.Message;
            _host.Log("Link could not start: " + ex.Message);
        }
        Changed?.Invoke();
    }

    private void OnFollowerMessage(string from, string? to, string text)
    {
        var msg = new ShowMessage(WallClock(), from, to, text);
        bool forMe = to is null || to.Equals(NodeName, StringComparison.OrdinalIgnoreCase);
        if (to is null) _master?.SendMessage(msg, except: from);
        else if (!forMe) _master?.SendMessage(msg);
        AddMessage(msg);
    }

    /// <summary>Text for the 'show' command.</summary>
    public string StatusText()
    {
        var v = View;
        var st = State;
        var sb = new StringBuilder();
        sb.AppendLine($"{v.Display}   {v.Detail}");
        sb.Append("show ").Append(Setting.Token(st.Phase));
        if (st.Start is { } s) sb.Append(" · start ").Append(ShowTime.Short(s, WallClock()));
        if (st.End is { } e) sb.Append(" · end ").Append(ShowTime.Short(e, WallClock()));
        if (st.Delay > TimeSpan.Zero) sb.Append(" · delayed ").Append(TimerEngine.Fmt(st.Delay));
        if (st.Phase != ShowPhase.Idle && st.Preroll > TimeSpan.Zero) sb.Append(" · preroll ").Append(TimerEngine.Fmt(st.Preroll));
        sb.AppendLine();
        sb.AppendLine("cue " + Setting.Token(Cue) + (_master is { } m && m.Followers.Count > 0
            ? " · acknowledged by " + (m.Followers.Any(f => f.Acknowledged) ? string.Join(", ", m.Followers.Where(f => f.Acknowledged).Select(f => f.Name)) : "nobody yet")
            : ""));
        sb.Append("link ").Append(LinkStatus);
        return sb.ToString();
    }
}
