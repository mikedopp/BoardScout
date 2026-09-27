using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace BoardScout.Services;

/// <summary>One access point radio heard in a Wi-Fi scan.</summary>
internal sealed record BeaconRadio(string Bssid, string? Ssid, int RssiDbm, double FrequencyMhz, bool GatewayUnit);

/// <summary>What the router says about itself, and how we know.</summary>
internal sealed record RouterIdentity(
    string? Manufacturer,
    string? Model,
    string? DeviceName,
    string? MacVendor,
    string? CertificateName,
    IReadOnlyList<string> Ssids,
    IReadOnlyList<BeaconRadio> Radios,
    int UnitsInRange)
{
    /// <summary>"TP-Link Deco BE63", or the best name the other clues allow.</summary>
    public string? DisplayName
    {
        get
        {
            if (Model is not null)
                return Manufacturer is not null && !Model.StartsWith(Manufacturer, StringComparison.OrdinalIgnoreCase)
                    ? $"{Manufacturer} {Model}"
                    : Model;
            if (CertificateName is not null && CertificateBrands.FirstOrDefault(b => CertificateName.Contains(b.Key, StringComparison.OrdinalIgnoreCase)) is { Value: { } brand })
                return brand;
            return MacVendor is null ? null : $"{MacVendor} router";
        }
    }

    public string? NameSource => Model is not null ? "its Wi-Fi beacon"
        : CertificateName is not null && CertificateBrands.Any(b => CertificateName.Contains(b.Key, StringComparison.OrdinalIgnoreCase)) ? "its web interface certificate"
        : MacVendor is not null ? "the maker of its network card"
        : null;

    // Names routers put in the certificate of their own web interface.
    private static readonly Dictionary<string, string> CertificateBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tplinkdeco.net"] = "TP-Link Deco",
        ["tplinkwifi.net"] = "TP-Link router",
        ["tplinklogin.net"] = "TP-Link router",
        ["routerlogin.net"] = "NETGEAR router",
        ["orbilogin"] = "NETGEAR Orbi",
        ["router.asus.com"] = "ASUS router",
        ["asusrouter.com"] = "ASUS router",
        ["myrouter.local"] = "Linksys router",
        ["linksyssmartwifi"] = "Linksys router",
        ["fritz.box"] = "AVM FRITZ!Box",
        ["unifi"] = "Ubiquiti UniFi",
        ["eero"] = "eero",
        ["mikrotik"] = "MikroTik router",
        ["openwrt"] = "OpenWrt router",
        ["synology"] = "Synology router"
    };
}

/// <summary>Another device on the local network.</summary>
internal sealed record LanDevice(
    IPAddress Address, string? Mac, string? Vendor, string? Name, string? NameSource, string? Model, bool RandomMac);

/// <summary>What a device's UPnP description says about it (friendly name, model, maker).</summary>
internal sealed record UpnpDescription(IPAddress Address, string? FriendlyName, string? Model, string? Manufacturer);

/// <summary>Windows' own profile for a network: the name in Settings, and Public or Private.</summary>
internal sealed record NetworkProfile(Guid AdapterId, string Name, string Category, DateTime? FirstConnected);

internal sealed record DiscoveryResult(
    IReadOnlyDictionary<string, RouterIdentity> Routers,
    IReadOnlyList<LanDevice> Devices,
    IReadOnlyDictionary<Guid, NetworkProfile> Profiles,
    DateTimeOffset At);

/// <summary>
/// Puts real names on the network: the router's make and model (from its own Wi-Fi beacon, which carries
/// WPS device info, or from the certificate of its web interface), Windows' name for each network, and
/// other devices on the LAN (this PC's neighbor cache plus the ones that answer multicast DNS, named by
/// multicast DNS, NetBIOS, or reverse DNS and labeled by maker from their MAC prefix).
/// All of it stays on the local network; nothing needs admin rights.
/// </summary>
internal static partial class NetworkDiscovery
{
    private const int MaxDevices = 64;

