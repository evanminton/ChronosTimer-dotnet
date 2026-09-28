using System.Net;
using System.Net.Sockets;
using ChronosTimer.Link;
using ChronosTimer.Show;

namespace ChronosTimer.Tests;

public class ShowScheduleTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 3, 19, 0, 0, TimeSpan.Zero);
    private static DateTimeOffset At(double seconds) => T0.AddSeconds(seconds);

    [Fact]
    public void Idle_without_a_start()
    {
        var s = new ShowSchedule();
        Assert.Equal(ShowPhase.Idle, s.Evaluate(T0).Phase);
        Assert.Throws<InvalidOperationException>(() => s.Hold(T0));
    }

    [Fact]
    public void Waits_runs_and_ends_on_schedule()
    {
        var s = new ShowSchedule();
        s.Set(At(60), At(660));

        var w = s.Evaluate(T0);
        Assert.Equal(ShowPhase.Waiting, w.Phase);
        Assert.Equal(TimeSpan.FromSeconds(60), w.UntilStart);
        Assert.Equal(TimeSpan.FromMinutes(10), w.Length);
        Assert.Null(w.Remaining);

        var r = s.Evaluate(At(120));
        Assert.Equal(ShowPhase.Running, r.Phase);
        Assert.Equal(TimeSpan.FromSeconds(540), r.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(60), r.Elapsed);

        var o = s.Evaluate(At(700));
        Assert.Equal(ShowPhase.Over, o.Phase);
        Assert.Equal(TimeSpan.FromSeconds(-40), o.Remaining);
    }

    [Fact]
    public void End_must_follow_start()
    {
        var s = new ShowSchedule();
        Assert.Throws<ArgumentException>(() => s.Set(At(60), At(60)));
        Assert.Throws<ArgumentException>(() => s.Set(null, At(60)));
    }

    [Fact]
    public void Late_artist_hold_delays_start_and_end_by_the_same_amount()
    {
        var s = new ShowSchedule();
        s.Set(At(60), At(660)); // 10 minutes
        s.Hold(At(30));          // held before the start

        var h = s.Evaluate(At(90)); // 30 s past the scheduled start, still held
        Assert.Equal(ShowPhase.Holding, h.Phase);
        Assert.False(h.Started);
        Assert.Equal(TimeSpan.FromSeconds(60), h.HeldFor);
        Assert.Equal(At(90), h.EffectiveStart);   // projected: start now
        Assert.Equal(At(690), h.EffectiveEnd);

        s.Release(At(150)); // artist arrives 90 s late
        var r = s.Evaluate(At(150));
        Assert.Equal(ShowPhase.Running, r.Phase);
        Assert.Equal(At(150), r.EffectiveStart);
        Assert.Equal(At(750), r.EffectiveEnd);
        Assert.Equal(TimeSpan.FromSeconds(90), r.Delay);
        Assert.Equal(TimeSpan.FromMinutes(10), r.Remaining); // full length kept
        Assert.Equal(TimeSpan.Zero, r.Elapsed);
    }

    [Fact]
    public void Hold_released_before_the_start_changes_nothing()
    {
        var s = new ShowSchedule();
        s.Set(At(60), At(660));
        s.Hold(At(10));
        s.Release(At(40));
        var w = s.Evaluate(At(40));
        Assert.Equal(ShowPhase.Waiting, w.Phase);
        Assert.Equal(At(60), w.EffectiveStart);
        Assert.Equal(At(660), w.EffectiveEnd);
        Assert.Equal(TimeSpan.Zero, w.Delay);
    }

    [Fact]
    public void Hold_while_running_pauses_the_count_and_moves_the_end()
    {
        var s = new ShowSchedule();
        s.Set(At(0), At(600));
        s.Hold(At(100));
        var h1 = s.Evaluate(At(110));
        var h2 = s.Evaluate(At(160));
        Assert.Equal(ShowPhase.Holding, h1.Phase);
        Assert.True(h1.Started);
        Assert.Equal(TimeSpan.FromSeconds(500), h1.Remaining);
        Assert.Equal(h1.Remaining, h2.Remaining); // frozen while held
        Assert.Equal(TimeSpan.FromSeconds(100), h2.Elapsed);

        s.Release(At(160));
        var r = s.Evaluate(At(170));
        Assert.Equal(ShowPhase.Running, r.Phase);
        Assert.Equal(At(660), r.EffectiveEnd);
        Assert.Equal(At(0), r.EffectiveStart);
        Assert.Equal(TimeSpan.FromSeconds(490), r.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(110), r.Elapsed);
    }

    [Fact]
    public void Show_without_an_end_counts_up()
    {
        var s = new ShowSchedule();
        s.Set(At(10), null);
        var r = s.Evaluate(At(70));
        Assert.Equal(ShowPhase.Running, r.Phase);
        Assert.Null(r.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(60), r.Elapsed);
    }

    [Fact]
    public void Parses_show_times()
    {
        var now = new DateTimeOffset(new DateTime(2026, 10, 3, 15, 0, 0), TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 3, 15, 0, 0)));
        var start = ShowTime.Parse("19:30", now)!.Value;
        Assert.Equal(new DateTime(2026, 10, 3, 19, 30, 0), start.LocalDateTime);
        Assert.Equal(new DateTime(2026, 10, 3, 19, 30, 0), ShowTime.Parse("7:30pm", now)!.Value.LocalDateTime);

        // an end time earlier than the start is the next day
        var end = ShowTime.Parse("01:00", now, start)!.Value;
        Assert.Equal(new DateTime(2026, 10, 4, 1, 0, 0), end.LocalDateTime);

        Assert.Equal(start.AddMinutes(90), ShowTime.Parse("+1h30m", now, start));
        Assert.Equal(now.AddMinutes(5), ShowTime.Parse("+5m", now));
        Assert.Null(ShowTime.Parse("none", now));
        Assert.Equal(now, ShowTime.Parse("now", now));

        var iso = ShowTime.Format(start);
        Assert.Equal(start, ShowTime.Parse(iso, now));
        Assert.Equal(start, ShowTime.Parse("2026-10-03 19:30", now));
        Assert.Throws<FormatException>(() => ShowTime.Parse("teatime", now));
    }
}

