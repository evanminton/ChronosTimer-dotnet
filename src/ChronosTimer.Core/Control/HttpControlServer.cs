using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ChronosTimer.Control;

/// <summary>
/// A small, dependency-free HTTP/1.1 + WebSocket server (runs anywhere .NET sockets run: Windows without admin rights,
/// macOS, Linux, iOS, Android).
/// </summary>
/// <remarks>
/// <code>
/// GET  /                         browser remote
/// GET  /api/status               status JSON
/// GET  /api/settings             every setting: name, value, default, description, allowed values
/// GET  /api/commands             every command with usage and description
/// GET  /api/log                  recent log lines (text)
/// GET  /api/command?c=locate+01:00:00:00      run a command line (POST: the body is the command line)
/// GET  /api/set?name=duration&amp;value=5m      change a setting
/// GET  /api/&lt;command&gt;/&lt;args…&gt;          e.g. /api/play, /api/locate/01:00:00:00, /api/duration/5m
/// GET  /ws                       WebSocket: status JSON pushed at status-rate; send command lines as text
/// </code>
/// When a token is set, every request except the sign-in page needs it: <c>?token=…</c>, an <c>X-Chronos-Token</c> or
/// <c>Authorization: Bearer …</c> header, or the <c>SameSite=Strict</c> cookie the remote page gets after
/// <c>/?token=…</c>. Browser requests from other origins are refused unless the origin is listed in the allowed origins
/// (e.g. a show-control dashboard), and even then they need the token.
/// </remarks>
public sealed class HttpControlServer : IDisposable
{
    private const string WsGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private readonly TimerHost _host;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private const string CookieName = "chronos_token";
    private readonly IPAddress _bind;
    private readonly byte[] _token;
    private readonly string _tokenText;
    private readonly HashSet<string> _origins;
    private int _clients;

