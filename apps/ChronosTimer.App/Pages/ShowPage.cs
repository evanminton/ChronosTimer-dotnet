using ChronosTimer.Link;
using ChronosTimer.Settings;
using ChronosTimer.Show;

namespace ChronosTimer.App.Pages;

/// <summary>
/// The show display: the master's running time with the cue tiles (the lit tile is the cue light) and messages, plus
/// the show schedule (start / end pickers and the hold button) and the link between timers. Wide landscape windows put
/// the time across the top with the cue tiles bottom left and messages bottom right; everything else stacks them.
/// </summary>
public sealed class ShowPage : ContentPage
{
    private readonly Label _source, _display, _detail, _latestText, _acks, _scheduleStatus, _linkStatus, _message;
    private readonly ProgressBar _progress;
    private readonly Border _latest;
    private readonly Grid _cueGrid, _timeCell;
    private readonly Button _ack, _hold;
    private readonly VerticalStackLayout _messages, _scheduleSection;
    private readonly Entry _messageEntry, _preroll;
    private readonly Picker _recipient;
    private readonly DatePicker _startDate, _endDate;
    private readonly TimePicker _startTime, _endTime;
    private readonly Switch _useEnd;
    private readonly Dictionary<LinkRole, Button> _roleButtons = new();
    private IDispatcherTimer? _timer;
    private int _shownMessages = -1;
    private string _recipients = "";
    private int _lastLen;
    private readonly Dictionary<CueLight, CueTile> _tiles = new();
    private readonly VerticalStackLayout _timeBox, _cuePanel, _messagesPanel;
    private readonly ContentView _top = new();
    private string _layoutKey = "";
    private bool _sideBySide;

    private static TimerHost Host => AppHost.Host;
    private static ShowController Show => AppHost.Host.Show;

    public ShowPage()
    {
        Title = "Show";
        BackgroundColor = Ui.Background;

        // ── master time ──
        _source = new Label { FontSize = 13, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center };
        _display = new Label
        {
            FontFamily = Ui.Mono,
            FontAttributes = FontAttributes.Bold,
            FontSize = 96,
            TextColor = Ui.Text,
            HorizontalTextAlignment = TextAlignment.Center,
            LineBreakMode = LineBreakMode.NoWrap,
        };
        _detail = new Label { FontSize = 16, TextColor = Ui.Muted, HorizontalTextAlignment = TextAlignment.Center };
        _progress = new ProgressBar { ProgressColor = Ui.Accent, BackgroundColor = Ui.Panel, HeightRequest = 6, Margin = new Thickness(0, 10, 0, 6) };

        _timeBox = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { _source, _display, _detail, _progress } };
        _timeCell = new Grid { Children = { _timeBox } };

        // ── cue tiles: one per cue, the active one lit as the cue light ──
        _cueGrid = new Grid { ColumnSpacing = 10, RowSpacing = 10, Margin = new Thickness(0, 6, 0, 2) };
        foreach (var cue in Enum.GetValues<CueLight>())
        {
            var tile = new CueTile(cue);
            var tap = new TapGestureRecognizer();
            tap.Tapped += (_, _) => { if (Show.Role != LinkRole.Follower) Run("cue " + Setting.Token(cue)); };
            tile.GestureRecognizers.Add(tap);
            _tiles[cue] = tile;
        }
        _ack = Ui.Button("✓  Acknowledge cue", () => Run("cue ack"), primary: true);
        _acks = Ui.Caption("");
        _acks.HorizontalTextAlignment = TextAlignment.Center;
        _cuePanel = new VerticalStackLayout { Spacing = 4, Children = { _cueGrid, _ack, _acks } };