public class ShowControllerTests
{
    private sealed class Rig : IDisposable
    {
        public readonly ManualClock Clock = new();
        public DateTimeOffset Wall = new(2026, 10, 3, 19, 0, 0, TimeSpan.Zero);
        public readonly TimerHost Host;

        public Rig()
        {
            Host = new TimerHost(engine: new TimerEngine(Clock));
            Host.Show.WallClock = () => Wall;
            Host.Engine.WallClock = () => Wall;
        }

        public void Advance(double seconds)
        {
            Clock.Advance(seconds);
            Wall = Wall.AddSeconds(seconds);
            Host.Show.Tick();
        }

        public void Dispose() => Host.Dispose();
    }

    [Fact]
    public void Scheduled_show_arms_a_countdown_and_starts_itself()
    {
        using var r = new Rig();
        r.Host.Show.Schedule(r.Wall.AddSeconds(10), r.Wall.AddSeconds(70));

        Assert.Equal(TimerMode.CountDown, r.Host.Engine.Mode);
        Assert.Equal(TimeSpan.FromSeconds(60), r.Host.Engine.Duration);
        Assert.Equal(TransportState.Stopped, r.Host.Status.State);
        Assert.Equal("−0:10", r.Host.Show.View.Display);
        Assert.Contains("Starts", r.Host.Show.View.Detail);

        r.Advance(10);
        Assert.Equal(TransportState.Running, r.Host.Status.State);
        Assert.Equal("1:00", r.Host.Status.Display);
        r.Advance(15);
        Assert.Equal("0:45", r.Host.Show.View.Display);
    }

