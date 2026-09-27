using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using ChronosTimer.Control;
using ChronosTimer.Settings;

namespace ChronosTimer.Tests;

public class CommandTests
{
    private static TimerHost Host()
    {
        var h = new TimerHost(engine: new TimerEngine(new ManualClock()));
        return h;
    }

    [Fact]
    public void Transport_and_settings_by_text()
    {
        using var h = Host();
        Assert.True(h.Execute("mode count-down").Ok);
        Assert.True(h.Execute("duration 10m").Ok);            // setting without 'set'
        Assert.True(h.Execute("set end-action stop").Ok);
        Assert.Equal("10:00", h.Status.Display);
        Assert.True(h.Execute("add +1m").Ok);
        Assert.Equal("11:00", h.Status.Display);
        Assert.True(h.Execute("play").Ok);
        Assert.Equal(TransportState.Running, h.Status.State);
        Assert.True(h.Execute("toggle").Ok);
        Assert.Equal(TransportState.Paused, h.Status.State);
        Assert.True(h.Execute("locate 1:30").Ok);
        Assert.Equal("1:30", h.Status.Display);
        Assert.True(h.Execute("mode").Ok); // next mode
        Assert.Equal(TimerMode.TimeOfDay, h.Engine.Mode);
    }

    [Fact]
    public void Errors_are_readable_and_never_throw()
    {
        using var h = Host();
        var r = h.Execute("locate banana");
        Assert.False(r.Ok);
        Assert.Contains("banana", r.Message);
        Assert.False(h.Execute("set rate 26").Ok);
        Assert.False(h.Execute("frobnicate").Ok);
        Assert.False(h.Execute("").Ok);
        Assert.False(h.Execute("set speed 0").Ok);
    }

    [Fact]
    public void Every_setting_is_described_and_round_trips()
    {
        using var h = Host();
        var skip = new HashSet<string> { "http", "http-port", "http-local-only", "osc", "osc-port", "output", "input", "output-device", "input-device" };
        foreach (var s in h.Settings)
        {
            Assert.False(string.IsNullOrWhiteSpace(s.Description), s.Name);
            Assert.False(string.IsNullOrWhiteSpace(s.Accepts), s.Name);
            if (skip.Contains(s.Name)) continue;
            string v = s.Value;
            s.Set(v);
            Assert.Equal(v, s.Value);
            if (s.Kind == SettingKind.Choice)
                foreach (var o in s.Options) { s.Set(o.Value); }
            s.Set(v);
        }
        Assert.Contains("duration", h.SettingsText());
        Assert.Contains("locate", h.Commands.Help());
    }

    [Fact]
    public void Save_and_load()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chronos-{Guid.NewGuid():N}.json");
        try
        {
            using (var a = Host())
            {
                a.Execute("mode count-down");
                a.Execute("duration 42s");
                a.Execute("rate 29.97df");
                a.Execute("user-text ABCD");
                a.Save(path);
            }
            using var b = Host();
            Assert.Empty(b.Load(path));
            Assert.Equal(TimerMode.CountDown, b.Engine.Mode);
            Assert.Equal(TimeSpan.FromSeconds(42), b.Engine.Duration);
            Assert.Equal(LinearTimecode.LtcFrameRate.Fps29_97Drop, b.Engine.Rate);
            Assert.Equal("ABCD", b.Engine.UserText);
        }
        finally { File.Delete(path); }
    }
}

public class OscTests
{
    [Fact]
    public void Round_trip()
    {
        byte[] data = Osc.Encode("/chronos/locate", "01:00:00:00", 1, 2.5f, true);
        var m = Assert.Single(Osc.Decode(data));
        Assert.Equal("/chronos/locate", m.Address);
        Assert.Equal(new object?[] { "01:00:00:00", 1, 2.5f, true }, m.Args);
        var bundle = Osc.EncodeBundle([new OscMessage("/a", [1]), new OscMessage("/b", ["x"])]);
        Assert.Equal(2, Osc.Decode(bundle).Count);
    }

