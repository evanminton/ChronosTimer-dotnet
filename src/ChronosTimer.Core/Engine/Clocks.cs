using System.Diagnostics;

namespace ChronosTimer;

/// <summary>A monotonic time base in seconds.</summary>
public interface IClock
{
    /// <summary>Current time in seconds. Only differences matter.</summary>
    double Now { get; }
}

/// <summary>The system's monotonic high-resolution clock.</summary>
public sealed class SystemClock : IClock
{
    private static readonly long Origin = Stopwatch.GetTimestamp();

    public static SystemClock Instance { get; } = new();

    public double Now => Stopwatch.GetElapsedTime(Origin).TotalSeconds;
}

/// <summary>A clock that only moves when told to (tests, offline rendering).</summary>
public sealed class ManualClock : IClock
{
    public double Now { get; set; }

    public void Advance(double seconds) => Now += seconds;
}

/// <summary>
/// A clock driven by an audio stream's sample counter, so code generated from it never drifts against the audio
/// device. Between callbacks it interpolates with the system clock (never past one callback's worth of audio).
/// </summary>
/// <remarks>
/// <see cref="Now"/> is the time of the sample currently being <em>heard</em>: rendered samples minus the output
/// latency. The audio thread calls <see cref="OnCallback"/> before rendering each buffer.
/// </remarks>
public sealed class AudioClock : IClock
{
    private readonly object _gate = new();
    private readonly double _base;
    private readonly int _sampleRate;
    private double _heard;          // seconds heard at the last callback (relative to _base)
    private double _cbSystemTime;   // system time of the last callback
    private double _maxAdvance;     // the last callback's buffer length
    private double _lastReturned = double.NegativeInfinity;

    /// <summary>Creates a clock that starts at <paramref name="start"/> seconds (so it continues another clock).</summary>
    public AudioClock(double start, int sampleRate)
    {
        _base = start;
        _sampleRate = sampleRate;
        _cbSystemTime = SystemClock.Instance.Now;
        _maxAdvance = 0.05;
    }

    public int SampleRate => _sampleRate;

    /// <summary>True once the audio thread has called back at least once.</summary>
    public bool Started { get; private set; }

    /// <summary>Called by the audio thread before rendering <paramref name="count"/> samples starting at <paramref name="renderedSamples"/>.</summary>
    public void OnCallback(long renderedSamples, int count, int latencySamples)
    {
        lock (_gate)
        {
            _heard = (double)(renderedSamples - latencySamples) / _sampleRate;
            _cbSystemTime = SystemClock.Instance.Now;
            _maxAdvance = Math.Max((double)count / _sampleRate, 0.001) * 2;
            Started = true;
        }
    }

    /// <summary>Time at which rendered sample <paramref name="sample"/> is heard.</summary>
    public double HeardTime(long sample, int latencySamples) => _base + (double)(sample - latencySamples) / _sampleRate;

    public double Now
    {
        get
        {
            lock (_gate)
            {
                double adv = Math.Clamp(SystemClock.Instance.Now - _cbSystemTime, 0, _maxAdvance);
                double t = _base + _heard + adv;
                if (t < _lastReturned) t = _lastReturned; // monotonic
                _lastReturned = t;
                return t;
            }
        }
    }
}
