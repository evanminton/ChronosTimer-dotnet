using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ChronosTimer.Link;

/// <summary>Which part a timer plays in a group of linked timers.</summary>
public enum LinkRole
{
    /// <summary>Not linked.</summary>
    Off,
    /// <summary>Serves its time, show schedule and cue light to followers; relays messages.</summary>
    Master,
    /// <summary>Mirrors a master's time and cue light; sends messages and cue acknowledgements.</summary>
    Follower,
}

/// <summary>One message on the link: a type and flat string fields.</summary>
public sealed record LinkPacket(string Type, IReadOnlyDictionary<string, string> Fields)
{
    public string this[string key] => Fields.GetValueOrDefault(key) ?? "";
}

/// <summary>
/// The timer link: newline-delimited JSON objects over TCP (default port 8490), each with a "t" (type) and string fields.
/// <code>
/// follower → master   hello {v, name, want, key}   msg {text, to}   ack   ping
/// master → follower   welcome {name, master}   deny {text}   state {ShowView fields}   msg {from, to, text, time}
/// </code>
/// Masters announce themselves with a UDP broadcast beacon (port 8491): "CHRONOS-LINK/1 &lt;port&gt; &lt;name&gt;".
/// </summary>
public static class LinkProtocol
{
    public const int Version = 1;
    public const int DefaultPort = 8490;
    public const int BeaconPort = 8491;
    public const int MaxLine = 16 * 1024;
    public const string AppName = "ChronosTimer";
    private const string BeaconTag = "CHRONOS-LINK/1";

    public static string Encode(string type, IEnumerable<KeyValuePair<string, string>>? fields = null)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("t", type);
            if (fields is not null)
                foreach (var (k, v) in fields)
                    if (k != "t") w.WriteString(k, v);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray()) + "\n";
    }

    public static string Encode(string type, params (string Key, string Value)[] fields) =>
        Encode(type, fields.Select(f => new KeyValuePair<string, string>(f.Key, f.Value)));

    /// <summary>Parses one line; null for anything that isn't a JSON object with a string "t".</summary>
    public static LinkPacket? Decode(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            string? type = null;
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                string v = p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString() ?? "",
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Null => "",
                    _ => p.Value.GetRawText(),
                };
                if (p.Name == "t") type = v; else d[p.Name] = v;
            }
            return type is null or "" ? null : new LinkPacket(type, d);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>"ChronosTimer07" for 7.</summary>
    public static string NodeName(int number) => AppName + number.ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The number of a node name ("ChronosTimer07" → 7), or null for a custom name.</summary>
    public static int? NodeNumber(string name)
    {
        if (!name.StartsWith(AppName, StringComparison.OrdinalIgnoreCase)) return null;
        string n = name[AppName.Length..];
        return n.Length >= 2 && n.All(char.IsAsciiDigit) && int.TryParse(n, NumberStyles.None, CultureInfo.InvariantCulture, out int v) && v > 0 ? v : null;
    }

    public static string Beacon(int port, string name) => FormattableString.Invariant($"{BeaconTag} {port} {name}");

    /// <summary>Parses a beacon: the master's link port and name.</summary>
    public static bool TryParseBeacon(string text, out int port, out string name)
    {
        port = 0; name = "";
        string[] parts = text.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || parts[0] != BeaconTag) return false;
        if (!int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65_535) return false;
        name = parts.Length > 2 ? parts[2] : "";
        return true;
    }

    /// <summary>Parses "host", "host:port" or "[v6]:port".</summary>
    public static (string Host, int Port) ParseEndpoint(string text, int defaultPort)
    {
        string t = text.Trim();
        if (t.Length == 0) throw new FormatException("Give the master's address (e.g. 169.254.10.20 or 169.254.10.20:8490), or 'auto'.");
        if (IPAddress.TryParse(t, out var ip)) return (ip.ToString(), defaultPort);
        int colon = t.LastIndexOf(':');
        if (colon > 0 && int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is >= 1 and <= 65_535)
            return (t[..colon].Trim('[', ']'), port);
        if (t.Contains(':') && !t.Contains('.')) throw new FormatException($"'{t}' is not host or host:port.");
        return (t, defaultPort);
    }
}

/// <summary>Reads newline-terminated UTF-8 lines from a stream with a length limit.</summary>
internal sealed class LineReader(Stream stream)
{
    private readonly byte[] _buf = new byte[LinkProtocol.MaxLine];
    private int _len;

    /// <summary>The next line, or null at the end of the stream. Throws <see cref="InvalidDataException"/> for an over-long line.</summary>
    public async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        while (true)
        {
            int nl = Array.IndexOf(_buf, (byte)'\n', 0, _len);
            if (nl >= 0)
            {
                string line = Encoding.UTF8.GetString(_buf, 0, nl).TrimEnd('\r');
                Buffer.BlockCopy(_buf, nl + 1, _buf, 0, _len - nl - 1);
                _len -= nl + 1;
                return line;
            }
            if (_len == _buf.Length) throw new InvalidDataException("Link message too long.");
            int n = await stream.ReadAsync(_buf.AsMemory(_len), ct).ConfigureAwait(false);
            if (n == 0) return null;
            _len += n;
        }
    }
}

/// <summary>Helpers shared by master and follower.</summary>
internal static class LinkNet
{
    public static void Configure(Socket s)
    {
        s.NoDelay = true;
        try { s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true); } catch (SocketException) { }
    }
}
