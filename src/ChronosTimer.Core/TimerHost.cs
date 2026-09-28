using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ChronosTimer.Audio;
using ChronosTimer.Control;
using ChronosTimer.Settings;
using ChronosTimer.Show;
using LinearTimecode;
using LinearTimecode.BinaryGroups;

namespace ChronosTimer;

/// <summary>
/// Everything a Chronos Timer app needs: the engine, LTC output and input on an <see cref="IAudioBackend"/>, the HTTP /
/// WebSocket and OSC control servers, the settings catalog, the text commands and settings persistence.
/// The console utility and the MAUI app are thin shells around this class.
/// </summary>
public sealed partial class TimerHost : IDisposable
{
    private readonly object _audioGate = new();
    private readonly List<string> _log = [];
    private readonly List<Setting> _settings = [];
    private readonly Dictionary<string, Setting> _byName = new(StringComparer.OrdinalIgnoreCase);

    // audio
    private IAudioStream? _outStream, _inStream;
    private LtcOutput? _ltcOut;
    private LtcInput? _ltcIn;
    private bool _outputEnabled, _inputEnabled;
    private string _outputDevice = "default", _inputDevice = "default";
    private int _sampleRate = 48_000, _outChannels = 2, _outChannel, _inChannel = 1, _bufferMs = 20;
    private double _levelDb = -12, _offsetMs, _riseUs = 40, _inputMinDb = -40;
    private bool _invert, _followRate = true;
    private ClockSource _clockSource = ClockSource.Auto;
    private LtcFrameRate? _inputRate;
    private string _outputStatus = "off", _inputStatus = "off";

    // control
    private HttpControlServer? _http;
    private OscControlServer? _osc;
    private bool _httpEnabled = true, _oscEnabled = true;
    private int _httpPort = 8480, _oscPort = 9000;
    private string _httpBind = NetworkBinding.Localhost, _oscBind = NetworkBinding.Localhost;
    private string _httpToken = NewToken(), _httpOrigins = "";
    private string _oscFeedback = "";
    private double _pushRate = 20;
    private bool _suspendApply;

    // Source of the command running on this thread (null = local UI / console).
    [ThreadStatic] private static string? t_source;

    public TimerHost(IAudioBackend? audio = null, TimerEngine? engine = null)
    {
        Audio = audio ?? NullAudioBackend.Instance;
        Engine = engine ?? new TimerEngine();
        Commands = new CommandSet();
        Show = new ShowController(this);
        BuildSettings();
        BuildShowSettings();
        BuildCommands();
        BuildShowCommands();
        Engine.Changed += what => { if (what.StartsWith("mode", StringComparison.Ordinal)) UpdateClockChoice(); StatusChanged?.Invoke(); };
        Engine.Ended += a => Log($"End reached → {Setting.Token(a)}");
    }

    public TimerEngine Engine { get; }
    public IAudioBackend Audio { get; }
    public CommandSet Commands { get; }
    public IReadOnlyList<Setting> Settings => _settings;

    /// <summary>Path used by save/load (null = <see cref="SettingsFile.DefaultPath"/>).</summary>
    public string? SettingsPath { get; set; }

    /// <summary>Raised for log lines (commands from remotes, errors, device changes). May come from any thread.</summary>
    public event Action<string>? Message;

    /// <summary>Raised after a setting or transport change (any thread).</summary>
    public event Action? StatusChanged;

    public IReadOnlyList<string> RecentLog { get { lock (_log) return [.. _log]; } }

    public void Log(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + line;
        lock (_log)
        {
            _log.Add(stamped);
            if (_log.Count > 500) _log.RemoveRange(0, _log.Count - 500);
        }
        Message?.Invoke(stamped);
    }

    public Setting? FindSetting(string name) => _byName.GetValueOrDefault(name.Trim().TrimStart('-'));

    /// <summary>Current status snapshot (with output state).</summary>
    public TimerStatus Status => Engine.GetStatus(_ltcOut?.IsActive ?? false);

    public string OutputStatus => _outputStatus;
    public string InputStatus => _inputStatus;
    public double InputLevel => _ltcIn?.Level ?? 0;
    public LtcFrameRate? InputDetectedRate => _ltcIn?.DetectedRate;
    public Timecode? LastSent => _ltcOut?.LastSent;
    public HttpControlServer? Http => _http;
    public OscControlServer? Osc => _osc;
    public double PushRate => _pushRate;

    /// <summary>Status as JSON (for HTTP/WebSocket).</summary>
    public string StatusJson() => Json.Status(Status, _outputStatus, _inputStatus, InputLevel);

    /// <summary>Runs a text command. <paramref name="source"/> (e.g. "http 10.0.0.5") is logged for remote commands.</summary>
    public CommandResult Execute(string line, string? source = null)
    {
        string? prev = t_source;
        t_source = source;
        CommandResult r;
        try { r = Commands.Execute(line); }
        finally { t_source = prev; }
        if (source is not null) Log($"[{source}] {line.Trim()} → {(r.Ok ? r.Message.Split('\n')[0] : "error: " + r.Message)}");
        return r;
    }

    // ═════════════════════════════ lifecycle ═════════════════════════════

    /// <summary>Opens audio and starts the control servers according to the settings.</summary>
    public void Start()
    {
        ApplyOutput();
        ApplyInput();
        ApplyHttp();
        ApplyOsc();
        Show.Start();
    }

    public void Dispose()
    {
        Show.Dispose();
        CloseOutput();
        CloseInput();
        _http?.Dispose(); _http = null;
        _osc?.Dispose(); _osc = null;
    }

    // ═════════════════════════════ audio ═════════════════════════════

    private int BufferFrames => Math.Max(64, _sampleRate * _bufferMs / 1000);

