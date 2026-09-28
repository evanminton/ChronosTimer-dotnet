using System.Globalization;
using ChronosTimer.Settings;

namespace ChronosTimer.Show;

/// <summary>The cue light the master sets and every linked timer shows.</summary>
public enum CueLight
{
    /// <summary>Dark.</summary>
    Off,
    /// <summary>Get ready (amber, flashing); followers can acknowledge.</summary>
    Standby,
    /// <summary>Go (green).</summary>
    Go,
    /// <summary>Time is nearly up (amber). Set automatically at the countdown's warning time when cue-auto is on.</summary>
    Warning,
    /// <summary>Time is up: wrap up (red, flashing). Set automatically at zero when cue-auto is on.</summary>
    End,
    /// <summary>Stop now (red).</summary>
    Stop,
}

/// <summary>A text message between linked timers. <see cref="To"/> is null for everyone.</summary>
public sealed record ShowMessage(DateTimeOffset Time, string From, string? To, string Text)
{
    public const int MaxLength = 500;

    public override string ToString() =>
        Time.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "  " + From + (To is null ? "" : " → " + To) + ": " + Text;

    /// <summary>Trims, flattens line breaks and limits the length.</summary>
    public static string Clean(string text)
    {
        string t = new string((text ?? "").Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return t.Length > MaxLength ? t[..MaxLength] : t;
    }
}

/// <summary>
/// What the Show display shows: the master's running time, its show schedule line and the cue light. A master builds it
/// from its own timer; a follower gets it from the master over the link.
/// </summary>
public sealed record ShowView
{
    /// <summary>Name of the timer the time comes from (the master when following).</summary>
    public string Source { get; init; } = "";

    /// <summary>False for a follower that has no master right now (the rest is the last thing received).</summary>
    public bool Live { get; init; } = true;

    /// <summary>Big text: the running time, or the time to the start before a scheduled show.</summary>
    public string Display { get; init; } = "";

    /// <summary>Line under it: e.g. "Starts 19:30:00 · ends 21:30:00".</summary>
    public string Detail { get; init; } = "";

    public TimerPhase Phase { get; init; }
    public TransportState State { get; init; }
    public ShowPhase ShowPhase { get; init; }
    public CueLight Cue { get; init; }
    public double? Progress { get; init; }

    /// <summary>Remaining time of the count (or of the show), if any.</summary>
    public TimeSpan? Remaining { get; init; }

    internal Dictionary<string, string> ToFields() => new()
    {
        ["source"] = Source,
        ["display"] = Display,
        ["detail"] = Detail,
        ["phase"] = Setting.Token(Phase),
        ["state"] = Setting.Token(State),
        ["show"] = Setting.Token(ShowPhase),
        ["cue"] = Setting.Token(Cue),
        ["progress"] = Progress is { } p ? p.ToString("0.####", CultureInfo.InvariantCulture) : "",
        ["remaining"] = Remaining is { } r ? r.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) : "",
    };

    internal static ShowView FromFields(IReadOnlyDictionary<string, string> f)
    {
        static T E<T>(IReadOnlyDictionary<string, string> f, string k) where T : struct, Enum
        {
            try { return f.TryGetValue(k, out string? v) ? Setting.ParseEnum<T>(v) : default; }
            catch (FormatException) { return default; }
        }
        static double? N(IReadOnlyDictionary<string, string> f, string k) =>
            f.TryGetValue(k, out string? v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

        return new ShowView
        {
            Source = f.GetValueOrDefault("source") ?? "",
            Display = f.GetValueOrDefault("display") ?? "",
            Detail = f.GetValueOrDefault("detail") ?? "",
            Phase = E<TimerPhase>(f, "phase"),
            State = E<TransportState>(f, "state"),
            ShowPhase = E<ShowPhase>(f, "show"),
            Cue = E<CueLight>(f, "cue"),
            Progress = N(f, "progress"),
            Remaining = N(f, "remaining") is { } r ? TimeSpan.FromSeconds(r) : null,
        };
    }
}
