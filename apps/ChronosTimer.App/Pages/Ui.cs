namespace ChronosTimer.App.Pages;

/// <summary>Shared colours, fonts and control helpers.</summary>
internal static class Ui
{
    public static readonly Color Background = Color.FromArgb("#0B1416");
    public static readonly Color Panel = Color.FromArgb("#12201F");
    public static readonly Color Line = Color.FromArgb("#23403C");
    public static readonly Color Text = Color.FromArgb("#E8F1EF");
    public static readonly Color Muted = Color.FromArgb("#8AA6A1");
    public static readonly Color Accent = Color.FromArgb("#2FD3B5");
    public static readonly Color Warning = Color.FromArgb("#FFB020");
    public static readonly Color Critical = Color.FromArgb("#FF4D4D");
    public static readonly Color Ended = Color.FromArgb("#7A8C89");

    public static string Mono => DeviceInfo.Platform == DevicePlatform.WinUI ? "Consolas"
        : DeviceInfo.Platform == DevicePlatform.Android ? "monospace"
        : "Menlo";

    public static Color PhaseColor(TimerPhase phase, bool running) => phase switch
    {
        TimerPhase.Warning => Warning,
        TimerPhase.Critical or TimerPhase.Overrun => Critical,
        TimerPhase.Ended => Ended,
        _ => running ? Text : Color.FromArgb("#C9D6D3"),
    };

    public static Button Button(string text, Action onClick, bool primary = false)
    {
        var b = new Button
        {
            Text = text,
            FontSize = 16,
            Padding = new Thickness(14, 10),
            CornerRadius = 10,
            MinimumHeightRequest = 44,
            MinimumWidthRequest = 76,
            BackgroundColor = primary ? Accent : Panel,
            TextColor = primary ? Color.FromArgb("#04211B") : Text,
            BorderColor = primary ? Accent : Line,
            BorderWidth = 1,
            FontAttributes = primary ? FontAttributes.Bold : FontAttributes.None,
            Margin = new Thickness(0, 0, 8, 8),
        };
        b.Clicked += (_, _) => onClick();
        return b;
    }

    public static FlexLayout Row(params View[] children)
    {
        var f = new FlexLayout { Wrap = Microsoft.Maui.Layouts.FlexWrap.Wrap, AlignItems = Microsoft.Maui.Layouts.FlexAlignItems.Start };
        foreach (var c in children) f.Children.Add(c);
        return f;
    }

    public static Label Heading(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        FontSize = 13,
        CharacterSpacing = 1.2,
        TextColor = Muted,
        FontAttributes = FontAttributes.Bold,
        Margin = new Thickness(0, 18, 0, 6),
    };

    public static Label Caption(string text, double size = 13) => new()
    {
        Text = text,
        FontSize = size,
        TextColor = Muted,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    public static Label MonoLabel(double size = 13) => new()
    {
        FontFamily = Mono,
        FontSize = size,
        TextColor = Text,
        LineBreakMode = LineBreakMode.WordWrap,
    };

    public static Entry Entry(string placeholder) => new()
    {
        Placeholder = placeholder,
        PlaceholderColor = Muted,
        TextColor = Text,
        BackgroundColor = Panel,
        FontFamily = Mono,
        MinimumHeightRequest = 44,
        ClearButtonVisibility = ClearButtonVisibility.WhileEditing,
    };

    public static Border Card(View content) => new()
    {
        Content = content,
        Stroke = Line,
        StrokeThickness = 1,
        BackgroundColor = Panel,
        Padding = new Thickness(14, 12),
        Margin = new Thickness(0, 8),
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
    };
}