    [Fact]
    public void Maps_to_commands()
    {
        var cs = new TimerHost().Commands;
        Assert.Equal("play", OscControlServer.ToCommand(new OscMessage("/chronos/play", [1f]), cs));
        Assert.Null(OscControlServer.ToCommand(new OscMessage("/chronos/play", [0f]), cs)); // button release
        Assert.Equal("locate 01:00:00:00", OscControlServer.ToCommand(new OscMessage("/chronos/locate", ["01:00:00:00"]), cs));
        Assert.Equal("set duration 300", OscControlServer.ToCommand(new OscMessage("/chronos/set/duration", [300]), cs));
        Assert.Equal("nudge +1", OscControlServer.ToCommand(new OscMessage("/chronos/nudge/+1", []), cs));
        Assert.Equal("output on", OscControlServer.ToCommand(new OscMessage("/output", [true]), cs));
        Assert.Equal("mode count-down", OscControlServer.ToCommand(new OscMessage("/chronos/cmd", ["mode count-down"]), cs));
    }

    [Fact]
    public async Task Udp_end_to_end()
    {
        int port = FreeUdpPort();
        using var h = new TimerHost();
        h.Apply([new("http", "off"), new("osc-port", port.ToString())]);
        h.Set("osc", "on");
        using var udp = new UdpClient(0);
        byte[] msg = Osc.Encode("/chronos/mode", "count-up");
        await udp.SendAsync(msg, msg.Length, new IPEndPoint(IPAddress.Loopback, port));
        using var cts = new CancellationTokenSource(3000);
        var reply = await udp.ReceiveAsync(cts.Token);
        var r = Assert.Single(Osc.Decode(reply.Buffer));
        Assert.Equal("/chronos/result", r.Address);
        Assert.Equal(1, r.Args[0]);
        Assert.Equal(TimerMode.CountUp, h.Engine.Mode);
    }

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(0);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }
}

public class HttpTests
{
    private static int FreeTcpPort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task Api_and_websocket()
    {
        int port = FreeTcpPort();
        using var h = new TimerHost();
        h.Apply([new("osc", "off"), new("http-port", port.ToString()), new("http-local-only", "on")]);
        h.Set("http", "on");
        Assert.NotNull(h.Http);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };

        string page = await http.GetStringAsync("/");
        Assert.Contains("Chronos Timer", page);

        string status = await http.GetStringAsync("/api/status");
        Assert.Contains("\"display\"", status);

        var res = await http.PostAsync("/api/command", new StringContent("mode count-down"));
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal(TimerMode.CountDown, h.Engine.Mode);

        Assert.True((await http.GetAsync("/api/duration/2m")).IsSuccessStatusCode);
        Assert.Equal(TimeSpan.FromMinutes(2), h.Engine.Duration);
        Assert.True((await http.GetAsync("/api/set?name=warning&value=30s")).IsSuccessStatusCode);
        Assert.Equal(TimeSpan.FromSeconds(30), h.Engine.Warning);
        Assert.Equal(HttpStatusCode.BadRequest, (await http.GetAsync("/api/locate/banana")).StatusCode);

        string settings = await http.GetStringAsync("/api/settings");
        Assert.Contains("\"end-action\"", settings);

        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(5000);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws"), cts.Token);
        var buf = new byte[65536];
        var first = await ws.ReceiveAsync(buf, cts.Token);
        Assert.Contains("\"display\"", Encoding.UTF8.GetString(buf, 0, first.Count));
        await ws.SendAsync(Encoding.UTF8.GetBytes("play"), WebSocketMessageType.Text, true, cts.Token);
        bool gotResult = false;
        for (int i = 0; i < 50 && !gotResult; i++)
        {
            var r = await ws.ReceiveAsync(buf, cts.Token);
            gotResult = Encoding.UTF8.GetString(buf, 0, r.Count).Contains("\"ok\":true");
        }
        Assert.True(gotResult);
        Assert.Equal(TransportState.Running, h.Engine.State);
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", cts.Token);
    }

    [Fact]
    public async Task WebSocket_framing_round_trip()
    {
        byte[] payload = Encoding.UTF8.GetBytes(new string('x', 70_000));
        byte[] frame = WebSocketFrame.Encode(1, payload, mask: [1, 2, 3, 4]);
        var reader = new WebSocketFrame.Reader(new MemoryStream(frame));
        var f = await reader.ReadAsync(CancellationToken.None);
        Assert.NotNull(f);
        Assert.Equal(1, f.Value.Opcode);
        Assert.Equal(payload, f.Value.Payload);
    }
}