    [Fact]
    public void Hold_button_delays_the_start_and_keeps_the_length()
    {
        using var r = new Rig();
        r.Host.Show.Schedule(r.Wall.AddSeconds(10), r.Wall.AddSeconds(70));
        Assert.True(r.Host.Execute("show hold").Ok);
        r.Advance(30); // 20 s past the start, still held
        Assert.Equal(TransportState.Stopped, r.Host.Status.State);
        Assert.Equal("HOLD", r.Host.Show.View.Display);

        Assert.True(r.Host.Execute("show release").Ok);
        Assert.Equal(TransportState.Running, r.Host.Status.State);
        Assert.Equal("1:00", r.Host.Status.Display); // full minute from the release
        Assert.Equal(r.Wall.AddSeconds(60), r.Host.Show.State.EffectiveEnd);

        // hold while running pauses; release continues from the same time
        r.Advance(20);
        Assert.True(r.Host.Execute("show toggle").Ok);
        Assert.Equal(TransportState.Paused, r.Host.Status.State);
        r.Advance(30);
        Assert.Equal("0:40", r.Host.Status.Display);
        Assert.True(r.Host.Execute("show toggle").Ok);
        Assert.Equal(TransportState.Running, r.Host.Status.State);
        r.Advance(10);
        Assert.Equal("0:30", r.Host.Status.Display);
        Assert.Equal(r.Wall.AddSeconds(30), r.Host.Show.State.EffectiveEnd);
    }

    [Fact]
    public void Show_settings_and_commands()
    {
        using var r = new Rig();
        Assert.True(r.Host.Execute("show start +10m").Ok);
        Assert.True(r.Host.Execute("show end +1h").Ok);
        Assert.Equal(r.Wall.AddMinutes(10), r.Host.Show.ScheduledStart);
        Assert.Equal(r.Wall.AddMinutes(70), r.Host.Show.ScheduledEnd);
        Assert.Equal(TimeSpan.FromHours(1), r.Host.Engine.Duration);
        Assert.False(r.Host.Execute("set show-end " + ShowTime.Format(r.Wall)).Ok); // before the start
        Assert.True(r.Host.Execute("show clear").Ok);
        Assert.Equal("none", r.Host.FindSetting("show-start")!.Value);
        Assert.False(r.Host.Execute("show hold").Ok);
        Assert.False(r.Host.Execute("show end 20:00").Ok); // no start yet
    }

    [Fact]
    public void Cue_light_by_command_and_automatically_from_the_countdown()
    {
        using var r = new Rig();
        Assert.True(r.Host.Execute("cue standby").Ok);
        Assert.Equal(CueLight.Standby, r.Host.Show.Cue);
        Assert.Equal(CueLight.Standby, r.Host.Show.View.Cue);
        Assert.False(r.Host.Execute("cue purple").Ok);
        Assert.False(r.Host.Execute("cue ack").Ok); // not a follower

        r.Host.Execute("mode count-down");
        r.Host.Execute("duration 90s");
        r.Host.Execute("warning 30s");
        r.Host.Execute("cue go");
        r.Host.Execute("play");
        r.Advance(30);
        Assert.Equal(CueLight.Go, r.Host.Show.Cue);
        r.Advance(31);
        Assert.Equal(CueLight.Warning, r.Host.Show.Cue);
        r.Advance(30);
        Assert.Equal(CueLight.End, r.Host.Show.Cue);

        // standby / stop are never overridden
        r.Host.Execute("cue stop");
        r.Host.Execute("restart");
        r.Advance(61);
        Assert.Equal(CueLight.Stop, r.Host.Show.Cue);

        r.Host.Execute("cue-auto off");
        r.Host.Execute("cue go");
        r.Host.Execute("restart");
        r.Advance(95);
        Assert.Equal(CueLight.Go, r.Host.Show.Cue);
    }

    [Fact]
    public void Messages_need_a_link()
    {
        using var r = new Rig();
        Assert.False(r.Host.Execute("message hello").Ok);
        Assert.Equal("ChronosTimer01", r.Host.Show.NodeName);
    }
}