    /// <param name="host">Timer to control.</param>
    /// <param name="bind">Address to listen on (<see cref="IPAddress.Loopback"/>, <see cref="IPAddress.Any"/> or one interface's address).</param>
    /// <param name="port">TCP port (0 = any free port).</param>
    /// <param name="token">Access token every request must carry; empty = no authentication.</param>
    /// <param name="allowedOrigins">Other web origins (e.g. <c>http://dashboard:3000</c>, or <c>*</c>) whose pages may call the API.</param>
    public HttpControlServer(TimerHost host, IPAddress bind, int port, string token = "", IEnumerable<string>? allowedOrigins = null)
    {
        _host = host;
        _bind = bind;
        _tokenText = token ?? "";
        _token = Encoding.UTF8.GetBytes(_tokenText);
        _origins = new HashSet<string>((allowedOrigins ?? []).Select(o => o.Trim().TrimEnd('/')).Where(o => o.Length > 0), StringComparer.OrdinalIgnoreCase);
        _listener = new TcpListener(bind, port);
        _listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    public int Port { get; private set; }

    /// <summary>Address the server listens on.</summary>
    public IPAddress BindAddress => _bind;

    /// <summary>WebSocket clients connected.</summary>
    public int WebSocketClients => _clients;

    /// <summary>Addresses the remote can be opened at (with the token, so they work as links).</summary>
    public IReadOnlyList<string> Urls
    {
        get
        {
            string q = _tokenText.Length > 0 ? "?token=" + Uri.EscapeDataString(_tokenText) : "";
            var list = new List<string>();
            foreach (var a in NetworkBinding.Reachable(_bind))
            {
                if (IPAddress.IsLoopback(a)) continue;
                string h = a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]" : a.ToString();
                list.Add($"http://{h}:{Port}/{q}");
            }
            if (_bind.Equals(IPAddress.Any) || IPAddress.IsLoopback(_bind)) list.Add($"http://localhost:{Port}/{q}");
            return list;
        }
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
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

    private sealed record Request(string Method, string Path, Dictionary<string, string> Query, Dictionary<string, string> Headers, string Body, string Remote);

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var req = await ReadRequest(stream, remote, timeout.Token).ConfigureAwait(false);
                if (req is null) return;

                string cors = CorsHeaders(req);
                if (!OriginAllowed(req))
                {
                    await WriteResponse(stream, 403, TextType, "Requests from other web pages are not allowed (see http-origins).", "", _cts.Token).ConfigureAwait(false);
                    return;
                }
                if (req.Method == "OPTIONS")
                {
                    await WriteResponse(stream, 204, TextType, "", cors, _cts.Token).ConfigureAwait(false);
                    return;
                }
                if (req.Path == "/ws" && req.Headers.TryGetValue("upgrade", out string? up) && up.Equals("websocket", StringComparison.OrdinalIgnoreCase))
                {
                    if (!Authorized(req)) { await WriteResponse(stream, 401, TextType, "Access token required.", cors, _cts.Token).ConfigureAwait(false); return; }
                    await WebSocketSession(stream, req).ConfigureAwait(false);
                    return;
                }
                var (status, type, body, extra) = Route(req);
                await WriteResponse(stream, status, type, body, extra + cors, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static async Task<Request?> ReadRequest(NetworkStream stream, string remote, CancellationToken ct)
    {
        var buf = new byte[65_536];
        int len = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (len == buf.Length) return null;
            int n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0) return null;
            len += n;
            headerEnd = buf.AsSpan(0, len).IndexOf("\r\n\r\n"u8);
        }
        string head = Encoding.ASCII.GetString(buf, 0, headerEnd);
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length < 2) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string l in lines.Skip(1))
        {
            int c = l.IndexOf(':');
            if (c > 0) headers[l[..c].Trim()] = l[(c + 1)..].Trim();
        }
        int bodyStart = headerEnd + 4;
        int contentLength = headers.TryGetValue("content-length", out string? cl) && int.TryParse(cl, out int v) ? Math.Clamp(v, 0, buf.Length - bodyStart) : 0;
        while (len - bodyStart < contentLength)
        {
            int n = await stream.ReadAsync(buf.AsMemory(len), ct).ConfigureAwait(false);
            if (n == 0) break;
            len += n;
        }
        string body = Encoding.UTF8.GetString(buf, bodyStart, Math.Min(contentLength, len - bodyStart));

        string target = first[1];
        string path = target, query = "";
        int q = target.IndexOf('?');
        if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }
        var qd = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string k = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string val = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            qd[k] = val;
        }
        return new Request(first[0].ToUpperInvariant(), path, qd, headers, body, remote);
    }

    private const string JsonType = "application/json; charset=utf-8", TextType = "text/plain; charset=utf-8", HtmlType = "text/html; charset=utf-8";

    // ───────────────────────────── access control ─────────────────────────────

    /// <summary>True when the request carries the token (or no token is set).</summary>
    private bool Authorized(Request r)
    {
        if (_token.Length == 0) return true;
        string? given = r.Query.GetValueOrDefault("token")
            ?? r.Headers.GetValueOrDefault("x-chronos-token")
            ?? (r.Headers.TryGetValue("authorization", out string? a) && a.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? a[7..].Trim() : null)
            ?? Cookie(r, CookieName);
        return given is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);
    }

    private static string? Cookie(Request r, string name)
    {
        if (!r.Headers.TryGetValue("cookie", out string? header)) return null;
        foreach (string part in header.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq > 0 && part[..eq].Trim() == name) return Uri.UnescapeDataString(part[(eq + 1)..].Trim());
        }
        return null;
    }

    /// <summary>Browsers send Origin on cross-origin (and most same-origin) requests; non-browser clients usually don't.</summary>
    private static bool SameOrigin(Request r, out string? origin)
    {
        if (!r.Headers.TryGetValue("origin", out origin) || origin.Length == 0) { origin = null; return true; }
        return Uri.TryCreate(origin, UriKind.Absolute, out var u) && r.Headers.TryGetValue("host", out string? host)
            && u.Authority.Equals(host, StringComparison.OrdinalIgnoreCase);
    }

    private bool OriginAllowed(Request r) =>
        SameOrigin(r, out string? o) || _origins.Contains("*") || _origins.Contains(o!.TrimEnd('/'));

    private string CorsHeaders(Request r)
    {
        if (SameOrigin(r, out string? o) || !OriginAllowed(r)) return "";
        return $"Access-Control-Allow-Origin: {o}\r\nVary: Origin\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
               "Access-Control-Allow-Headers: Content-Type, Authorization, X-Chronos-Token\r\n";
    }

    private static string SignInPage(bool wrong) => $$"""
        <!doctype html><html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Chronos Timer</title>
        <style>body{font:16px system-ui,sans-serif;background:#111;color:#eee;display:grid;place-items:center;min-height:100vh;margin:0}form{display:grid;gap:10px;padding:16px;max-width:320px}input,button{font:inherit;padding:8px;border-radius:6px;border:1px solid #555;background:#222;color:#eee}</style></head>
        <body><form method="get" action="/"><h1>Chronos Timer</h1><p>Enter the access token shown by the timer (setting <b>http-token</b>), or open the link it shows.</p>
        {{(wrong ? "<p style=\"color:#f66\">Wrong token.</p>" : "")}}<input name="token" autocomplete="current-password" autofocus><button>Open remote</button></form></body></html>
        """;

    private (int Status, string Type, string Body, string Headers) Route(Request r)
    {
        string p = r.Path.TrimEnd('/');
        if (p == "/favicon.ico") return (204, TextType, "", "");
        bool ok = Authorized(r);
        if (p is "" or "/index.html" or "/remote")
        {
            if (!ok) return (401, HtmlType, SignInPage(r.Query.ContainsKey("token")), "");
            // Signed in with ?token=: keep it in a same-site cookie and drop it from the address bar.
            if (_token.Length > 0 && r.Query.ContainsKey("token"))
                return (303, TextType, "", $"Set-Cookie: {CookieName}={Uri.EscapeDataString(_tokenText)}; Path=/; HttpOnly; SameSite=Strict\r\nLocation: {(p.Length == 0 ? "/" : p)}\r\n");
            return (200, HtmlType, WebRemotePage.Html, "");
        }
        if (!p.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) return (404, TextType, "Not found. Try / (remote) or /api/status.", "");
        if (!ok) return (401, JsonType, Json.Result(CommandResult.Error("Access token required (http-token).")), "WWW-Authenticate: Bearer\r\n");
        var (status, type, body) = Api(r, p);
        return (status, type, body, "");
    }

    private (int Status, string Type, string Body) Api(Request r, string p)
    {
        string sub = p.Length > 4 ? p[5..] : "";
        switch (sub.ToLowerInvariant())
        {
            case "" or "help":
                return (200, TextType, _host.Commands.Help() + "\n" + _host.RemoteText());
            case "status":
                return (200, JsonType, _host.StatusJson());
            case "settings":
                return (200, JsonType, Json.Settings(_host.Settings));
            case "commands":
                return (200, JsonType, Json.Commands(_host.Commands.All));
            case "log":
                return (200, TextType, string.Join('\n', _host.RecentLog));
            case "command":
                {
                    string line = r.Query.GetValueOrDefault("c") ?? r.Query.GetValueOrDefault("command") ?? r.Body;
                    var res = _host.Execute(line, "http " + r.Remote);
                    return (res.Ok ? 200 : 400, JsonType, Json.Result(res));
                }
            case "set":
                {
                    string name = r.Query.GetValueOrDefault("name") ?? "";
                    string value = r.Query.GetValueOrDefault("value") ?? r.Body;
                    var res = _host.Execute($"set {name} \"{value}\"", "http " + r.Remote);
                    return (res.Ok ? 200 : 400, JsonType, Json.Result(res));
                }
            default:
                {
                    // /api/<command>/<args…>
                    string line = string.Join(' ', sub.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString));
                    if (r.Body.Length > 0 && r.Method == "POST") line += " " + r.Body.Trim();
                    var res = _host.Execute(line, "http " + r.Remote);
                    return (res.Ok ? 200 : 400, JsonType, Json.Result(res));
                }
        }
    }

    private static async Task WriteResponse(NetworkStream s, int status, string type, string body, string extraHeaders, CancellationToken ct)
    {
        string reason = status switch { 200 => "OK", 204 => "No Content", 303 => "See Other", 400 => "Bad Request", 401 => "Unauthorized", 403 => "Forbidden", 404 => "Not Found", _ => "Error" };
        byte[] b = Encoding.UTF8.GetBytes(body);
        string head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {b.Length}\r\nCache-Control: no-store\r\n" +
                      extraHeaders + "Connection: close\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), ct).ConfigureAwait(false);
        if (b.Length > 0) await s.WriteAsync(b, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    // ───────────────────────────── WebSocket (RFC 6455) ─────────────────────────────

    private async Task WebSocketSession(NetworkStream s, Request req)
    {
        if (!req.Headers.TryGetValue("sec-websocket-key", out string? key)) return;
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key.Trim() + WsGuid)));
        string head = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n";
        await s.WriteAsync(Encoding.ASCII.GetBytes(head), _cts.Token).ConfigureAwait(false);

        Interlocked.Increment(ref _clients);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Send(byte opcode, byte[] payload)
        {
            await sendLock.WaitAsync(session.Token).ConfigureAwait(false);
            try { await s.WriteAsync(WebSocketFrame.Encode(opcode, payload), session.Token).ConfigureAwait(false); }
            finally { sendLock.Release(); }
        }

        var pusher = Task.Run(async () =>
        {
            string last = "";
            var lastSent = DateTime.MinValue;
            while (!session.IsCancellationRequested)
            {
                string json = _host.StatusJson();
                if (json != last || DateTime.UtcNow - lastSent > TimeSpan.FromSeconds(1))
                {
                    await Send(1, Encoding.UTF8.GetBytes(json)).ConfigureAwait(false);
                    last = json;
                    lastSent = DateTime.UtcNow;
                }
                await Task.Delay(TimeSpan.FromSeconds(1 / Math.Max(1, _host.PushRate)), session.Token).ConfigureAwait(false);
            }
        });

        try
        {
            var reader = new WebSocketFrame.Reader(s);
            while (!session.IsCancellationRequested)
            {
                var frame = await reader.ReadAsync(session.Token).ConfigureAwait(false);
                if (frame is null || frame.Value.Opcode == 8) { try { await Send(8, []).ConfigureAwait(false); } catch (IOException) { } break; }
                switch (frame.Value.Opcode)
                {
                    case 9: await Send(10, frame.Value.Payload).ConfigureAwait(false); break;
                    case 1:
                        {
                            string text = Encoding.UTF8.GetString(frame.Value.Payload);
                            var res = _host.Execute(text, "ws " + req.Remote);
                            await Send(1, Encoding.UTF8.GetBytes(Json.Result(res))).ConfigureAwait(false);
                            break;
                        }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidDataException) { }
        finally
        {
            session.Cancel();
            try { await pusher.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException) { }
            Interlocked.Decrement(ref _clients);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (SocketException) { }
        _listener.Dispose();
    }
}

/// <summary>WebSocket framing (RFC 6455 §5).</summary>
public static class WebSocketFrame
{
    /// <summary>Encodes a final, unmasked frame (server → client), or a masked one when <paramref name="mask"/> is given.</summary>
    public static byte[] Encode(byte opcode, ReadOnlySpan<byte> payload, byte[]? mask = null)
    {
        int n = payload.Length;
        int headerLen = 2 + (n < 126 ? 0 : n <= ushort.MaxValue ? 2 : 8) + (mask is null ? 0 : 4);
        var f = new byte[headerLen + n];
        f[0] = (byte)(0x80 | (opcode & 0x0F));
        int pos = 2;
        byte m = mask is null ? (byte)0 : (byte)0x80;
        if (n < 126) f[1] = (byte)(m | n);
        else if (n <= ushort.MaxValue) { f[1] = (byte)(m | 126); f[2] = (byte)(n >> 8); f[3] = (byte)n; pos = 4; }
        else { f[1] = (byte)(m | 127); for (int i = 0; i < 8; i++) f[2 + i] = (byte)((long)n >> (56 - 8 * i)); pos = 10; }
        if (mask is not null) { mask.AsSpan(0, 4).CopyTo(f.AsSpan(pos)); pos += 4; }
        payload.CopyTo(f.AsSpan(pos));
        if (mask is not null) for (int i = 0; i < n; i++) f[pos + i] ^= mask[i & 3];
        return f;
    }

    /// <summary>Reads frames from a stream, joining fragmented messages.</summary>
    public sealed class Reader(Stream stream)
    {
        private const int MaxMessage = 1 << 20;

        public async Task<(byte Opcode, byte[] Payload)?> ReadAsync(CancellationToken ct)
        {
            byte opcode = 0;
            using var message = new MemoryStream();
            while (true)
            {
                var h = new byte[2];
                if (!await Fill(h, ct).ConfigureAwait(false)) return null;
                bool fin = (h[0] & 0x80) != 0;
                byte op = (byte)(h[0] & 0x0F);
                bool masked = (h[1] & 0x80) != 0;
                long len = h[1] & 0x7F;
                if (len == 126) { var e = new byte[2]; if (!await Fill(e, ct).ConfigureAwait(false)) return null; len = (e[0] << 8) | e[1]; }
                else if (len == 127) { var e = new byte[8]; if (!await Fill(e, ct).ConfigureAwait(false)) return null; len = 0; for (int i = 0; i < 8; i++) len = (len << 8) | e[i]; }
                if (len > MaxMessage || message.Length + len > MaxMessage) throw new InvalidDataException("WebSocket message too large.");
                byte[] mask = new byte[4];
                if (masked && !await Fill(mask, ct).ConfigureAwait(false)) return null;
                var payload = new byte[len];
                if (!await Fill(payload, ct).ConfigureAwait(false)) return null;
                if (masked) for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                if (op >= 8) return (op, payload); // control frames are never fragmented
                if (op != 0) opcode = op;
                message.Write(payload);
                if (fin) return (opcode, message.ToArray());
            }
        }

        private async Task<bool> Fill(byte[] buffer, CancellationToken ct)
        {
            int got = 0;
            while (got < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(got), ct).ConfigureAwait(false);
                if (n == 0) return false;
                got += n;
            }
            return true;
        }
    }
}
