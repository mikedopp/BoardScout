using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace BoardScout.Platform;

/// <summary>One network interface from /sys/class/net.</summary>
internal sealed class NetLink
{
    public required string Name { get; init; }
    public int Index { get; init; }

    /// <summary>The hardware behind it, or null for software interfaces (bridges, VPNs, containers).</summary>
    public string? DevicePath { get; init; }

    public string? PciAddress { get; init; }
    public string? UsbPath { get; init; }
    public string? Driver { get; init; }
    public bool Wireless { get; init; }

    /// <summary>up, down, dormant, lowerlayerdown, or unknown (many tunnels say unknown while working).</summary>
    public string OperState { get; init; } = "unknown";

    public bool Carrier { get; init; }
    public long SpeedMbps { get; init; }
    public string? Duplex { get; init; }
    public string? Mac { get; init; }
    public int Mtu { get; init; }

    /// <summary>ethernet, wifi, bridge, bond, vlan, veth, tap, tun, wireguard, vpn, loopback, or virtual.</summary>
    public required string Kind { get; init; }

    /// <summary>The bridge or bond this interface belongs to.</summary>
    public string? Master { get; init; }

    public List<string> Members { get; } = [];

    /// <summary>The MAC was made up by the kernel or set by software rather than burned into the card.</summary>
    public bool RandomMac { get; init; }

    public bool Physical => DevicePath is not null && Kind is "ethernet" or "wifi";
    public bool Up => OperState == "up" || (OperState == "unknown" && Carrier);
}

internal sealed record DefaultRoute(string Interface, IPAddress Gateway, int Metric);

internal readonly record struct NetCounters(long BytesIn, long PacketsIn, long ErrorsIn, long DroppedIn,
    long BytesOut, long PacketsOut, long ErrorsOut, long DroppedOut);

/// <summary>What the Wi-Fi card says about its connection.</summary>
internal sealed record WifiLink(string Interface, bool Connected, string? Ssid, string? Bssid, int? SignalDbm, double? FrequencyMhz,
    double? RxMbps, double? TxMbps, string? Standard, string? Security, int? Channel);

/// <summary>Network interfaces, routes, neighbors, DNS, and counters, from /sys/class/net and /proc/net.</summary>
internal static partial class NetLinks
{
    public static List<NetLink> Read()
    {
        var links = new List<NetLink>();
        foreach (var name in Sys.Children("/sys/class/net"))
        {
            var path = $"/sys/class/net/{name}";
            var device = Sys.Real(Path.Combine(path, "device"));
            var type = Sys.Int(Path.Combine(path, "type")) ?? 0;
            var wireless = Sys.Exists(Path.Combine(path, "wireless")) || Sys.Exists(Path.Combine(path, "phy80211"));
            var driver = device is null ? null : Sys.LinkName(Path.Combine(path, "device", "driver"));
            var kind = type == 772 ? "loopback"
                : wireless ? "wifi"
                : Sys.Exists(Path.Combine(path, "bridge")) ? "bridge"
                : Sys.Exists(Path.Combine(path, "bonding")) ? "bond"
                : name.StartsWith("veth", StringComparison.Ordinal) ? "veth"
                : name.StartsWith("wg", StringComparison.Ordinal) || type == 65534 && name.Contains("wg", StringComparison.Ordinal) ? "wireguard"
                : name.StartsWith("tailscale", StringComparison.Ordinal) || name.StartsWith("zt", StringComparison.Ordinal) || name.StartsWith("tun", StringComparison.Ordinal) || type == 65534 ? "vpn"
                : name.StartsWith("vnet", StringComparison.Ordinal) || name.StartsWith("tap", StringComparison.Ordinal) ? "tap"
                : Sys.Exists($"/proc/net/vlan/{name}") ? "vlan"
                : device is not null ? "ethernet"
                : "virtual";
            var speed = Sys.Long(Path.Combine(path, "speed")) ?? 0;
            var mac = Sys.Read(Path.Combine(path, "address"));
            var assign = Sys.Int(Path.Combine(path, "addr_assign_type")) ?? 0;
            var link = new NetLink
            {
                Name = name,
                Index = Sys.Int(Path.Combine(path, "ifindex")) ?? 0,
                DevicePath = device,
                PciAddress = Pci.AddressIn(device),
                UsbPath = device is not null && device.Contains("/usb", StringComparison.Ordinal) ? UsbAncestor(device) : null,
                Driver = driver,
                Wireless = wireless,
                OperState = Sys.Read(Path.Combine(path, "operstate")) ?? "unknown",
                Carrier = Sys.Read(Path.Combine(path, "carrier")) == "1",
                SpeedMbps = speed > 0 && speed < 1_000_000 ? speed : 0,
                Duplex = Sys.Read(Path.Combine(path, "duplex")),
                Mac = mac is null || mac == "00:00:00:00:00:00" || mac.Length != 17 ? null : mac.ToUpperInvariant(),
                Mtu = Sys.Int(Path.Combine(path, "mtu")) ?? 0,
                Kind = kind,
                Master = Sys.LinkName(Path.Combine(path, "master")),
                RandomMac = assign is 1 or 3
            };
            links.Add(link);
        }
        foreach (var link in links)
            if (link.Master is not null && links.FirstOrDefault(l => l.Name == link.Master) is { } master)
                master.Members.Add(link.Name);
        return links;
    }

