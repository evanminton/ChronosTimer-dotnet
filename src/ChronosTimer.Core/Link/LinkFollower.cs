using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ChronosTimer.Show;

namespace ChronosTimer.Link;

/// <summary>
/// The follower end of the timer link: finds the master (by address, or by its beacon with "auto"), keeps the
/// connection up, receives the show view and messages, and sends messages and cue acknowledgements.
/// </summary>
public sealed class LinkFollower : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _master;
    private readonly int _defaultPort, _beaconPort;
    private readonly string _requestedName, _key;
    private readonly object _gate = new();
    private NetworkStream? _stream;
    private string _want;

    /// <param name="master">"auto" (find the master by its beacon), or host[:port].</param>
    /// <param name="defaultPort">Link port when the address has none.</param>
    /// <param name="name">This node's name, or "auto" to be numbered by the master.</param>
    /// <param name="want">The node name this timer had before (kept when free).</param>
    /// <param name="key">The master's link key.</param>
    /// <param name="beaconPort">UDP port masters announce themselves on.</param>
    public LinkFollower(string master, int defaultPort, string name, string want, string key, int beaconPort = LinkProtocol.BeaconPort)
    {
        _master = master.Trim();
        if (!IsAuto(_master)) LinkProtocol.ParseEndpoint(_master, defaultPort); // validate
        _defaultPort = defaultPort;
        _requestedName = name;
        _want = want;
        _key = key ?? "";
        _beaconPort = beaconPort;
        Status = IsAuto(_master) ? "looking for a master…" : "connecting to " + _master + "…";
    }

    public static bool IsAuto(string master) => master.Length == 0 || master.Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>"connected to ChronosTimer01 (169.254.3.4)", "looking for a master…", "refused: wrong link key", …</summary>
    public string Status { get; private set; }

    public bool Connected { get; private set; }

    /// <summary>The node name the master gave this timer (empty until connected once).</summary>
    public string AssignedName { get; private set; } = "";

    public string MasterName { get; private set; } = "";

    /// <summary>Connection state changed (connected, lost, refused). Any thread.</summary>
    public event Action? StatusChanged;

    /// <summary>The master's show view arrived. Any thread.</summary>
    public event Action<ShowView>? StateReceived;

    /// <summary>A message arrived. Any thread.</summary>
    public event Action<ShowMessage>? MessageReceived;

    public void Start() => _ = Task.Run(RunLoop);

    public void Dispose()
    {
        _cts.Cancel();
        lock (_gate) { _stream?.Dispose(); _stream = null; }
    }

    /// <summary>Sends a message through the master (to one node, or null for everyone). False when not connected.</summary>
    public bool SendMessage(string text, string? to) => SendLine(LinkProtocol.Encode("msg", ("text", text), ("to", to ?? "")));

    /// <summary>Acknowledges the cue light. False when not connected.</summary>
    public bool Acknowledge() => SendLine(LinkProtocol.Encode("ack"));

    private bool SendLine(string line)
    {
        NetworkStream? s;
        lock (_gate) s = _stream;
        if (s is null) return false;
        try
        {
            lock (s) s.Write(Encoding.UTF8.GetBytes(line));
            return true;
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { return false; }
    }

    private void SetStatus(string status, bool connected)
    {
        Status = status;
        Connected = connected;
        StatusChanged?.Invoke();
    }

    private async Task RunLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            TimeSpan retry = TimeSpan.FromSeconds(1);
            try
            {
                IPEndPoint? ep;
                string label;
                if (IsAuto(_master))
                {
                    SetStatus("looking for a master…", false);
                    (ep, label) = await FindMaster(ct).ConfigureAwait(false);
                }
                else
                {
                    var (host, port) = LinkProtocol.ParseEndpoint(_master, _defaultPort);
                    SetStatus($"connecting to {_master}…", false);
                    var addrs = IPAddress.TryParse(host, out var ip) ? [ip] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
                    ep = addrs.Length > 0 ? new IPEndPoint(addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs[0], port) : null;
                    label = _master;
                }
                if (ep is null) { retry = TimeSpan.FromSeconds(2); continue; }
                retry = await Session(ep, label, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException)
            {
                if (Connected || !Status.StartsWith("refused", StringComparison.Ordinal))
                    SetStatus("master not reachable (" + (ex is OperationCanceledException ? "timed out" : ex.Message) + "), retrying…", false);
                retry = TimeSpan.FromSeconds(2);
            }
            finally
            {
                lock (_gate) { _stream?.Dispose(); _stream = null; }
            }
            try { await Task.Delay(retry, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task<(IPEndPoint?, string)> FindMaster(CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, _beaconPort));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        while (true)
        {
            UdpReceiveResult r;
            try { r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return (null, ""); }
            if (LinkProtocol.TryParseBeacon(Encoding.UTF8.GetString(r.Buffer), out int port, out string name))
                return (new IPEndPoint(r.RemoteEndPoint.Address, port), $"{name} ({r.RemoteEndPoint.Address})");
        }
    }

    /// <summary>One connection; returns how long to wait before reconnecting.</summary>
    private async Task<TimeSpan> Session(IPEndPoint ep, string label, CancellationToken ct)
    {
        using var client = new TcpClient(ep.AddressFamily);
        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(ep, connectTimeout.Token).ConfigureAwait(false);
        }
        LinkNet.Configure(client.Client);
        var stream = client.GetStream();
        lock (_gate) _stream = stream;
        var reader = new LineReader(stream);
        SendLine(LinkProtocol.Encode("hello", ("v", LinkProtocol.Version.ToString(CultureInfo.InvariantCulture)), ("name", _requestedName), ("want", _want), ("key", _key)));

        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pinger = Task.Run(async () =>
        {
            try
            {
                while (!session.IsCancellationRequested)
                {
                    await Task.Delay(1000, session.Token).ConfigureAwait(false);
                    if (!SendLine(LinkProtocol.Encode("ping"))) break;
                }
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            while (true)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(session.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(4)); // the master pushes the view many times a second
                string? line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                if (line is null)
                {
                    if (Connected) SetStatus("master closed the link, reconnecting…", false);
                    return TimeSpan.FromSeconds(1);
                }
                if (LinkProtocol.Decode(line) is not { } p) continue;
                switch (p.Type)
                {
                    case "welcome":
                        AssignedName = p["name"];
                        MasterName = p["master"];
                        if (LinkProtocol.NodeNumber(AssignedName) is not null) _want = AssignedName;
                        SetStatus($"connected to {(MasterName.Length > 0 ? MasterName + " (" + ep.Address + ")" : label)} as {AssignedName}", true);
                        break;
                    case "deny":
                        SetStatus("refused: " + p["text"], false);
                        return TimeSpan.FromSeconds(5);
                    case "state":
                        StateReceived?.Invoke(ShowView.FromFields(p.Fields) with { Live = true });
                        break;
                    case "msg":
                        long.TryParse(p["time"], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ms);
                        var time = ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.Now;
                        string text = ShowMessage.Clean(p["text"]);
                        if (text.Length > 0) MessageReceived?.Invoke(new ShowMessage(time, p["from"], p["to"].Length > 0 ? p["to"] : null, text));
                        break;
                }
            }
        }
        finally
        {
            session.Cancel();
            await pinger.ConfigureAwait(false);
            if (Connected) SetStatus("link lost, reconnecting…", false);
        }
    }
}
