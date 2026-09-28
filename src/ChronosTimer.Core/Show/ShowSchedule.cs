using System.Globalization;

namespace ChronosTimer.Show;

/// <summary>Where a scheduled show is.</summary>
public enum ShowPhase
{
    /// <summary>No show scheduled.</summary>
    Idle,
    /// <summary>Scheduled; counting down to the start.</summary>
    Waiting,
    /// <summary>Held: before the start the start waits for the release; while running the count is paused.</summary>
    Holding,
    /// <summary>Between the (delayed) start and the (delayed) end.</summary>
    Running,
    /// <summary>Past the (delayed) end.</summary>
    Over,
}

/// <summary>A snapshot of a <see cref="ShowSchedule"/> at one moment. Times of a current hold are projected as if it were released now.</summary>
public sealed record ShowState
{
    public ShowPhase Phase { get; init; }

    /// <summary>The start and end as scheduled (no delay).</summary>
    public DateTimeOffset? Start { get; init; }
    public DateTimeOffset? End { get; init; }

    /// <summary>Start and end moved by the holds so far (and by the current hold, as if released now).</summary>
    public DateTimeOffset? EffectiveStart { get; init; }
    public DateTimeOffset? EffectiveEnd { get; init; }

    /// <summary>How far the end has moved (every hold).</summary>
    public TimeSpan Delay { get; init; }

    /// <summary>How long the current hold has lasted (zero when not holding).</summary>
    public TimeSpan HeldFor { get; init; }

    /// <summary>True once the show has started.</summary>
    public bool Started { get; init; }

    /// <summary>Time left until the start (before it).</summary>
    public TimeSpan? UntilStart { get; init; }

    /// <summary>Time left until the end (after the start, when an end is set; negative when over).</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Running time since the start, holds excluded (after the start).</summary>
    public TimeSpan? Elapsed { get; init; }

    /// <summary>Scheduled end − start, when an end is set. Holds never shorten it: the end moves instead.</summary>
    public TimeSpan? Length { get; init; }
}

/// <summary>
/// A show's scheduled start and end, and the hold button for a late start ("the artist is late"): hold before the start
/// and the start waits until the hold is released; the end moves by the same amount, so the show keeps its full length.
/// A hold while the show runs pauses it and moves the end by the length of the hold.
/// Pure logic on wall-clock instants; not thread-safe (the owner locks).
/// </summary>
public sealed class ShowSchedule
{
    private DateTimeOffset? _heldSince;
    private TimeSpan _startDelay, _delay;

    public DateTimeOffset? Start { get; private set; }
    public DateTimeOffset? End { get; private set; }
    public bool Holding => _heldSince is not null;

    /// <summary>Schedules a show (end optional: without one the show counts up from the start). Clears holds and delays.</summary>
    public void Set(DateTimeOffset? start, DateTimeOffset? end)
    {
        if (end is not null && start is null) throw new ArgumentException("Set a start time before the end time.");
        if (end is { } e && start is { } s && e <= s) throw new ArgumentException("The end time must be after the start time.");
        Start = start;
        End = end;
        _heldSince = null;
        _startDelay = _delay = TimeSpan.Zero;
    }

    /// <summary>Removes the schedule.</summary>
    public void Clear() => Set(null, null);

    /// <summary>Starts a hold. Before the start, the start waits until <see cref="Release"/>.</summary>
    public void Hold(DateTimeOffset now)
    {
        if (Start is null) throw new InvalidOperationException("No show is scheduled (set a start time first).");
        if (_heldSince is not null) return;
        if (End is not null && now >= End.Value + _delay) throw new InvalidOperationException("The show is already over.");
        _heldSince = now;
    }

    /// <summary>Ends the hold: the start (if the hold spanned it) and the end move by the delay.</summary>
    public void Release(DateTimeOffset now)
    {
        if (_heldSince is not { } since) return;
        (_startDelay, _delay) = Project(since, now);
        _heldSince = null;
    }

    /// <summary>The start/end delays if the current hold (begun at <paramref name="since"/>) ends at <paramref name="now"/>.</summary>
    private (TimeSpan StartDelay, TimeSpan Delay) Project(DateTimeOffset since, DateTimeOffset now)
    {
        var effStart = Start!.Value + _startDelay;
        if (since < effStart)
        {
            // Held before the start: only the part past the start delays anything.
            var d = now > effStart ? now - effStart : TimeSpan.Zero;
            return (_startDelay + d, _delay + d);
        }
        return (_startDelay, _delay + (now > since ? now - since : TimeSpan.Zero));
    }

