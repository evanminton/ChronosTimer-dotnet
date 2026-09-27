using System.Globalization;
using LinearTimecode;

namespace ChronosTimer.Settings;

/// <summary>The kind of value a setting takes (for building editors and help text).</summary>
public enum SettingKind
{
    Choice,
    Toggle,
    Number,
    Text,
    Timecode,
    Duration,
}

/// <summary>One allowed value of a choice setting.</summary>
public sealed record SettingOption(string Value, string Description);

/// <summary>
/// A named, human-readable setting, readable and writable as text. The same catalog drives the command line, text
/// commands, HTTP, OSC, the web remote and the app's settings page.
/// </summary>
public sealed class Setting
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Func<IReadOnlyList<SettingOption>>? _dynamicOptions;

    public Setting(string name, string category, SettingKind kind, string description, Func<string> get, Action<string> set,
        IReadOnlyList<SettingOption>? options = null, string? unit = null, Func<IReadOnlyList<SettingOption>>? dynamicOptions = null)
    {
        Name = name;
        Category = category;
        Kind = kind;
        Description = description;
        _get = get;
        _set = set;
        StaticOptions = options ?? [];
        Unit = unit;
        _dynamicOptions = dynamicOptions;
        Default = get();
    }

    /// <summary>kebab-case name, e.g. "end-action".</summary>
    public string Name { get; }
    public string Category { get; }
    public SettingKind Kind { get; }
    public string Description { get; }
    public string? Unit { get; }

    /// <summary>The value when the catalog was built.</summary>
    public string Default { get; }

    private IReadOnlyList<SettingOption> StaticOptions { get; }

    /// <summary>Allowed values (choices, or e.g. the current device list).</summary>
    public IReadOnlyList<SettingOption> Options => _dynamicOptions?.Invoke() ?? StaticOptions;

    public string Value => _get();

    /// <summary>Sets from text. Throws <see cref="FormatException"/> / <see cref="ArgumentException"/> with a readable message.</summary>
    public void Set(string text)
    {
        _set((text ?? "").Trim());
        Changed?.Invoke(this);
    }

    /// <summary>Raised after a successful <see cref="Set"/>.</summary>
    public event Action<Setting>? Changed;

    /// <summary>One-line hint of what may be typed.</summary>
    public string Accepts => Kind switch
    {
        SettingKind.Choice => string.Join(" | ", Options.Select(o => o.Value)),
        SettingKind.Toggle => "on | off",
        SettingKind.Timecode => "HH:MM:SS:FF",
        SettingKind.Duration => "time (5:00, 90s, 1h30m, 00:05:00:00)",
        SettingKind.Number => Unit is null ? "number" : $"number ({Unit})",
        _ => Options.Count > 0 ? "text, e.g. " + string.Join(" | ", Options.Take(6).Select(o => o.Value)) : "text",
    };

    public override string ToString() => $"{Name} = {Value}";

    // ───────────── parsing helpers ─────────────

    public static bool ParseBool(string text) => text.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "1" or "enable" or "enabled" => true,
        "off" or "false" or "no" or "0" or "disable" or "disabled" => false,
        _ => throw new FormatException($"'{text}' is not on/off."),
    };

    public static string Bool(bool b) => b ? "on" : "off";

    public static T ParseEnum<T>(string text) where T : struct, Enum
    {
        string key = Norm(text);
        foreach (var v in Enum.GetValues<T>())
            if (Norm(v.ToString()) == key) return v;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && Enum.IsDefined(typeof(T), i))
            return (T)(object)i;
        throw new FormatException($"'{text}' is not one of: {string.Join(", ", Enum.GetValues<T>().Select(Token))}.");
    }

    /// <summary>kebab-case token for an enum value, e.g. CountDown → "countdown", TimeOfDay → "time-of-day".</summary>
    public static string Token<T>(T value) where T : struct, Enum => Kebab(value.ToString());

    internal static string Kebab(string s)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsUpper(s[i]) && i > 0 && !char.IsUpper(s[i - 1])) sb.Append('-');
            sb.Append(char.ToLowerInvariant(s[i]));
        }
        return sb.ToString();
    }

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    public static double ParseNumber(string text, double min, double max)
    {
        string t = text.Trim().TrimEnd('%');
        foreach (string unit in new[] { "dbfs", "db", "ms", "hz", "khz", "us", "µs", "x" })
            if (t.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) { t = t[..^unit.Length].Trim(); break; }
        if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            throw new FormatException($"'{text}' is not a number.");
        if (text.Trim().EndsWith("khz", StringComparison.OrdinalIgnoreCase)) v *= 1000;
        if (v < min || v > max) throw new FormatException(FormattableString.Invariant($"{v} is outside {min}–{max}."));
        return v;
    }

    public static string Number(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    public static IReadOnlyList<SettingOption> RateOptions { get; } =
        [.. LtcFrameRateExtensions.All.Select(r => new SettingOption(r.Token(), r.DisplayName() + " fps — " + r.Description()))];
}
