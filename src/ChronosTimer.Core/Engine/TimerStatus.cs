using LinearTimecode;

namespace ChronosTimer;

/// <summary>An immutable snapshot of the timer, taken at <see cref="Time"/>.</summary>
public sealed record TimerStatus
{
    /// <summary>Engine clock time of the snapshot (seconds).</summary>
    public double Time { get; init; }
    public TimerMode Mode { get; init; }
    public TransportState State { get; init; }
    public LtcFrameRate Rate { get; init; }

    /// <summary>The current time address (what LTC carries in timecode, time-of-day and chase modes).</summary>
    public Timecode Timecode { get; init; }

    /// <summary>The main display text, formatted per <see cref="DisplayFormat"/>.</summary>
    public string Display { get; init; } = "";

    /// <summary>A second line: e.g. "of 00:05:00", "remaining 00:01:12", "chasing 25 fps".</summary>
    public string Detail { get; init; } = "";

    public DisplayFormat DisplayFormat { get; init; }

    /// <summary>Time since the start (count modes and timecode mode); zero for time of day and chase.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Time left until the end (countdown, or any count with a limit); negative in overrun.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>The count's length (countdown/count-up duration, timecode start → end), if any.</summary>
    public TimeSpan? Length { get; init; }

    /// <summary>0–1 through <see cref="Length"/>, if any.</summary>
    public double? Progress { get; init; }

    public TimerPhase Phase { get; init; }
    public double Speed { get; init; }
    public bool Reverse { get; init; }
    public ChaseStatus Chase { get; init; }

    /// <summary>Last time address received on the LTC input (chase), if any.</summary>
    public Timecode? ChaseInput { get; init; }

    /// <summary>True when LTC is being sent (output open and the engine says "active").</summary>
    public bool OutputActive { get; init; }

    public override string ToString() => $"{Display}  {State} {Mode} {Rate.DisplayName()}{(Detail.Length > 0 ? "  " + Detail : "")}";
}