    /// <param name="sweep">Ping every address on the local network first, so every device that answers ARP
    /// shows up (only when the user asks: it touches every address on the network).</param>
    public static async Task<DiscoveryResult> RunAsync(NetworkFacts facts, CancellationToken token, bool sweep = false)
    {
        var adapters = facts.Adapters.Where(a => a.Up && a.IPv4.Count > 0).ToList();
        if (sweep) await SweepAsync(adapters, token);
        var profiles = Task.Run(ReadProfiles, token);
        var beacons = ReadBeaconsAsync(facts, token);
        var neighbors = Task.Run(() => ReadNeighbors(adapters), token);
        var responders = Task.WhenAll(adapters.Select(a => MdnsRespondersAsync(a, token)));
        var upnp = Task.WhenAll(adapters.Select(a => UpnpDevicesAsync(a, token)));
        var certificates = facts.Routers.Keys.ToDictionary(k => k, k => CertificateNameAsync(IPAddress.Parse(k), token));

        // LAN devices: the neighbor cache plus anything that answered the multicast DNS or UPnP queries.
        var found = new Dictionary<string, (IPAddress Address, string? Mac, AdapterFacts Adapter)>();
        foreach (var entry in await neighbors) found.TryAdd(entry.Address.ToString(), entry);
        foreach (var (adapter, addresses) in adapters.Zip(await responders))
            foreach (var address in addresses)
                found.TryAdd(address.ToString(), (address, null, adapter));
        var descriptions = new Dictionary<string, UpnpDescription>();
        foreach (var (adapter, list) in adapters.Zip(await upnp))
            foreach (var description in list)
            {
                descriptions.TryAdd(description.Address.ToString(), description);
                found.TryAdd(description.Address.ToString(), (description.Address, null, adapter));
            }
        var skip = facts.Adapters.SelectMany(a => a.IPv4).Select(c => c.Split('/')[0]).ToHashSet();
        var devices = found.Values.Where(d => !skip.Contains(d.Address.ToString())).Take(MaxDevices).ToList();

        var named = await Task.WhenAll(devices.Select(d => NameAsync(d.Address, d.Adapter,
            descriptions.GetValueOrDefault(d.Address.ToString()), token)));
        var vendors = Oui.Resolve(devices.Select(d => d.Mac).Concat(facts.Routers.Values.Select(r => r.Mac)).Where(m => m is not null)!);
        var lan = devices.Zip(named, (d, n) =>
            {
                var description = descriptions.GetValueOrDefault(d.Address.ToString());
                var vendor = Oui.Prefix(d.Mac) is { } prefix && vendors.TryGetValue(prefix, out var v) ? v : null;
                return new LanDevice(d.Address, d.Mac, vendor ?? description?.Manufacturer, n.Name, n.Source,
                    description?.Model, Oui.IsRandomized(d.Mac));
            })
            .OrderBy(d => ToUInt(d.Address))
            .ToList();

        var beaconList = await beacons;
        var routers = new Dictionary<string, RouterIdentity>();
        foreach (var (address, router) in facts.Routers)
        {
            var certificate = await certificates[address];
            var vendor = Oui.Prefix(router.Mac) is { } prefix && vendors.TryGetValue(prefix, out var v) ? v : null;
            routers[address] = Identify(router, beaconList, certificate, vendor);
        }

        return new DiscoveryResult(routers, lan, await profiles, DateTimeOffset.Now);
    }

    // ---- Router identity -----------------------------------------------------------------------

    private static RouterIdentity Identify(RouterFacts router, List<Beacon> beacons, string? certificate, string? vendor)
    {
        var gateway = ParseMac(router.Mac);
        var radios = new List<BeaconRadio>();
        string? manufacturer = null, model = null, deviceName = null;
        var ssids = new List<string>();

        if (gateway is not null)
        {
            // Radios in the router's own MAC block (same first four bytes): the router and, usually, the
            // mesh units sold with it.
            var own = beacons.Where(b => SameBlock(b.Bssid, gateway)).ToList();
            ssids = own.Select(b => b.Ssid).Where(s => !string.IsNullOrEmpty(s)).Distinct().ToList()!;
            // Every radio broadcasting one of those names from the same maker is part of the same network
            // (mesh satellites and extenders included).
            var home = beacons.Where(b => own.Contains(b) ||
                (b.Ssid is not null && ssids.Contains(b.Ssid) && b.Bssid.AsSpan(0, 3).SequenceEqual(gateway.AsSpan(0, 3)))).ToList();
            foreach (var b in home.OrderByDescending(b => b.Rssi))
                radios.Add(new BeaconRadio(FormatMac(b.Bssid), b.Ssid, b.Rssi, b.FrequencyMhz, SameUnit(b.Bssid, gateway)));
            var wps = home.FirstOrDefault(b => b.Model is not null);
            (manufacturer, model, deviceName) = (wps?.Manufacturer, wps?.Model, wps?.DeviceName);
        }

        // One radio per band per unit, so the busiest band tells how many units are in range.
        var units = radios.Count == 0 ? 0 : radios.GroupBy(r => NetworkProbe.Band(r.FrequencyMhz, null)).Max(g => g.Count());
        return new RouterIdentity(manufacturer, model, deviceName, vendor, certificate, ssids, radios, units);
    }