    // "/sys/devices/pci0000:00/0000:00:14.0/usb1/1-3/1-3:1.0/net/enx…" → ".../usb1/1-3"
    private static string? UsbAncestor(string path)
    {
        var parts = path.Split('/');
        var index = Array.FindLastIndex(parts, p => Regex.IsMatch(p, @"^\d+-[\d.]+$"));
        return index > 0 ? string.Join('/', parts[..(index + 1)]) : null;
    }

    public static List<DefaultRoute> ReadDefaultRoutes() =>
        Sys.Lines("/proc/net/route").Skip(1).Select(ParseRoute).Where(r => r is not null).Select(r => r!).OrderBy(r => r.Metric).ToList();

    /// <summary>A default-route line from /proc/net/route (destination 0, gateway set), or null.</summary>
    public static DefaultRoute? ParseRoute(string line)
    {
        var f = line.Split((char[])['\t', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 8 || f[1] != "00000000" || f[7] != "00000000") return null;
        if (!uint.TryParse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var gateway) || gateway == 0) return null;
        var flags = int.TryParse(f[3], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fl) ? fl : 0;
        if ((flags & 0x1) == 0) return null; // RTF_UP
        // The kernel prints addresses in host (little-endian) order.
        var address = new IPAddress(BitConverter.GetBytes(gateway));
        return new DefaultRoute(f[0], address, int.TryParse(f[6], out var metric) ? metric : 0);
    }

