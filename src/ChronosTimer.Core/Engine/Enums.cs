namespace ChronosTimer;

/// <summary>What the timer counts. Switchable at any time, also while running.</summary>
public enum TimerMode
{
    /// <summary>Timecode generator: counts up from a start address (e.g. 01:00:00:00), optional end address.</summary>
    Timecode,
    /// <summary>Stopwatch: counts elapsed time up from zero, optional limit.</summary>
    CountUp,
    /// <summary>Countdown: counts a duration down to zero, then continues into overrun, stops, pauses or loops.</summary>
    CountDown,
    /// <summary>Time of day: follows the computer clock (local or UTC) plus an offset.</summary>
    TimeOfDay,
    /// <summary>Chase: follows LTC read from an audio input, freewheels through dropouts, optionally regenerates it.</summary>
    Chase,
}

/// <summary>Transport state.</summary>
public enum TransportState
{
    /// <summary>Halted. LTC output follows <c>stopped-output</c> (silence by default).</summary>
    Stopped,
    /// <summary>Counting.</summary>
    Running,
    /// <summary>Holding the current position. LTC output follows <c>paused-output</c> (repeat the held frame by default).</summary>
    Paused,
}

/// <summary>What happens when a count reaches its end (duration, end address, or zero when reversing).</summary>
public enum EndAction
{
    /// <summary>Keep counting past the end (countdown shows the overrun as +time).</summary>
    Continue,
    /// <summary>Stop at the end.</summary>
    Stop,
    /// <summary>Pause (hold) at the end.</summary>
    Pause,
    /// <summary>Jump back to the beginning and keep running.</summary>
    Loop,
}

/// <summary>LTC output while the transport isn't running.</summary>
public enum IdleOutput
{
    /// <summary>No signal.</summary>
    Silence,
    /// <summary>Repeat the current frame (a "still" code that readers show as a held address).</summary>
    Hold,
}

/// <summary>Which count a count-up / countdown timer sends as LTC.</summary>
public enum LtcSource
{
    /// <summary>Elapsed time since the start (plus <c>ltc-offset</c>); runs forward in both count modes.</summary>
    Elapsed,
    /// <summary>Time remaining (plus <c>ltc-offset</c>); the address runs down, so it is sent as reverse code.</summary>
    Remaining,
    /// <summary>The time of day, whatever the timer shows.</summary>
    TimeOfDay,
}

/// <summary>What goes into the 32 user bits (binary groups) of the generated LTC.</summary>
public enum UserBitsMode
{
    /// <summary>All zero.</summary>
    None,
    /// <summary>Eight hex digits from <c>user-bits</c>.</summary>
    Hex,
    /// <summary>Four 8-bit characters from <c>user-text</c> (ST 12-1 §8.4.2, BGF 001).</summary>
    Text,
    /// <summary>The current date and time zone per SMPTE ST 309 (BGF 100, or 110 with clock time).</summary>
    Date,
}

/// <summary>How the big display is formatted.</summary>
public enum DisplayFormat
{
    /// <summary>Timecode for timecode, time-of-day and chase modes; compact for count-up and countdown.</summary>
    Auto,
    /// <summary>HH:MM:SS:FF (HH:MM:SS;FF for drop-frame).</summary>
    Timecode,
    /// <summary>HH:MM:SS.</summary>
    Clock,
    /// <summary>M:SS, or H:MM:SS from one hour.</summary>
    Compact,
    /// <summary>M:SS.t (tenths), or H:MM:SS.t from one hour.</summary>
    Tenths,
}

/// <summary>Colour state of the timer, for tally lights and presenter displays.</summary>
public enum TimerPhase
{
    /// <summary>Nothing special.</summary>
    Normal,
    /// <summary>Countdown at or below <c>warning</c>.</summary>
    Warning,
    /// <summary>Countdown at or below <c>critical</c>.</summary>
    Critical,
    /// <summary>Past the end (countdown below zero, count-up past its limit).</summary>
    Overrun,
    /// <summary>Stopped or paused by the end action.</summary>
    Ended,
}

/// <summary>Chase lock state.</summary>
public enum ChaseStatus
{
    /// <summary>No LTC received (or not in chase mode).</summary>
    NoSignal,
    /// <summary>Following incoming code.</summary>
    Locked,
    /// <summary>Code lost; extrapolating for up to <c>freewheel</c> frames.</summary>
    Freewheel,
}

/// <summary>Which clock the timer counts from.</summary>
public enum ClockSource
{
    /// <summary>The audio output's sample clock while LTC output runs (drift-free code); otherwise the system clock. Time of day and chase always use the system clock.</summary>
    Auto,
    /// <summary>Always the system (monotonic) clock.</summary>
    System,
    /// <summary>The audio output's sample clock whenever output is open.</summary>
    Audio,
}