    private void ApplyOutput()
    {
        if (_suspendApply) return;
        lock (_audioGate)
        {
            CloseOutputLocked();
            if (!_outputEnabled) { _outputStatus = "off"; return; }
            try
            {
                var ltc = new LtcOutput(Engine, _sampleRate)
                {
                    Amplitude = (float)Math.Pow(10, _levelDb / 20),
                    RiseTime = TimeSpan.FromTicks((long)Math.Round(_riseUs * 10)),
                    Invert = _invert,
                    Channel = _outChannel,
                    OffsetSeconds = _offsetMs / 1000,
                };
                var stream = Audio.OpenOutput(_outputDevice, _sampleRate, _outChannels, BufferFrames, ltc.Render);
                ltc.LatencySamples = stream.LatencySamples;
                stream.Failed += ex => { Log("LTC output failed: " + ex.Message); ThreadPool.QueueUserWorkItem(_ => { lock (_audioGate) { CloseOutputLocked(); _outputStatus = "failed: " + ex.Message; } }); };
                _ltcOut = ltc;
                _outStream = stream;
                UpdateClockChoice();
                _outputStatus = FormattableString.Invariant($"{stream.DeviceName} · {stream.SampleRate / 1000.0:0.#} kHz · {stream.Channels} ch · {1000.0 * stream.LatencySamples / stream.SampleRate:0} ms");
                Log("LTC output on: " + _outputStatus);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _outputStatus = "failed: " + ex.Message;
                Log("LTC output failed: " + ex.Message);
            }
        }
    }

    private void CloseOutput() { lock (_audioGate) CloseOutputLocked(); }

    private void CloseOutputLocked()
    {
        var s = _outStream; var o = _ltcOut;
        _outStream = null; _ltcOut = null;
        s?.Dispose();
        o?.DetachClock();
    }

    private void UpdateClockChoice()
    {
        var o = _ltcOut;
        if (o is null) return;
        var mode = Engine.Mode;
        o.UseAudioClock = _clockSource == ClockSource.Audio || (_clockSource == ClockSource.Auto && mode is not (TimerMode.TimeOfDay or TimerMode.Chase));
    }

    private void ApplyInput()
    {
        if (_suspendApply) return;
        lock (_audioGate)
        {
            CloseInputLocked();
            if (!_inputEnabled) { _inputStatus = "off"; return; }
            try
            {
                var ltc = new LtcInput(Engine, _sampleRate, _inputRate)
                {
                    Channel = _inChannel,
                    FollowRate = _followRate,
                    MinimumLevel = Math.Pow(10, _inputMinDb / 20),
                };
                var stream = Audio.OpenInput(_inputDevice, _sampleRate, Math.Max(1, _inChannel), BufferFrames, ltc.Process);
                ltc.LatencySamples = stream.LatencySamples;
                stream.Failed += ex => { Log("LTC input failed: " + ex.Message); ThreadPool.QueueUserWorkItem(_ => { lock (_audioGate) { CloseInputLocked(); _inputStatus = "failed: " + ex.Message; } }); };
                _ltcIn = ltc;
                _inStream = stream;
                _inputStatus = FormattableString.Invariant($"{stream.DeviceName} · {stream.SampleRate / 1000.0:0.#} kHz · ch {_inChannel}");
                Log("LTC input on: " + _inputStatus);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _inputStatus = "failed: " + ex.Message;
                Log("LTC input failed: " + ex.Message);
            }
        }
    }

    private void CloseInput() { lock (_audioGate) CloseInputLocked(); }

    private void CloseInputLocked()
    {
        var s = _inStream;
        _inStream = null; _ltcIn = null;
        s?.Dispose();
    }

    // Live-adjustable output parameters (no reopen needed).
    private void TuneOutput()
    {
        if (_ltcOut is not { } o) return;
        o.Amplitude = (float)Math.Pow(10, _levelDb / 20);
        o.RiseTime = TimeSpan.FromTicks((long)Math.Round(_riseUs * 10));
        o.Invert = _invert;
        o.Channel = _outChannel;
        o.OffsetSeconds = _offsetMs / 1000;
        UpdateClockChoice();
    }

    private void TuneInput()
    {
        if (_ltcIn is not { } i) return;
        i.Channel = _inChannel;
        i.FollowRate = _followRate;
        i.MinimumLevel = Math.Pow(10, _inputMinDb / 20);
    }

    // ═════════════════════════════ control servers ═════════════════════════════

    private void ApplyHttp()
    {
        if (_suspendApply) return;
        _http?.Dispose();
        _http = null;
        if (!_httpEnabled) return;
        try
        {
            var origins = _httpOrigins.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
            _http = new HttpControlServer(this, BindAddress(_httpBind, "HTTP"), _httpPort, _httpToken, origins);
            _http.Start();
            Log($"HTTP/WebSocket control on {_http.BindAddress} port {_http.Port}: {string.Join("  ", _http.Urls)}"
                + (_httpToken.Length == 0 ? "  (no access token: anyone who can reach it has full control)" : ""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _http = null;
            Log($"HTTP control could not start on port {_httpPort}: {ex.Message}");
        }
    }

    private void ApplyOsc()
    {
        if (_suspendApply) return;
        _osc?.Dispose();
        _osc = null;
        if (!_oscEnabled) return;
        try
        {
            _osc = new OscControlServer(this, _oscPort, BindAddress(_oscBind, "OSC"));
            try { _osc.SetFeedbackTargets(_oscFeedback); }
            catch (FormatException ex) { Log("OSC feedback off: " + ex.Message); }
            _osc.Start();
            Log($"OSC control on {_osc.BindAddress} UDP port {_osc.Port} (address prefix /chronos)" + (_oscFeedback.Length > 0 ? $", feedback to {_oscFeedback}" : ""));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _osc = null;
            Log($"OSC control could not start on port {_oscPort}: {ex.Message}");
        }
    }

    /// <summary>The address for a bind setting; falls back to this computer only when the interface is gone.</summary>
    private IPAddress BindAddress(string bind, string what)
    {
        if (NetworkBinding.Resolve(bind) is { } ip) return ip;
        Log($"{what}: network interface '{bind}' not found or down; listening on this computer only.");
        return IPAddress.Loopback;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();

    private static string ParseToken(string v)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "" or "off" or "none": return "";
            case "new" or "generate" or "random": return NewToken();
        }
        string t = v.Trim();
        if (t.Length < 8 || t.Length > 128 || !t.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or '~'))
            throw new FormatException("The token must be 8–128 letters, digits, '-', '_', '.' or '~' (or 'new' for a random one, 'off' for none).");
        return t;
    }