    private static bool SameBlock(byte[] bssid, byte[] gateway) => bssid.AsSpan(0, 4).SequenceEqual(gateway.AsSpan(0, 4));

    // The router unit itself: its radios' BSSIDs sit just above its LAN MAC.
    private static bool SameUnit(byte[] bssid, byte[] gateway)
    {
        if (!SameBlock(bssid, gateway)) return false;
        var a = (bssid[4] << 8) | bssid[5];
        var b = (gateway[4] << 8) | gateway[5];
        return Math.Abs(a - b) <= 16;
    }

    // Windows forgets scan results after a while when Wi-Fi is not in use. If the router's radios are not
    // in the list, ask the Wi-Fi card for a fresh scan (a few seconds, no connection is made).
    private static async Task<List<Beacon>> ReadBeaconsAsync(NetworkFacts facts, CancellationToken token)
    {
        var list = await Task.Run(ReadBeacons, token);
        var gateways = facts.Routers.Values.Select(r => ParseMac(r.Mac)).Where(m => m is not null).Select(m => m!).ToList();
        bool HasHome(List<Beacon> beacons) => beacons.Any(b => gateways.Any(g => SameBlock(b.Bssid, g)));
        if (gateways.Count == 0 || HasHome(list) || !StartWifiScan()) return list;
        for (var i = 0; i < 12 && !HasHome(list); i++)
        {
            await Task.Delay(400, token);
            list = await Task.Run(ReadBeacons, token);
        }
        return list;
    }

