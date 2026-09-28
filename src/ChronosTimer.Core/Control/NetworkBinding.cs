using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using ChronosTimer.Settings;

namespace ChronosTimer.Control;

/// <summary>
/// Which network interface a control server listens on: <c>localhost</c> (this computer only), <c>all</c> (every
/// interface), a network interface by name (e.g. <c>eth0</c>, <c>Wi-Fi</c>) or an IP address of this computer.
/// </summary>
public static class NetworkBinding
{
    public const string Localhost = "localhost";
    public const string All = "all";

    /// <summary>The link-local (APIPA, 169.254.x.x) network: timers cabled together without a DHCP server.</summary>
    public const string Apipa = "apipa";

    /// <summary>The choices: localhost, all, and every network interface that is up and has an IP address.</summary>
    public static IReadOnlyList<SettingOption> Options()
    {
        var list = new List<SettingOption>
        {
            new(Localhost, "This computer only"),
            new(All, "Every network interface"),
            new(Apipa, "The link-local 169.254.x.x (APIPA) network: timers cabled together without DHCP"),
        };
        foreach (var (name, addrs) in Interfaces())
            list.Add(new SettingOption(name, string.Join(", ", addrs.Select(a => a.ToString()))));
        return list;
    }

    /// <summary>Validates and normalizes a bind value. Throws <see cref="FormatException"/> for an unknown interface.</summary>
    public static string Normalize(string value)
    {
        string v = value.Trim();
        switch (v.ToLowerInvariant())
        {
            case "" or "localhost" or "loopback" or "local" or "127.0.0.1" or "::1": return Localhost;
            case "all" or "any" or "*" or "0.0.0.0" or "::": return All;
            case "apipa" or "link-local" or "linklocal" or "169.254": return Apipa;
        }
        if (IPAddress.TryParse(v, out var ip)) return ip.ToString();
        foreach (var (name, _) in Interfaces())
            if (name.Equals(v, StringComparison.OrdinalIgnoreCase)) return name;
        throw new FormatException($"'{v}' is not localhost, all, an IP address or a network interface of this computer ({string.Join(", ", Options().Select(o => o.Value))}).");
    }

    /// <summary>The address to bind for a (normalized) value, or null when the interface is missing or down.</summary>
    public static IPAddress? Resolve(string value)
    {
        if (value.Equals(Localhost, StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (value.Equals(All, StringComparison.OrdinalIgnoreCase)) return IPAddress.Any;
        if (value.Equals(Apipa, StringComparison.OrdinalIgnoreCase))
            return Interfaces().SelectMany(i => i.Addresses).FirstOrDefault(IsApipa);
        if (IPAddress.TryParse(value, out var ip)) return ip;
        foreach (var (name, addrs) in Interfaces())
            if (name.Equals(value, StringComparison.OrdinalIgnoreCase))
                return addrs.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addrs.FirstOrDefault();
        return null;
    }

    /// <summary>IPv4 addresses a client can reach a server bound to <paramref name="bound"/> at.</summary>
    public static IReadOnlyList<IPAddress> Reachable(IPAddress bound)
    {
        if (!bound.Equals(IPAddress.Any)) return [bound];
        return [.. Interfaces().SelectMany(i => i.Addresses).Where(a => a.AddressFamily == AddressFamily.InterNetwork)];
    }

    /// <summary>True for an IPv4 link-local (APIPA) address, 169.254.0.0/16.</summary>
    public static bool IsApipa(IPAddress a)
    {
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    /// <summary>IPv4 broadcast addresses that reach the networks a server bound to <paramref name="bound"/> is on.</summary>
    public static IReadOnlyList<IPAddress> Broadcasts(IPAddress bound)
    {
        var set = new List<IPAddress>();
        if (IPAddress.IsLoopback(bound)) return [IPAddress.Loopback];
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (!bound.Equals(IPAddress.Any) && !bound.Equals(u.Address)) continue;
                    byte[] ip = u.Address.GetAddressBytes();
                    byte[] mask = u.IPv4Mask?.GetAddressBytes() ?? [255, 255, 0, 0];
                    if (mask.Length != 4 || mask.All(m => m == 0)) mask = IsApipa(u.Address) ? [255, 255, 0, 0] : [255, 255, 255, 0];
                    var bc = new IPAddress([.. ip.Select((x, i) => (byte)(x | ~mask[i]))]);
                    if (!set.Contains(bc)) set.Add(bc);
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotImplementedException) { }
        if (!set.Contains(IPAddress.Broadcast)) set.Add(IPAddress.Broadcast);
        return set;
    }

    private static List<(string Name, List<IPAddress> Addresses)> Interfaces()
    {
        var list = new List<(string, List<IPAddress>)>();
        try
        {
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var addrs = ni.GetIPProperties().UnicastAddresses
                    .Select(u => u.Address)
                    .Where(a => a.AddressFamily == AddressFamily.InterNetwork || (a.AddressFamily == AddressFamily.InterNetworkV6 && !a.IsIPv6LinkLocal))
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToList();
                if (addrs.Count > 0) list.Add((ni.Name, addrs));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or PlatformNotSupportedException or NotImplementedException) { }
        return list;
    }
}
