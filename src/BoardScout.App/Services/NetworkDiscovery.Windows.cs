using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace BoardScout.Services;

// Windows half of network discovery: the Wi-Fi card's scan list (WLAN API), Windows' network profiles
// (Network List Manager), and the neighbor cache (GetIpNetTable).
internal static partial class NetworkDiscovery
{
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
