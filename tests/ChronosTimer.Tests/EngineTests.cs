using LinearTimecode;

namespace ChronosTimer.Tests;

public class TimeInputTests
{
    [Theory]
    [InlineData("01:00:00:00", 3600)]
    [InlineData("1:30:00", 5400)]
    [InlineData("5:00", 300)]
    [InlineData("4:59.5", 299.5)]
    [InlineData("90", 90)]
    [InlineData("1h30m", 5400)]
    [InlineData("5m", 300)]
    [InlineData("45s", 45)]
    [InlineData("250ms", 0.25)]
    [InlineData("00:00:01:12", 1.48)]
    public void Parses_seconds(string text, double seconds) =>
        Assert.Equal(seconds, TimeInput.Parse(text).ToSeconds(LtcFrameRate.Fps25), 6);

    [Fact]
    public void Relative_values_carry_sign()
    {
        var t = TimeInput.Parse("-10s");
        Assert.True(t.Relative);
        Assert.Equal(-10, t.ToSeconds(LtcFrameRate.Fps25), 6);
        Assert.Equal(-250, t.ToFrames(LtcFrameRate.Fps25));
        Assert.Equal(12, TimeInput.Parse("+12f").ToFrames(LtcFrameRate.Fps25));
    }

    [Fact]
    public void Timecode_labels()
    {
        Assert.Equal("01:00:00:00", TimeInput.Parse("1:00:00").ToTimecode(LtcFrameRate.Fps25).ToString());
        Assert.Equal("00:01:30:00", TimeInput.Parse("90").ToTimecode(LtcFrameRate.Fps25).ToString());
        Assert.Equal("10:00:00;00", TimeInput.Parse("10:00:00;00").ToTimecode(LtcFrameRate.Fps29_97).ToString());
        Assert.Equal("00:01:00;02", TimeInput.Parse("00:01:00").ToTimecode(LtcFrameRate.Fps29_97Drop).ToString());
        Assert.Throws<FormatException>(() => TimeInput.Parse("00:01:00;00").ToTimecode(LtcFrameRate.Fps29_97Drop));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1:2:3:4:5")]
    [InlineData("5x")]
    public void Rejects_garbage(string text) => Assert.False(TimeInput.TryParse(text, out _));
}

public class EngineTests
{
    private static (TimerEngine Engine, ManualClock Clock) Make()
    {
        var clock = new ManualClock();
        var e = new TimerEngine(clock) { WallClock = () => new DateTimeOffset(2026, 9, 27, 14, 30, 0, TimeSpan.FromHours(-7)) };
        return (e, clock);
    }

    [Fact]
    public void Timecode_mode_counts_from_start()
    {
        var (e, c) = Make();
        e.Rate = LtcFrameRate.Fps25;
        e.Start = Timecode.Parse("01:00:00:00", LtcFrameRate.Fps25);
        Assert.Equal("01:00:00:00", e.GetStatus().Display);
        e.Play();
        c.Advance(1.5);
        Assert.Equal("01:00:01:12", e.GetStatus().Display);
        e.Pause();
        c.Advance(10);
        Assert.Equal("01:00:01:12", e.GetStatus().Display);
        e.Play();
        c.Advance(0.5);
        Assert.Equal("01:00:02:00", e.GetStatus().Display);
    }

    [Fact]
    public void Locate_nudge_reset()
    {
        var (e, c) = Make();
        e.Locate("10:00:00:00");
        Assert.Equal("10:00:00:00", e.GetStatus().Display);
        e.Nudge(-1);
        Assert.Equal("09:59:59:24", e.GetStatus().Display);
        e.Nudge(TimeInput.Parse("+1s"));
        Assert.Equal("10:00:00:24", e.GetStatus().Display);
        e.Reset();
        Assert.Equal("01:00:00:00", e.GetStatus().Display);
        e.Locate("00:30:00:00"); // before the start
        Assert.Equal("00:30:00:00", e.GetStatus().Display);
    }

    [Fact]
    public void Drop_frame_counts_real_time()
    {
        var (e, c) = Make();
        e.Rate = LtcFrameRate.Fps29_97Drop;
        e.Start = Timecode.Parse("00:00:00;00", LtcFrameRate.Fps29_97Drop);
        e.Play();
        c.Advance(600); // ten real minutes
        Assert.Equal("00:10:00;00", e.GetStatus().Display);
    }

    [Fact]
    public void Countdown_display_and_phases()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountDown;
        e.Duration = TimeSpan.FromMinutes(5);
        e.Warning = TimeSpan.FromMinutes(1);
        e.Critical = TimeSpan.FromSeconds(10);
        Assert.Equal("5:00", e.GetStatus().Display);
        e.Play();
        c.Advance(0.5);
        Assert.Equal("5:00", e.GetStatus().Display); // rounds up
        c.Advance(0.5);
        Assert.Equal("4:59", e.GetStatus().Display);
        c.Advance(239);
        Assert.Equal(TimerPhase.Warning, e.GetStatus().Phase);
        c.Advance(55);
        Assert.Equal(TimerPhase.Critical, e.GetStatus().Phase);
        c.Advance(7);
        var s = e.GetStatus();
        Assert.Equal(TimerPhase.Overrun, s.Phase);
        Assert.Equal("+0:02", s.Display);
    }