public class LinkTests
{
    private static async Task Until(Func<bool> condition, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(10)) Assert.Fail("Timed out waiting for " + what);
            await Task.Delay(20);
        }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public void Protocol_round_trips()
    {
        string line = LinkProtocol.Encode("msg", ("from", "ChronosTimer02"), ("text", "places \"please\"\nnow"));
        Assert.EndsWith("\n", line);
        var p = LinkProtocol.Decode(line.TrimEnd('\n'));
        Assert.NotNull(p);
        Assert.Equal("msg", p.Type);
        Assert.Equal("places \"please\"\nnow", p["text"]);
        Assert.Equal("", p["missing"]);
        Assert.Null(LinkProtocol.Decode("not json"));
        Assert.Null(LinkProtocol.Decode("[1]"));
        Assert.Null(LinkProtocol.Decode("{\"x\":1}"));

        Assert.Equal("ChronosTimer07", LinkProtocol.NodeName(7));
        Assert.Equal(7, LinkProtocol.NodeNumber("ChronosTimer07"));
        Assert.Null(LinkProtocol.NodeNumber("FOH"));
        Assert.Null(LinkProtocol.NodeNumber("ChronosTimer"));

        Assert.True(LinkProtocol.TryParseBeacon(LinkProtocol.Beacon(8490, "ChronosTimer01"), out int port, out string name));
        Assert.Equal(8490, port);
        Assert.Equal("ChronosTimer01", name);
        Assert.False(LinkProtocol.TryParseBeacon("hello 1 2", out _, out _));

        Assert.Equal(("169.254.1.2", 8490), LinkProtocol.ParseEndpoint("169.254.1.2", 8490));
        Assert.Equal(("stage-pc", 9000), LinkProtocol.ParseEndpoint("stage-pc:9000", 8490));
        Assert.Throws<FormatException>(() => LinkProtocol.ParseEndpoint("", 8490));

        var v = new ShowView { Source = "ChronosTimer01", Display = "4:59", Detail = "Starts 19:30", Phase = TimerPhase.Warning, State = TransportState.Running, ShowPhase = ShowPhase.Running, Cue = CueLight.Standby, Progress = 0.25, Remaining = TimeSpan.FromSeconds(299.5) };
        var back = ShowView.FromFields(LinkProtocol.Decode(LinkProtocol.Encode("state", v.ToFields()).TrimEnd())!.Fields);
        Assert.Equal(v, back);
    }

    [Fact]
    public void Apipa_binding()
    {
        Assert.Equal("apipa", Control.NetworkBinding.Normalize("APIPA"));
        Assert.Equal("apipa", Control.NetworkBinding.Normalize("link-local"));
        Assert.True(Control.NetworkBinding.IsApipa(IPAddress.Parse("169.254.12.34")));
        Assert.False(Control.NetworkBinding.IsApipa(IPAddress.Parse("192.168.1.2")));
        Assert.Contains(Control.NetworkBinding.Options(), o => o.Value == "apipa");
    }

    [Fact]
    public async Task Master_names_followers_and_relays_state_messages_and_acks()
    {
        using var master = new LinkMaster(IPAddress.Loopback, 0, "ChronosTimer01", "secret");
        var fromFollowers = new List<string>();
        var acks = new List<string>();
        master.MessageReceived += (from, to, text) => { lock (fromFollowers) fromFollowers.Add($"{from}>{to}:{text}"); };
        master.Acknowledged += n => { lock (acks) acks.Add(n); };
        master.Start();
        string addr = "127.0.0.1:" + master.Port;

        using var a = new LinkFollower(addr, 8490, "auto", "", "secret");
        using var b = new LinkFollower(addr, 8490, "auto", "ChronosTimer05", "secret");
        using var c = new LinkFollower(addr, 8490, "FOH", "", "secret");
        using var bad = new LinkFollower(addr, 8490, "auto", "", "wrong");
        var aViews = new List<ShowView>();
        var bMsgs = new List<ShowMessage>();
        a.StateReceived += v => { lock (aViews) aViews.Add(v); };
        b.MessageReceived += m => { lock (bMsgs) bMsgs.Add(m); };
        a.Start();
        await Until(() => a.Connected, "a to connect");
        b.Start();
        c.Start();
        bad.Start();
        await Until(() => b.Connected && c.Connected, "b and c to connect");
        await Until(() => bad.Status.StartsWith("refused"), "the wrong key to be refused");

        Assert.Equal("ChronosTimer02", a.AssignedName);
        Assert.Equal("ChronosTimer05", b.AssignedName); // asked for its old number
        Assert.Equal("FOH", c.AssignedName);
        Assert.Equal("ChronosTimer01", a.MasterName);
        Assert.Equal(3, master.Followers.Count);
        Assert.False(bad.Connected);

        master.SendState(new ShowView { Source = "ChronosTimer01", Display = "12:34", Cue = CueLight.Go });
        await Until(() => { lock (aViews) return aViews.Any(v => v.Display == "12:34" && v.Cue == CueLight.Go); }, "the view");

        Assert.True(master.SendMessage(new ShowMessage(DateTimeOffset.Now, "ChronosTimer01", "ChronosTimer05", "standby")));
        Assert.False(master.SendMessage(new ShowMessage(DateTimeOffset.Now, "ChronosTimer01", "Nobody", "hi")));
        await Until(() => { lock (bMsgs) return bMsgs.Any(m => m.Text == "standby" && m.From == "ChronosTimer01"); }, "the message");

        Assert.True(a.SendMessage("ready", null));
        Assert.True(c.Acknowledge());
        await Until(() => { lock (fromFollowers) return fromFollowers.Contains("ChronosTimer02>:ready"); }, "the follower message");
        await Until(() => { lock (acks) return acks.Contains("FOH"); }, "the ack");
        Assert.Contains(master.Followers, f => f.Name == "FOH" && f.Acknowledged);
        master.ResetAcks();
        Assert.DoesNotContain(master.Followers, f => f.Acknowledged);

        c.Dispose();
        await Until(() => master.Followers.Count == 2, "c to leave");
    }

    [Fact]
    public async Task Follower_finds_the_master_by_its_beacon()
    {
        int beacon;
        using (var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0))) beacon = ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
        using var master = new LinkMaster(IPAddress.Loopback, 0, "ChronosTimer01", "", [IPAddress.Loopback], beacon);
        master.Start();
        using var f = new LinkFollower("auto", 8490, "auto", "", "", beacon);
        f.Start();
        await Until(() => f.Connected, "the follower to find the master");
        Assert.Equal("ChronosTimer01", f.MasterName);
        Assert.Equal("ChronosTimer02", f.AssignedName);
    }

    [Fact]
    public async Task Linked_hosts_mirror_the_masters_time_cue_and_messages()
    {
        int port = FreePort();
        using var m = new TimerHost(engine: new TimerEngine(new ManualClock()));
        using var f = new TimerHost(engine: new TimerEngine(new ManualClock()));
        foreach (var h in new[] { m, f })
        {
            h.Apply([new("http", "off"), new("osc", "off"), new("link-bind", "localhost"), new("link-port", port.ToString())]);
        }
        m.Apply([new("link-role", "master"), new("mode", "count-down"), new("duration", "7m")]);
        f.Apply([new("link-role", "follower"), new("link-master", "127.0.0.1")]);
        m.Start();
        f.Start();

        await Until(() => f.Show.View.Live && f.Show.View.Display == "7:00", "the follower to mirror the master");
        Assert.Equal("ChronosTimer01", f.Show.View.Source);
        Assert.Equal("ChronosTimer02", f.Show.NodeName);
        Assert.Equal("ChronosTimer02", f.FindSetting("link-node")!.Value);

        Assert.True(m.Execute("cue standby").Ok);
        await Until(() => f.Show.Cue == CueLight.Standby, "the cue light");
        Assert.False(f.Execute("cue go").Ok); // only the master sets it
        Assert.True(f.Execute("cue ack").Ok);
        await Until(() => m.Show.Followers.Any(x => x.Acknowledged), "the acknowledgement");

        Assert.True(f.Execute("message on headset please").Ok);
        await Until(() => m.Show.Messages.Any(x => x.From == "ChronosTimer02" && x.Text == "on headset please"), "the message at the master");
        Assert.True(m.Execute("message @ChronosTimer02 thanks").Ok);
        await Until(() => f.Show.Messages.Any(x => x.From == "ChronosTimer01" && x.Text == "thanks"), "the reply");
        Assert.False(m.Execute("message @ChronosTimer09 hi").Ok);
        Assert.Contains("ChronosTimer02", m.Execute("link").Message);
    }
}
