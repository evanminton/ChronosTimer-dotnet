using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using ChronosTimer.Show;

namespace ChronosTimer.Link;

/// <summary>A follower connected to this master.</summary>
public sealed record LinkFollowerInfo(string Name, string Address, bool Acknowledged);

/// <summary>
/// The master end of the timer link: accepts followers, names them (ChronosTimer02, 03, …), pushes the show view,
/// relays messages and collects cue acknowledgements. Announces itself with a UDP beacon so followers find it.
/// </summary>
public sealed class LinkMaster : IDisposable
{
    private sealed class Peer(string name, string address, TcpClient client)
    {
        public string Name { get; } = name;
        public string Address { get; } = address;
        public TcpClient Client { get; } = client;
        public Channel<string> Out { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });
        public volatile bool Acked;
    }

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly List<Peer> _peers = [];
    private readonly byte[] _key;
    private readonly IReadOnlyList<IPAddress> _beaconTargets;
    private readonly int _beaconPort;

    /// <param name="bind">Address to listen on.</param>
    /// <param name="port">TCP port (0 = any free port).</param>
    /// <param name="name">This timer's node name.</param>
    /// <param name="key">Link key followers must give (empty = any follower may join).</param>
    /// <param name="beaconTargets">Broadcast addresses for the discovery beacon (empty = no beacon).</param>
    /// <param name="beaconPort">UDP port of the beacon.</param>
    public LinkMaster(IPAddress bind, int port, string name, string key, IReadOnlyList<IPAddress>? beaconTargets = null, int beaconPort = LinkProtocol.BeaconPort)
    {
        Name = name;
        BindAddress = bind;
        _key = Encoding.UTF8.GetBytes(key ?? "");
        _beaconTargets = beaconTargets ?? [];
        _beaconPort = beaconPort;
        _listener = new TcpListener(bind, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public string Name { get; }
    public IPAddress BindAddress { get; }
    public int Port { get; private set; }

    /// <summary>Human-readable events (joins, leaves, refusals). Any thread.</summary>
    public event Action<string>? Log;

    /// <summary>A follower sent a message: from, to (null = everyone), text. Any thread.</summary>
    public event Action<string, string?, string>? MessageReceived;

    /// <summary>A follower acknowledged the cue light. Any thread.</summary>
    public event Action<string>? Acknowledged;

    /// <summary>A follower joined or left. Any thread.</summary>
    public event Action? FollowersChanged;

    public IReadOnlyList<LinkFollowerInfo> Followers
    {
        get { lock (_gate) return [.. _peers.Select(p => new LinkFollowerInfo(p.Name, p.Address, p.Acked))]; }
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        if (_beaconTargets.Count > 0) _ = Task.Run(BeaconLoop);
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        lock (_gate)
        {
            foreach (var p in _peers) { p.Out.Writer.TryComplete(); p.Client.Dispose(); }
            _peers.Clear();
        }
    }

    /// <summary>Pushes the show view to every follower.</summary>
    public void SendState(ShowView view) => Send(LinkProtocol.Encode("state", view.ToFields()), null);

    /// <summary>
    /// Sends a message to one follower (<see cref="ShowMessage.To"/>) or to every follower but <paramref name="except"/>.
    /// Returns false when the named follower isn't connected.
    /// </summary>
    public bool SendMessage(ShowMessage m, string? except = null)
    {
        string line = LinkProtocol.Encode("msg", ("from", m.From), ("to", m.To ?? ""), ("text", m.Text),
            ("time", m.Time.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (m.To is { } to)
        {
            if (to.Equals(Name, StringComparison.OrdinalIgnoreCase)) return true;
            return Send(line, p => p.Name.Equals(to, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        Send(line, p => except is null || !p.Name.Equals(except, StringComparison.OrdinalIgnoreCase));
        return true;
    }

    /// <summary>Clears every follower's acknowledgement (the cue changed).</summary>
    public void ResetAcks()
    {
        lock (_gate) foreach (var p in _peers) p.Acked = false;
    }

    /// <summary>True when a node with this name is connected.</summary>
    public bool HasFollower(string name)
    {
        lock (_gate) return _peers.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private int Send(string line, Func<Peer, bool>? filter)
    {
        int n = 0;
        lock (_gate)
            foreach (var p in _peers)
                if (filter is null || filter(p)) { p.Out.Writer.TryWrite(line); n++; }
        return n;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        Peer? peer = null;
        string remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        try
        {
            LinkNet.Configure(client.Client);
            var stream = client.GetStream();
            var reader = new LineReader(stream);

            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
            {
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                var hello = await reader.ReadLineAsync(helloTimeout.Token).ConfigureAwait(false) is { } l ? LinkProtocol.Decode(l) : null;
                if (hello is not { Type: "hello" }) return;
                if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(hello["key"]), _key))
                {
                    await Write(stream, LinkProtocol.Encode("deny", ("text", "wrong link key")), cts.Token).ConfigureAwait(false);
                    Log?.Invoke($"Link: refused {remote} (wrong link key)");
                    return;
                }
                lock (_gate)
                {
                    peer = new Peer(AssignName(hello["name"], hello["want"]), remote, client);
                    _peers.Add(peer);
                }
            }
            await Write(stream, LinkProtocol.Encode("welcome", ("name", peer.Name), ("master", Name)), cts.Token).ConfigureAwait(false);
            Log?.Invoke($"Link: {peer.Name} joined from {remote}");
            FollowersChanged?.Invoke();

            var writer = Task.Run(async () =>
            {
                try
                {
                    await foreach (string line in peer.Out.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
                        await Write(stream, line, cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
                finally { cts.Cancel(); }
            });

            while (!cts.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                idle.CancelAfter(TimeSpan.FromSeconds(6)); // followers ping every second
                string? line = await reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                if (line is null) break;
                if (LinkProtocol.Decode(line) is not { } p) continue;
                switch (p.Type)
                {
                    case "ack":
                        peer.Acked = true;
                        Acknowledged?.Invoke(peer.Name);
                        break;
                    case "msg":
                        string text = ShowMessage.Clean(p["text"]);
                        if (text.Length > 0) MessageReceived?.Invoke(peer.Name, p["to"].Length > 0 ? p["to"] : null, text);
                        break;
                }
            }
            cts.Cancel();
            await writer.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            cts.Cancel();
            client.Dispose();
            if (peer is not null)
            {
                bool removed;
                lock (_gate) removed = _peers.Remove(peer);
                peer.Out.Writer.TryComplete();
                if (removed && !_cts.IsCancellationRequested)
                {
                    Log?.Invoke($"Link: {peer.Name} left");
                    FollowersChanged?.Invoke();
                }
            }
        }
    }

    private static Task Write(NetworkStream s, string line, CancellationToken ct) => s.WriteAsync(Encoding.UTF8.GetBytes(line), ct).AsTask();

    /// <summary>Custom names are kept; "auto" nodes get the number they ask for when free, otherwise the lowest free one.</summary>
    private string AssignName(string requested, string want)
    {
        bool Taken(string n) => n.Equals(Name, StringComparison.OrdinalIgnoreCase) || _peers.Any(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase));
        string r = requested.Trim();
        if (r.Length > 0 && !r.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            r = ShowMessage.Clean(r);
            if (r.Length > 40) r = r[..40];
            if (!Taken(r)) return r;
            for (int i = 2; ; i++) if (!Taken($"{r}-{i}")) return $"{r}-{i}";
        }
        if (LinkProtocol.NodeNumber(want) is { } w && !Taken(LinkProtocol.NodeName(w))) return LinkProtocol.NodeName(w);
        for (int i = 2; ; i++) if (!Taken(LinkProtocol.NodeName(i))) return LinkProtocol.NodeName(i);
    }

    private async Task BeaconLoop()
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        try
        {
            if (!BindAddress.Equals(IPAddress.Any) && BindAddress.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(BindAddress))
                udp.Client.Bind(new IPEndPoint(BindAddress, 0));
        }
        catch (SocketException) { }
        byte[] data = Encoding.UTF8.GetBytes(LinkProtocol.Beacon(Port, Name));
        while (!_cts.IsCancellationRequested)
        {
            foreach (var target in _beaconTargets)
            {
                try { await udp.SendAsync(data, new IPEndPoint(target, _beaconPort), _cts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                catch (OperationCanceledException) { return; }
            }
            try { await Task.Delay(1000, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }
}
