using LinearTimecode;
using LinearTimecode.Audio;

namespace ChronosTimer.Audio;

/// <summary>
/// Reads LTC from an audio input and feeds it to the engine's chase. Call <see cref="Process"/> from an input callback.
/// </summary>
public sealed class LtcInput
{
    private readonly TimerEngine _engine;
    private readonly LtcDecoder _decoder;
    private float[] _mono = new float[4096];
    private long _bufferStart;
    private int _bufferLen;
    private double _bufferEndTime;
    private float _peak;

    /// <param name="engine">Engine to feed.</param>
    /// <param name="sampleRate">Input sample rate.</param>
    /// <param name="rate">Rate of the incoming code, or null to detect it.</param>
    public LtcInput(TimerEngine engine, int sampleRate, LtcFrameRate? rate = null)
    {
        _engine = engine;
        SampleRate = sampleRate;
        _decoder = new LtcDecoder(sampleRate, rate);
        _decoder.FrameDecoded += OnFrame;
    }

    public int SampleRate { get; }

    /// <summary>Input channel, 1-based.</summary>
    public int Channel { get; set; } = 1;

    /// <summary>Samples between the converter and <see cref="Process"/>.</summary>
    public int LatencySamples { get; set; }

    /// <summary>Minimum signal level (0–1) the decoder accepts.</summary>
    public double MinimumLevel
    {
        get => _decoder.MinimumLevel;
        set => _decoder.MinimumLevel = value;
    }

    /// <summary>When the incoming code's frame count (24/25/30) or drop-frame flag differs from the engine's rate, switch the engine to it (chase mode only).</summary>
    public bool FollowRate { get; set; } = true;

    /// <summary>Recent peak input level, 0–1 (decays).</summary>
    public float Level => _peak;

    /// <summary>Rate detected from the incoming code.</summary>
    public LtcFrameRate? DetectedRate => _decoder.DetectedRate;

    /// <summary>Last codeword read.</summary>
    public LtcDecodedFrame? LastFrame => _decoder.LastFrame;

    /// <summary>Raised on the audio thread for each codeword read.</summary>
    public event Action<LtcDecodedFrame>? FrameReceived;

    /// <summary>Feeds an interleaved buffer.</summary>
    public void Process(ReadOnlySpan<float> interleaved, int channels)
    {
        if (channels < 1) throw new ArgumentOutOfRangeException(nameof(channels));
        int frames = interleaved.Length / channels;
        if (_mono.Length < frames) _mono = new float[frames];
        int ch = Math.Clamp(Channel, 1, channels) - 1;
        float peak = _peak * 0.9f;
        for (int i = 0; i < frames; i++)
        {
            float v = interleaved[i * channels + ch];
            _mono[i] = v;
            float a = Math.Abs(v);
            if (a > peak) peak = a;
        }
        _peak = peak;

        _bufferEndTime = _engine.Now - (double)LatencySamples / SampleRate;
        _bufferStart = _decoder.SamplePosition;
        _bufferLen = frames;
        _decoder.Process(_mono.AsSpan(0, frames));
    }

    private void OnFrame(LtcDecodedFrame f)
    {
        double time = _bufferEndTime - (_bufferStart + _bufferLen - f.StartSample) / SampleRate;
        var tc = f.Timecode;
        if (FollowRate && _engine.Mode == TimerMode.Chase)
        {
            var er = _engine.Rate;
            if (tc.Rate.Base() != er.Base() || tc.Rate.IsDropFrame() != er.IsDropFrame())
                _engine.Rate = tc.Rate;
        }
        _engine.OnChaseFrame(tc, time, f.Speed);
        FrameReceived?.Invoke(f);
    }
}