    [Fact]
    public void Countdown_stop_at_end()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountDown;
        e.Duration = TimeSpan.FromSeconds(10);
        e.EndAction = EndAction.Stop;
        var ended = new ManualResetEventSlim();
        e.Ended += _ => ended.Set();
        e.Play();
        c.Advance(12);
        var s = e.GetStatus();
        Assert.Equal(TransportState.Stopped, s.State);
        Assert.Equal("0:00", s.Display);
        Assert.Equal(TimerPhase.Ended, s.Phase);
        Assert.True(ended.Wait(2000));
        e.Play(); // starts over
        c.Advance(1);
        Assert.Equal("0:09", e.GetStatus().Display);
    }

    [Fact]
    public void Countdown_loop()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountDown;
        e.Duration = TimeSpan.FromSeconds(10);
        e.EndAction = EndAction.Loop;
        e.Play();
        c.Advance(13);
        var s = e.GetStatus();
        Assert.Equal(TransportState.Running, s.State);
        Assert.Equal("0:07", s.Display);
    }

    [Fact]
    public void Add_time_and_locate_in_countdown()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountDown;
        e.Duration = TimeSpan.FromMinutes(5);
        e.AddTime(TimeSpan.FromMinutes(1));
        Assert.Equal("6:00", e.GetStatus().Display);
        e.Locate("2:30");
        Assert.Equal("2:30", e.GetStatus().Display);
    }

    [Fact]
    public void Count_up_with_speed_and_reverse()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountUp;
        e.Speed = 2;
        e.Play();
        c.Advance(30);
        Assert.Equal("1:00", e.GetStatus().Display);
        e.Reverse = true;
        c.Advance(10);
        Assert.Equal("0:40", e.GetStatus().Display);
    }

    [Fact]
    public void Time_of_day_follows_wall_clock_plus_offset()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.TimeOfDay;
        e.Rate = LtcFrameRate.Fps25;
        Assert.Equal("14:30:00:00", e.GetStatus().Display);
        c.Advance(2); // the wall clock callback is fixed, but engine time maps onto it
        Assert.Equal("14:30:00:00", e.GetStatus().Display);
        e.TimeOfDayOffset = TimeSpan.FromHours(1);
        Assert.Equal("15:30:00:00", e.GetStatus().Display);
        e.Locate("09:00:00:00");
        Assert.Equal("09:00:00:00", e.GetStatus().Display);
        e.Jam();
        Assert.Equal("14:30:00:00", e.GetStatus().Display);
    }

    [Fact]
    public void Reverse_continue_resumes_below_zero()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.CountDown;
        e.Duration = TimeSpan.FromSeconds(10);
        e.EndAction = EndAction.Continue;
        e.Reverse = true;
        e.Play();
        c.Advance(3);
        e.Pause();
        Assert.Equal("0:13", e.GetStatus().Display);
        e.Play(); // resumes, doesn't jump back to the end
        Assert.Equal("0:13", e.GetStatus().Display);
    }

    [Fact]
    public void Switching_to_time_of_day_while_paused_holds_the_time()
    {
        var (e, c) = Make();
        e.Rate = LtcFrameRate.Fps25;
        e.Mode = TimerMode.CountUp;
        e.Play();
        c.Advance(5);
        e.Pause();
        e.Mode = TimerMode.TimeOfDay;
        Assert.Equal("14:30:00:00", e.GetStatus().Display);
    }

    [Fact]
    public void Audio_clock_heard_time_matches_now()
    {
        var ac = new AudioClock(5, 48_000);
        ac.OnCallback(renderedSamples: 960, count: 480, latencySamples: 960);
        // Right at the callback the sample being heard is 960 samples behind the rendered count: sample 0.
        Assert.Equal(5.0, ac.HeardTime(0), 9);
        Assert.Equal(5.0 + 960 / 48_000.0, ac.HeardTime(960), 9);
        Assert.InRange(ac.Now, 5.0, 5.0 + 2 * 480 / 48_000.0);
    }

    [Fact]
    public void Mode_switch_keeps_transport()
    {
        var (e, c) = Make();
        e.Play();
        e.Mode = TimerMode.CountUp;
        Assert.Equal(TransportState.Running, e.GetStatus().State);
        e.Mode = TimerMode.Chase;
        var s = e.GetStatus();
        Assert.Equal(ChaseStatus.NoSignal, s.Chase);
        Assert.StartsWith("--", s.Display);
    }

    [Fact]
    public void Chase_follows_and_freewheels()
    {
        var (e, c) = Make();
        e.Mode = TimerMode.Chase;
        e.FreewheelFrames = 10;
        e.Play();
        e.OnChaseFrame(Timecode.Parse("10:00:00:00", LtcFrameRate.Fps25), c.Now, 1);
        c.Advance(0.02);
        Assert.Equal(ChaseStatus.Locked, e.GetStatus().Chase);
        Assert.Equal("10:00:00:00", e.GetStatus().Display);
        c.Advance(0.2); // no more code: freewheel
        var s = e.GetStatus();
        Assert.Equal(ChaseStatus.Freewheel, s.Chase);
        Assert.Equal("10:00:00:05", s.Display);
        c.Advance(1);
        Assert.Equal(ChaseStatus.NoSignal, e.GetStatus().Chase);
        Assert.False(e.OutputAt(c.Now).Active);
    }

    [Fact]
    public void User_bits_modes()
    {
        var (e, _) = Make();
        var tc = Timecode.Parse("10:00:00:00", LtcFrameRate.Fps25);
        var wall = new DateTimeOffset(2026, 9, 27, 10, 0, 0, TimeSpan.FromHours(-7));
        e.UserBitsMode = UserBitsMode.Text;
        e.UserText = "REEL";
        Assert.Equal("REEL", e.BuildFrame(tc, wall).UserBits.ToText());
        e.UserBitsMode = UserBitsMode.Date;
        var f = e.BuildFrame(tc, wall);
        Assert.Equal(BinaryGroupFlags.DateTimeZone, f.BinaryGroupFlags);
        e.Mode = TimerMode.TimeOfDay;
        Assert.Equal(BinaryGroupFlags.ClockTimeDateTimeZone, e.BuildFrame(tc, wall).BinaryGroupFlags);
    }
}
