namespace ChronosTimer.App.Pages;

/// <summary>Remote-control addresses, a command line, the command reference and the log.</summary>
public sealed class ControlPage : ContentPage
{
    private readonly Label _remote, _output, _log;
    private readonly Entry _command;
    private readonly List<string> _history = [];
    private bool _visible, _ticking;

    private static TimerHost Host => AppHost.Host;

    public ControlPage()
    {
        Title = "Control";
        BackgroundColor = Ui.Background;

        _remote = Ui.MonoLabel(13);
        _command = Ui.Entry("Command: help · status · locate 01:00:00:00 · set level -18 · devices");
        _command.Completed += (_, _) => RunCommand();
        _output = Ui.MonoLabel(13);
        _log = Ui.MonoLabel(12);
        _log.TextColor = Ui.Muted;

        var reference = Ui.MonoLabel(12);
        reference.Text = Host.Commands.Help() + "\nTimes: " + TimeInput.Help + ".";

        var cmdRow = new Grid { ColumnDefinitions = { new(GridLength.Star), new(GridLength.Auto) }, ColumnSpacing = 8 };
        cmdRow.Add(_command, 0, 0);
        cmdRow.Add(Ui.Button("Send", RunCommand, primary: true), 1, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 8, 16, 32),
                Spacing = 4,
                Children =
                {
                    Ui.Heading("Remote control"),
                    Ui.Card(_remote),
                    Ui.Caption("Open the browser remote on any phone, tablet or computer on the same network. Companion, QLab, TouchOSC and show-control systems can use the HTTP API or OSC."),
                    Ui.Heading("Command"),
                    cmdRow,
                    Ui.Card(_output),
                    Ui.Heading("Commands"),
                    Ui.Card(reference),
                    Ui.Heading("Log"),
                    Ui.Card(_log),
                },
            },
        };
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _visible = true;
        Refresh();
        if (_ticking) return;
        _ticking = true;
        Dispatcher.StartTimer(TimeSpan.FromSeconds(1), () =>
        {
            if (_visible) Refresh();
            _ticking = _visible;
            return _visible;
        });
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        _visible = false;
    }

    private void RunCommand()
    {
        string line = (_command.Text ?? "").Trim();
        if (line.Length == 0) return;
        _history.Insert(0, "› " + line);
        var r = Host.Execute(line);
        _history.Insert(0, r.Ok ? r.Message : "⚠ " + r.Message);
        if (_history.Count > 40) _history.RemoveRange(40, _history.Count - 40);
        _output.Text = string.Join("\n", _history.AsEnumerable().Reverse().TakeLast(20));
        _command.Text = "";
        Refresh();
    }

    private void Refresh()
    {
        _remote.Text = Host.RemoteText();
        _log.Text = string.Join("\n", Host.RecentLog.TakeLast(40).Reverse());
    }
}
