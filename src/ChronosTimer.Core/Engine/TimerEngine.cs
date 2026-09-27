using System.Globalization;
using LinearTimecode;
using LinearTimecode.BinaryGroups;

namespace ChronosTimer;

/// <summary>The time address the LTC output should carry at a given moment.</summary>
/// <param name="Active">False when the output should be silent.</param>
/// <param name="Frames">Continuous address count (fractional; wraps at 24 h when converted).</param>
/// <param name="Rate">Frame rate of the code.</param>
/// <param name="Moving">False when the address is held (paused / hold output).</param>
/// <param name="Reverse">True when the address runs down (sent as reverse code).</param>
/// <param name="Speed">Absolute play speed (1 = nominal).</param>
public readonly record struct OutputPoint(bool Active, double Frames, LtcFrameRate Rate, bool Moving, bool Reverse, double Speed);

/// <summary>
/// The timer: modes, transport, speed/direction, end actions and chase, driven by an <see cref="IClock"/>.
/// Thread-safe: every member may be called from any thread (UI, audio callback, network).
/// </summary>
public sealed class TimerEngine
{
    private readonly object _gate = new();
    private IClock _clock;

    // settings
    private TimerMode _mode = TimerMode.Timecode;
    private LtcFrameRate _rate = LtcFrameRate.Fps25;
    private Timecode _start = new(1, 0, 0, 0, LtcFrameRate.Fps25);
    private Timecode? _end;
    private TimeSpan _duration = TimeSpan.FromMinutes(5);
    private TimeSpan _limit = TimeSpan.Zero;
    private EndAction _endAction = EndAction.Continue;
    private double _speed = 1.0;
    private bool _reverse;
    private TimeSpan _todOffset;
    private bool _todUtc;
    private TimeSpan _warning = TimeSpan.FromMinutes(1);
    private TimeSpan _critical = TimeSpan.FromSeconds(10);
    private DisplayFormat _display = DisplayFormat.Auto;
    private LtcSource _ltcSource = LtcSource.Elapsed;
    private Timecode _ltcOffset = Timecode.Zero(LtcFrameRate.Fps25);
    private IdleOutput _stoppedOutput = IdleOutput.Silence;
    private IdleOutput _pausedOutput = IdleOutput.Hold;
    private bool _stopResets;
    private UserBitsMode _ubMode = UserBitsMode.None;
    private UserBits _userBits;
    private string _userText = "";
    private DateFormat _dateFormat = DateFormat.Yymmdd;
    private bool _daylightSaving;
    private bool _colorFrame;
    private int _freewheelFrames = 25;
    private long _chaseOffsetFrames;

    // transport
    private TransportState _state = TransportState.Stopped;
    private double _anchorElapsed;   // seconds of count at _anchorTime
    private double _anchorTime;
    private bool _ended;

    // chase
    private Timecode? _chaseTc;
    private double _chaseTime = double.NegativeInfinity;
    private double _chaseSpeed = 1;

    public TimerEngine(IClock? clock = null)
    {
        _clock = clock ?? SystemClock.Instance;
        _anchorTime = _clock.Now;
    }

    /// <summary>Raised (outside the lock) after any setting or transport change, with a short human-readable description.</summary>
    public event Action<string>? Changed;

    /// <summary>Raised when a count reaches its end (duration, end address, zero), with the action taken.</summary>
    public event Action<EndAction>? Ended;

    /// <summary>Returns the local (or UTC) wall-clock time. Replaceable for tests.</summary>
    public Func<DateTimeOffset> WallClock { get; set; } = () => DateTimeOffset.Now;

    // ───────────────────────────── clock ─────────────────────────────

