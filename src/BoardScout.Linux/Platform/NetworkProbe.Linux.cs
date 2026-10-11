using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using BoardScout.Platform;

namespace BoardScout.Services;

// Linux half of the network probe: interfaces and addresses (/sys/class/net, /proc/net), routes, the
// router's MAC from the neighbor cache, NetworkManager's connectivity check, and Wi-Fi from iw or nmcli.
internal static partial class NetworkProbe
{
    /// <summary>A stable id for an interface name, where Windows would use the adapter's GUID.</summary>
    public static Guid InterfaceGuid(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes("boardscout-net:" + name)));

    private static List<AdapterFacts> ReadAdapters()
    {
        var links = NetLinks.Read();
        var routes = NetLinks.ReadDefaultRoutes();
        var (dns, _) = NetLinks.ReadDnsServers();
        var ipv6 = ReadIPv6();
        var primary = routes.FirstOrDefault()?.Interface;
        var list = new List<AdapterFacts>();
        Dictionary<string, NetworkInterface> interfaces;
        try
        {
            interfaces = NetworkInterface.GetAllNetworkInterfaces().GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.First());
        }
        catch (NetworkInformationException)
        {
            interfaces = [];
        }

        foreach (var link in links.Where(l => l.Kind != "loopback"))
        {
            var ipv4 = new List<string>();
            if (interfaces.TryGetValue(link.Name, out var nic))
            {
                try
                {
                    ipv4 = nic.GetIPProperties().UnicastAddresses
                        .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                        .Select(u => $"{u.Address}/{u.PrefixLength}")
                        .ToList();
                }
                catch (NetworkInformationException)
                {
                }
            }
            var gateways = routes.Where(r => r.Interface == link.Name).Select(r => r.Gateway).Distinct().ToList();
            list.Add(new AdapterFacts(
                link.Name,
                link.Name,
                Describe(link),
                link.Kind switch
                {
                    "wifi" => NetworkInterfaceType.Wireless80211,
                    "vpn" or "wireguard" => NetworkInterfaceType.Tunnel,
                    "virtual" => NetworkInterfaceType.Unknown,
                    _ => NetworkInterfaceType.Ethernet
                },
                link.Up,
                link.SpeedMbps * 1_000_000,
                link.Mac,
                ipv4,
                ipv6.GetValueOrDefault(link.Name) ?? [],
                gateways,
                // systemd-resolved keeps one upstream list; it belongs with the interfaces that reach the Internet.
                gateways.Count > 0 ? dns : [],
                ReadDhcpServer(link.Index) is { } dhcp ? [dhcp] : [],
                link.Index,
                link.Name == primary));
        }
        return list;
    }

    private static string Describe(NetLink link) => link.Kind switch
    {
        "bridge" => link.Members.Count > 0 ? $"Bridge over {string.Join(", ", link.Members)}" : "Bridge",
        "bond" => $"Bonded link over {string.Join(", ", link.Members)}",
        "vlan" => "VLAN interface",
        "veth" => "Container link (veth)",
        "tap" => "Virtual machine link (tap)",
        "wireguard" => "WireGuard VPN",
        "vpn" => link.Name.StartsWith("tailscale", StringComparison.Ordinal) ? "Tailscale VPN" : link.Name.StartsWith("zt", StringComparison.Ordinal) ? "ZeroTier VPN" : "VPN or tunnel",
        "virtual" => "Software interface",
        _ => link.Driver is null ? link.Name : $"{link.Name} ({link.Driver} driver)"
    };

    // /proc/net/if_inet6: address, ifindex, prefix, scope, flags (0x01 = temporary privacy address), name.
    private static Dictionary<string, List<(string Address, string Scope)>> ReadIPv6()
    {
        var result = new Dictionary<string, List<(string, string)>>(StringComparer.Ordinal);
        foreach (var line in Sys.Lines("/proc/net/if_inet6"))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 6 || f[0].Length != 32) continue;
            var address = IPAddress.Parse(string.Join(':', Enumerable.Range(0, 8).Select(i => f[0].Substring(i * 4, 4))));
            var flags = int.TryParse(f[4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fl) ? fl : 0;
            var scope = address.IsIPv6LinkLocal ? "link-local"
                : address.IsIPv6UniqueLocal ? "unique-local"
                : (flags & 0x01) != 0 ? "temporary"
                : "global";
            if (!result.TryGetValue(f[5], out var addresses)) result[f[5]] = addresses = [];
            addresses.Add((address.ToString(), scope));
        }
        return result;
    }

    // systemd-networkd keeps each DHCP lease in /run/systemd/netif/leases/<ifindex>.
    private static IPAddress? ReadDhcpServer(int index)
    {
        foreach (var line in Sys.Lines($"/run/systemd/netif/leases/{index}"))
            if (line.StartsWith("SERVER_ADDRESS=", StringComparison.Ordinal) && IPAddress.TryParse(line[15..], out var server))
                return server;
        return null;
    }

    private static RouterFacts ReadRouter(IPAddress gateway)
    {
        long? ping = null;
        try
        {
            using var sender = new Ping();
            var reply = sender.Send(gateway, 1000);
            if (reply.Status == IPStatus.Success) ping = reply.RoundtripTime;
        }
        catch (PingException)
        {
        }
        // After the ping the neighbor cache holds the router's MAC.
        var mac = NetLinks.ReadArp().FirstOrDefault(n => n.Address.Equals(gateway)).Mac;
        return new RouterFacts(gateway, mac, ReverseLookupAsync(gateway).GetAwaiter().GetResult(), ping);
    }

    /// <summary>The name DNS gives an address (a PTR lookup through the system resolver), or null.</summary>
    internal static async Task<string?> ReverseLookupAsync(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        try
        {
            var lookup = Dns.GetHostEntryAsync(address);
            if (await Task.WhenAny(lookup, Task.Delay(1500)) != lookup) return null;
            var name = (await lookup).HostName;
            return string.IsNullOrWhiteSpace(name) || name == address.ToString() ? null : name.TrimEnd('.');
        }
        catch
        {
            return null;
        }
    }

    // What NetworkManager's own connectivity check last concluded (nothing is sent to read it).
    private static bool? InternetAccess() => Tools.Run("nmcli", "-t", "networking", "connectivity")?.Trim() switch
    {
        "full" => true,
        "none" or "limited" or "portal" => false,
        _ => null
    };

    private static List<WifiFacts> ReadWifi()
    {
        var result = new List<WifiFacts>();
        foreach (var link in NetLinks.Read().Where(l => l.Wireless))
        {
            var wifi = NetLinks.ReadWifi(link);
            result.Add(new WifiFacts(
                InterfaceGuid(link.Name),
                wifi.Connected ? "connected" : link.OperState == "down" ? "disconnected" : link.OperState,
                wifi.Ssid,
                wifi.Bssid,
                wifi.SignalDbm is { } dbm ? Math.Clamp(2 * (dbm + 100), 0, 100) : null,
                wifi.SignalDbm,
                wifi.Channel,
                wifi.FrequencyMhz,
                wifi.Standard,
                wifi.RxMbps,
                wifi.TxMbps,
                wifi.Security));
        }
        return result;
    }
}