        // ── messages ──
        _messages = new VerticalStackLayout { Spacing = 2 };
        _messageEntry = Ui.Entry("Message to linked timers (@ChronosTimer02 … for one)");
        _messageEntry.FontFamily = null;
        _messageEntry.Completed += (_, _) => SendMessage();
        _recipient = new Picker { TextColor = Ui.Text, BackgroundColor = Ui.Panel, MinimumWidthRequest = 150, Title = "To" };
        var msgRow = new Grid { ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        msgRow.Add(_recipient, 0, 0);
        msgRow.Add(_messageEntry, 1, 0);
        msgRow.Add(Ui.Button("Send", SendMessage, primary: true), 2, 0);
        var quick = Ui.Row();
        foreach (string q in new[] { "Standby", "Places please", "5 minutes", "Holding", "Go when ready", "Copy that" })
            quick.Children.Add(Ui.Button(q, () => Run("message " + Target() + q)));
        _messagesPanel = new VerticalStackLayout { Children = { Ui.Heading("Messages"), Ui.Card(_messages), msgRow, quick } };
        _latestText = new Label { FontSize = 18, FontAttributes = FontAttributes.Bold, TextColor = Ui.Text, LineBreakMode = LineBreakMode.WordWrap };
        _latest = new Border
        {
            Content = _latestText,
            BackgroundColor = Ui.Panel,
            Stroke = Ui.Accent,
            StrokeThickness = 2,
            Padding = new Thickness(14, 10),
            Margin = new Thickness(0, 6),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
            IsVisible = false,
        };

        // ── schedule ──
        var today = DateTime.Today;
        _startDate = new DatePicker { Date = today, TextColor = Ui.Text, BackgroundColor = Ui.Panel, Format = "ddd d MMM yyyy" };
        _startTime = new TimePicker { Time = new TimeSpan(DateTime.Now.Hour + 1 > 23 ? 23 : DateTime.Now.Hour + 1, 0, 0), TextColor = Ui.Text, BackgroundColor = Ui.Panel, Format = "HH:mm" };
        _endDate = new DatePicker { Date = today, TextColor = Ui.Text, BackgroundColor = Ui.Panel, Format = "ddd d MMM yyyy" };
        _endTime = new TimePicker { Time = new TimeSpan(Math.Min(23, DateTime.Now.Hour + 2), 0, 0), TextColor = Ui.Text, BackgroundColor = Ui.Panel, Format = "HH:mm" };
        _useEnd = new Switch { OnColor = Ui.Accent, IsToggled = true };
        _useEnd.Toggled += (_, e) => { _endDate.IsEnabled = e.Value; _endTime.IsEnabled = e.Value; };
        var pickers = new Grid
        {
            ColumnDefinitions = { new(GridLength.Auto), new(GridLength.Star), new(GridLength.Star) },
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 8,
            RowSpacing = 8,
        };
        pickers.Add(new Label { Text = "Start", TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center, WidthRequest = 70 }, 0, 0);
        pickers.Add(_startDate, 1, 0);
        pickers.Add(_startTime, 2, 0);
        pickers.Add(new HorizontalStackLayout { Spacing = 4, Children = { _useEnd, new Label { Text = "End", TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center } } }, 0, 1);
        pickers.Add(_endDate, 1, 1);
        pickers.Add(_endTime, 2, 1);
        _preroll = Ui.Entry("5");
        _preroll.Completed += (_, _) => ApplyPreroll();
        _preroll.Unfocused += (_, _) => ApplyPreroll();
        pickers.Add(new Label { Text = "Preroll", TextColor = Ui.Text, VerticalOptions = LayoutOptions.Center }, 0, 2);
        pickers.Add(_preroll, 1, 2);
        pickers.Add(new Label { Text = "seconds (or frames: 12f)", TextColor = Ui.Muted, FontSize = 13, VerticalOptions = LayoutOptions.Center }, 2, 2);

        _hold = Ui.Button("⏸  HOLD START", () => Run("show toggle"));
        _hold.FontAttributes = FontAttributes.Bold;
        _hold.MinimumHeightRequest = 56;
        _scheduleStatus = Ui.Caption("");
        _scheduleSection = new VerticalStackLayout
        {
            Spacing = 4,
            Children =
            {
                Ui.Heading("Show schedule"),
                Ui.Card(pickers),
                Ui.Row(Ui.Button("Schedule show", Schedule, primary: true), Ui.Button("Start now", () => Run("show start now")), Ui.Button("Clear", () => Run("show clear"))),
                _hold,
                Ui.Caption("Preroll starts the timer and its LTC this long before the start, so receivers are locked when the show starts."),
                Ui.Caption("Hold keeps the timer from starting until you release it (late artist). The preroll waits too and runs in full after the release. The end time moves by the same amount, so the show keeps its full length. Hold while running pauses the show and moves the end."),
                _scheduleStatus,
            },
        };

        // ── link ──
        var roles = Ui.Row();
        foreach (var (role, label) in new[] { (LinkRole.Off, "Not linked"), (LinkRole.Master, "Master"), (LinkRole.Follower, "Follower") })
        {
            var b = Ui.Button(label, () => Run("link-role " + Setting.Token(role)));
            _roleButtons[role] = b;
            roles.Children.Add(b);
        }
        _linkStatus = Ui.Caption("");
        _message = new Label { FontSize = 13, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.WordWrap };

        var stack = new VerticalStackLayout
        {
            Padding = new Thickness(16, 8, 16, 32),
            Spacing = 4,
            Children =
            {
                _top,
                _scheduleSection,
                Ui.Heading("Link"), roles, _linkStatus,
                Ui.Caption("Timers on the same network find each other: set one to Master and the others to Follower. By default they use the 169.254.x.x (APIPA) network of a direct cable or switch without DHCP. More in Settings › Link."),
                _message,
            },
        };
        Content = new ScrollView { Content = stack };
        SizeChanged += (_, _) => { ApplyLayout(); FitDisplay(force: true); };
        ApplyLayout();
    }

