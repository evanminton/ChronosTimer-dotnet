using LinearTimecode;
using LinearTimecode.Audio;

namespace ChronosTimer.Audio;

/// <summary>
/// Turns the engine into an LTC audio signal. Call <see cref="Render"/> from an audio output callback.
/// </summary>
/// <remarks>
/// Each codeword's address is taken from <see cref="TimerEngine.OutputAt"/> at the moment the codeword will be
/// <em>heard</em> (callback time + buffered latency + <see cref="OffsetSeconds"/>). A phase lock keeps consecutive
/// codewords exactly one address apart, so callback jitter never repeats or skips a frame; a real jump (locate, clock
/// slip) re-locks on the next codeword.
/// </remarks>
public sealed class LtcOutput
{
    private const int Chunk = 64;

    private readonly TimerEngine _engine;
    private LtcGenerator? _gen;
    private float[] _mono = new float[4096];
    private long _rendered;
    private double _chunkTime;

    private bool _haveLast;
    private long _last;
    private double _phase;
    private volatile bool _active;
    private Timecode? _lastSent;

    public LtcOutput(TimerEngine engine, int sampleRate)
    {
        _engine = engine;
        SampleRate = sampleRate;
    }

    public int SampleRate { get; }

    /// <summary>Peak level, 0–1.</summary>
    public float Amplitude { get; set; } = 0.25f;

    /// <summary>Rise/fall time (ST 12-1 §9.6.1: 40 µs ± 10).</summary>
    public TimeSpan RiseTime { get; set; } = TimeSpan.FromTicks(400);

    public bool Invert { get; set; }

    /// <summary>Output channel, 1-based; 0 = every channel.</summary>
    public int Channel { get; set; }

    /// <summary>Samples buffered between <see cref="Render"/> and the converter (set from the stream).</summary>
    public int LatencySamples { get; set; }

    /// <summary>Extra time added when choosing each codeword's address (positive = send later addresses, to make up for downstream delay).</summary>
    public double OffsetSeconds { get; set; }

    /// <summary>
    /// When true, the engine counts from this output's sample clock (attached on the next callback), so the code never
    /// drifts against the audio device. When false, the engine's own clock is used.
    /// </summary>
    public bool UseAudioClock { get; set; }

    /// <summary>The sample clock (created on the first callback with <see cref="UseAudioClock"/>).</summary>
    public AudioClock? AudioClock => _ac;
    private AudioClock? _ac;

    /// <summary>Gives the engine back the system clock if it is counting from this output (call when closing).</summary>
    public void DetachClock()
    {
        if (_ac is not null && ReferenceEquals(_engine.Clock, _ac)) _engine.Clock = SystemClock.Instance;
    }

    /// <summary>True while codewords are being sent.</summary>
    public bool IsActive => _active;

    /// <summary>The last address put into a codeword.</summary>
    public Timecode? LastSent => _lastSent;

    /// <summary>Total samples rendered.</summary>
    public long SamplesRendered => _rendered;

    /// <summary>Raised on the audio thread when a codeword starts (address sent).</summary>
    public event Action<LtcFrame>? FrameSent;

    /// <summary>Fills an interleaved buffer.</summary>
    public void Render(Span<float> interleaved, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        int frames = interleaved.Length / channels;
        if (_mono.Length < frames) _mono = new float[frames];
        var mono = _mono.AsSpan(0, frames);

        if (UseAudioClock)
        {
            _ac ??= new AudioClock(_engine.Now + (double)LatencySamples / SampleRate, SampleRate);
            _ac.OnCallback(_rendered, frames, LatencySamples);
            if (!ReferenceEquals(_engine.Clock, _ac)) _engine.Clock = _ac;
        }
        else DetachClock();

        double baseTime = _ac is not null && ReferenceEquals(_engine.Clock, _ac)
            ? _ac.HeardTime(_rendered)
            : _engine.Now + (double)LatencySamples / SampleRate;
        baseTime += OffsetSeconds;

        int pos = 0;
        while (pos < frames)
        {
            int n = Math.Min(Chunk, frames - pos);
            _chunkTime = baseTime + (double)pos / SampleRate;
            var p = _engine.OutputAt(_chunkTime);

            if (!p.Active)
            {
                _gen = null;
                _haveLast = false;
                _active = false;
                mono.Slice(pos, n).Clear();
                pos += n;
                continue;
            }

            if (_gen is null || _gen.Rate.CodewordRate() != p.Rate.CodewordRate() || _gen.Rate.Base() != p.Rate.Base() || _gen.Rate.IsDropFrame() != p.Rate.IsDropFrame())
            {
                _haveLast = false;
                var first = FrameFor(p);
                _gen = new LtcGenerator(first, SampleRate) { AdvanceDateAtMidnight = false };
                _gen.FrameHook = (_, _) => FrameFor(_engine.OutputAt(_chunkTime));
                _gen.FrameStarted += (f, _) => { _lastSent = f.Timecode; FrameSent?.Invoke(f); };
            }

            _gen.Amplitude = Amplitude;
            _gen.RiseTime = RiseTime;
            _gen.Invert = Invert;
            double speed = p.Moving ? p.Speed : 1;
            if (Math.Abs(_gen.Speed - speed) > 1e-9) _gen.Speed = speed;
            if (_gen.Reverse != p.Reverse) _gen.Reverse = p.Reverse;

            _gen.Read(mono.Slice(pos, n));
            _active = true;
            pos += n;
        }

        // spread mono to the channels
        if (channels == 1) mono.CopyTo(interleaved);
        else
        {
            int only = Channel >= 1 && Channel <= channels ? Channel - 1 : -1;
            for (int i = 0, k = 0; i < frames; i++)
                for (int c = 0; c < channels; c++, k++)
                    interleaved[k] = only < 0 || c == only ? mono[i] : 0f;
        }
        _rendered += frames;
    }

    private LtcFrame FrameFor(OutputPoint p)
    {
        long frame;
        if (!p.Moving)
        {
            frame = (long)Math.Floor(p.Frames + 1e-6);
            _haveLast = false;
        }
        else
        {
            long expected = _last + (p.Reverse ? -1 : 1);
            if (_haveLast && Math.Abs(p.Frames - _phase - expected) < 0.5)
                frame = expected;
            else
            {
                frame = (long)Math.Floor(p.Frames + 1e-6);
                _phase = p.Frames - frame;
            }
            _haveLast = true;
        }
        _last = frame;
        var tc = Timecode.FromTotalFrames(frame, p.Rate);
        return _engine.BuildFrame(tc, _engine.WallTimeAt(_chunkTime));
    }
}
