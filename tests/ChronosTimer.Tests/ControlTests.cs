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
        var skip = new HashSet<string> { "http", "http-port", "http-bind", "http-token", "http-origins", "osc", "osc-port", "osc-bind", "output", "input", "output-device", "input-device" };
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

    [Fact]
    public void Load_of_a_bad_file_is_an_error_not_a_crash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"chronos-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "this is not json");
            using var h = Host();
            h.Apply([new("http", "off"), new("osc", "off")]);
            var r = h.Execute("load " + path);
            Assert.False(r.Ok);
            File.WriteAllText(path, "[1, 2]");
            Assert.False(h.Execute("load " + path).Ok);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Remotes_cannot_save_or_load_other_files()
    {
        using var h = Host();
        h.Apply([new("http", "off"), new("osc", "off")]);
        string path = Path.Combine(Path.GetTempPath(), $"chronos-{Guid.NewGuid():N}.json");
        var r = h.Execute("save " + path, "http 10.0.0.9");
        Assert.False(r.Ok);
        Assert.False(File.Exists(path));
        Assert.False(h.Execute("load " + path, "osc 10.0.0.9").Ok);
    }

    [Fact]
    public void Bad_osc_feedback_host_is_an_error()
    {
        using var h = Host();
        h.Apply([new("http", "off"), new("osc", "off")]);
        Assert.False(h.Execute("set osc-feedback no-such-host.invalid:9001").Ok);
        Assert.False(h.Execute("set osc-feedback nonsense").Ok);
        Assert.Equal("", h.FindSetting("osc-feedback")!.Value);
    }

    [Fact]
    public void Bind_and_token_settings()
    {
        using var h = Host();
        h.Apply([new("http", "off"), new("osc", "off")]);
        Assert.Equal("localhost", h.FindSetting("http-bind")!.Value);
        Assert.Equal("localhost", h.FindSetting("osc-bind")!.Value);
        Assert.Contains(h.FindSetting("http-bind")!.Options, o => o.Value == "all");
        Assert.True(h.Execute("set http-bind 0.0.0.0").Ok);
        Assert.Equal("all", h.FindSetting("http-bind")!.Value);
        Assert.False(h.Execute("set http-bind no-such-interface-xyz").Ok);
        Assert.True(h.FindSetting("http-token")!.Value.Length >= 16);
        Assert.False(h.Execute("set http-token short").Ok);
        Assert.False(h.Execute("set http-token \"has;semicolon\"").Ok);
        Assert.True(h.Execute("set http-token off").Ok);
        Assert.Equal("", h.FindSetting("http-token")!.Value);
        Assert.Empty(h.Apply([new("http-local-only", "on")])); // old settings files still load
    }

    [Theory]
    [InlineData("48kHz", 48_000)]
    [InlineData("44.1khz", 44_100)]
    [InlineData("48000hz", 48_000)]
    [InlineData("-12dBFS", -12)]
    public void Numbers_with_units(string text, double value) =>
        Assert.Equal(value, Setting.ParseNumber(text, -100_000, 200_000), 6);
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

    public static TheoryData<byte[]> Malformed => new()
    {
        // second string argument runs past the end
        Concat(Osc.Encode("/a", "x", "y").AsSpan(0, 10).ToArray()),
        // blob size near int.MaxValue
        Concat(Osc.Encode("/a", new byte[] { 1 }).AsSpan(0, 8).ToArray(), [0x7F, 0xFF, 0xFF, 0xFE]),
        // bundle element size near int.MaxValue
        Concat(Osc.EncodeBundle([]), [0x7F, 0xFF, 0xFF, 0xF0, 0, 0, 0, 0]),
        // bundle without a time tag
        Concat("#bundle\0"u8.ToArray()),
        // type tags without room for the int
        Concat("/a\0\0,i\0\0"u8.ToArray()),
    };

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];

    [Theory]
    [MemberData(nameof(Malformed))]
    public void Malformed_packets_throw_format_exception(byte[] packet) =>
        Assert.Throws<FormatException>(() => Osc.Decode(packet));

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
        h.Apply([new("osc", "off"), new("http-port", port.ToString()), new("http-bind", "localhost"), new("http-token", "test-token-123")]);
        h.Set("http", "on");
        Assert.NotNull(h.Http);
        Assert.All(h.Http.Urls, u => Assert.EndsWith("?token=test-token-123", u));
        using var anon = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(5) };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/status")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/save/x.json?token=wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/")).StatusCode); // sign-in page

        // a page on another site can't use the API, even with the token
        var cross = new HttpRequestMessage(HttpMethod.Get, "/api/status");
        cross.Headers.Add("Origin", "http://evil.example");
        cross.Headers.Add("X-Chronos-Token", "test-token-123");
        Assert.Equal(HttpStatusCode.Forbidden, (await anon.SendAsync(cross)).StatusCode);

        // signing in with ?token= sets a same-site cookie and redirects
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = anon.BaseAddress };
        var signIn = await browser.GetAsync("/?token=test-token-123");
        Assert.Equal(HttpStatusCode.SeeOther, signIn.StatusCode);
        Assert.Contains(signIn.Headers.GetValues("Set-Cookie"), c => c.Contains("SameSite=Strict"));

        using var http = new HttpClient { BaseAddress = anon.BaseAddress, Timeout = TimeSpan.FromSeconds(5) };
        http.DefaultRequestHeaders.Add("X-Chronos-Token", "test-token-123");
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
        ws.Options.SetRequestHeader("X-Chronos-Token", "test-token-123");
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
