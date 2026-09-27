using ChronosTimer.Control;
using ChronosTimer.Settings;

namespace ChronosTimer.App.Pages;

/// <summary>Every setting of the timer, generated from the host's catalog, with its description and allowed values.</summary>
public sealed class SettingsPage : ContentPage
{
    private readonly List<(Setting Setting, Action Refresh)> _rows = [];
    private readonly Label _status;
    private bool _refreshing;
    private bool _dirty, _visible, _ticking;

    private static TimerHost Host => AppHost.Host;

    public SettingsPage()
    {
        Title = "Settings";
        BackgroundColor = Ui.Background;
        _status = new Label { FontSize = 13, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.WordWrap };

        var stack = new VerticalStackLayout { Padding = new Thickness(16, 8, 16, 32), Spacing = 2 };
        stack.Children.Add(Ui.Row(
            Ui.Button("Save now", () => { AppHost.SaveNow(); _status.Text = "Saved to " + AppHost.SettingsPath; }),
            Ui.Button("Restore defaults", async () =>
            {
                if (await DisplayAlertAsync("Restore defaults", "Set every setting back to its default?", "Restore", "Cancel"))
                {
                    Host.Defaults();
                    Host.Start();
                    RefreshAll();
                    _status.Text = "Defaults restored.";
                }
            })));
        stack.Children.Add(_status);
        stack.Children.Add(Ui.Caption("Settings are saved automatically. The same names work as commands ('duration 5m'), over HTTP (/api/set?name=duration&value=5m), OSC (/chronos/set/duration 5m) and on the chronos-timer command line (--duration 5m)."));

        foreach (var group in Host.Settings.GroupBy(s => s.Category))
        {
            stack.Children.Add(Ui.Heading(group.Key));
            foreach (var s in group) stack.Children.Add(BuildRow(s));
        }
        Content = new ScrollView { Content = stack };
        Host.StatusChanged += () => _dirty = true;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        RefreshAll();
        if (_ticking) return;
        _ticking = true;
        Dispatcher.StartTimer(TimeSpan.FromMilliseconds(500), () =>
        {
            if (_dirty) { _dirty = false; RefreshAll(); }
            _ticking = _visible;
            return _visible;
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
    }

    private void RefreshAll()
    {
        _refreshing = true;
        try { foreach (var (_, refresh) in _rows) refresh(); }
        finally { _refreshing = false; }
    }

    private View BuildRow(Setting s)
    {
        var error = new Label { FontSize = 12, TextColor = Ui.Critical, IsVisible = false, LineBreakMode = LineBreakMode.WordWrap };

        async void Apply(string value)
        {
            if (_refreshing) return;
            if (s.Name == "input" && value == "on" && !await AppHost.EnsureMicrophoneAsync())
            {
                error.Text = "Microphone access is needed to read LTC.";
                error.IsVisible = true;
                return;
            }
            CommandResult r;
            try { r = Host.Set(s.Name, value); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException) { r = CommandResult.Error(ex.Message); }
            error.Text = r.Ok ? "" : r.Message;
            error.IsVisible = !r.Ok;
            _dirty = true;
        }

        View editor;
        Action refresh;
        var options = s.Options;
        if (s.Kind == SettingKind.Toggle)
        {
            var sw = new Switch { OnColor = Ui.Accent, HorizontalOptions = LayoutOptions.Start };
            sw.Toggled += (_, e) => Apply(e.Value ? "on" : "off");
            editor = sw;
            refresh = () => sw.IsToggled = s.Value == "on";
        }
        else if (s.Kind == SettingKind.Choice || options.Count > 0)
        {
            var picker = new Picker { TextColor = Ui.Text, BackgroundColor = Ui.Panel, TitleColor = Ui.Muted, MinimumHeightRequest = 44 };
            List<SettingOption> current = [];
            void Fill()
            {
                current = [.. s.Options];
                if (!current.Any(o => o.Value.Equals(s.Value, StringComparison.OrdinalIgnoreCase))) current.Insert(0, new SettingOption(s.Value, "current"));
                picker.ItemsSource = current.Select(o => o.Description.Length > 0 ? $"{o.Value} — {o.Description}" : o.Value).ToList();
                picker.SelectedIndex = current.FindIndex(o => o.Value.Equals(s.Value, StringComparison.OrdinalIgnoreCase));
            }
            picker.SelectedIndexChanged += (_, _) => { if (picker.SelectedIndex >= 0 && picker.SelectedIndex < current.Count) Apply(current[picker.SelectedIndex].Value); };
            editor = picker;
            refresh = () =>
            {
                int idx = current.FindIndex(o => o.Value.Equals(s.Value, StringComparison.OrdinalIgnoreCase));
                if (idx < 0 || s.Kind != SettingKind.Choice) Fill(); else if (picker.SelectedIndex != idx) picker.SelectedIndex = idx;
            };
        }
        else
        {
            var entry = Ui.Entry(s.Accepts);
            entry.Completed += (_, _) => Apply(entry.Text ?? "");
            entry.Unfocused += (_, _) => { if ((entry.Text ?? "") != s.Value) Apply(entry.Text ?? ""); };
            editor = entry;
            refresh = () => { if (!entry.IsFocused) entry.Text = s.Value; };
        }
        _rows.Add((s, refresh));

        var name = new Label { Text = s.Name, FontFamily = Ui.Mono, FontSize = 14, TextColor = Ui.Text, FontAttributes = FontAttributes.Bold, VerticalOptions = LayoutOptions.Center };
        var desc = Ui.Caption(s.Description + (s.Unit is not null ? $" ({s.Unit})" : "") + $"  Default: {s.Default}.", 12);

        var grid = new Grid
        {
            ColumnDefinitions = { new(new GridLength(170)), new(GridLength.Star) },
            RowDefinitions = { new(GridLength.Auto), new(GridLength.Auto), new(GridLength.Auto) },
            ColumnSpacing = 12,
            Padding = new Thickness(0, 6),
        };
        grid.Add(name, 0, 0);
        grid.Add(editor, 1, 0);
        grid.Add(desc, 1, 1);
        grid.Add(error, 1, 2);
        if (DeviceInfo.Idiom == DeviceIdiom.Phone)
        {
            // stack on narrow screens
            grid.ColumnDefinitions[0].Width = GridLength.Star;
            grid.ColumnDefinitions[1].Width = new GridLength(0);
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Grid.SetColumn(editor, 0); Grid.SetRow(editor, 1);
            Grid.SetColumn(desc, 0); Grid.SetRow(desc, 2);
            Grid.SetColumn(error, 0); Grid.SetRow(error, 3);
        }
        return new VerticalStackLayout { Children = { grid, new BoxView { HeightRequest = 1, Color = Ui.Line } } };
    }
}
