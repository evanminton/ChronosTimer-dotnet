using ChronosTimer.Audio;
using LinearTimecode;
using LinearTimecode.Audio;

namespace ChronosTimer.Tests;

public class LtcOutputTests
{
    private const int Sr = 48_000;

    /// <summary>Renders <paramref name="seconds"/> of output in 10 ms callbacks, advancing the manual clock (with optional jitter).</summary>
    private static float[] Render(TimerEngine e, ManualClock c, double seconds, double jitterMs = 0, Action<int>? each = null, int channels = 1)
    {
        var o = new LtcOutput(e, Sr) { LatencySamples = 960, Amplitude = 0.5f };
        var rng = new Random(1);
        int block = Sr / 100;
        var all = new List<float>();
        var buf = new float[block * channels];
        double t0 = c.Now;
        for (int i = 0; i < seconds * 100; i++)
        {
            each?.Invoke(i);
            o.Render(buf, channels);
            for (int k = 0; k < block; k++) all.Add(buf[k * channels]);
            // callback timing jitter (doesn't accumulate, like a real audio callback)
            c.Now = t0 + (i + 1) * 0.01 + (jitterMs > 0 ? (rng.NextDouble() - 0.5) * 2 * jitterMs / 1000 : 0);
        }
        return [.. all];
    }

    private static (TimerEngine, ManualClock) Engine(string start = "01:00:00:00", LtcFrameRate rate = LtcFrameRate.Fps25)
    {
        var c = new ManualClock();
        var e = new TimerEngine(c) { Rate = rate };
        e.Start = Timecode.Parse(start, rate);
        return (e, c);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void Running_output_is_continuous(double jitterMs)
    {
        var (e, c) = Engine();
        e.Play();
        var frames = LtcDecoder.DecodeAll(Render(e, c, 3, jitterMs), Sr, LtcFrameRate.Fps25);
        Assert.InRange(frames.Count, 70, 76);
        for (int i = 1; i < frames.Count; i++)
            Assert.Equal(frames[i - 1].Timecode.TotalFrames + 1, frames[i].Timecode.TotalFrames);
        Assert.InRange(frames[0].Timecode.TotalFrames - Timecode.Parse("01:00:00:00", LtcFrameRate.Fps25).TotalFrames, 0, 3);
    }

    [Fact]
    public void Stopped_is_silent_paused_holds()
    {
        var (e, c) = Engine();
        Assert.All(Render(e, c, 0.5), v => Assert.Equal(0f, v));
        e.Play();
        c.Advance(1);
        e.Pause();
        var frames = LtcDecoder.DecodeAll(Render(e, c, 1), Sr, LtcFrameRate.Fps25);
        Assert.True(frames.Count > 20);
        Assert.All(frames, f => Assert.Equal(frames[0].Timecode, f.Timecode));
    }

    [Fact]
    public void Locate_while_running_jumps_once()
    {
        var (e, c) = Engine();
        e.Play();
        var frames = LtcDecoder.DecodeAll(Render(e, c, 2, each: i => { if (i == 100) e.Locate("10:00:00:00"); }), Sr, LtcFrameRate.Fps25);
        int jumps = 0;
        for (int i = 1; i < frames.Count; i++)
            if (frames[i].Timecode.TotalFrames != frames[i - 1].Timecode.TotalFrames + 1) jumps++;
        Assert.Equal(1, jumps);
        Assert.StartsWith("10:00:00", frames[^1].Timecode.ToString());
    }

    [Fact]
    public void Countdown_remaining_is_reverse_code()
    {
        var c = new ManualClock();
        var e = new TimerEngine(c) { Mode = TimerMode.CountDown, Duration = TimeSpan.FromMinutes(1), LtcSource = LtcSource.Remaining, Rate = LtcFrameRate.Fps30 };
        e.Play();
        var frames = LtcDecoder.DecodeAll(Render(e, c, 2), Sr, LtcFrameRate.Fps30);
        Assert.True(frames.Count > 50);
        Assert.All(frames.Skip(1), f => Assert.Equal(LtcDirection.Reverse, f.Direction));
        Assert.True(frames[^1].Timecode.TotalFrames < frames[1].Timecode.TotalFrames);
    }

    [Fact]
    public void Channel_selection()
    {
        var (e, c) = Engine();
        e.Play();
        var o = new LtcOutput(e, Sr) { Channel = 2 };
        var buf = new float[960 * 2];
        o.Render(buf, 2);
        for (int i = 0; i < 960; i++) Assert.Equal(0f, buf[i * 2]);
        Assert.Contains(buf.Where((_, i) => i % 2 == 1), v => v != 0);
    }

    [Fact]
    public void Input_feeds_chase()
    {
        var c = new ManualClock();
        var e = new TimerEngine(c) { Mode = TimerMode.Chase, Rate = LtcFrameRate.Fps25 };
        var input = new LtcInput(e, Sr);
        float[] signal = LtcGenerator.Render(new LtcFrame(Timecode.Parse("05:00:00:00", LtcFrameRate.Fps25)), 25, Sr);
        for (int i = 0; i + 480 <= signal.Length; i += 480)
        {
            input.Process(signal.AsSpan(i, 480), 1);
            c.Advance(0.01);
        }
        var s = e.GetStatus();
        Assert.Equal(ChaseStatus.Locked, s.Chase);
        Assert.NotNull(s.ChaseInput);
        Assert.StartsWith("05:00:00:2", s.ChaseInput.Value.ToString());
        Assert.StartsWith("05:00:0", s.Display);
    }
}