    private static Color CueColor(CueLight cue, bool lit) => cue switch
    {
        CueLight.Standby => lit ? Ui.Warning : Color.FromArgb("#4A3A12"),
        CueLight.Go => lit ? Color.FromArgb("#2ECC71") : Color.FromArgb("#12402A"),
        CueLight.Warning => lit ? Color.FromArgb("#FF8C1A") : Color.FromArgb("#4A2A0E"),
        CueLight.End => lit ? Ui.Critical : Color.FromArgb("#4A1616"),
        CueLight.Stop => lit ? Ui.Critical : Color.FromArgb("#4A1616"),
        _ => Ui.Line,
    };

    private string Target() => _recipient.SelectedIndex > 0 && _recipient.SelectedItem is string s ? "@" + s + " " : "";

    private void SendMessage()
    {
        string text = (_messageEntry.Text ?? "").Trim();
        if (text.Length == 0) return;
        if (Run("message " + (text.StartsWith('@') ? "" : Target()) + text)) _messageEntry.Text = "";
    }

    private bool ApplyPreroll()
    {
        string text = (_preroll.Text ?? "").Trim();
        if (text.Length == 0 || text == PrerollText()) return true;
        if (!Run("show preroll " + text)) return false;
        _preroll.Text = PrerollText();
        return true;
    }

    private static string PrerollText() => Show.Preroll.TotalSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private void Schedule()
    {
        if (!ApplyPreroll()) return;
        var start = (_startDate.Date ?? DateTime.Today).Date + (_startTime.Time ?? TimeSpan.Zero);
        var cmd = "show start " + ShowTime.Format(new DateTimeOffset(start, TimeZoneInfo.Local.GetUtcOffset(start)));
        if (!Run("show clear") || !Run(cmd)) return;
        if (_useEnd.IsToggled)
        {
            var end = (_endDate.Date ?? DateTime.Today).Date + (_endTime.Time ?? TimeSpan.Zero);
            if (end <= start) end = end.AddDays(Math.Ceiling((start - end).TotalDays + 1e-9));
            Run("show end " + ShowTime.Format(new DateTimeOffset(end, TimeZoneInfo.Local.GetUtcOffset(end))));
        }
    }