    // ═════════════════════════════ persistence ═════════════════════════════

    /// <summary>Saves every setting as a flat JSON object.</summary>
    public string Save(string? path = null)
    {
        path ??= SettingsPath ?? SettingsFile.DefaultPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, Json.SettingsFile(_settings));
        return path;
    }

    /// <summary>Loads settings from a file; returns the problems (unknown names, bad values).</summary>
    public IReadOnlyList<string> Load(string? path = null)
    {
        path ??= SettingsPath ?? SettingsFile.DefaultPath();
        return Apply(Json.ReadFlat(File.ReadAllText(path)));
    }

    /// <summary>Applies many settings at once (audio and servers restart once at the end).</summary>
    public IReadOnlyList<string> Apply(IEnumerable<KeyValuePair<string, string>> values)
    {
        var problems = new List<string>();
        _suspendApply = true;
        try
        {
            foreach (var (k, v) in values)
            {
                if (k.Equals("http-local-only", StringComparison.OrdinalIgnoreCase)) continue; // replaced by http-bind (default localhost)
                if (FindSetting(k) is not { } s) { problems.Add($"unknown setting '{k}'"); continue; }
                try { s.Set(v); }
                catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { problems.Add($"{k}: {ex.Message}"); }
            }
        }
        finally { _suspendApply = false; }
        StatusChanged?.Invoke();
        return problems;
    }

    /// <summary>Restores every setting to its built-in default.</summary>
    public void Defaults() => Apply(_settings.Select(s => new KeyValuePair<string, string>(s.Name, s.Default)).ToList());

    // ═════════════════════════════ settings catalog ═════════════════════════════

    private void Add(Setting s)
    {
        s.Changed += _ => { if (!_suspendApply) StatusChanged?.Invoke(); };
        _settings.Add(s);
        _byName[s.Name] = s;
    }

    private static IReadOnlyList<SettingOption> Opts(params (string Value, string Description)[] o) => [.. o.Select(x => new SettingOption(x.Value, x.Description))];

    private IReadOnlyList<SettingOption> DeviceOptions(bool output)
    {
        var list = new List<SettingOption> { new("default", "System default device") };
        try
        {
            foreach (var d in output ? Audio.GetOutputDevices() : Audio.GetInputDevices())
                if (d.Id != "default") list.Add(new SettingOption(d.Id, d.Name + (d.Channels > 0 ? $" ({d.Channels} ch)" : "")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { list.Add(new SettingOption("?", "Device list unavailable: " + ex.Message)); }
        return list;
    }

    private Timecode ParseTc(string v) => TimeInput.Parse(v).ToTimecode(Engine.Rate);

    private static TimeSpan ParseSpan(string v, LtcFrameRate rate) => TimeSpan.FromSeconds(TimeInput.Parse(v).ToSeconds(rate));

    private void BuildSettings()
    {
        var e = Engine;
        const string T = "Timer", C = "Countdown", D = "Time of day", CH = "Chase", O = "LTC output", U = "User bits", I = "LTC input", R = "Remote control";

        Add(new Setting("mode", T, SettingKind.Choice, "What the timer counts.",
            () => Setting.Token(e.Mode), v => e.Mode = Setting.ParseEnum<TimerMode>(v),
            Opts(("timecode", "Timecode generator: counts up from the start address, optional end address"),
                 ("count-up", "Stopwatch: elapsed time from zero, optional limit"),
                 ("count-down", "Countdown: duration down to zero, then the end action"),
                 ("time-of-day", "Follows the computer clock (plus offset)"),
                 ("chase", "Follows LTC from the audio input; freewheels through dropouts; regenerates it on the output"))));
        Add(new Setting("rate", T, SettingKind.Choice, "Frame rate of the count and the LTC.",
            () => e.Rate.Token(), v => e.Rate = LtcFrameRateExtensions.Parse(v), Setting.RateOptions));
        Add(new Setting("start", T, SettingKind.Timecode, "Timecode mode: address at the start / reset point.",
            () => e.Start.ToString(), v => e.Start = ParseTc(v)));
        Add(new Setting("end", T, SettingKind.Timecode, "Timecode mode: end address where the end action applies ('none' = run on).",
            () => e.End?.ToString() ?? "none", v => e.End = v is "" or "none" or "off" ? null : ParseTc(v)));
        Add(new Setting("limit", T, SettingKind.Duration, "Count-up: time where the end action applies (0 = no limit).",
            () => TimerEngine.Fmt(e.Limit), v => e.Limit = ParseSpan(v, e.Rate)));
        Add(new Setting("end-action", T, SettingKind.Choice, "What happens at the end (countdown zero, count-up limit, end address).",
            () => Setting.Token(e.EndAction), v => e.EndAction = Setting.ParseEnum<EndAction>(v),
            Opts(("continue", "Keep counting (countdown shows +overrun)"), ("stop", "Stop at the end"), ("pause", "Hold at the end"), ("loop", "Start over and keep running"))));
        Add(new Setting("speed", T, SettingKind.Number, "Play speed (1 = real time; the LTC bit rate follows).",
            () => Setting.Number(e.Speed), v => e.Speed = Setting.ParseNumber(v, 0.01, 100), unit: "×"));
        Add(new Setting("reverse", T, SettingKind.Toggle, "Count backwards (timecode sent as reverse LTC).",
            () => Setting.Bool(e.Reverse), v => e.Reverse = v == "toggle" ? !e.Reverse : Setting.ParseBool(v)));
        Add(new Setting("stop-resets", T, SettingKind.Toggle, "Stop also returns to the start.",
            () => Setting.Bool(e.StopResets), v => e.StopResets = Setting.ParseBool(v)));
        Add(new Setting("display", T, SettingKind.Choice, "Format of the big display.",
            () => Setting.Token(e.DisplayFormat), v => e.DisplayFormat = Setting.ParseEnum<DisplayFormat>(v),
            Opts(("auto", "Timecode for timecode/time of day/chase, compact for count modes"), ("timecode", "HH:MM:SS:FF"), ("clock", "HH:MM:SS"), ("compact", "M:SS (H:MM:SS from one hour)"), ("tenths", "M:SS.t"))));

        Add(new Setting("duration", C, SettingKind.Duration, "Countdown length.",
            () => TimerEngine.Fmt(e.Duration), v => e.Duration = ParseSpan(v, e.Rate)));
        Add(new Setting("warning", C, SettingKind.Duration, "Countdown turns amber at or below this time left.",
            () => TimerEngine.Fmt(e.Warning), v => e.Warning = ParseSpan(v, e.Rate)));
        Add(new Setting("critical", C, SettingKind.Duration, "Countdown turns red at or below this time left.",
            () => TimerEngine.Fmt(e.Critical), v => e.Critical = ParseSpan(v, e.Rate)));

        Add(new Setting("tod-offset", D, SettingKind.Duration, "Time of day: offset added to the clock (e.g. -00:00:02 or +1h).",
            () => TimerEngine.Fmt(e.TimeOfDayOffset), v =>
            {
                var t = TimeInput.Parse(v);
                e.TimeOfDayOffset = TimeSpan.FromSeconds(t.ToSeconds(e.Rate));
            }));
        Add(new Setting("tod-utc", D, SettingKind.Toggle, "Time of day: use UTC instead of local time (use with MJD dates).",
            () => Setting.Bool(e.TimeOfDayUtc), v => e.TimeOfDayUtc = Setting.ParseBool(v)));

        Add(new Setting("freewheel", CH, SettingKind.Number, "Chase: frames to keep running after the incoming code stops.",
            () => e.FreewheelFrames.ToString(CultureInfo.InvariantCulture), v => e.FreewheelFrames = (int)Setting.ParseNumber(v, 0, 10_000), unit: "frames"));
        Add(new Setting("chase-offset", CH, SettingKind.Number, "Chase: frames added to the received address (negative = earlier).",
            () => e.ChaseOffsetFrames.ToString(CultureInfo.InvariantCulture), v => e.ChaseOffsetFrames = long.TryParse(v, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long fr) ? fr : TimeInput.Parse(v).ToFrames(e.Rate), unit: "frames"));
        Add(new Setting("follow-rate", CH, SettingKind.Toggle, "Chase: switch the timer's rate to the incoming code's frame count / drop-frame.",
            () => Setting.Bool(_followRate), v => { _followRate = Setting.ParseBool(v); TuneInput(); }));

        Add(new Setting("output", O, SettingKind.Toggle, "Send LTC on the audio output.",
            () => Setting.Bool(_outputEnabled), v => { _outputEnabled = Setting.ParseBool(v); ApplyOutput(); }));
        Add(new Setting("output-device", O, SettingKind.Text, "Audio output device ('devices' lists them).",
            () => _outputDevice, v => { _outputDevice = v.Length == 0 ? "default" : v; ApplyOutput(); }, dynamicOptions: () => DeviceOptions(true)));
        Add(new Setting("sample-rate", O, SettingKind.Choice, "Audio sample rate for output and input.",
            () => _sampleRate.ToString(CultureInfo.InvariantCulture), v => { _sampleRate = (int)Setting.ParseNumber(v, 8_000, 192_000); ApplyOutput(); ApplyInput(); },
            Opts(("44100", "44.1 kHz"), ("48000", "48 kHz (video standard)"), ("96000", "96 kHz"))));
        Add(new Setting("output-channels", O, SettingKind.Number, "Channels to open on the output device.",
            () => _outChannels.ToString(CultureInfo.InvariantCulture), v => { _outChannels = (int)Setting.ParseNumber(v, 1, 64); ApplyOutput(); }));
        Add(new Setting("output-channel", O, SettingKind.Number, "Channel that carries LTC (1-based; 0 = all channels).",
            () => _outChannel.ToString(CultureInfo.InvariantCulture), v => { _outChannel = (int)Setting.ParseNumber(v, 0, 64); TuneOutput(); }));
        Add(new Setting("level", O, SettingKind.Number, "Output level, dB full scale (−18 … −6 typical).",
            () => Setting.Number(_levelDb), v => { _levelDb = Setting.ParseNumber(v, -60, 0); TuneOutput(); }, unit: "dBFS"));
        Add(new Setting("buffer", O, SettingKind.Number, "Audio buffer size (lower = less latency, higher = safer).",
            () => _bufferMs.ToString(CultureInfo.InvariantCulture), v => { _bufferMs = (int)Setting.ParseNumber(v, 2, 500); ApplyOutput(); ApplyInput(); }, unit: "ms"));
        Add(new Setting("output-offset", O, SettingKind.Number, "Advance the code by this many ms (positive = send later addresses, to make up for downstream delay).",
            () => Setting.Number(_offsetMs), v => { _offsetMs = Setting.ParseNumber(v, -5_000, 5_000); TuneOutput(); }, unit: "ms"));
        Add(new Setting("rise-time", O, SettingKind.Number, "Edge rise/fall time (ST 12-1: 40 ± 10 µs; 0 = hard edges).",
            () => Setting.Number(_riseUs), v => { _riseUs = Setting.ParseNumber(v, 0, 200); TuneOutput(); }, unit: "µs"));
        Add(new Setting("invert", O, SettingKind.Toggle, "Invert the output polarity.",
            () => Setting.Bool(_invert), v => { _invert = Setting.ParseBool(v); TuneOutput(); }));
        Add(new Setting("clock", O, SettingKind.Choice, "Time base of the count.",
            () => Setting.Token(_clockSource), v => { _clockSource = Setting.ParseEnum<ClockSource>(v); UpdateClockChoice(); },
            Opts(("auto", "Audio sample clock while LTC output runs (drift-free), system clock otherwise; time of day and chase use the system clock"),
                 ("system", "Always the system clock"), ("audio", "Audio sample clock whenever output is on"))));
        Add(new Setting("stopped-output", O, SettingKind.Choice, "LTC while stopped.",
            () => Setting.Token(e.StoppedOutput), v => e.StoppedOutput = Setting.ParseEnum<IdleOutput>(v),
            Opts(("silence", "No signal"), ("hold", "Repeat the current frame"))));
        Add(new Setting("paused-output", O, SettingKind.Choice, "LTC while paused.",
            () => Setting.Token(e.PausedOutput), v => e.PausedOutput = Setting.ParseEnum<IdleOutput>(v),
            Opts(("silence", "No signal"), ("hold", "Repeat the current frame"))));
        Add(new Setting("ltc-source", O, SettingKind.Choice, "Count-up / countdown: which count is sent as LTC.",
            () => Setting.Token(e.LtcSource), v => e.LtcSource = Setting.ParseEnum<LtcSource>(v),
            Opts(("elapsed", "Elapsed time (+ ltc-offset), runs forward"), ("remaining", "Time left (+ ltc-offset), runs down as reverse code"), ("time-of-day", "The time of day"))));
        Add(new Setting("ltc-offset", O, SettingKind.Timecode, "Count-up / countdown: address added to the count sent as LTC (e.g. 01:00:00:00).",
            () => e.LtcOffset.ToString(), v => e.LtcOffset = ParseTc(v)));

        Add(new Setting("user-bits-mode", U, SettingKind.Choice, "Content of the 32 user bits (binary groups).",
            () => Setting.Token(e.UserBitsMode), v => e.UserBitsMode = Setting.ParseEnum<UserBitsMode>(v),
            Opts(("none", "All zero"), ("hex", "Eight hex digits (user-bits)"), ("text", "Four characters (user-text), BGF 001"), ("date", "SMPTE ST 309 date + time zone of the computer, BGF 100/110"))));
        Add(new Setting("user-bits", U, SettingKind.Text, "Hex user bits, group 8 first (e.g. 12345678).",
            () => e.UserBits.ToString(), v => e.UserBits = UserBits.Parse(v)));
        Add(new Setting("user-text", U, SettingKind.Text, "Up to four 8-bit characters (e.g. REEL).",
            () => e.UserText, v => e.UserText = v));
        Add(new Setting("date-format", U, SettingKind.Choice, "ST 309 date format.",
            () => e.DateFormat == DateFormat.ModifiedJulianDate ? "mjd" : "yymmdd",
            v => e.DateFormat = v.Trim().ToLowerInvariant() is "mjd" or "modified-julian-date" or "modifiedjuliandate" ? DateFormat.ModifiedJulianDate
                : v.Trim().ToLowerInvariant() is "yymmdd" or "ymd" ? DateFormat.Yymmdd : throw new FormatException($"'{v}' is not yymmdd or mjd."),
            Opts(("yymmdd", "Local date, time address = local time"), ("mjd", "Modified Julian Date, time address = UTC"))));
        Add(new Setting("dst", U, SettingKind.Toggle, "ST 309 daylight-saving flag.",
            () => Setting.Bool(e.DaylightSaving), v => e.DaylightSaving = Setting.ParseBool(v)));
        Add(new Setting("color-frame", U, SettingKind.Toggle, "Set the color frame flag (ST 12-1 §8.3).",
            () => Setting.Bool(e.ColorFrame), v => e.ColorFrame = Setting.ParseBool(v)));

        Add(new Setting("input", I, SettingKind.Toggle, "Read LTC from the audio input (for chase mode).",
            () => Setting.Bool(_inputEnabled), v => { _inputEnabled = Setting.ParseBool(v); ApplyInput(); }));
        Add(new Setting("input-device", I, SettingKind.Text, "Audio input device ('devices' lists them).",
            () => _inputDevice, v => { _inputDevice = v.Length == 0 ? "default" : v; ApplyInput(); }, dynamicOptions: () => DeviceOptions(false)));
        Add(new Setting("input-channel", I, SettingKind.Number, "Input channel carrying LTC (1-based).",
            () => _inChannel.ToString(CultureInfo.InvariantCulture), v => { _inChannel = (int)Setting.ParseNumber(v, 1, 64); ApplyInput(); }));
        Add(new Setting("input-rate", I, SettingKind.Choice, "Rate of the incoming code ('auto' = detect).",
            () => _inputRate?.Token() ?? "auto", v => { _inputRate = v.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : LtcFrameRateExtensions.Parse(v); ApplyInput(); },
            [new SettingOption("auto", "Detect from the signal"), .. Setting.RateOptions]));
        Add(new Setting("input-level", I, SettingKind.Number, "Minimum input level accepted as code.",
            () => Setting.Number(_inputMinDb), v => { _inputMinDb = Setting.ParseNumber(v, -80, 0); TuneInput(); }, unit: "dBFS"));

        Add(new Setting("http", R, SettingKind.Toggle, "HTTP + WebSocket control and the browser remote.",
            () => Setting.Bool(_httpEnabled), v => { _httpEnabled = Setting.ParseBool(v); ApplyHttp(); }));
        Add(new Setting("http-port", R, SettingKind.Number, "TCP port for HTTP / WebSocket.",
            () => _httpPort.ToString(CultureInfo.InvariantCulture), v => { _httpPort = (int)Setting.ParseNumber(v, 1, 65_535); ApplyHttp(); }));
        Add(new Setting("http-bind", R, SettingKind.Choice, "Network interface HTTP listens on: localhost (this computer only), all, or one interface (e.g. the show network).",
            () => _httpBind, v => { _httpBind = NetworkBinding.Normalize(v); ApplyHttp(); }, dynamicOptions: NetworkBinding.Options));
        Add(new Setting("http-token", R, SettingKind.Text, "Access token for HTTP / WebSocket / the browser remote ('new' = random, 'off' = none: anyone who can reach the port has control).",
            () => _httpToken, v => { _httpToken = ParseToken(v); ApplyHttp(); }));
        Add(new Setting("http-origins", R, SettingKind.Text, "Other web pages allowed to call the API, e.g. http://dashboard:3000 (comma-separated; * = any; they still need the token).",
            () => _httpOrigins, v => { _httpOrigins = v; ApplyHttp(); }));
        Add(new Setting("osc", R, SettingKind.Toggle, "OSC control over UDP (address prefix /chronos).",
            () => Setting.Bool(_oscEnabled), v => { _oscEnabled = Setting.ParseBool(v); ApplyOsc(); }));
        Add(new Setting("osc-port", R, SettingKind.Number, "UDP port for OSC.",
            () => _oscPort.ToString(CultureInfo.InvariantCulture), v => { _oscPort = (int)Setting.ParseNumber(v, 1, 65_535); ApplyOsc(); }));
        Add(new Setting("osc-bind", R, SettingKind.Choice, "Network interface OSC listens on: localhost (this computer only), all, or one interface. OSC has no password, so pick the show network.",
            () => _oscBind, v => { _oscBind = NetworkBinding.Normalize(v); ApplyOsc(); }, dynamicOptions: NetworkBinding.Options));
        Add(new Setting("osc-feedback", R, SettingKind.Text, "Send OSC status to these host:port targets (comma-separated; empty = none).",
            () => _oscFeedback, v =>
            {
                OscControlServer.ParseTargets(v); // validate (and resolve) before storing
                _oscFeedback = v;
                _osc?.SetFeedbackTargets(v);
            }));
        Add(new Setting("status-rate", R, SettingKind.Number, "Status pushes per second (WebSocket and OSC feedback).",
            () => Setting.Number(_pushRate), v => _pushRate = Setting.ParseNumber(v, 1, 60), unit: "Hz"));
    }

    // ═════════════════════════════ commands ═════════════════════════════

    private CommandResult Ok(string m) => CommandResult.Success(m);

    private static CommandResult LocalOnly(string cmd) =>
        CommandResult.Error($"{cmd} with a file name only works on this computer; remote control can use '{cmd}' (the settings file).");

    private string Short() { var s = Status; return $"{s.Display}  {Setting.Token(s.State)}"; }

    private void BuildCommands()
    {
        var e = Engine;
        var c = Commands;
        c.Add("play", "play", "Start or resume counting (from an ended count: start over).", (_, _) => { e.Play(); return Ok(Short()); }, "start", "run", "go");
        c.Add("pause", "pause", "Hold the current position.", (_, _) => { e.Pause(); return Ok(Short()); }, "hold");
        c.Add("stop", "stop", "Halt (see stop-resets, stopped-output).", (_, _) => { e.Stop(); return Ok(Short()); });
        c.Add("toggle", "toggle", "Play ↔ pause.", (_, _) => { e.Toggle(); return Ok(Short()); }, "space");
        c.Add("reset", "reset", "Back to the start, keeping play/pause.", (_, _) => { e.Reset(); return Ok(Short()); }, "rewind", "home");
        c.Add("restart", "restart", "Back to the start and play.", (_, _) => { e.Restart(); return Ok(Short()); });
        c.Add("locate", "locate <time>", "Go to an address (timecode), elapsed (count-up), time left (countdown), or set the time-of-day/chase offset so it reads <time>. " + TimeInput.Help + ".",
            (a, raw) => { if (raw.Length == 0) return CommandResult.Error("locate needs a time, e.g. locate 01:00:00:00"); e.Locate(raw); return Ok(Short()); }, "goto", "cue", "loc");
        c.Add("nudge", "nudge <±time|±frames>", "Move by frames (nudge +1, nudge -25) or time (nudge +10s, nudge -1m).",
            (a, raw) =>
            {
                if (raw.Length == 0) return CommandResult.Error("nudge needs an amount, e.g. nudge +1 or nudge -10s");
                string r = raw.Trim();
                if (long.TryParse(r, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long frames)) e.Nudge(frames);
                else { if (r[0] is not ('+' or '-')) r = "+" + r; e.Nudge(TimeInput.Parse(r)); }
                return Ok(Short());
            }, "jog", "trim");
        c.Add("add", "add <±time>", "Countdown: add (or remove, with -) time to the duration; count-up: to the limit.",
            (a, raw) =>
            {
                if (raw.Length == 0) return CommandResult.Error("add needs a time, e.g. add 1m or add -30s");
                string r = raw.Trim(); if (r[0] is not ('+' or '-')) r = "+" + r;
                e.AddTime(TimeSpan.FromSeconds(TimeInput.Parse(r).ToSeconds(e.Rate)));
                return Ok(Short() + "  duration " + TimerEngine.Fmt(e.Mode == TimerMode.CountUp ? e.Limit : e.Duration));
            });
        c.Add("jam", "jam", "Time of day: clear the offset. Timecode: jump to the time of day. Chase: clear the offset. Count modes: reset.", (_, _) => { e.Jam(); return Ok(Short()); }, "sync");
        c.Add("mode", "mode <timecode|count-up|count-down|time-of-day|chase>", "Switch mode (also while running). No argument: next mode.",
            (a, raw) =>
            {
                if (raw.Length == 0)
                {
                    var all = Enum.GetValues<TimerMode>();
                    e.Mode = all[(Array.IndexOf(all, e.Mode) + 1) % all.Length];
                }
                else e.Mode = Setting.ParseEnum<TimerMode>(raw);
                return Ok("mode " + Setting.Token(e.Mode) + "  " + Short());
            });
        c.Add("rate", "rate <fps>", "Frame rate: " + string.Join(", ", LtcFrameRateExtensions.All.Select(r => r.Token())) + ".",
            (a, raw) => { if (raw.Length > 0) e.Rate = LtcFrameRateExtensions.Parse(raw); return Ok($"rate {e.Rate.DisplayName()} — {e.Rate.Description()}"); }, "fps");
        c.Add("speed", "speed <x>", "Play speed, e.g. speed 1, speed 0.5, speed 2.",
            (a, raw) => { if (raw.Length > 0) e.Speed = Setting.ParseNumber(raw, 0.01, 100); return Ok(FormattableString.Invariant($"speed ×{e.Speed:0.###}")); });
        c.Add("reverse", "reverse [on|off|toggle]", "Count backwards. No argument: toggle.",
            (a, raw) => { e.Reverse = raw.Length == 0 || raw.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? !e.Reverse : Setting.ParseBool(raw); return Ok("reverse " + Setting.Bool(e.Reverse)); });
        c.Add("output", "output [on|off|toggle]", "LTC output on/off. No argument: show status.",
            (a, raw) => { if (raw.Length > 0) Set("output", raw.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? Setting.Bool(!_outputEnabled) : raw); return Ok("output " + _outputStatus); });
        c.Add("input", "input [on|off|toggle]", "LTC input on/off. No argument: show status.",
            (a, raw) => { if (raw.Length > 0) Set("input", raw.Equals("toggle", StringComparison.OrdinalIgnoreCase) ? Setting.Bool(!_inputEnabled) : raw); return Ok("input " + _inputStatus); });
        c.Add("set", "set <setting> <value>", "Change a setting ('settings' lists them all).",
            (a, raw) =>
            {
                if (a.Length == 0) return CommandResult.Error("set needs a setting name, e.g. set duration 5m");
                string name = a[0];
                string value = raw.Length > name.Length ? raw[(raw.IndexOf(name, StringComparison.Ordinal) + name.Length)..].Trim() : "";
                if (value.Length > 1 && value[0] == '"' && value[^1] == '"') value = value[1..^1];
                return Set(name, value);
            });
        c.Add("get", "get <setting>", "Show a setting with its description and allowed values.",
            (a, raw) => FindSetting(raw) is { } s ? Ok(Describe(s)) : CommandResult.Error($"No setting '{raw}'. Type 'settings' for the list."));
        c.Add("settings", "settings [category|name]", "List every setting with its value and description.",
            (a, raw) => Ok(SettingsText(raw)), "config", "options");
        c.Add("status", "status", "Show the timer status.", (_, _) => Ok(StatusText()), "st", "?");
        c.Add("devices", "devices", "List audio devices.", (_, _) => Ok(DevicesText()));
        c.Add("save", "save [file]", "Save the settings (default: the settings file; another file only from this computer's UI).",
            (a, raw) => raw.Length > 0 && t_source is not null ? LocalOnly("save") : Ok("saved " + Save(raw.Length > 0 ? raw : null)));
        c.Add("load", "load [file]", "Load settings from a file (another file only from this computer's UI).",
            (a, raw) =>
            {
                if (raw.Length > 0 && t_source is not null) return LocalOnly("load");
                string path = raw.Length > 0 ? raw : SettingsPath ?? SettingsFile.DefaultPath();
                var p = Load(path);
                Start();
                return p.Count == 0 ? Ok("loaded " + path) : CommandResult.Error("loaded " + path + " with problems: " + string.Join("; ", p));
            });
        c.Add("defaults", "defaults", "Restore every setting to its default.", (_, _) => { Defaults(); Start(); return Ok("defaults restored"); });
        c.Add("remote", "remote", "Show the HTTP / WebSocket / OSC addresses.", (_, _) => Ok(RemoteText()), "urls");
        c.Add("log", "log", "Show recent log lines.", (_, _) => Ok(string.Join('\n', RecentLog.TakeLast(30))));
        c.Add("help", "help [command]", "List commands, or explain one.",
            (a, raw) =>
            {
                if (raw.Length > 0)
                {
                    if (Commands.Find(raw) is { } ci) return Ok($"{ci.Usage}\n  {ci.Description}" + (ci.Aliases.Count > 0 ? $"\n  also: {string.Join(", ", ci.Aliases)}" : ""));
                    if (FindSetting(raw) is { } s) return Ok(Describe(s));
                    return CommandResult.Error($"No command or setting '{raw}'.");
                }
                return Ok("Commands:\n" + Commands.Help() + "\nAny setting can also be changed with '<setting> <value>' (e.g. 'duration 5m'); 'settings' lists them.\nTimes: " + TimeInput.Help + ".");
            });

        // "<setting> [value]" works without "set"
        c.Fallback = (word, rest) => FindSetting(word) is { } s ? (rest.Length == 0 ? Ok(Describe(s)) : Set(s.Name, rest)) : null;
    }

    /// <summary>Sets a setting by name from text.</summary>
    public CommandResult Set(string name, string value)
    {
        if (FindSetting(name) is not { } s) return CommandResult.Error($"No setting '{name}'. Type 'settings' for the list.");
        s.Set(value);
        return Ok($"{s.Name} = {s.Value}");
    }

    public static string Describe(Setting s)
    {
        var sb = new StringBuilder();
        sb.Append(s.Name).Append(" = ").Append(s.Value).Append("   (default ").Append(s.Default).AppendLine(")");
        sb.Append("  ").AppendLine(s.Description);
        sb.Append("  accepts: ").Append(s.Accepts);
        if (s.Kind == SettingKind.Choice || (s.Kind == SettingKind.Text && s.Options.Count > 0))
            foreach (var o in s.Options) sb.AppendLine().Append("    ").Append(o.Value.PadRight(14)).Append(o.Description);
        return sb.ToString();
    }

    public string SettingsText(string filter = "")
    {
        filter = filter.Trim();
        if (filter.Length > 0 && FindSetting(filter) is { } one) return Describe(one);
        var sb = new StringBuilder();
        int w = _settings.Max(s => s.Name.Length);
        int vw = Math.Min(24, _settings.Max(s => s.Value.Length));
        foreach (var g in _settings.GroupBy(s => s.Category))
        {
            if (filter.Length > 0 && !g.Key.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            sb.AppendLine(g.Key);
            foreach (var s in g)
                sb.Append("  ").Append(s.Name.PadRight(w + 2)).Append(s.Value.PadRight(vw + 2)).AppendLine(s.Description);
        }
        return sb.ToString().TrimEnd();
    }

    public string StatusText()
    {
        var s = Status;
        var sb = new StringBuilder();
        sb.AppendLine($"{s.Display}   {s.Detail}");
        sb.AppendLine($"mode {Setting.Token(s.Mode)} · {Setting.Token(s.State)} · {s.Rate.DisplayName()} fps · timecode {s.Timecode} · phase {Setting.Token(s.Phase)}"
            + FormattableString.Invariant($" · speed ×{s.Speed:0.###}") + (s.Reverse ? " · reverse" : ""));
        if (s.Remaining is { } r) sb.AppendLine("remaining " + TimerEngine.Fmt(r) + (s.Length is { } l ? " of " + TimerEngine.Fmt(l) : ""));
        sb.AppendLine("LTC out: " + _outputStatus + (s.OutputActive ? " · sending" + (LastSent is { } ls ? " " + ls : "") : ""));
        sb.Append("LTC in:  " + _inputStatus + (_ltcIn is not null ? FormattableString.Invariant($" · level {20 * Math.Log10(Math.Max(1e-6, InputLevel)):0} dBFS") + (InputDetectedRate is { } dr ? " · " + dr.DisplayName() : "") + (s.ChaseInput is { } ci ? " · last " + ci : "") : ""));
        return sb.ToString();
    }

    public string DevicesText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Audio backend: {Audio.Name}");
        sb.AppendLine("Outputs:");
        try { foreach (var d in Audio.GetOutputDevices()) sb.AppendLine("  " + d); } catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("  (" + ex.Message + ")"); }
        sb.AppendLine("Inputs:");
        try { foreach (var d in Audio.GetInputDevices()) sb.AppendLine("  " + d); } catch (Exception ex) when (ex is not OutOfMemoryException) { sb.AppendLine("  (" + ex.Message + ")"); }
        sb.Append("Use: set output-device <id>, set input-device <id>");
        return sb.ToString();
    }

    public string RemoteText()
    {
        var sb = new StringBuilder();
        if (_http is { } h)
        {
            sb.AppendLine("Browser remote: " + string.Join("  ", h.Urls));
            sb.AppendLine("HTTP API:   GET /api/status · GET /api/settings · GET /api/commands · GET|POST /api/command?c=<command> · GET /api/<command>/<args…>");
            sb.AppendLine("WebSocket:  ws://<host>:" + h.Port + "/ws  (status JSON pushed; send text commands)");
        }
        else sb.AppendLine("HTTP: off");
        if (_osc is { } o)
            sb.Append($"OSC (UDP {o.Port}): /chronos/<command> [args] · /chronos/set/<setting> <value> · /chronos/cmd \"<text>\" · /chronos/status (reply)");
        else sb.Append("OSC: off");
        return sb.ToString();
    }
}

/// <summary>Where settings live. Portable: a <c>chronos-timer.json</c> next to the program wins.</summary>
public static class SettingsFile
{
    public const string FileName = "chronos-timer.json";

    /// <summary>Next to the executable when such a file exists (portable use, e.g. from a USB stick); otherwise the user's app-data folder.</summary>
    public static string DefaultPath()
    {
        string portable = Path.Combine(AppContext.BaseDirectory, FileName);
        if (File.Exists(portable)) return portable;
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create);
        if (string.IsNullOrEmpty(dir)) dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "ChronosTimer", FileName);
    }

    /// <summary>The portable location (next to the program).</summary>
    public static string PortablePath => Path.Combine(AppContext.BaseDirectory, FileName);
}
