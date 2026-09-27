using System.Net;
using System.Net.Sockets;

namespace ChronosTimer.Control;

/// <summary>
/// OSC over UDP. Every text command is available as <c>/chronos/&lt;command&gt; [args]</c> (the <c>/chronos</c> prefix is
/// optional), settings as <c>/chronos/set/&lt;setting&gt; value</c> or <c>/chronos/&lt;setting&gt; value</c>, any command
/// line as <c>/chronos/cmd "text"</c>. <c>/chronos/status</c> replies to the sender with the status messages.
/// Button controllers that send 1 on press and 0 on release trigger once (the 0 is ignored for commands without arguments).
/// </summary>
public sealed class OscControlServer : IDisposable
{
    public const string Prefix = "/chronos";

    private readonly TimerHost _host;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _targetsGate = new();
    private List<IPEndPoint> _targets = [];
    private readonly Dictionary<string, string> _lastSent = new();
    private DateTime _lastFull;
    private Task? _rx, _tx;

    /// <param name="host">Timer to control.</param>
    /// <param name="port">UDP port (0 = any free port).</param>
    /// <param name="bind">Address to listen on (default <see cref="IPAddress.Any"/>).</param>
    public OscControlServer(TimerHost host, int port, IPAddress? bind = null)
    {
        _host = host;
        bind ??= IPAddress.Any;
        _udp = new UdpClient(bind.AddressFamily);
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(bind, port));
        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        BindAddress = bind;
    }

    public int Port { get; }

    /// <summary>Address the server listens on.</summary>
    public IPAddress BindAddress { get; }

    public void Start()
    {
        _rx = Task.Run(ReceiveLoop);
        _tx = Task.Run(FeedbackLoop);
    }

    /// <summary>Sets feedback targets from "host:port, host:port". Throws <see cref="FormatException"/> for a bad or unresolvable target.</summary>
    public void SetFeedbackTargets(string text)
    {
        var list = ParseTargets(text);
        lock (_targetsGate) { _targets = list; _lastSent.Clear(); }
    }

    /// <summary>Parses and resolves "host:port, host:port". Throws <see cref="FormatException"/> for a bad or unresolvable target.</summary>
    public static List<IPEndPoint> ParseTargets(string text)
    {
        var list = new List<IPEndPoint>();
        foreach (string part in (text ?? "").Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.LastIndexOf(':');
            if (colon <= 0 || !int.TryParse(part[(colon + 1)..], out int port) || port is < 1 or > 65535)
                throw new FormatException($"'{part}' is not host:port.");
            string h = part[..colon].Trim('[', ']');
            IPAddress? ip;
            if (IPAddress.TryParse(h, out var parsed)) ip = parsed;
            else
            {
                try { ip = Dns.GetHostAddresses(h).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); }
                catch (Exception ex) when (ex is SocketException or ArgumentException) { throw new FormatException($"Can't resolve '{h}': {ex.Message}", ex); }
            }
            if (ip is null) throw new FormatException($"Can't resolve '{h}'.");
            list.Add(new IPEndPoint(ip, port));
        }
        return list;
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try { r = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; } // e.g. ICMP port unreachable on Windows

            IReadOnlyList<OscMessage> msgs;
            try { msgs = Osc.Decode(r.Buffer); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException or OverflowException)
            { _host.Log($"[osc {r.RemoteEndPoint}] bad packet: {ex.Message}"); continue; }

            foreach (var m in msgs)
            {
                try { Handle(m, r.RemoteEndPoint); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                { _host.Log($"[osc {r.RemoteEndPoint}] {m.Address}: {ex.Message}"); }
            }
        }
    }

    /// <summary>Translates an OSC message into a text command (null = ignore). Public for tests and custom transports.</summary>
    public static string? ToCommand(OscMessage m, CommandSet commands)
    {
        string addr = m.Address;
        if (addr.StartsWith(Prefix + "/", StringComparison.OrdinalIgnoreCase)) addr = addr[Prefix.Length..];
        string[] parts = addr.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;
        var args = m.Args.Select(Osc.ArgText).Where(a => a.Length > 0).ToList();

        if (parts[0].Equals("cmd", StringComparison.OrdinalIgnoreCase))
            return args.Count > 0 ? string.Join(' ', args) : null;

        if (parts.Length == 1 && commands.Find(parts[0]) is { } info && !info.Usage.Contains('<') && !info.Usage.Contains('['))
        {
            // button semantics: 1 = press, 0 = release
            if (m.Args.Count == 1 && m.Args[0] is int or float or double && Convert.ToDouble(m.Args[0], System.Globalization.CultureInfo.InvariantCulture) == 0) return null;
            if (m.Args.Count == 1 && m.Args[0] is false) return null;
            return parts[0];
        }

        // /chronos/set/duration 300 → set duration 300 ; /chronos/locate 01:00:00:00 ; /chronos/nudge/+1
        var words = new List<string>(parts);
        words.AddRange(args.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a));
        return string.Join(' ', words);
    }

    private void Handle(OscMessage m, IPEndPoint from)
    {
        string addr = m.Address.TrimEnd('/');
        if (addr.Equals(Prefix + "/status", StringComparison.OrdinalIgnoreCase) || addr.Equals("/status", StringComparison.OrdinalIgnoreCase))
        {
            SendStatus([from], full: true);
            return;
        }
        if (addr.Equals(Prefix + "/ping", StringComparison.OrdinalIgnoreCase))
        {
            Send([from], new OscMessage(Prefix + "/pong", []));
            return;
        }
        string? cmd = ToCommand(m, _host.Commands);
        if (cmd is null) return;
        var result = _host.Execute(cmd, "osc " + from);
        Send([from], new OscMessage(Prefix + "/result", [result.Ok ? 1 : 0, result.Message]));
    }

    private async Task FeedbackLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(1 / Math.Max(1, _host.PushRate)), _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            List<IPEndPoint> targets;
            lock (_targetsGate) targets = _targets;
            if (targets.Count == 0) continue;
            bool full = DateTime.UtcNow - _lastFull > TimeSpan.FromSeconds(2);
            if (full) _lastFull = DateTime.UtcNow;
            try { SendStatus(targets, full); }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
        }
    }

    private void SendStatus(IReadOnlyList<IPEndPoint> to, bool full)
    {
        var s = _host.Status;
        var items = new List<(string Key, object Value)>
        {
            ("display", s.Display),
            ("detail", s.Detail),
            ("timecode", s.Timecode.ToString()),
            ("state", Settings.Setting.Token(s.State)),
            ("mode", Settings.Setting.Token(s.Mode)),
            ("phase", Settings.Setting.Token(s.Phase)),
            ("running", s.State == TransportState.Running ? 1 : 0),
            ("elapsed", (float)Math.Round(s.Elapsed.TotalSeconds, 1)),
            ("remaining", (float)Math.Round(s.Remaining?.TotalSeconds ?? 0, 1)),
            ("progress", (float)Math.Round(s.Progress ?? 0, 3)),
            ("chase", Settings.Setting.Token(s.Chase)),
        };
        var msgs = new List<OscMessage>();
        lock (_targetsGate)
        {
            foreach (var (k, v) in items)
            {
                string text = Osc.ArgText(v);
                if (!full && _lastSent.TryGetValue(k, out string? prev) && prev == text) continue;
                _lastSent[k] = text;
                msgs.Add(new OscMessage($"{Prefix}/{k}", [v]));
            }
        }
        foreach (var m in msgs) Send(to, m);
    }

    private void Send(IReadOnlyList<IPEndPoint> to, OscMessage m)
    {
        byte[] data = Osc.Encode(m);
        foreach (var ep in to)
        {
            try { _udp.Send(data, data.Length, ep); }
            catch (SocketException) { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
        try { Task.WaitAll(new[] { _rx, _tx }.OfType<Task>().ToArray(), 500); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