    private bool Run(string command)
    {
        var r = Host.Execute(command);
        _message.Text = r.Ok ? r.Message.Split('\n')[0] : "⚠ " + r.Message;
        _message.TextColor = r.Ok ? Ui.Muted : Ui.Critical;
        Refresh();
        return r.Ok;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        DeviceDisplay.Current.KeepScreenOn = true;
        if (Show.ScheduledStart is { } s)
        {
            var l = s.ToLocalTime();
            _startDate.Date = l.Date;
            _startTime.Time = l.TimeOfDay;
        }
        if (Show.ScheduledEnd is { } e)
        {
            var l = e.ToLocalTime();
            _endDate.Date = l.Date;
            _endTime.Time = l.TimeOfDay;
        }
        _preroll.Text = PrerollText();
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
        var v = Show.View;
        var role = Show.Role;
        bool flash = DateTime.Now.Millisecond < 500;

        _source.Text = role == LinkRole.Follower
            ? (v.Live ? $"MASTER {v.Source}  ·  THIS TIMER {Show.NodeName}" : "NO MASTER")
            : $"{v.Source}  ·  {Setting.Token(v.State).ToUpperInvariant()}" + (v.ShowPhase != ShowPhase.Idle ? "  ·  SHOW " + Setting.Token(v.ShowPhase).ToUpperInvariant() : "");
        _display.Text = v.Display;
        _display.TextColor = !v.Live ? Ui.Ended
            : v.Phase == TimerPhase.Overrun && !flash ? Ui.Critical.WithAlpha(0.45f)
            : Ui.PhaseColor(v.Phase, v.State == TransportState.Running);
        FitDisplay(force: false);
        _detail.Text = v.Detail;
        _progress.IsVisible = v.Progress is not null;
        _progress.Progress = v.Progress ?? 0;

        bool blink = v.Cue is CueLight.Standby or CueLight.End;
        bool lit = v.Cue != CueLight.Off && (!blink || flash);
        foreach (var (cue, tile) in _tiles)
            tile.Update(active: cue == v.Cue, lit, blink, dimmed: role == LinkRole.Follower && cue != v.Cue);

        _ack.IsVisible = role == LinkRole.Follower;
        _scheduleSection.IsVisible = role != LinkRole.Follower;
        var followers = Show.Followers;
        _acks.Text = role == LinkRole.Master && followers.Count > 0
            ? "Linked: " + string.Join("   ", followers.Select(f => f.Name + (f.Acknowledged ? " ✓" : "")))
            : "";
        _acks.IsVisible = _acks.Text.Length > 0;

        var st = Show.State;
        _hold.Text = Show.Holding ? "▶  RELEASE HOLD" : st.Started ? "⏸  HOLD SHOW" : "⏸  HOLD START";
        _hold.BackgroundColor = Show.Holding ? Ui.Warning : Ui.Panel;
        _hold.TextColor = Show.Holding ? Color.FromArgb("#0B1416") : Ui.Text;
        _hold.IsEnabled = st.Phase is not (ShowPhase.Idle or ShowPhase.Over);
        _scheduleStatus.Text = st.Phase == ShowPhase.Idle ? "No show scheduled." : Show.StatusText().Split('\n')[1];

        foreach (var (r, b) in _roleButtons)
        {
            b.BorderColor = r == role ? Ui.Accent : Ui.Line;
            b.TextColor = r == role ? Ui.Accent : Ui.Text;
        }
        _linkStatus.Text = $"This timer: {Show.NodeName}   ·   {Show.LinkStatus}";

        string recipients = string.Join("|", followers.Select(f => f.Name));
        if (recipients != _recipients)
        {
            _recipients = recipients;
            string? sel = _recipient.SelectedItem as string;
            _recipient.ItemsSource = new List<string> { "Everyone" }.Concat(followers.Select(f => f.Name)).ToList();
            int i = sel is null ? 0 : Math.Max(0, _recipient.ItemsSource.IndexOf(sel));
            _recipient.SelectedIndex = i;
        }
        _recipient.IsVisible = role == LinkRole.Master;
        _latest.IsVisible = !_sideBySide && !string.IsNullOrEmpty(_latestText.Text);

        var msgs = Show.Messages;
        if (msgs.Count != _shownMessages)
        {
            _shownMessages = msgs.Count;
            _messages.Children.Clear();
            if (msgs.Count == 0) _messages.Children.Add(Ui.Caption(role == LinkRole.Off ? "Link timers to send messages." : "No messages yet."));
            var last = msgs.Count > 0 ? msgs[^1] : null;
            _latestText.Text = last?.ToString() ?? "";
            _latest.Stroke = last?.To is not null ? Ui.Warning : Ui.Accent;
            foreach (var m in msgs.TakeLast(30).Reverse())
            {
                bool mine = m.From == Show.NodeName;
                _messages.Children.Add(new Label
                {
                    Text = m.ToString(),
                    FontSize = 15,
                    TextColor = mine ? Ui.Muted : Ui.Text,
                    FontAttributes = m.To is not null && !mine ? FontAttributes.Bold : FontAttributes.None,
                    LineBreakMode = LineBreakMode.WordWrap,
                });
            }
        }
    }

    private void FitDisplay(bool force)
    {
        int len = Math.Max(4, _display.Text?.Length ?? 11);
        if (!force && len == _lastLen) return;
        _lastLen = len;
        double w = Width > 0 ? Width - 32 : 800;
        double size = Math.Clamp(w / (len * 0.62), 28, 320);
        if (Height > 0) size = Math.Min(size, Height * (_sideBySide ? 0.34 : 0.28));
        _display.FontSize = size;
    }

