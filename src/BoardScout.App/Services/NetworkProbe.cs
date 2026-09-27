using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using BoardScout.Models;
using BoardScout.UI;

namespace BoardScout.Services;

internal sealed record AdapterFacts(
    string Id,
    string Name,
    string Description,
    NetworkInterfaceType Type,
    bool Up,
    long SpeedBitsPerSecond,
    string? Mac,
    IReadOnlyList<string> IPv4,
    IReadOnlyList<(string Address, string Scope)> IPv6,
    IReadOnlyList<IPAddress> Gateways,
    IReadOnlyList<IPAddress> DnsServers,
    IReadOnlyList<IPAddress> DhcpServers,
    int Index,
    bool Primary);

internal sealed record RouterFacts(IPAddress Address, string? Mac, string? HostName, long? PingMs);

internal sealed record DnsFacts(IPAddress Address, string? HostName);

internal sealed record WifiFacts(
    Guid InterfaceId,
    string State,
    string? Ssid,
    string? Bssid,
    int? SignalPercent,
    int? RssiDbm,
    int? Channel,
    double? FrequencyMhz,
    string? Phy,
    double? ReceiveMbps,
    double? TransmitMbps,
    string? Security);

internal sealed record NetworkFacts(
    IReadOnlyList<AdapterFacts> Adapters,
    IReadOnlyDictionary<string, RouterFacts> Routers,
    IReadOnlyDictionary<string, DnsFacts> Dns,
    IReadOnlyList<WifiFacts> Wifi,
    bool? InternetAccess);

/// <summary>
/// The network path out of this PC: adapters and their addresses, the router (gateway) with its MAC and
/// name, DNS servers, Wi-Fi association details, and whether Windows sees Internet access. Everything here
/// stays on the local network. The public IP lookup is separate and only runs when the user asks.
/// </summary>
internal static class NetworkProbe
{
    public const string WanLookupUrl = "https://1.1.1.1/cdn-cgi/trace";

    public static NetworkFacts Capture()
    {
        var adapters = ReadAdapters();
        var gateways = adapters.Where(a => a.Up).SelectMany(a => a.Gateways).Distinct().ToList();
        var dnsServers = adapters.Where(a => a.Up).SelectMany(a => a.DnsServers).Distinct().ToList();

        // Reverse lookups and pings run in parallel so a silent router costs one timeout, not several.
        var routerTasks = gateways.ToDictionary(g => g.ToString(), g => Task.Run(() => ReadRouter(g)));
        var dnsTasks = dnsServers.ToDictionary(d => d.ToString(), d => Task.Run(async () => new DnsFacts(d, await ReverseLookupAsync(d))));
        var wifi = ReadWifi();
        var internet = InternetAccess();
        Task.WaitAll([.. routerTasks.Values, .. dnsTasks.Values], TimeSpan.FromSeconds(3));

        return new NetworkFacts(
            adapters,
            routerTasks.Where(t => t.Value.IsCompletedSuccessfully).ToDictionary(t => t.Key, t => t.Value.Result),
            dnsTasks.Where(t => t.Value.IsCompletedSuccessfully).ToDictionary(t => t.Key, t => t.Value.Result),
            wifi,
            internet);
    }