    private static bool StartWifiScan()
    {
        IntPtr handle = IntPtr.Zero, interfaces = IntPtr.Zero;
        var started = false;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return false;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out interfaces) != 0) return false;
            var count = Marshal.ReadInt32(interfaces);
            for (var i = 0; i < count; i++)
            {
                var id = Marshal.PtrToStructure<Guid>(interfaces + 8 + i * 532);
                started |= WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0;
            }
        }
        catch (DllNotFoundException)
        {
        }
        finally
        {
            if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return started;
    }

    // Pings every address on each local network (at most the /22 around this PC), so the neighbor list
    // fills with every device that answers ARP, even the ones that ignore the ping itself.
    private static async Task SweepAsync(List<AdapterFacts> adapters, CancellationToken token)
    {
        var targets = new HashSet<IPAddress>();
        foreach (var adapter in adapters)
        {
            foreach (var cidr in adapter.IPv4)
            {
                var parts = cidr.Split('/');
                if (!IPAddress.TryParse(parts[0], out var own) || !int.TryParse(parts[1], out var prefix)) continue;
                if (parts[0].StartsWith("169.254.", StringComparison.Ordinal)) continue;
                prefix = Math.Clamp(prefix, 22, 30);
                var mask = uint.MaxValue << (32 - prefix);
                var network = ToUInt(own) & mask;
                var broadcast = network | ~mask;
                for (var host = network + 1; host < broadcast; host++)
                {
                    var address = new IPAddress(new[] { (byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host });
                    if (!address.Equals(own)) targets.Add(address);
                }
            }
        }
        using var gate = new SemaphoreSlim(128);
        await Task.WhenAll(targets.Select(async target =>
        {
            await gate.WaitAsync(token);
            try
            {
                using var ping = new System.Net.NetworkInformation.Ping();
                await ping.SendPingAsync(target, TimeSpan.FromMilliseconds(600), cancellationToken: token);
            }
            catch (System.Net.NetworkInformation.PingException)
            {
            }
            finally
            {
                gate.Release();
            }
        }));
    }

    private sealed record Beacon(byte[] Bssid, string? Ssid, int Rssi, double FrequencyMhz, string? Manufacturer, string? Model, string? DeviceName);

    // The Wi-Fi card's scan list (no new scan is started): each access point's BSSID, SSID, signal, and
    // the WPS device info many routers include in their beacons.
    private static List<Beacon> ReadBeacons()
    {
        var result = new List<Beacon>();
        IntPtr handle = IntPtr.Zero, interfaces = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return result;
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out interfaces) != 0) return result;
            var count = Marshal.ReadInt32(interfaces);
            for (var i = 0; i < count; i++)
            {
                var id = Marshal.PtrToStructure<Guid>(interfaces + 8 + i * 532);
                if (WlanGetNetworkBssList(handle, ref id, IntPtr.Zero, 3, false, IntPtr.Zero, out var list) != 0) continue;
                try
                {
                    // WLAN_BSS_LIST: dwTotalSize, dwNumberOfItems, WLAN_BSS_ENTRY[] of 360 bytes: SSID length @0,
                    // SSID @4, BSSID @40, RSSI @56, center frequency (kHz) @92, IE offset @352, IE size @356.
                    var total = Marshal.ReadInt32(list);
                    var items = Marshal.ReadInt32(list + 4);
                    for (var j = 0; j < items; j++)
                    {
                        var entry = list + 8 + j * 360;
                        var ssidLength = Math.Clamp(Marshal.ReadInt32(entry), 0, 32);
                        var ssid = new byte[ssidLength];
                        Marshal.Copy(entry + 4, ssid, 0, ssidLength);
                        var bssid = new byte[6];
                        Marshal.Copy(entry + 40, bssid, 0, 6);
                        var ieOffset = Marshal.ReadInt32(entry + 352);
                        var ieSize = Marshal.ReadInt32(entry + 356);
                        byte[] ies = [];
                        if (ieSize is > 0 and < 4096 && 8 + j * 360 + ieOffset + ieSize <= total)
                        {
                            ies = new byte[ieSize];
                            Marshal.Copy(entry + ieOffset, ies, 0, ieSize);
                        }
                        var (manufacturer, model, deviceName) = ReadWps(ies);
                        result.Add(new Beacon(bssid, ssidLength == 0 ? null : Encoding.UTF8.GetString(ssid),
                            Marshal.ReadInt32(entry + 56), Marshal.ReadInt32(entry + 92) / 1000d, manufacturer, model, deviceName));
                    }
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }
        }
        catch (DllNotFoundException)
        {
        }
        finally
        {
            if (interfaces != IntPtr.Zero) WlanFreeMemory(interfaces);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
        return result;
    }

    // Vendor-specific IE 00:50:F2 type 4 (WPS): attributes 0x1021 manufacturer, 0x1023 model name,
    // 0x1024 model number, 0x1011 device name.
    private static (string? Manufacturer, string? Model, string? DeviceName) ReadWps(byte[] ies)
    {
        string? manufacturer = null, modelName = null, modelNumber = null, deviceName = null;
        for (var p = 0; p + 2 <= ies.Length;)
        {
            int id = ies[p], length = ies[p + 1];
            if (p + 2 + length > ies.Length) break;
            if (id == 221 && length >= 4 && ies[p + 2] == 0x00 && ies[p + 3] == 0x50 && ies[p + 4] == 0xF2 && ies[p + 5] == 0x04)
            {
                for (var a = p + 6; a + 4 <= p + 2 + length;)
                {
                    var type = (ies[a] << 8) | ies[a + 1];
                    var size = (ies[a + 2] << 8) | ies[a + 3];
                    if (a + 4 + size > p + 2 + length) break;
                    var value = Encoding.UTF8.GetString(ies, a + 4, size).Trim('\0', ' ');
                    if (value.Length > 0)
                    {
                        switch (type)
                        {
                            case 0x1021: manufacturer = value; break;
                            case 0x1023: modelName = value; break;
                            case 0x1024: modelNumber = value; break;
                            case 0x1011: deviceName = value; break;
                        }
                    }
                    a += 4 + size;
                }
            }
            p += 2 + length;
        }
        // Model name and number are often the same ("Deco BE63"); some routers put the model in one only.
        var model = modelName ?? modelNumber;
        if (modelName is not null && modelNumber is not null && !modelName.Contains(modelNumber, StringComparison.OrdinalIgnoreCase))
            model = $"{modelName} {modelNumber}";
        return (manufacturer, model, deviceName);
    }

    // The name in the certificate of the router's HTTPS interface ("tplinkdeco.net"). Reading it does not
    // log in or send any request beyond the TLS handshake.
    private static async Task<string?> CertificateNameAsync(IPAddress router, CancellationToken token)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(router, 443, timeout.Token);
            using var tls = new SslStream(tcp.GetStream(), false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = router.ToString(),
                RemoteCertificateValidationCallback = (_, _, _, _) => true
            }, timeout.Token);
            if (tls.RemoteCertificate is null) return null;
            using var certificate = new X509Certificate2(tls.RemoteCertificate);
            var name = certificate.GetNameInfo(X509NameType.DnsName, false);
            return string.IsNullOrWhiteSpace(name) || IPAddress.TryParse(name, out _) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    // ---- Windows network profiles --------------------------------------------------------------

    private static Dictionary<Guid, NetworkProfile> ReadProfiles()
    {
        var result = new Dictionary<Guid, NetworkProfile>();
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null || Activator.CreateInstance(type) is not INetworkListManager manager) return result;
            var connections = manager.GetNetworkConnections();
            for (var i = 0; i < 32; i++)
            {
                connections.Next(1, out var connection, out var fetched);
                if (fetched == 0 || connection is null) break;
                var network = connection.GetNetwork();
                network.GetTimeCreatedAndConnected(out var createdLow, out var createdHigh, out _, out _);
                var created = ((long)createdHigh << 32) | createdLow;
                var category = network.GetCategory() switch { 0 => "Public", 1 => "Private", 2 => "Domain", _ => "Unknown" };
                result[connection.GetAdapterId()] = new NetworkProfile(connection.GetAdapterId(), network.GetName(), category,
                    created > 0 ? DateTime.FromFileTime(created) : null);
            }
        }
        catch
        {
        }
        return result;
    }

    // ---- LAN devices -----------------------------------------------------------------------------

    private static List<(IPAddress Address, string? Mac, AdapterFacts Adapter)> ReadNeighbors(List<AdapterFacts> adapters)
    {
        var result = new List<(IPAddress, string?, AdapterFacts)>();
        var size = 0;
        GetIpNetTable(IntPtr.Zero, ref size, false);
        if (size <= 0) return result;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetIpNetTable(buffer, ref size, true) != 0) return result;
            var count = Marshal.ReadInt32(buffer);
            for (var i = 0; i < count; i++)
            {
                // MIB_IPNETROW: dwIndex, dwPhysAddrLen, bPhysAddr[8], dwAddr, dwType (3 dynamic, 4 static).
                var row = buffer + 4 + i * 24;
                var index = Marshal.ReadInt32(row);
                var length = Marshal.ReadInt32(row + 4);
                var type = Marshal.ReadInt32(row + 20);
                if (type is not (3 or 4) || length != 6) continue;
                var mac = new byte[6];
                Marshal.Copy(row + 8, mac, 0, 6);
                if (mac.All(b => b == 0) || mac.All(b => b == 0xFF) || (mac[0] & 0x01) != 0) continue;
                var address = new IPAddress((uint)Marshal.ReadInt32(row + 16));
                var adapter = adapters.FirstOrDefault(a => a.Index == index && a.IPv4.Any(c => InSubnet(address, c)));
                if (adapter is not null) result.Add((address, FormatMac(mac), adapter));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        return result;
    }

    // Asks "what services are on this network?" over multicast DNS and notes who answers.
    private static async Task<List<IPAddress>> MdnsRespondersAsync(AdapterFacts adapter, CancellationToken token)
    {
        var result = new List<IPAddress>();
        if (!TryLocalAddress(adapter, out var local)) return result;
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var query = MdnsQuery("_services._dns-sd._udp.local");
            await udp.SendAsync(query, new IPEndPoint(MdnsGroup, 5353), token);
            var deadline = DateTime.UtcNow.AddMilliseconds(1200);
            while (DateTime.UtcNow < deadline)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    var reply = await udp.ReceiveAsync(wait.Token);
                    var from = reply.RemoteEndPoint.Address;
                    if (!result.Contains(from) && adapter.IPv4.Any(c => InSubnet(from, c))) result.Add(from);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (SocketException)
        {
        }
        return result;
    }

    private static async Task<(string? Name, string? Source)> NameAsync(IPAddress address, AdapterFacts adapter,
        UpnpDescription? description, CancellationToken token)
    {
        var mdns = MdnsNameAsync(address, adapter, token);
        var netbios = NetBiosNameAsync(address, adapter, token);
        var dns = NetworkProbe.ReverseLookupAsync(address);
        await Task.WhenAll(mdns, netbios, dns);
        if (mdns.Result is { } fromMdns) return (fromMdns, "multicast DNS");
        if (netbios.Result is { } fromNetbios) return (fromNetbios, "NetBIOS");
        if (description?.FriendlyName is { } fromUpnp) return (fromUpnp, "UPnP");
        if (dns.Result is { } fromDns) return (fromDns.Split('.')[0], "DNS");
        return (null, null);
    }

    // SSDP search, then each responder's UPnP description (friendly name, model, maker). Only descriptions
    // served by the responding device itself, on this network, are fetched.
    private static async Task<List<UpnpDescription>> UpnpDevicesAsync(AdapterFacts adapter, CancellationToken token)
    {
        var result = new List<UpnpDescription>();
        if (!TryLocalAddress(adapter, out var local)) return result;
        var locations = new Dictionary<IPAddress, Uri>();
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var search = Encoding.ASCII.GetBytes(
                "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 1\r\nST: upnp:rootdevice\r\n\r\n");
            await udp.SendAsync(search, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900), token);
            var deadline = DateTime.UtcNow.AddMilliseconds(1500);
            while (DateTime.UtcNow < deadline)
            {
                using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
                wait.CancelAfter(deadline - DateTime.UtcNow);
                try
                {
                    var reply = await udp.ReceiveAsync(wait.Token);
                    var from = reply.RemoteEndPoint.Address;
                    var location = SsdpLocation().Match(Encoding.ASCII.GetString(reply.Buffer)).Groups[1].Value;
                    if (!adapter.IPv4.Any(c => InSubnet(from, c)) || locations.ContainsKey(from)) continue;
                    if (Uri.TryCreate(location, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttp &&
                        IPAddress.TryParse(uri.Host, out var host) && host.Equals(from))
                        locations[from] = uri;
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
        catch (SocketException)
        {
            return result;
        }

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2), MaxResponseContentBufferSize = 256 * 1024 };
        var fetched = await Task.WhenAll(locations.Take(16).Select(async pair =>
        {
            try
            {
                var xml = await http.GetStringAsync(pair.Value, token);
                string? Tag(string name)
                {
                    var match = Regex.Match(xml, $"<{name}>([^<]{{1,120}})</{name}>", RegexOptions.IgnoreCase);
                    return match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
                }
                return new UpnpDescription(pair.Key, Tag("friendlyName"), Tag("modelName"), Tag("manufacturer"));
            }
            catch
            {
                return null;
            }
        }));
        result.AddRange(fetched.Where(d => d is not null)!);
        return result;
    }

    private static async Task<string?> MdnsNameAsync(IPAddress address, AdapterFacts adapter, CancellationToken token)
    {
        if (!TryLocalAddress(adapter, out var local)) return null;
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            var octets = address.GetAddressBytes();
            await udp.SendAsync(MdnsQuery($"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.in-addr.arpa", ptr: true),
                new IPEndPoint(MdnsGroup, 5353), token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(700);
            var reply = await udp.ReceiveAsync(wait.Token);
            var text = Encoding.ASCII.GetString(reply.Buffer.Select(b => b is >= 0x20 and < 0x7F ? b : (byte)'.').ToArray());
            var match = LocalName().Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }
        catch
        {
            return null;
        }
    }

    // NetBIOS node status: Windows PCs and NAS boxes answer with their computer name.
    private static async Task<string?> NetBiosNameAsync(IPAddress address, AdapterFacts adapter, CancellationToken token)
    {
        if (!TryLocalAddress(adapter, out var local)) return null;
        try
        {
            using var udp = new UdpClient(new IPEndPoint(local, 0));
            byte[] query = [0x42, 0x53, 0x00, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0, 0x20, (byte)'C', (byte)'K',
                .. Enumerable.Repeat((byte)'A', 30), 0x00, 0x00, 0x21, 0x00, 0x01];
            await udp.SendAsync(query, new IPEndPoint(address, 137), token);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(700);
            var reply = (await udp.ReceiveAsync(wait.Token)).Buffer;
            // Answer: header (12) + name (34) + type/class/TTL/length (10) + name count @56, then 18-byte entries.
            if (reply.Length < 57 + 18 || reply[56] == 0) return null;
            for (var i = 0; i < reply[56] && 57 + i * 18 + 18 <= reply.Length; i++)
            {
                var entry = 57 + i * 18;
                var flags = (reply[entry + 16] << 8) | reply[entry + 17];
                if (reply[entry + 15] == 0x00 && (flags & 0x8000) == 0) // workstation name, not a group
                    return Encoding.ASCII.GetString(reply, entry, 15).Trim();
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly IPAddress MdnsGroup = IPAddress.Parse("224.0.0.251");

    private static byte[] MdnsQuery(string name, bool ptr = true)
    {
        var bytes = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }
        // Type PTR, class IN with the "unicast response" bit, so the answer comes straight back to us.
        bytes.AddRange([0, 0, (byte)(ptr ? 12 : 255), 0x80, 1]);
        return [.. bytes];
    }

    private static bool TryLocalAddress(AdapterFacts adapter, out IPAddress address)
    {
        address = IPAddress.Any;
        var first = adapter.IPv4.Select(c => c.Split('/')[0]).FirstOrDefault(a => !a.StartsWith("169.254.", StringComparison.Ordinal));
        return first is not null && IPAddress.TryParse(first, out address!);
    }

    internal static bool InSubnet(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix)) return false;
        if (address.AddressFamily != AddressFamily.InterNetwork || network.AddressFamily != AddressFamily.InterNetwork || prefix is < 1 or > 32) return false;
        var mask = uint.MaxValue << (32 - prefix);
        return (ToUInt(address) & mask) == (ToUInt(network) & mask);
    }

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b.Length == 4 ? (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]) : 0;
    }

    private static byte[]? ParseMac(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(char.IsAsciiHexDigit).ToArray());
        return hex.Length == 12 ? Convert.FromHexString(hex) : null;
    }

    private static string FormatMac(byte[] mac) => string.Join(':', mac.Select(b => b.ToString("X2")));

    [GeneratedRegex(@"([A-Za-z0-9][A-Za-z0-9-]{0,62})\.local", RegexOptions.CultureInvariant)]
    private static partial Regex LocalName();

    [GeneratedRegex(@"^LOCATION:\s*(\S+)", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SsdpLocation();

    [DllImport("iphlpapi.dll")]
    private static extern int GetIpNetTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool sort);

    [DllImport("wlanapi.dll")]
    private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [DllImport("wlanapi.dll")]
    private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid interfaceId, IntPtr ssid, int bssType,
        [MarshalAs(UnmanagedType.Bool)] bool securityEnabled, IntPtr reserved, out IntPtr list);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);

    [DllImport("wlanapi.dll")]
    private static extern int WlanScan(IntPtr handle, ref Guid interfaceId, IntPtr ssid, IntPtr ieData, IntPtr reserved);

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworks(int flags);
        INetwork GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [DispId(-4)] object NewEnum { [return: MarshalAs(UnmanagedType.IUnknown)] get; }
        void Next(int count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection, out int fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { get; }
        bool IsConnected { get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        string GetName();
        void SetName(string name);
        string GetDescription();
        void SetDescription(string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.IUnknown)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint lowCreated, out uint highCreated, out uint lowConnected, out uint highConnected);
        bool IsConnectedToInternet { get; }
        bool IsConnected { get; }
        int GetConnectivity();
        int GetCategory();
    }
}