    /// <summary>
    /// Wide landscape: time across the top, cue tiles bottom left, messages bottom right. Otherwise the time, the cue
    /// tiles (six across, or three or two on narrow screens), the newest message and the message list stack.
    /// </summary>
    private void ApplyLayout()
    {
        bool side = Width >= 900 && Width > Height * 1.25;
        double w = Width > 0 ? Width - 32 : 800;
        int cols = side ? 3 : w >= 640 ? 6 : w >= 360 ? 3 : 2;
        double h = Height > 0 ? Height : 700;
        double tileHeight = side ? Math.Clamp(h * 0.12, 56, 120) : Math.Clamp(h * 0.15, 64, 150);
        double colWidth = ((side ? w / 2 - 12 : w) - 10 * (cols - 1)) / cols;
        double font = Math.Clamp(Math.Min(tileHeight * 0.22, colWidth * 0.11), 12, 32);
        foreach (var tile in _tiles.Values) tile.Size(tileHeight, font);
        _timeCell.MinimumHeightRequest = side ? h * 0.5 : 0;

        string key = side + "/" + cols;
        if (key == _layoutKey) return;
        _layoutKey = key;
        _sideBySide = side;

        _cueGrid.Clear();
        _cueGrid.ColumnDefinitions.Clear();
        _cueGrid.RowDefinitions.Clear();
        for (int c = 0; c < cols; c++) _cueGrid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        int i = 0;
        foreach (var tile in _tiles.Values)
        {
            if (i % cols == 0) _cueGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            _cueGrid.Add(tile, i % cols, i / cols);
            i++;
        }

        foreach (var v in new View[] { _timeCell, _cuePanel, _latest, _messagesPanel })
            (v.Parent as Layout)?.Remove(v);
        _top.Content = null;
        _acks.HorizontalTextAlignment = side ? TextAlignment.Start : TextAlignment.Center;
        _cuePanel.VerticalOptions = _messagesPanel.VerticalOptions = side ? LayoutOptions.End : LayoutOptions.Start;
        if (side)
        {
            var grid = new Grid
            {
                ColumnDefinitions = { new(GridLength.Star), new(GridLength.Star) },
                RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto) },
                ColumnSpacing = 24,
            };
            grid.Add(_timeCell, 0, 0);
            grid.SetColumnSpan(_timeCell, 2);
            grid.Add(_cuePanel, 0, 1);
            grid.Add(_messagesPanel, 1, 1);
            _top.Content = grid;
        }
        else
        {
            _top.Content = new VerticalStackLayout { Children = { _timeCell, _cuePanel, _latest, _messagesPanel } };
        }
        _latest.IsVisible = !side && !string.IsNullOrEmpty(_latestText.Text);
    }

    /// <summary>A large cue button. The active tile fills with its colour (flashing for standby and end) and is the cue light.</summary>
    private sealed class CueTile : Border
    {
        private static readonly Color Ink = Color.FromArgb("#0B1416");
        private readonly CueLight _cue;
        private readonly Label _name, _flashing;
        private string _state = "";

        public CueTile(CueLight cue)
        {
            _cue = cue;
            _name = new Label { Text = Setting.Token(cue).ToUpperInvariant(), FontAttributes = FontAttributes.Bold, CharacterSpacing = 2, HorizontalTextAlignment = TextAlignment.Center, LineBreakMode = LineBreakMode.NoWrap };
            _flashing = new Label { Text = "FLASHING", FontSize = 11, CharacterSpacing = 1.5, FontAttributes = FontAttributes.Bold, HorizontalTextAlignment = TextAlignment.Center, IsVisible = false };
            Content = new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Spacing = 2, Children = { _name, _flashing } };
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 };
            Padding = new Thickness(4);
            SemanticProperties.SetDescription(this, "Cue " + _name.Text);
        }

        public void Size(double height, double font)
        {
            HeightRequest = height;
            _name.FontSize = font;
            _flashing.FontSize = Math.Max(9, font * 0.45);
        }

        public void Update(bool active, bool lit, bool blink, bool dimmed)
        {
            string state = $"{active}{lit}{blink}{dimmed}";
            if (state == _state) return;
            _state = state;
            bool off = _cue == CueLight.Off;
            bool filled = active && lit && !off;
            BackgroundColor = filled ? CueColor(_cue, true) : off ? Ui.Panel : CueColor(_cue, false);
            Stroke = off ? (active ? Ui.Muted : Ui.Line) : CueColor(_cue, true);
            StrokeThickness = active ? 5 : 2;
            Opacity = dimmed ? 0.45 : 1;
            _name.TextColor = filled ? Ink : off && !active ? Ui.Muted : Ui.Text;
            _flashing.TextColor = _name.TextColor;
            _flashing.IsVisible = active && blink;
        }
    }
}