    private static List<AdapterFacts> ReadAdapters()
    {
        var primaryIndex = BestInterfaceIndex();
        var list = new List<AdapterFacts>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            try
            {
                var properties = nic.GetIPProperties();
                var index = -1;
                try { index = properties.GetIPv4Properties()?.Index ?? -1; } catch (NetworkInformationException) { }
                var ipv4 = properties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(u => $"{u.Address}/{u.PrefixLength}")
                    .ToList();
                var ipv6 = properties.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(u => (u.Address.ToString(), Scope(u)))
                    .ToList();
                var mac = nic.GetPhysicalAddress().GetAddressBytes();
                list.Add(new AdapterFacts(
                    nic.Id,
                    nic.Name,
                    nic.Description,
                    nic.NetworkInterfaceType,
                    nic.OperationalStatus == OperationalStatus.Up,
                    nic.Speed,
                    mac.Length == 6 ? string.Join(':', mac.Select(b => b.ToString("X2"))) : null,
                    ipv4,
                    ipv6,
                    properties.GatewayAddresses.Select(g => g.Address)
                        .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any)).ToList(),
                    properties.DnsAddresses.Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToList(),
                    properties.DhcpServerAddresses.ToList(),
                    index,
                    index >= 0 && index == primaryIndex));
            }
            catch (NetworkInformationException)
            {
            }
        }
        return list;
    }

    private static string Scope(UnicastIPAddressInformation address)
    {
        if (address.Address.IsIPv6LinkLocal) return "link-local";
        if (address.Address.IsIPv6UniqueLocal) return "unique-local";
        return address.SuffixOrigin == SuffixOrigin.Random ? "temporary" : "global";
    }

    // Which interface Windows would use to reach the Internet. A routing-table lookup; nothing is sent.
    private static int BestInterfaceIndex()
    {
        var target = BitConverter.ToUInt32(IPAddress.Parse("1.1.1.1").GetAddressBytes(), 0);
        return GetBestInterface(target, out var index) == 0 ? (int)index : -1;
    }

    private static RouterFacts ReadRouter(IPAddress gateway)
    {
        string? mac = null;
        var buffer = new byte[6];
        var length = buffer.Length;
        if (SendARP(BitConverter.ToInt32(gateway.GetAddressBytes(), 0), 0, buffer, ref length) == 0 && length == 6)
            mac = string.Join(':', buffer.Select(b => b.ToString("X2")));

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

        return new RouterFacts(gateway, mac, ReverseLookupAsync(gateway).GetAwaiter().GetResult(), ping);
    }

    // A plain DNS PTR query. Dns.GetHostEntry falls back to NetBIOS and waits ~1.5 s on hosts without a name.
    private static Task<string?> ReverseLookupAsync(IPAddress address) => Task.Run(() =>
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return null;
        var octets = address.GetAddressBytes();
        var query = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.in-addr.arpa";
        IntPtr records = IntPtr.Zero;
        try
        {
            if (DnsQuery(query, DnsTypePtr, DnsQueryNoNetbt | DnsQueryNoMulticast, IntPtr.Zero, out records, IntPtr.Zero) != 0)
                return null;
            // DNS_RECORD: pNext, pName, wType @16 ... Data @32 (for PTR: the host name pointer).
            for (var record = records; record != IntPtr.Zero; record = Marshal.ReadIntPtr(record))
            {
                if ((ushort)Marshal.ReadInt16(record, 2 * IntPtr.Size) != DnsTypePtr) continue;
                var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(record, 2 * IntPtr.Size + 16));
                if (!string.IsNullOrWhiteSpace(name)) return name.TrimEnd('.');
            }
            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (records != IntPtr.Zero) DnsRecordListFree(records, 1);
        }
    });

    // What Windows' own connectivity check (NCSI) concluded. Reading it sends nothing.
    private static bool? InternetAccess()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return null;
            dynamic manager = Activator.CreateInstance(type)!;
            try { return (bool)manager.IsConnectedToInternet; }
            finally { Marshal.FinalReleaseComObject(manager); }
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Asks Cloudflare which public IP this network reaches the Internet from. Only on request.</summary>
    public static async Task<WanLookup> LookupPublicAddressAsync(CancellationToken token)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"BoardScout/{VersionButton.AppVersion}");
            var text = await http.GetStringAsync(WanLookupUrl, token);
            var fields = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Split('=', 2))
                .Where(parts => parts.Length == 2)
                .GroupBy(parts => parts[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());
            return fields.TryGetValue("ip", out var ip) && IPAddress.TryParse(ip, out _)
                ? new WanLookup
                {
                    Ok = true,
                    Ip = ip,
                    Location = fields.GetValueOrDefault("loc"),
                    Edge = fields.GetValueOrDefault("colo"),
                    CheckedAt = DateTimeOffset.Now.ToString("HH:mm:ss")
                }
                : new WanLookup { Error = "Cloudflare's answer had no IP address in it." };
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new WanLookup { Error = "Cloudflare did not answer within 8 seconds." };
        }
        catch (HttpRequestException ex)
        {
            return new WanLookup { Error = $"Could not reach Cloudflare: {ex.Message}" };
        }
    }

    private static List<WifiFacts> ReadWifi()
    {
        var result = new List<WifiFacts>();
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return result;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out list) != 0) return result;
            var count = Marshal.ReadInt32(list);
            for (var i = 0; i < count; i++)
            {
                // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then WLAN_INTERFACE_INFO
                // (InterfaceGuid, strInterfaceDescription[256], isState) of 532 bytes each.
                var item = list + 8 + i * 532;
                var guid = Marshal.PtrToStructure<Guid>(item);
                var state = Marshal.ReadInt32(item + 16 + 512);
                result.Add(state == 1 ? ReadConnection(handle, guid) : new WifiFacts(guid, StateName(state),
                    null, null, null, null, null, null, null, null, null, null));
            }
        }
        catch (DllNotFoundException)
        {
            // Windows Server without the Wireless LAN Service.
        }
        finally
        {
            if (list != IntPtr.Zero) WlanFreeMemory(list);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return result;
    }

    private static WifiFacts ReadConnection(IntPtr handle, Guid id)
    {
        string? ssid = null, bssid = null, phy = null, security = null;
        int? signal = null, rssi = null, channel = null;
        double? rx = null, tx = null, frequency = null;
        byte[]? bssidBytes = null;

        if (Query(handle, id, 7, out var connection, out var size) && size >= 604)
        {
            try
            {
                // WLAN_CONNECTION_ATTRIBUTES: isState, mode, strProfileName[256], then WLAN_ASSOCIATION_ATTRIBUTES at 520:
                // SSID length @520, SSID @524, BSS type @556, BSSID @560, PHY type @568, PHY index @572,
                // signal quality @576, rx rate @580, tx rate @584 (kbps); WLAN_SECURITY_ATTRIBUTES at 588.
                var ssidLength = Math.Clamp(Marshal.ReadInt32(connection + 520), 0, 32);
                var ssidBytes = new byte[ssidLength];
                Marshal.Copy(connection + 524, ssidBytes, 0, ssidLength);
                ssid = Encoding.UTF8.GetString(ssidBytes);
                bssidBytes = new byte[6];
                Marshal.Copy(connection + 560, bssidBytes, 0, 6);
                bssid = string.Join(':', bssidBytes.Select(b => b.ToString("X2")));
                phy = PhyName(Marshal.ReadInt32(connection + 568));
                signal = Marshal.ReadInt32(connection + 576);
                rx = Marshal.ReadInt32(connection + 580) / 1000d;
                tx = Marshal.ReadInt32(connection + 584) / 1000d;
                security = Marshal.ReadInt32(connection + 588) == 0 ? "Open (no encryption)" : AuthName(Marshal.ReadInt32(connection + 596));
            }
            finally
            {
                WlanFreeMemory(connection);
            }
        }

        if (Query(handle, id, 8, out var channelData, out _))
        {
            channel = Marshal.ReadInt32(channelData);
            WlanFreeMemory(channelData);
        }
        if (Query(handle, id, 0x10000102, out var rssiData, out _))
        {
            rssi = Marshal.ReadInt32(rssiData);
            WlanFreeMemory(rssiData);
        }
        if (bssidBytes is not null) frequency = CenterFrequency(handle, id, bssidBytes);

        return new WifiFacts(id, "connected", ssid, bssid, signal, rssi, channel, frequency, phy, rx, tx, security);
    }

    // The access point's channel center frequency tells 2.4, 5, and 6 GHz apart (channel numbers overlap).
    private static double? CenterFrequency(IntPtr handle, Guid id, byte[] bssid)
    {
        if (WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out var list) != 0) return null;
        try
        {
            // WLAN_BSS_LIST: dwTotalSize, dwNumberOfItems, then WLAN_BSS_ENTRY (360 bytes): BSSID @40, center frequency (kHz) @92.
            var count = Marshal.ReadInt32(list + 4);
            for (var i = 0; i < count; i++)
            {
                var entry = list + 8 + i * 360;
                var candidate = new byte[6];
                Marshal.Copy(entry + 40, candidate, 0, 6);
                if (!candidate.AsSpan().SequenceEqual(bssid)) continue;
                var kilohertz = Marshal.ReadInt32(entry + 92);
                return kilohertz is > 2_400_000 and < 7_200_000 ? kilohertz / 1000d : null;
            }
            return null;
        }
        finally
        {
            WlanFreeMemory(list);
        }
    }

    public static string? Band(double? frequencyMhz, int? channel)
    {
        if (frequencyMhz is { } mhz)
            return mhz switch { < 2500 => "2.4 GHz", < 5900 => "5 GHz", _ => "6 GHz" };
        return channel switch { null => null, <= 14 => "2.4 GHz", _ => null };
    }

    private static bool Query(IntPtr handle, Guid id, int opcode, out IntPtr data, out int size)
    {
        var result = WlanQueryInterface(handle, ref id, opcode, IntPtr.Zero, out size, out data, out _);
        if (result == 0 && data != IntPtr.Zero) return true;
        data = IntPtr.Zero;
        return false;
    }

    private static string StateName(int state) => state switch
    {
        0 => "not ready",
        1 => "connected",
        2 => "ad hoc",
        3 => "disconnecting",
        4 => "disconnected",
        5 => "associating",
        6 => "discovering",
        7 => "authenticating",
        _ => "unknown"
    };

    private static string? PhyName(int phy) => phy switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        10 => "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => null
    };

    private static string AuthName(int algorithm) => algorithm switch
    {
        1 => "Open",
        2 => "WEP (shared key)",
        3 => "WPA-Enterprise",
        4 => "WPA-Personal",
        6 => "WPA2-Enterprise",
        7 => "WPA2-Personal",
        8 or 11 => "WPA3-Enterprise",
        9 => "WPA3-Personal",
        10 => "OWE (enhanced open)",
        _ => "Encrypted"
    };

    private const ushort DnsTypePtr = 12;
    private const uint DnsQueryNoNetbt = 0x80;
    private const uint DnsQueryNoMulticast = 0x800;

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode, EntryPoint = "DnsQuery_W")]
    private static extern int DnsQuery(string name, ushort type, uint options, IntPtr extra, out IntPtr results, IntPtr reserved);

    [DllImport("dnsapi.dll")]
    private static extern void DnsRecordListFree(IntPtr records, int freeType);

    [DllImport("iphlpapi.dll")]
    private static extern int SendARP(int destination, int source, byte[] macAddress, ref int length);

    [DllImport("iphlpapi.dll")]
    private static extern int GetBestInterface(uint destination, out uint index);

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceId, int opcode, IntPtr reserved,
        out int dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid interfaceId, IntPtr ssid, int bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