    /// <summary>The neighbor cache: complete entries only (flag 0x2).</summary>
    public static List<(IPAddress Address, string Mac, string Interface)> ReadArp()
    {
        var result = new List<(IPAddress, string, string)>();
        foreach (var line in Sys.Lines("/proc/net/arp").Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || !IPAddress.TryParse(f[0], out var address)) continue;
            var flags = f[2].StartsWith("0x", StringComparison.Ordinal) && int.TryParse(f[2][2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;
            if ((flags & 0x2) == 0 || f[3] == "00:00:00:00:00:00") continue;
            result.Add((address, f[3].ToUpperInvariant(), f[5]));
        }
        return result;
    }

    /// <summary>
    /// The DNS servers actually asked. With systemd-resolved, /etc/resolv.conf only names the local stub
    /// (127.0.0.53), so the upstream list comes from resolved's own file.
    /// </summary>
    public static (List<IPAddress> Servers, bool Resolved) ReadDnsServers()
    {
        var resolved = Sys.Exists("/run/systemd/resolve/resolv.conf");
        var file = resolved ? "/run/systemd/resolve/resolv.conf" : "/etc/resolv.conf";
        var servers = Sys.Lines(file)
            .Select(l => l.Trim())
            .Where(l => l.StartsWith("nameserver", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1))
            .Select(a => IPAddress.TryParse(a, out var ip) ? ip : null)
            .Where(ip => ip is not null && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
            .Select(ip => ip!)
            .Distinct()
            .ToList();
        return (servers, resolved);
    }

    public static Dictionary<string, NetCounters> ReadCounters()
    {
        var result = new Dictionary<string, NetCounters>(StringComparer.Ordinal);
        foreach (var line in Sys.Lines("/proc/net/dev").Skip(2))
            if (ParseNetDev(line) is { } entry) result[entry.Name] = entry.Counters;
        return result;
    }

    public static (string Name, NetCounters Counters)? ParseNetDev(string line)
    {
        var colon = line.IndexOf(':');
        if (colon < 0) return null;
        var name = line[..colon].Trim();
        var f = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 16) return null;
        long N(int i) => long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (name, new NetCounters(N(0), N(1), N(2), N(3), N(8), N(9), N(10), N(11)));
    }

    /// <summary>The Wi-Fi association, from iw when installed, else NetworkManager, else the kernel's signal level.</summary>
    public static WifiLink ReadWifi(NetLink link)
    {
        if (Tools.Run("iw", "dev", link.Name, "link") is { } iw)
        {
            var parsed = ParseIwLink(link.Name, iw);
            if (parsed.Connected && Tools.Run("iw", "dev", link.Name, "station", "dump") is { } station)
                parsed = parsed with { RxMbps = MatchDouble(station, @"rx bitrate:\s*([\d.]+)") ?? parsed.RxMbps };
            if (parsed.Connected && NmcliWifi(link.Name) is { } nm)
                parsed = parsed with { Security = nm.Security ?? parsed.Security };
            return parsed;
        }
        if (NmcliWifi(link.Name) is { } fromNm) return fromNm;
        // /proc/net/wireless: signal level in dBm for the associated access point.
        foreach (var line in Sys.Lines("/proc/net/wireless").Skip(2))
        {
            var f = line.Split((char[])[' ', ':'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length >= 4 && f[0] == link.Name && double.TryParse(f[3].TrimEnd('.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var level))
                return new WifiLink(link.Name, link.Up, null, null, (int)level, null, null, null, null, null, null);
        }
        return new WifiLink(link.Name, false, null, null, null, null, null, null, null, null, null);
    }

    public static WifiLink ParseIwLink(string name, string text)
    {
        if (!text.StartsWith("Connected to", StringComparison.Ordinal))
            return new WifiLink(name, false, null, null, null, null, null, null, null, null, null);
        var bssid = Regex.Match(text, @"Connected to ([0-9a-fA-F:]{17})").Groups[1].Value.ToUpperInvariant();
        var ssid = Regex.Match(text, @"^\s*SSID: (.*)$", RegexOptions.Multiline).Groups[1].Value.Trim();
        var frequency = MatchDouble(text, @"freq:\s*([\d.]+)");
        var signal = MatchDouble(text, @"signal:\s*(-?[\d.]+)\s*dBm");
        var tx = MatchDouble(text, @"tx bitrate:\s*([\d.]+)");
        var rx = MatchDouble(text, @"rx bitrate:\s*([\d.]+)");
        var rateLine = Regex.Match(text, @"tx bitrate:[^\n]*").Value;
        var standard = rateLine.Contains("EHT", StringComparison.Ordinal) ? "Wi-Fi 7 (802.11be)"
            : rateLine.Contains("HE-", StringComparison.Ordinal) ? "Wi-Fi 6 (802.11ax)"
            : rateLine.Contains("VHT", StringComparison.Ordinal) ? "Wi-Fi 5 (802.11ac)"
            : rateLine.Contains("MCS", StringComparison.Ordinal) ? "Wi-Fi 4 (802.11n)"
            : null;
        if (standard?.StartsWith("Wi-Fi 6", StringComparison.Ordinal) == true && frequency > 5925) standard = "Wi-Fi 6E (802.11ax)";
        return new WifiLink(name, true, ssid.Length == 0 ? null : ssid, bssid.Length == 0 ? null : bssid,
            signal is null ? null : (int)Math.Round(signal.Value), frequency, rx, tx, standard, null, ChannelOf(frequency));
    }

    // NetworkManager's view: "*:AA\:BB\:…:Home:5180 MHz:866 Mbit/s:82:WPA2" (colons inside fields are escaped).
    private static WifiLink? NmcliWifi(string name)
    {
        var text = Tools.Run("nmcli", "-t", "-f", "IN-USE,BSSID,SSID,FREQ,RATE,SIGNAL,SECURITY", "device", "wifi", "list", "ifname", name, "--rescan", "no");
        if (text is null) return null;
        foreach (var line in text.Split('\n'))
        {
            var f = SplitNmcli(line);
            if (f.Count < 7 || f[0] != "*") continue;
            var frequency = MatchDouble(f[3], @"([\d.]+)");
            var percent = int.TryParse(f[5], out var p) ? p : (int?)null;
            return new WifiLink(name, true, f[2].Length == 0 ? null : f[2], f[1].ToUpperInvariant(),
                percent is null ? null : percent.Value / 2 - 100, frequency, MatchDouble(f[4], @"([\d.]+)"), null, null,
                f[6].Length == 0 || f[6] == "--" ? "Open (no encryption)" : f[6], ChannelOf(frequency));
        }
        return null;
    }

    public static List<string> SplitNmcli(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\\' && i + 1 < line.Length) current.Append(line[++i]);
            else if (line[i] == ':') { fields.Add(current.ToString()); current.Clear(); }
            else current.Append(line[i]);
        }
        fields.Add(current.ToString().TrimEnd('\r'));
        return fields;
    }

    public static int? ChannelOf(double? mhz) => mhz switch
    {
        null => null,
        2484 => 14,
        >= 2412 and < 2484 => (int)((mhz.Value - 2407) / 5),
        >= 5950 and <= 7125 => (int)((mhz.Value - 5950) / 5),
        >= 5000 and < 5950 => (int)((mhz.Value - 5000) / 5),
        _ => null
    };

    private static double? MatchDouble(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