    /// <summary>The time base. Setting it keeps the count continuous.</summary>
    public IClock Clock
    {
        get { lock (_gate) return _clock; }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_gate)
            {
                if (ReferenceEquals(value, _clock)) return;
                double oldNow = _clock.Now, newNow = value.Now;
                _anchorElapsed = ElapsedAt(oldNow);
                _anchorTime = newNow;
                if (!double.IsNegativeInfinity(_chaseTime)) _chaseTime += newNow - oldNow;
                _clock = value;
            }
        }
    }

    /// <summary>Current engine time.</summary>
    public double Now { get { lock (_gate) return _clock.Now; } }

    private double ElapsedAt(double t) =>
        _state == TransportState.Running ? _anchorElapsed + (t - _anchorTime) * _speed * (_reverse ? -1 : 1) : _anchorElapsed;

    private void Rebase(double now)
    {
        _anchorElapsed = ElapsedAt(now);
        _anchorTime = now;
    }

    private void Raise(string what)
    {
        Changed?.Invoke(what);
    }

    private void Mutate(string what, Action<double> change)
    {
        EndAction? ended;
        lock (_gate)
        {
            double now = _clock.Now;
            ended = CheckEnd(now);
            Rebase(now);
            change(now);
        }
        if (ended is { } e) Ended?.Invoke(e);
        Raise(what);
    }

    // ───────────────────────────── settings ─────────────────────────────

    public TimerMode Mode
    {
        get { lock (_gate) return _mode; }
        set => Mutate($"mode {value}", _ => { _mode = value; _ended = false; });
    }

    public LtcFrameRate Rate
    {
        get { lock (_gate) return _rate; }
        set => Mutate($"rate {value.DisplayName()}", _ =>
        {
            _rate = value;
            _start = Relabel(_start, value);
            if (_end is { } e) _end = Relabel(e, value);
            _ltcOffset = Relabel(_ltcOffset, value);
            if (_chaseTc is { } c) _chaseTc = Relabel(c, value);
        });
    }

    private static Timecode Relabel(Timecode tc, LtcFrameRate rate)
    {
        var t = Timecode.CreateUnchecked(tc.Hours, tc.Minutes, tc.Seconds, Math.Min(tc.Frames, rate.FramesPerSecond() - 1), rate);
        return t.IsValid ? t : Timecode.CreateUnchecked(tc.Hours, tc.Minutes, tc.Seconds, 2, rate);
    }

    /// <summary>Timecode mode: the address at the start (reset point).</summary>
    public Timecode Start
    {
        get { lock (_gate) return _start; }
        set => Mutate($"start {value}", _ => _start = Relabel(value, _rate));
    }

    /// <summary>Timecode mode: optional end address, where <see cref="EndAction"/> applies.</summary>
    public Timecode? End
    {
        get { lock (_gate) return _end; }
        set => Mutate($"end {value?.ToString() ?? "none"}", _ => _end = value is { } v ? Relabel(v, _rate) : null);
    }

    /// <summary>Countdown length.</summary>
    public TimeSpan Duration
    {
        get { lock (_gate) return _duration; }
        set
        {
            if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), "Duration can't be negative.");
            Mutate($"duration {Fmt(value)}", _ => { _duration = value; _ended = false; });
        }
    }

    /// <summary>Count-up limit (zero = none), where <see cref="EndAction"/> applies.</summary>
    public TimeSpan Limit
    {
        get { lock (_gate) return _limit; }
        set
        {
            if (value < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(value), "Limit can't be negative.");
            Mutate($"limit {Fmt(value)}", _ => { _limit = value; _ended = false; });
        }
    }

    public EndAction EndAction
    {
        get { lock (_gate) return _endAction; }
        set => Mutate($"end-action {value}", _ => _endAction = value);
    }

    /// <summary>Play speed, 0.01–100 (1 = real time).</summary>
    public double Speed
    {
        get { lock (_gate) return _speed; }
        set
        {
            if (!(value is >= 0.01 and <= 100)) throw new ArgumentOutOfRangeException(nameof(value), "Speed must be between 0.01 and 100.");
            Mutate(FormattableString.Invariant($"speed {value:0.###}"), _ => _speed = value);
        }
    }

    /// <summary>Count backwards (timecode runs down; countdown counts back up).</summary>
    public bool Reverse
    {
        get { lock (_gate) return _reverse; }
        set => Mutate($"reverse {(value ? "on" : "off")}", _ => _reverse = value);
    }

    /// <summary>Time-of-day mode: offset added to the clock.</summary>
    public TimeSpan TimeOfDayOffset
    {
        get { lock (_gate) return _todOffset; }
        set => Mutate($"tod-offset {Fmt(value)}", _ => _todOffset = value);
    }

    /// <summary>Time-of-day mode: use UTC instead of local time.</summary>
    public bool TimeOfDayUtc
    {
        get { lock (_gate) return _todUtc; }
        set => Mutate($"tod-utc {(value ? "on" : "off")}", _ => _todUtc = value);
    }

    public TimeSpan Warning
    {
        get { lock (_gate) return _warning; }
        set => Mutate($"warning {Fmt(value)}", _ => _warning = value);
    }

    public TimeSpan Critical
    {
        get { lock (_gate) return _critical; }
        set => Mutate($"critical {Fmt(value)}", _ => _critical = value);
    }

    public DisplayFormat DisplayFormat
    {
        get { lock (_gate) return _display; }
        set => Mutate($"display {value}", _ => _display = value);
    }

    public LtcSource LtcSource
    {
        get { lock (_gate) return _ltcSource; }
        set => Mutate($"ltc-source {value}", _ => _ltcSource = value);
    }

    /// <summary>Count modes: address added to the elapsed/remaining count sent as LTC (e.g. 01:00:00:00).</summary>
    public Timecode LtcOffset
    {
        get { lock (_gate) return _ltcOffset; }
        set => Mutate($"ltc-offset {value}", _ => _ltcOffset = Relabel(value, _rate));
    }

    public IdleOutput StoppedOutput
    {
        get { lock (_gate) return _stoppedOutput; }
        set => Mutate($"stopped-output {value}", _ => _stoppedOutput = value);
    }

    public IdleOutput PausedOutput
    {
        get { lock (_gate) return _pausedOutput; }
        set => Mutate($"paused-output {value}", _ => _pausedOutput = value);
    }

    /// <summary>When on, Stop also returns to the start.</summary>
    public bool StopResets
    {
        get { lock (_gate) return _stopResets; }
        set => Mutate($"stop-resets {(value ? "on" : "off")}", _ => _stopResets = value);
    }

    public UserBitsMode UserBitsMode
    {
        get { lock (_gate) return _ubMode; }
        set => Mutate($"user-bits-mode {value}", _ => _ubMode = value);
    }

    public UserBits UserBits
    {
        get { lock (_gate) return _userBits; }
        set => Mutate($"user-bits {value}", _ => _userBits = value);
    }

    public string UserText
    {
        get { lock (_gate) return _userText; }
        set
        {
            _ = UserBits.FromText(value ?? ""); // validates (≤ 4 8-bit characters)
            Mutate($"user-text '{value}'", _ => _userText = value ?? "");
        }
    }

    public DateFormat DateFormat
    {
        get { lock (_gate) return _dateFormat; }
        set => Mutate($"date-format {value}", _ => _dateFormat = value);
    }

    public bool DaylightSaving
    {
        get { lock (_gate) return _daylightSaving; }
        set => Mutate($"dst {(value ? "on" : "off")}", _ => _daylightSaving = value);
    }

    public bool ColorFrame
    {
        get { lock (_gate) return _colorFrame; }
        set => Mutate($"color-frame {(value ? "on" : "off")}", _ => _colorFrame = value);
    }

    /// <summary>Chase: frames to keep running after the input code stops.</summary>
    public int FreewheelFrames
    {
        get { lock (_gate) return _freewheelFrames; }
        set
        {
            if (value is < 0 or > 10_000) throw new ArgumentOutOfRangeException(nameof(value), "Freewheel must be 0–10000 frames.");
            Mutate($"freewheel {value}", _ => _freewheelFrames = value);
        }
    }

    /// <summary>Chase: frames added to the received address (negative = behind).</summary>
    public long ChaseOffsetFrames
    {
        get { lock (_gate) return _chaseOffsetFrames; }
        set => Mutate($"chase-offset {value}", _ => _chaseOffsetFrames = value);
    }

    public TransportState State { get { lock (_gate) { CheckEndLocked(); return _state; } } }

    private void CheckEndLocked()
    {
        var e = CheckEnd(_clock.Now);
        if (e is not null) ThreadPool.QueueUserWorkItem(_ => { Ended?.Invoke(e.Value); Raise($"ended ({e.Value})"); });
    }

    // ───────────────────────────── transport ─────────────────────────────

    /// <summary>Start (or resume) counting. From an ended count, starts over.</summary>
    public void Play() => Mutate("play", now =>
    {
        if (_ended || AtEnd(now)) { _anchorElapsed = RestartPoint(); _ended = false; }
        _state = TransportState.Running;
    });

    /// <summary>Hold the current position.</summary>
    public void Pause() => Mutate("pause", now =>
    {
        if (_state != TransportState.Running) return;
        if (_mode == TimerMode.TimeOfDay) _pausedTodFrames = WallTimeOfDay(now, includeOffset: true).TotalSeconds * TodFramesPerSecond(_rate);
        _state = TransportState.Paused;
    });

    /// <summary>Halt (and return to the start when <see cref="StopResets"/> is on).</summary>
    public void Stop() => Mutate("stop", _ =>
    {
        _state = TransportState.Stopped;
        if (_stopResets) { _anchorElapsed = RestartPoint(); _ended = false; }
    });

    /// <summary>Running → paused, otherwise play.</summary>
    public void Toggle()
    {
        if (State == TransportState.Running) Pause(); else Play();
    }

    /// <summary>Return to the start, keeping the transport state.</summary>
    public void Reset() => Mutate("reset", _ => { _anchorElapsed = RestartPoint(); _ended = false; });

    /// <summary>Return to the start and play.</summary>
    public void Restart() => Mutate("restart", _ => { _anchorElapsed = RestartPoint(); _ended = false; _state = TransportState.Running; });

    private double RestartPoint()
    {
        if (!_reverse) return 0;
        return LengthSeconds() ?? 0; // counting backwards starts from the end
    }

    /// <summary>
    /// Go to a position. Timecode mode: an address (e.g. 01:02:03:04). Count-up: elapsed time. Countdown: time remaining.
    /// Time of day: sets the offset so the clock reads this now. Chase: sets the chase offset so the output reads this now.
    /// </summary>
    public void Locate(TimeInput t) => Mutate($"locate {t}", now =>
    {
        _ended = false;
        switch (_mode)
        {
            case TimerMode.Timecode:
                {
                    var tc = t.ToTimecode(_rate);
                    long diff = WrapSigned((long)tc.TotalFrames - _start.TotalFrames, _rate); // before the start counts as negative
                    _anchorElapsed = diff / _rate.CodewordRate();
                    break;
                }
            case TimerMode.CountUp:
                _anchorElapsed = Math.Abs(t.ToSeconds(_rate));
                break;
            case TimerMode.CountDown:
                _anchorElapsed = _duration.TotalSeconds - t.ToSeconds(_rate);
                break;
            case TimerMode.TimeOfDay:
                {
                    double target = t.ToTimecode(_rate).TotalFrames / TodFramesPerSecond(_rate);
                    double clock = WallTimeOfDay(now, includeOffset: false).TotalSeconds;
                    double off = target - clock;
                    off -= Math.Round(off / 86_400) * 86_400;
                    _todOffset = TimeSpan.FromSeconds(off);
                    break;
                }
            case TimerMode.Chase:
                if (_chaseTc is not null)
                {
                    long target = t.ToTimecode(_rate).TotalFrames;
                    long cur = (long)Math.Floor(ChaseFramesAt(now));
                    _chaseOffsetFrames = WrapSigned(target - cur, _rate);
                }
                break;
        }
    });

    /// <summary>Locate from text (see <see cref="TimeInput"/>).</summary>
    public void Locate(string text) => Locate(TimeInput.Parse(text));

    /// <summary>Move the position by a signed number of frames (time of day: the offset; chase: the chase offset).</summary>
    public void Nudge(long frames) => Mutate($"nudge {frames:+0;-0}", now =>
    {
        switch (_mode)
        {
            case TimerMode.TimeOfDay: _todOffset += TimeSpan.FromSeconds(frames / TodFramesPerSecond(_rate)); break;
            case TimerMode.Chase: _chaseOffsetFrames += frames; break;
            case TimerMode.CountDown: _anchorElapsed -= frames / _rate.CodewordRate(); break; // + = more time left
            default: _anchorElapsed += frames / _rate.CodewordRate(); break;
        }
    });

    /// <summary>Move by a signed time (see <see cref="TimeInput"/>, e.g. "+10s", "-1m", "+12f").</summary>
    public void Nudge(TimeInput t) => Nudge(t.ToFrames(_rate));

    /// <summary>Countdown / count-up: add (or remove) time from the length.</summary>
    public void AddTime(TimeSpan delta) => Mutate($"add {Fmt(delta)}", _ =>
    {
        if (_mode == TimerMode.CountUp) _limit = TimeSpan.FromTicks(Math.Max(0, (_limit + delta).Ticks));
        else _duration = TimeSpan.FromTicks(Math.Max(0, (_duration + delta).Ticks));
        _ended = false;
    });

    /// <summary>
    /// Jam: time of day → clear the offset; timecode mode → jump to the current time of day; chase → clear the chase
    /// offset; count modes → reset.
    /// </summary>
    public void Jam() => Mutate("jam", now =>
    {
        switch (_mode)
        {
            case TimerMode.TimeOfDay: _todOffset = TimeSpan.Zero; break;
            case TimerMode.Chase: _chaseOffsetFrames = 0; break;
            case TimerMode.Timecode:
                {
                    var tc = Timecode.FromTimeOfDay(WallTimeOfDay(now, includeOffset: false), _rate);
                    long diff = WrapSigned((long)tc.TotalFrames - _start.TotalFrames, _rate);
                    _anchorElapsed = diff / _rate.CodewordRate();
                    break;
                }
            default: _anchorElapsed = RestartPoint(); _ended = false; break;
        }
    });

    private static long WrapSigned(long frames, LtcFrameRate rate)
    {
        long perDay = rate.AddressesPerDay();
        long w = Wrap(frames, rate);
        return w >= perDay / 2 ? w - perDay : w;
    }

    private static long Wrap(long frames, LtcFrameRate rate)
    {
        long perDay = rate.AddressesPerDay();
        frames %= perDay;
        if (frames < 0) frames += perDay;
        return frames;
    }

    // ───────────────────────────── chase input ─────────────────────────────

    /// <summary>
    /// Feeds a codeword read from the LTC input: its address, the engine time at which its first bit was heard,
    /// and its measured speed (negative = reverse).
    /// </summary>
    public void OnChaseFrame(Timecode tc, double time, double speed)
    {
        lock (_gate)
        {
            _chaseTc = tc.Rate == _rate ? tc : Relabel(tc, _rate);
            _chaseTime = time;
            _chaseSpeed = Math.Abs(speed) is > 0.05 and < 20 ? speed : 1;
        }
    }

    private double ChaseFramesAt(double t)
    {
        if (_chaseTc is not { } c) return 0;
        double age = Math.Max(0, t - _chaseTime);
        double freewheel = _freewheelFrames / _rate.CodewordRate();
        double run = Math.Min(age, freewheel + 1 / _rate.CodewordRate());
        return c.TotalFrames + run * _rate.CodewordRate() * _chaseSpeed;
    }

    private ChaseStatus ChaseStatusAt(double t)
    {
        if (_chaseTc is null) return ChaseStatus.NoSignal;
        double age = t - _chaseTime;
        double frame = 1 / _rate.CodewordRate();
        if (age < 3 * frame / Math.Max(0.05, Math.Abs(_chaseSpeed))) return ChaseStatus.Locked;
        if (age < (_freewheelFrames + 1) * frame) return ChaseStatus.Freewheel;
        return ChaseStatus.NoSignal;
    }

    // ───────────────────────────── evaluation ─────────────────────────────

    private double? LengthSeconds()
    {
        switch (_mode)
        {
            case TimerMode.Timecode:
                if (_end is not { } e) return null;
                long diff = Wrap((long)e.TotalFrames - _start.TotalFrames, _rate);
                return diff == 0 ? null : diff / _rate.CodewordRate();
            case TimerMode.CountUp:
                return _limit > TimeSpan.Zero ? _limit.TotalSeconds : null;
            case TimerMode.CountDown:
                return _duration.TotalSeconds;
            default:
                return null;
        }
    }

    private bool AtEnd(double now)
    {
        if (LengthSeconds() is not { } len) return false;
        double el = ElapsedAt(now);
        return _reverse ? el <= 0 : el >= len && _endAction != EndAction.Continue;
    }

    /// <summary>Applies the end action if the count has passed its end. Returns the action applied, if any.</summary>
    private EndAction? CheckEnd(double now)
    {
        if (_state != TransportState.Running || _endAction == EndAction.Continue) return null;
        if (LengthSeconds() is not { } len) return null;
        double el = ElapsedAt(now);
        bool past = _reverse ? el <= 0 : el >= len;
        if (!past) return null;

        switch (_endAction)
        {
            case EndAction.Stop:
            case EndAction.Pause:
                _anchorElapsed = _reverse ? 0 : len;
                _anchorTime = now;
                _state = _endAction == EndAction.Stop ? TransportState.Stopped : TransportState.Paused;
                _ended = true;
                break;
            case EndAction.Loop:
                double m = len <= 0 ? 0 : ((el % len) + len) % len;
                _anchorElapsed = m;
                _anchorTime = now;
                break;
        }
        return _endAction;
    }

    private static double TodFramesPerSecond(LtcFrameRate rate) =>
        rate.IsDropFrame() ? rate.CodewordRate() : rate.FramesPerSecond();

    private TimeSpan WallTimeOfDay(double t, bool includeOffset)
    {
        var wall = WallClock() + TimeSpan.FromSeconds(t - _clock.Now);
        var tod = _todUtc ? wall.UtcDateTime.TimeOfDay : wall.DateTime.TimeOfDay;
        if (includeOffset) tod += _todOffset;
        long ticks = tod.Ticks % TimeSpan.TicksPerDay;
        if (ticks < 0) ticks += TimeSpan.TicksPerDay;
        return TimeSpan.FromTicks(ticks);
    }

    /// <summary>Wall-clock instant corresponding to engine time <paramref name="t"/> (plus the time-of-day offset in that mode).</summary>
    public DateTimeOffset WallTimeAt(double t)
    {
        lock (_gate)
        {
            var wall = WallClock() + TimeSpan.FromSeconds(t - _clock.Now);
            if (_mode == TimerMode.TimeOfDay) wall += _todOffset;
            return _todUtc ? wall.ToUniversalTime() : wall;
        }
    }

    private double _pausedTodFrames;

    /// <summary>What the LTC output should carry at engine time <paramref name="t"/>.</summary>
    public OutputPoint OutputAt(double t)
    {
        EndAction? ended;
        OutputPoint p;
        lock (_gate)
        {
            ended = CheckEnd(Math.Min(t, _clock.Now));
            p = OutputAtLocked(t);
        }
        if (ended is { } e) ThreadPool.QueueUserWorkItem(_ => { Ended?.Invoke(e); Raise($"ended ({e})"); });
        return p;
    }

    private OutputPoint OutputAtLocked(double t)
    {
        double el = ElapsedAt(t);
        bool running = _state == TransportState.Running;
        var idle = _state == TransportState.Stopped ? _stoppedOutput : _pausedOutput;
        bool active = running || idle == IdleOutput.Hold;

        if (_mode == TimerMode.Chase)
        {
            var cs = ChaseStatusAt(t);
            bool chaseActive = cs != ChaseStatus.NoSignal && active;
            bool moving = running && cs != ChaseStatus.NoSignal;
            return new OutputPoint(chaseActive, ChaseFramesAt(t) + _chaseOffsetFrames, _rate, moving, moving && _chaseSpeed < 0, Math.Clamp(Math.Abs(_chaseSpeed), 0.05, 20));
        }

        bool tod = _mode == TimerMode.TimeOfDay || (_mode is TimerMode.CountUp or TimerMode.CountDown && _ltcSource == LtcSource.TimeOfDay);
        if (tod)
        {
            double f = _mode == TimerMode.TimeOfDay && _state == TransportState.Paused
                ? _pausedTodFrames
                : WallTimeOfDay(t, includeOffset: _mode == TimerMode.TimeOfDay).TotalSeconds * TodFramesPerSecond(_rate);
            bool moving = _mode != TimerMode.TimeOfDay || running;
            return new OutputPoint(active, f, _rate, moving, false, 1);
        }

        double frames;
        bool down;
        switch (_mode)
        {
            case TimerMode.Timecode:
                frames = _start.TotalFrames + el * _rate.CodewordRate();
                down = _reverse;
                break;
            case TimerMode.CountDown when _ltcSource == LtcSource.Remaining:
                frames = _ltcOffset.TotalFrames + (_duration.TotalSeconds - el) * _rate.CodewordRate();
                down = !_reverse;
                break;
            case TimerMode.CountUp when _ltcSource == LtcSource.Remaining && LengthSeconds() is { } len:
                frames = _ltcOffset.TotalFrames + (len - el) * _rate.CodewordRate();
                down = !_reverse;
                break;
            default:
                frames = _ltcOffset.TotalFrames + el * _rate.CodewordRate();
                down = _reverse;
                break;
        }
        return new OutputPoint(active, frames, _rate, running, running && down, _speed);
    }

    /// <summary>Builds the LTC frame for an address: user bits, binary group flags and color frame flag per the settings.</summary>
    public LtcFrame BuildFrame(Timecode tc, DateTimeOffset wall)
    {
        UserBitsMode mode; UserBits ub; string text; DateFormat df; bool dst, cf, clock;
        lock (_gate)
        {
            mode = _ubMode; ub = _userBits; text = _userText; df = _dateFormat; dst = _daylightSaving; cf = _colorFrame;
            clock = _mode == TimerMode.TimeOfDay || (_mode is TimerMode.CountUp or TimerMode.CountDown && _ltcSource == LtcSource.TimeOfDay);
        }
        var frame = new LtcFrame(tc) { ColorFrame = cf };
        switch (mode)
        {
            case UserBitsMode.Hex:
                return frame with { UserBits = ub, BinaryGroupFlags = clock ? BinaryGroupFlags.ClockTime : BinaryGroupFlags.Unspecified };
            case UserBitsMode.Text:
                return frame with { UserBits = UserBits.FromText(text), BinaryGroupFlags = BinaryGroupFlags.EightBitCharacters };
            case UserBitsMode.Date:
                DateTimeZone dtz;
                try { dtz = DateTimeZone.ForInstant(wall, df, dst); }
                catch (ArgumentException) { dtz = DateTimeZone.ForInstant(wall.ToUniversalTime(), df, dst); }
                return frame.WithDateTimeZone(dtz, clockTime: clock);
            default:
                return frame with { BinaryGroupFlags = clock ? BinaryGroupFlags.ClockTime : BinaryGroupFlags.Unspecified };
        }
    }

    /// <summary>Takes a snapshot of the timer now.</summary>
    public TimerStatus GetStatus() => GetStatus(outputActive: false);

    /// <summary>Takes a snapshot; <paramref name="outputActive"/> is filled in by the host that owns the audio output.</summary>
    public TimerStatus GetStatus(bool outputActive)
    {
        EndAction? ended;
        TimerStatus s;
        lock (_gate)
        {
            double now = _clock.Now;
            ended = CheckEnd(now);
            s = StatusLocked(now, outputActive);
        }
        if (ended is { } e) ThreadPool.QueueUserWorkItem(_ => { Ended?.Invoke(e); Raise($"ended ({e})"); });
        return s;
    }

    private TimerStatus StatusLocked(double now, bool outputActive)
    {
        double el = ElapsedAt(now);
        double? len = LengthSeconds();
        var fmt = _display == DisplayFormat.Auto
            ? (_mode is TimerMode.CountUp or TimerMode.CountDown ? DisplayFormat.Compact : DisplayFormat.Timecode)
            : _display;

        Timecode tc;
        string display, detail = "";
        TimeSpan? remaining = null;
        var phase = TimerPhase.Normal;
        var chase = ChaseStatus.NoSignal;
        int perDay = _rate.AddressesPerDay();

        switch (_mode)
        {
            case TimerMode.Timecode:
                {
                    long f = (long)Math.Floor(_start.TotalFrames + el * _rate.CodewordRate() + 1e-6);
                    tc = Timecode.FromTotalFrames(f, _rate);
                    display = FormatAddress(tc, fmt);
                    if (len is { } l)
                    {
                        remaining = TimeSpan.FromSeconds(l - el);
                        detail = $"{_start} → {_end}";
                        if (el > l) phase = TimerPhase.Overrun;
                    }
                    else detail = $"from {_start}";
                    break;
                }
            case TimerMode.CountUp:
                {
                    tc = Timecode.FromTotalFrames((long)Math.Floor(Math.Max(0, el) * _rate.CodewordRate() + 1e-6) % perDay, _rate);
                    display = FormatSpan(Math.Max(0, el), fmt, ceiling: false);
                    if (len is { } l)
                    {
                        remaining = TimeSpan.FromSeconds(l - el);
                        detail = "limit " + FormatSpan(l, DisplayFormat.Clock, false);
                        if (el > l) phase = TimerPhase.Overrun;
                    }
                    break;
                }
            case TimerMode.CountDown:
                {
                    double rem = _duration.TotalSeconds - el;
                    remaining = TimeSpan.FromSeconds(rem);
                    if (rem < 0)
                    {
                        phase = TimerPhase.Overrun;
                        display = "+" + FormatSpan(-rem, fmt, ceiling: false);
                    }
                    else
                    {
                        display = FormatSpan(rem, fmt, ceiling: true);
                        if (rem <= _critical.TotalSeconds) phase = TimerPhase.Critical;
                        else if (rem <= _warning.TotalSeconds) phase = TimerPhase.Warning;
                    }
                    tc = Timecode.FromTotalFrames((long)Math.Floor(Math.Abs(rem) * _rate.CodewordRate()) % perDay, _rate);
                    detail = "of " + FormatSpan(_duration.TotalSeconds, DisplayFormat.Clock, false) + (rem < 0 ? "  overrun" : "");
                    break;
                }
            case TimerMode.TimeOfDay:
                {
                    if (_state != TransportState.Paused)
                        _pausedTodFrames = WallTimeOfDay(now, includeOffset: true).TotalSeconds * TodFramesPerSecond(_rate);
                    tc = Timecode.FromTotalFrames((long)Math.Floor(_pausedTodFrames), _rate);
                    if (!_rate.IsDropFrame())
                        tc = Timecode.FromTimeOfDay(TimeSpan.FromSeconds(_pausedTodFrames / TodFramesPerSecond(_rate)), _rate);
                    display = FormatAddress(tc, fmt);
                    detail = (_todUtc ? "UTC" : "local time") + (_todOffset != TimeSpan.Zero ? " " + (_todOffset < TimeSpan.Zero ? "−" : "+") + FormatSpan(Math.Abs(_todOffset.TotalSeconds), DisplayFormat.Tenths, false) : "");
                    break;
                }
            default: // chase
                {
                    chase = ChaseStatusAt(now);
                    tc = Timecode.FromTotalFrames((long)Math.Floor(ChaseFramesAt(now) + _chaseOffsetFrames + 1e-6), _rate);
                    display = _chaseTc is null ? FormatAddress(Timecode.Zero(_rate), fmt).Replace('0', '-') : FormatAddress(tc, fmt);
                    detail = chase switch
                    {
                        ChaseStatus.Locked => FormattableString.Invariant($"locked ×{Math.Abs(_chaseSpeed):0.00}{(_chaseSpeed < 0 ? " reverse" : "")}"),
                        ChaseStatus.Freewheel => "freewheel",
                        _ => "no LTC input",
                    } + (_chaseOffsetFrames != 0 ? $"  offset {_chaseOffsetFrames:+0;-0} fr" : "");
                    break;
                }
        }

        if (_ended) phase = TimerPhase.Ended;
        double? progress = len is { } ln && ln > 0 ? Math.Clamp(el / ln, 0, 1) : null;

        return new TimerStatus
        {
            Time = now,
            Mode = _mode,
            State = _state,
            Rate = _rate,
            Timecode = tc,
            Display = display,
            Detail = detail,
            DisplayFormat = fmt,
            Elapsed = _mode is TimerMode.TimeOfDay or TimerMode.Chase ? TimeSpan.Zero : TimeSpan.FromSeconds(el),
            Remaining = remaining,
            Length = len is { } lx ? TimeSpan.FromSeconds(lx) : null,
            Progress = progress,
            Phase = phase,
            Speed = _mode == TimerMode.Chase ? Math.Abs(_chaseSpeed) : _speed,
            Reverse = _mode == TimerMode.Chase ? _chaseSpeed < 0 : _reverse,
            Chase = chase,
            ChaseInput = _chaseTc,
            OutputActive = outputActive,
        };
    }

    // ───────────────────────────── formatting ─────────────────────────────

    private static string FormatAddress(Timecode tc, DisplayFormat fmt)
    {
        int fps = tc.Rate.FramesPerSecond();
        switch (fmt)
        {
            case DisplayFormat.Clock:
                return string.Create(CultureInfo.InvariantCulture, $"{tc.Hours:00}:{tc.Minutes:00}:{tc.Seconds:00}");
            case DisplayFormat.Compact:
                return tc.Hours > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{tc.Hours}:{tc.Minutes:00}:{tc.Seconds:00}")
                    : string.Create(CultureInfo.InvariantCulture, $"{tc.Minutes}:{tc.Seconds:00}");
            case DisplayFormat.Tenths:
                int tenths = tc.Frames * 10 / fps;
                return tc.Hours > 0
                    ? string.Create(CultureInfo.InvariantCulture, $"{tc.Hours}:{tc.Minutes:00}:{tc.Seconds:00}.{tenths}")
                    : string.Create(CultureInfo.InvariantCulture, $"{tc.Minutes}:{tc.Seconds:00}.{tenths}");
            default:
                return tc.ToString();
        }
    }

    /// <summary>Formats a duration in seconds. <paramref name="ceiling"/> rounds up (countdowns show 5:00 until a full second has passed).</summary>
    internal string FormatSpan(double seconds, DisplayFormat fmt, bool ceiling)
    {
        seconds = Math.Max(0, seconds);
        switch (fmt)
        {
            case DisplayFormat.Timecode:
                {
                    long f = ceiling ? (long)Math.Ceiling(seconds * _rate.CodewordRate() - 1e-6) : (long)Math.Floor(seconds * _rate.CodewordRate() + 1e-6);
                    long perDay = _rate.AddressesPerDay();
                    return Timecode.FromTotalFrames(f % perDay, _rate).ToString();
                }
            case DisplayFormat.Tenths:
                {
                    long t = ceiling ? (long)Math.Ceiling(seconds * 10 - 1e-6) : (long)Math.Floor(seconds * 10 + 1e-6);
                    long s = t / 10;
                    return s >= 3600
                        ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}.{t % 10}")
                        : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}.{t % 10}");
                }
            default:
                {
                    long s = ceiling ? (long)Math.Ceiling(seconds - 1e-6) : (long)Math.Floor(seconds + 1e-6);
                    if (fmt == DisplayFormat.Clock)
                        return string.Create(CultureInfo.InvariantCulture, $"{s / 3600:00}:{s / 60 % 60:00}:{s % 60:00}");
                    return s >= 3600
                        ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
                        : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
                }
        }
    }

    /// <summary>Human-readable duration: "01:30:00", "00:05:00.5", "-00:00:10".</summary>
    public static string Fmt(TimeSpan t)
    {
        string sign = t < TimeSpan.Zero ? "-" : "";
        t = t.Duration();
        string s = string.Create(CultureInfo.InvariantCulture, $"{sign}{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00}");
        int ms = t.Milliseconds;
        return ms == 0 ? s : s + string.Create(CultureInfo.InvariantCulture, $".{ms:000}").TrimEnd('0');
    }
}
