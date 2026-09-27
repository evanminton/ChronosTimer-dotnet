using LinearTimecode;
using ChronosTimer.Settings;

namespace ChronosTimer.App.Pages;

/// <summary>The big display with transport, nudge, modes, locate and quick output/input switches.</summary>
public sealed class TimerPage : ContentPage
{
    private readonly Label _display, _detail, _chips, _ltc, _remote, _message;
    private readonly ProgressBar _progress;
    private readonly Button _playPause;
    private readonly Dictionary<TimerMode, Button> _modeButtons = new();
    private readonly Entry _locate, _duration;
    private readonly Switch _output, _input;
    private IDispatcherTimer? _timer;
    private bool _updatingSwitches;
    private int _lastLen;

    private static TimerHost Host => AppHost.Host;

    public TimerPage()
    {
        Title = "Chronos Timer";
        BackgroundColor = Ui.Background;

        _chips = new Label { FontSize = 13, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center };
        _display = new Label
        {
            FontFamily = Ui.Mono,
            FontAttributes = FontAttributes.Bold,
            FontSize = 96,
            TextColor = Ui.Text,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
            Margin = new Thickness(0, 8, 0, 0),
        };
        _detail = new Label { FontSize = 16, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center };
        _progress = new ProgressBar { ProgressColor = Ui.Accent, BackgroundColor = Ui.Panel, HeightRequest = 6, Margin = new Thickness(0, 10, 0, 14) };

        _playPause = Ui.Button("▶  Play", () => Run("toggle"), primary: true);
        var transport = Ui.Row(_playPause, Ui.Button("■  Stop", () => Run("stop")), Ui.Button("⟲  Reset", () => Run("reset")), Ui.Button("↻  Restart", () => Run("restart")));
        var nudge = Ui.Row(
            Ui.Button("−1 s", () => Run("nudge -1s")), Ui.Button("−1 fr", () => Run("nudge -1")), Ui.Button("+1 fr", () => Run("nudge +1")), Ui.Button("+1 s", () => Run("nudge +1s")),
            Ui.Button("−1 min", () => Run("add -1m")), Ui.Button("+1 min", () => Run("add +1m")), Ui.Button("Jam", () => Run("jam")), Ui.Button("⇄ Reverse", () => Run("reverse")));

        var modes = Ui.Row();
        foreach (var (mode, label) in new[] { (TimerMode.Timecode, "Timecode"), (TimerMode.CountUp, "Count up"), (TimerMode.CountDown, "Countdown"), (TimerMode.TimeOfDay, "Time of day"), (TimerMode.Chase, "Chase") })
        {
            var b = Ui.Button(label, () => Run("mode " + Setting.Token(mode)));
            _modeButtons[mode] = b;
            modes.Children.Add(b);
        }

        _locate = Ui.Entry("Locate: 01:00:00:00 · 5:00 · 90s");
        _locate.Completed += (_, _) => { if (!string.IsNullOrWhiteSpace(_locate.Text)) Run("locate " + _locate.Text); };
        _duration = Ui.Entry("Countdown: 10:00 · 45m");
        _duration.Completed += (_, _) => { if (!string.IsNullOrWhiteSpace(_duration.Text)) Run("set duration " + _duration.Text); };
        var entries = new Grid
        {
            ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) },
            ColumnSpacing = 8,
        };
        entries.Add(_locate, 0, 0);
        entries.Add(Ui.Button("Locate", () => { if (!string.IsNullOrWhiteSpace(_locate.Text)) Run("locate " + _locate.Text); }), 1, 0);
        entries.Add(_duration, 2, 0);
        entries.Add(Ui.Button("Set", () => { if (!string.IsNullOrWhiteSpace(_duration.Text)) Run("set duration " + _duration.Text); }), 3, 0);

        _output = new Switch { OnColor = Ui.Accent };
        _output.Toggled += (_, e) => { if (!_updatingSwitches) Run("output " + (e.Value ? "on" : "off")); };
        _input = new Switch { OnColor = Ui.Accent };
        _input.Toggled += async (_, e) =>
        {
            if (_updatingSwitches) return;
            if (e.Value && !await AppHost.EnsureMicrophoneAsync()) { _message!.Text = "Microphone access is needed to read LTC."; Refresh(); return; }
            Run("input " + (e.Value ? "on" : "off"));
        };
        var switches = new HorizontalStackLayout
        {
            Spacing = 10,
            Children =
            {
                new Label { Text = "LTC out", TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center }, _output,
                new Label { Text = "LTC in", TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center, Margin = new Thickness(16, 0, 0, 0) }, _input,
            },
        };

        _ltc = Ui.Caption("");
        _remote = Ui.Caption("");
        _message = new Label { FontSize = 13, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.WordWrap };

        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8, 16, 32),
            Spacing = 4,
            Children =
            {
                _chips, _display, _detail, _progress, transport, nudge,
                Ui.Heading("Mode"), modes,
                Ui.Heading("Go to"), entries,
                Ui.Heading("Signal"), switches, _ltc, _remote, _message,
            },
        };
        Content = new ScrollView { Content = stack };
        SizeChanged += (_, _) => FitDisplay(force: true);

        AddMenus();
    }

    private void Run(string command)
    {
        var r = Host.Execute(command);
        _message.Text = r.Ok ? r.Message.Split('\n')[0] : "⚠ " + r.Message;
        _message.TextColor = r.Ok ? Ui.Muted : Ui.Critical;
        Refresh();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.Current.KeepScreenOn = true;
        _timer ??= Dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(40);
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _timer?.Stop();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var s = Host.Status;
        _display.Text = s.Display;
        _display.TextColor = s.Phase == TimerPhase.Overrun && DateTime.Now.Millisecond >= 500 ? Ui.Critical.WithAlpha(0.45f) : Ui.PhaseColor(s.Phase, s.State == TransportState.Running);
        FitDisplay(force: false);
        _detail.Text = s.Detail;
        _chips.Text = $"{Setting.Token(s.Mode).ToUpperInvariant()}  ·  {Setting.Token(s.State).ToUpperInvariant()}  ·  {s.Rate.DisplayName()} fps"
                      + FormattableString.Invariant($"{(s.Speed != 1 ? $"  ·  ×{s.Speed:0.###}" : "")}{(s.Reverse ? "  ·  REVERSE" : "")}");
        _progress.IsVisible = s.Progress is not null;
        _progress.Progress = s.Progress ?? 0;
        _playPause.Text = s.State == TransportState.Running ? "❚❚  Pause" : "▶  Play";
        foreach (var (mode, b) in _modeButtons)
        {
            bool sel = mode == s.Mode;
            b.BorderColor = sel ? Ui.Accent : Ui.Line;
            b.TextColor = sel ? Ui.Accent : Ui.Text;
        }
        _ltc.Text = $"Out: {Host.OutputStatus}{(s.OutputActive ? "  ▶ " + Host.LastSent : "")}\nIn: {Host.InputStatus}"
                    + (s.Mode == TimerMode.Chase ? $"  ·  {Setting.Token(s.Chase)}{(s.ChaseInput is { } ci ? " " + ci : "")}" : "");
        _remote.Text = Host.Http is { } h ? "Remote: " + string.Join("  ", h.Urls.Take(3)) + (Host.Osc is { } o ? $"   ·   OSC UDP {o.Port}" : "") : "Remote control off (Settings › Remote control)";

        _updatingSwitches = true;
        _output.IsToggled = Host.FindSetting("output")?.Value == "on";
        _input.IsToggled = Host.FindSetting("input")?.Value == "on";
        _updatingSwitches = false;
    }

    private void FitDisplay(bool force)
    {
        int len = Math.Max(4, _display.Text?.Length ?? 11);
        if (!force && len == _lastLen) return;
        _lastLen = len;
        double w = Width > 0 ? Width - 32 : 800;
        double size = Math.Clamp(w / (len * 0.62), 28, 260);
        if (Height > 0) size = Math.Min(size, Height * 0.34);
        _display.FontSize = size;
    }

    private void AddMenus()
    {
        if (DeviceInfo.Idiom != DeviceIdiom.Desktop) return;
        MenuFlyoutItem Item(string text, string command, string key, KeyboardAcceleratorModifiers mods = KeyboardAcceleratorModifiers.Ctrl)
        {
            var i = new MenuFlyoutItem { Text = text };
            i.Clicked += (_, _) => Run(command);
            i.KeyboardAccelerators.Add(new KeyboardAccelerator { Modifiers = mods, Key = key });
            return i;
        }
        var transport = new MenuBarItem { Text = "Transport" };
        transport.Add(Item("Play / Pause", "toggle", "P"));
        transport.Add(Item("Stop", "stop", "S", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        transport.Add(Item("Reset", "reset", "R"));
        transport.Add(Item("Restart", "restart", "R", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        transport.Add(Item("Nudge −1 frame", "nudge -1", "J"));
        transport.Add(Item("Nudge +1 frame", "nudge +1", "K"));
        transport.Add(Item("Nudge −1 second", "nudge -1s", "J", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        transport.Add(Item("Nudge +1 second", "nudge +1s", "K", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        transport.Add(Item("Add 1 minute", "add +1m", "U"));
        transport.Add(Item("Remove 1 minute", "add -1m", "U", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        transport.Add(Item("Reverse", "reverse", "B"));
        transport.Add(Item("Jam", "jam", "G"));
        var mode = new MenuBarItem { Text = "Mode" };
        mode.Add(Item("Timecode", "mode timecode", "F2", KeyboardAcceleratorModifiers.None));
        mode.Add(Item("Count up", "mode count-up", "F3", KeyboardAcceleratorModifiers.None));
        mode.Add(Item("Countdown", "mode count-down", "F4", KeyboardAcceleratorModifiers.None));
        mode.Add(Item("Time of day", "mode time-of-day", "F5", KeyboardAcceleratorModifiers.None));
        mode.Add(Item("Chase", "mode chase", "F6", KeyboardAcceleratorModifiers.None));
        var signal = new MenuBarItem { Text = "Signal" };
        signal.Add(Item("LTC output on/off", "output toggle", "O", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        signal.Add(Item("LTC input on/off", "input toggle", "I", KeyboardAcceleratorModifiers.Ctrl | KeyboardAcceleratorModifiers.Shift));
        MenuBarItems.Add(transport);
        MenuBarItems.Add(mode);
        MenuBarItems.Add(signal);
    }
}
