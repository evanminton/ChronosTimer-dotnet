using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace ChronosTimer.Control;

/// <summary>An OSC 1.0 message: address pattern and arguments (int, long, float, double, string, bool, null, byte[]).</summary>
public sealed record OscMessage(string Address, IReadOnlyList<object?> Args)
{
    public override string ToString() =>
        Address + (Args.Count > 0 ? " " + string.Join(" ", Args.Select(Osc.ArgText)) : "");
}

/// <summary>OSC 1.0 encoder/decoder (messages and bundles).</summary>
public static class Osc
{
    public static byte[] Encode(string address, params object?[] args) => Encode(new OscMessage(address, args));

    public static byte[] Encode(OscMessage m)
    {
        using var ms = new MemoryStream();
        WriteString(ms, m.Address);
        var tags = new StringBuilder(",");
        foreach (var a in m.Args)
            tags.Append(a switch
            {
                int => 'i',
                long => 'h',
                float => 'f',
                double => 'd',
                string => 's',
                bool b => b ? 'T' : 'F',
                null => 'N',
                byte[] => 'b',
                _ => throw new ArgumentException($"Unsupported OSC argument type {a.GetType().Name}."),
            });
        WriteString(ms, tags.ToString());
        Span<byte> buf = stackalloc byte[8];
        foreach (var a in m.Args)
        {
            switch (a)
            {
                case int i: BinaryPrimitives.WriteInt32BigEndian(buf, i); ms.Write(buf[..4]); break;
                case long l: BinaryPrimitives.WriteInt64BigEndian(buf, l); ms.Write(buf[..8]); break;
                case float f: BinaryPrimitives.WriteSingleBigEndian(buf, f); ms.Write(buf[..4]); break;
                case double d: BinaryPrimitives.WriteDoubleBigEndian(buf, d); ms.Write(buf[..8]); break;
                case string s: WriteString(ms, s); break;
                case byte[] b:
                    BinaryPrimitives.WriteInt32BigEndian(buf, b.Length); ms.Write(buf[..4]);
                    ms.Write(b);
                    Pad(ms);
                    break;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Encodes several messages as an immediate bundle.</summary>
    public static byte[] EncodeBundle(IEnumerable<OscMessage> messages)
    {
        using var ms = new MemoryStream();
        WriteString(ms, "#bundle");
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(buf, 1); // "immediately"
        ms.Write(buf);
        foreach (var m in messages)
        {
            byte[] e = Encode(m);
            BinaryPrimitives.WriteInt32BigEndian(buf, e.Length);
            ms.Write(buf[..4]);
            ms.Write(e);
        }
        return ms.ToArray();
    }

    /// <summary>Decodes a packet (message or bundle, nested bundles flattened). Throws <see cref="FormatException"/> on malformed data.</summary>
    public static IReadOnlyList<OscMessage> Decode(ReadOnlySpan<byte> packet)
    {
        var list = new List<OscMessage>();
        DecodeInto(packet, list, 0);
        return list;
    }

    private static void DecodeInto(ReadOnlySpan<byte> p, List<OscMessage> list, int depth)
    {
        if (depth > 8) throw new FormatException("OSC bundles nested too deeply.");
        if (p.Length == 0) return;
        int pos = 0;
        string first = ReadString(p, ref pos);
        if (first == "#bundle")
        {
            Need(p, pos, 8);
            pos += 8; // time tag
            while (pos < p.Length)
            {
                if (pos + 4 > p.Length) throw new FormatException("Truncated OSC bundle.");
                int size = BinaryPrimitives.ReadInt32BigEndian(p[pos..]);
                pos += 4;
                if (size < 0 || (long)pos + size > p.Length) throw new FormatException("Bad OSC bundle element size.");
                DecodeInto(p.Slice(pos, size), list, depth + 1);
                pos += size;
            }
            return;
        }
        if (!first.StartsWith('/')) throw new FormatException($"'{first}' is not an OSC address.");
        var args = new List<object?>();
        if (pos < p.Length)
        {
            string tags = ReadString(p, ref pos);
            if (!tags.StartsWith(',')) throw new FormatException("Missing OSC type tags.");
            foreach (char t in tags.AsSpan(1))
            {
                switch (t)
                {
                    case 'i': Need(p, pos, 4); args.Add(BinaryPrimitives.ReadInt32BigEndian(p[pos..])); pos += 4; break;
                    case 'f': Need(p, pos, 4); args.Add(BinaryPrimitives.ReadSingleBigEndian(p[pos..])); pos += 4; break;
                    case 'h': Need(p, pos, 8); args.Add(BinaryPrimitives.ReadInt64BigEndian(p[pos..])); pos += 8; break;
                    case 'd': Need(p, pos, 8); args.Add(BinaryPrimitives.ReadDoubleBigEndian(p[pos..])); pos += 8; break;
                    case 's' or 'S': args.Add(ReadString(p, ref pos)); break;
                    case 'T': args.Add(true); break;
                    case 'F': args.Add(false); break;
                    case 'N': args.Add(null); break;
                    case 'I': args.Add(double.PositiveInfinity); break;
                    case 'c': Need(p, pos, 4); args.Add(((char)BinaryPrimitives.ReadInt32BigEndian(p[pos..])).ToString()); pos += 4; break;
                    case 'r' or 'm': Need(p, pos, 4); args.Add(BinaryPrimitives.ReadInt32BigEndian(p[pos..])); pos += 4; break;
                    case 't': Need(p, pos, 8); args.Add(BinaryPrimitives.ReadInt64BigEndian(p[pos..])); pos += 8; break;
                    case 'b':
                        {
                            Need(p, pos, 4);
                            int n = BinaryPrimitives.ReadInt32BigEndian(p[pos..]); pos += 4;
                            Need(p, pos, n);
                            args.Add(p.Slice(pos, n).ToArray());
                            pos += (int)Math.Min(((long)n + 3) & ~3L, p.Length - pos);
                            break;
                        }
                    case '[' or ']': break;
                    default: throw new FormatException($"Unsupported OSC type tag '{t}'.");
                }
            }
        }
        list.Add(new OscMessage(first, args));
    }

    private static void Need(ReadOnlySpan<byte> p, int pos, int n)
    {
        if (n < 0 || pos < 0 || (long)pos + n > p.Length) throw new FormatException("Truncated OSC message.");
    }

    private static string ReadString(ReadOnlySpan<byte> p, ref int pos)
    {
        if (pos < 0 || pos >= p.Length) throw new FormatException("Truncated OSC message.");
        int end = p[pos..].IndexOf((byte)0);
        if (end < 0) throw new FormatException("Unterminated OSC string.");
        string s = Encoding.UTF8.GetString(p.Slice(pos, end));
        pos += (end + 4) & ~3;
        return s;
    }

    private static void WriteString(Stream s, string text)
    {
        byte[] b = Encoding.UTF8.GetBytes(text);
        s.Write(b);
        s.WriteByte(0);
        Pad(s);
    }

    private static void Pad(Stream s)
    {
        while (s.Length % 4 != 0) s.WriteByte(0);
    }

    /// <summary>Text form of an argument for commands: bools → on/off, numbers invariant.</summary>
    public static string ArgText(object? a) => a switch
    {
        null => "",
        bool b => b ? "on" : "off",
        float f => f.ToString("0.######", CultureInfo.InvariantCulture),
        double d => d.ToString("0.######", CultureInfo.InvariantCulture),
        IFormattable x => x.ToString(null, CultureInfo.InvariantCulture),
        byte[] b => Convert.ToHexString(b),
        _ => a.ToString() ?? "",
    };
}