    public ShowState Evaluate(DateTimeOffset now)
    {
        if (Start is not { } start) return new ShowState { Phase = ShowPhase.Idle };

        var (sd, d) = _heldSince is { } since ? Project(since, now) : (_startDelay, _delay);
        bool preStartHold = _heldSince is { } hs && hs < start + _startDelay;
        var effStart = start + sd;
        var effEnd = End + d;
        bool started = !preStartHold && now >= effStart;

        var phase = _heldSince is not null ? ShowPhase.Holding
            : !started ? ShowPhase.Waiting
            : effEnd is { } ee && now >= ee ? ShowPhase.Over
            : ShowPhase.Running;

        return new ShowState
        {
            Phase = phase,
            Start = start,
            End = End,
            EffectiveStart = effStart,
            EffectiveEnd = effEnd,
            Delay = d,
            HeldFor = _heldSince is { } h ? now - h : TimeSpan.Zero,
            Started = started,
            UntilStart = started ? null : effStart - now,
            Remaining = started && effEnd is { } re ? re - now : null,
            Elapsed = started ? now - effStart - (d - sd) : null,
            Length = End - start,
        };
    }
}

/// <summary>Parsing and formatting of show start / end times.</summary>
public static class ShowTime
{
    public const string Help = "a date and time (2026-10-03 19:30), a time today (19:30, 7:30pm), +time from now or, for the end, from the start (+1h30m), or 'none'";

    private static readonly string[] TimeFormats = ["H:mm", "HH:mm", "H:mm:ss", "HH:mm:ss", "h:mmtt", "h:mm tt", "htt", "h tt", "h:mm:sstt"];

    /// <summary>
    /// Parses a show time. <paramref name="after"/> (the start, for an end time) anchors relative times and rolls a
    /// bare clock time onto the next day when it would not be after it. Returns null for "none".
    /// </summary>
    public static DateTimeOffset? Parse(string text, DateTimeOffset now, DateTimeOffset? after = null)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0 || t.Equals("none", StringComparison.OrdinalIgnoreCase) || t.Equals("off", StringComparison.OrdinalIgnoreCase) || t.Equals("clear", StringComparison.OrdinalIgnoreCase))
            return null;
        if (t.Equals("now", StringComparison.OrdinalIgnoreCase)) return now;

        if (t[0] == '+')
        {
            if (!TimeInput.TryParse(t, out var rel, out string? err)) throw new FormatException(err ?? $"'{text}' is not a time.");
            return (after ?? now) + TimeSpan.FromSeconds(Math.Abs(rel.ToSeconds(LinearTimecode.LtcFrameRate.Fps25)));
        }

        var inv = CultureInfo.InvariantCulture;
        string compact = t.Replace(" ", "").ToUpperInvariant();
        if (DateTime.TryParseExact(compact, TimeFormats, inv, DateTimeStyles.None, out var tod)
            || DateTime.TryParseExact(t.ToUpperInvariant(), TimeFormats, inv, DateTimeStyles.None, out tod))
        {
            var date = now.ToLocalTime().Date;
            DateTimeOffset At(DateTime d) { var dt = d + tod.TimeOfDay; return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt)); }
            var day = At(date);
            if (after is { } a) while (day <= a) { date = date.AddDays(1); day = At(date); }
            return day;
        }

        if (DateTimeOffset.TryParse(t, inv, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out var full)) return full;
        if (DateTimeOffset.TryParse(t, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AllowWhiteSpaces, out full)) return full;
        throw new FormatException($"'{text}' is not a date/time. Use {Help}.");
    }

    /// <summary>Round-trippable text for settings ("2026-10-03T19:30:00+02:00", or "none").</summary>
    public static string Format(DateTimeOffset? t) => t is { } v ? v.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) : "none";

    /// <summary>Short local text for displays: "19:30:00", or "Sat 19:30:00" when not today.</summary>
    public static string Short(DateTimeOffset t, DateTimeOffset now)
    {
        var l = t.ToLocalTime();
        string clock = l.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return l.Date == now.ToLocalTime().Date ? clock : l.ToString("ddd d MMM ", CultureInfo.InvariantCulture) + clock;
    }
}
