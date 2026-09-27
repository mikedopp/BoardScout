using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace BoardScout.Services;

/// <summary>One present device from the Windows device tree.</summary>
internal sealed class DeviceNode
{
    public uint Handle { get; init; }
    public string InstanceId { get; init; } = "";
    public string? Class { get; init; }
    public string Name { get; init; } = "";
    public string? BusReportedName { get; init; }
    public string? DriverKey { get; init; }
    public string? Service { get; init; }
    public uint Problem { get; init; }
    public PciLink? Pci { get; init; }
    public UsbPort? Usb { get; set; }

    /// <summary>Number of downstream ports, for USB hubs (root hubs included).</summary>
    public int? HubPorts { get; set; }
    public DeviceNode? Parent { get; set; }
    public List<DeviceNode> Children { get; } = [];

    public bool IdStarts(string prefix) => InstanceId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    public bool IsClass(string name) => string.Equals(Class, name, StringComparison.OrdinalIgnoreCase);

    public IEnumerable<DeviceNode> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}

/// <summary>Negotiated and maximum PCIe link. Speed is the generation: 1 = 2.5 GT/s … 5 = 32 GT/s.</summary>
internal sealed record PciLink(int Speed, int Width, int MaxSpeed, int MaxWidth)
{
    public bool BelowMax => Speed < MaxSpeed || Width < MaxWidth;
}

/// <summary>What a USB hub reports for one of its ports. Flags are USB_NODE_CONNECTION_INFORMATION_EX_V2 flags.</summary>
internal sealed record UsbPort(int Port, int Speed, int Flags, bool IsHub)
{
    public bool SuperSpeedPlus => (Flags & 4) != 0;
    public bool SuperSpeed => (Flags & 1) != 0;
    public bool SuperSpeedCapable => (Flags & 2) != 0;
    public bool SuperSpeedPlusCapable => (Flags & 8) != 0;
}

/// <summary>What a disk reports about itself. Bus is the STORAGE_BUS_TYPE (7 USB, 11 SATA, 17 NVMe).</summary>
internal sealed record DiskFacts(int Number, string? Vendor, string? Product, string? Firmware, int Bus, long? SizeBytes, double? TemperatureC);

/// <summary>One active display output. Technology is DISPLAYCONFIG_VIDEO_OUTPUT_TECHNOLOGY.</summary>
internal sealed record DisplayTarget(string? MonitorInstance, string? FriendlyName, uint Technology, int Connector, int? Width, int? Height, double? RefreshHz);

/// <summary>
/// Reads the Windows device tree through cfgmgr32 (no admin needed): every present device, its class and
/// names, the PCIe link each PCI device negotiated, and — from the USB hubs themselves — which port each
/// USB device is on and the speed it actually runs at.
/// </summary>
internal static class DeviceTree
{
    private static readonly Guid PciPropertyKeys = new("3AB22E31-8264-4B4E-9AF5-A8D2D8E33E62");
    private static readonly Guid BusReportedDescription = new("540B947E-8B40-45BC-A8A2-6A0B894CBDA2");
    private static readonly Guid UsbHubInterface = new("F18A0E88-C30C-11D0-8815-00A0C906BED8");
    private static readonly Guid DiskInterface = new("53F56307-B6BF-11D0-94F2-00A0C91EFB8B");

    public static DeviceNode? Capture()
    {
        if (CM_Locate_DevNodeW(out var root, null, 0) != 0) return null;
        var tree = Read(root, null, 0);
        AttachUsbPorts(tree);
        return tree;
    }

    private static DeviceNode Read(uint handle, DeviceNode? parent, int depth)
    {
        var id = DeviceId(handle);
        CM_Get_DevNode_Status(out _, out var problem, handle, 0);
        var node = new DeviceNode
        {
            Handle = handle,
            InstanceId = id,
            Class = Registry(handle, CmDrpClass),
            Name = Registry(handle, CmDrpFriendlyName) ?? Registry(handle, CmDrpDeviceDesc) ?? id,
            BusReportedName = id.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase) ? StringProperty(handle, BusReportedDescription, 4) : null,
            DriverKey = Registry(handle, CmDrpDriver),
            Service = Registry(handle, CmDrpService),
            Problem = problem,
            Pci = id.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase) ? ReadPciLink(handle) : null,
            Parent = parent
        };
        if (depth < 32 && CM_Get_Child(out var child, handle, 0) == 0)
        {
            do node.Children.Add(Read(child, node, depth + 1));
            while (CM_Get_Sibling(out child, child, 0) == 0);
        }
        return node;
    }

    private static PciLink? ReadPciLink(uint handle)
    {
        var speed = UIntProperty(handle, PciPropertyKeys, 9);
        var width = UIntProperty(handle, PciPropertyKeys, 10);
        if (speed is null or 0 || width is null or 0) return null;
        return new PciLink((int)speed, (int)width,
            (int)(UIntProperty(handle, PciPropertyKeys, 11) ?? speed.Value),
            (int)(UIntProperty(handle, PciPropertyKeys, 12) ?? width.Value));
    }

    // Asks every USB hub which device sits on which port and at what speed.
    private static void AttachUsbPorts(DeviceNode tree)
    {
        var hubs = new Dictionary<uint, DeviceNode>();
        foreach (var node in tree.Descendants())
            if (node.IdStarts(@"USB\")) hubs[node.Handle] = node;

        foreach (var (devInst, path) in InterfacePaths(UsbHubInterface))
        {
            if (!hubs.TryGetValue(devInst, out var hub)) continue;
            using var handle = CreateFile(path, GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            var ports = PortCount(handle);
            hub.HubPorts = ports;
            for (var port = 1; port <= ports; port++)
            {
                var connection = ConnectionInfo(handle, port);
                if (connection is null) continue;
                var driverKey = PortDriverKey(handle, port);
                var child = hub.Children.FirstOrDefault(c =>
                    driverKey is not null && string.Equals(c.DriverKey, driverKey, StringComparison.OrdinalIgnoreCase));
                if (child is not null) child.Usb = connection;
            }
        }
    }

    private static IEnumerable<(uint DevInst, string Path)> InterfacePaths(Guid interfaceClass)
    {
        var guid = interfaceClass;
        var set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (set == new IntPtr(-1)) yield break;
        try
        {
            for (var index = 0; ; index++)
            {
                var data = new SpDeviceInterfaceData { Size = Marshal.SizeOf<SpDeviceInterfaceData>() };
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref data)) yield break;
                var info = new SpDevinfoData { Size = Marshal.SizeOf<SpDevinfoData>() };
                SetupDiGetDeviceInterfaceDetailW(set, ref data, IntPtr.Zero, 0, out var required, ref info);
                if (required <= 0) continue;
                var detail = Marshal.AllocHGlobal(required);
                try
                {
                    Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (SetupDiGetDeviceInterfaceDetailW(set, ref data, detail, required, out _, ref info))
                    {
                        var path = Marshal.PtrToStringUni(detail + 4);
                        if (path is not null) yield return (info.DevInst, path);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static int PortCount(SafeFileHandle hub)
    {
        var buffer = new byte[80];
        return DeviceIoControl(hub, IoctlUsbGetNodeInformation, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero)
            ? buffer[6] // USB_NODE_INFORMATION: NodeType (4) + USB_HUB_DESCRIPTOR (bLength, bDescriptorType, bNumberOfPorts…)
            : 0;
    }

    private static UsbPort? ConnectionInfo(SafeFileHandle hub, int port)
    {
        // USB_NODE_CONNECTION_INFORMATION_EX is packed: ConnectionIndex(4) + USB_DEVICE_DESCRIPTOR(18) +
        // CurrentConfigurationValue(1) + Speed(1) + DeviceIsHub(1) + DeviceAddress(2) + NumberOfOpenPipes(4) + ConnectionStatus(4).
        var buffer = new byte[512];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationEx, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        if (BitConverter.ToInt32(buffer, 31) != 1) return null; // DeviceConnected
        var speed = buffer[23];
        var isHub = buffer[24] != 0;

        // The _V2 query is the only reliable SuperSpeed signal: EX keeps reporting "high speed" for USB 3 devices.
        var v2 = new byte[16];
        BitConverter.GetBytes(port).CopyTo(v2, 0);
        BitConverter.GetBytes(16).CopyTo(v2, 4);
        BitConverter.GetBytes(7).CopyTo(v2, 8); // Usb110 | Usb200 | Usb300
        var flags = DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationExV2, v2, v2.Length, v2, v2.Length, out _, IntPtr.Zero)
            ? BitConverter.ToInt32(v2, 12)
            : 0;
        return new UsbPort(port, speed, flags, isHub);
    }

    private static string? PortDriverKey(SafeFileHandle hub, int port)
    {
        var probe = new byte[10];
        BitConverter.GetBytes(port).CopyTo(probe, 0);
        if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionDriverkeyName, probe, probe.Length, probe, probe.Length, out _, IntPtr.Zero))
            return null;
        var needed = BitConverter.ToInt32(probe, 4);
        if (needed <= 8 || needed > 4096) return null;
        var buffer = new byte[needed];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionDriverkeyName, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        return Encoding.Unicode.GetString(buffer, 8, needed - 8).TrimEnd('\0');
    }

    /// <summary>Which \\.\PhysicalDriveN each disk device is.</summary>
    public static Dictionary<uint, int> DiskNumbers()
    {
        var numbers = new Dictionary<uint, int>();
        foreach (var (devInst, path) in InterfacePaths(DiskInterface))
        {
            using var handle = CreateFile(path, 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            var buffer = new byte[12]; // STORAGE_DEVICE_NUMBER: DeviceType, DeviceNumber, PartitionNumber
            if (DeviceIoControl(handle, IoctlStorageGetDeviceNumber, [], 0, buffer, buffer.Length, out _, IntPtr.Zero))
                numbers[devInst] = BitConverter.ToInt32(buffer, 4);
        }
        return numbers;
    }

    /// <summary>What a disk says about itself. Opens the disk with no access rights, so no admin is needed.
    /// Serial numbers are deliberately not read.</summary>
    public static DiskFacts? ReadDisk(int number)
    {
        using var handle = OpenDisk(number);
        if (handle.IsInvalid) return null;

        string? vendor = null, product = null, firmware = null;
        var bus = 0;
        var descriptor = new byte[1024];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, PropertyQuery(0), 12, descriptor, descriptor.Length, out _, IntPtr.Zero))
        {
            // STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset @12, ProductIdOffset @16, ProductRevisionOffset @20, BusType @28.
            vendor = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 12));
            product = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 16));
            firmware = AsciiAt(descriptor, BitConverter.ToInt32(descriptor, 20));
            bus = BitConverter.ToInt32(descriptor, 28);
        }

        long? size = null;
        var geometry = new byte[256];
        if (DeviceIoControl(handle, IoctlDiskGetDriveGeometryEx, [], 0, geometry, geometry.Length, out var returned, IntPtr.Zero) && returned >= 32)
            size = BitConverter.ToInt64(geometry, 24); // DISK_GEOMETRY (24 bytes), then DiskSize

        double? temperature = null;
        var thermal = new byte[512];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, PropertyQuery(52), 12, thermal, thermal.Length, out _, IntPtr.Zero) &&
            BitConverter.ToUInt16(thermal, 12) > 0)
        {
            // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount @12, first STORAGE_TEMPERATURE_INFO @24 (Temperature at +2).
            var celsius = BitConverter.ToInt16(thermal, 26);
            if (celsius is > 0 and < 120) temperature = celsius;
        }
        return new DiskFacts(number, vendor, product, firmware, bus, size, temperature);
    }

    /// <summary>Cumulative bytes read and written since boot, or null when the disk keeps no counters.</summary>
    public static (long Read, long Written)? DiskCounters(int number)
    {
        using var handle = OpenDisk(number);
        if (handle.IsInvalid) return null;
        var buffer = new byte[88]; // DISK_PERFORMANCE: BytesRead, BytesWritten, ...
        return DeviceIoControl(handle, IoctlDiskPerformance, [], 0, buffer, buffer.Length, out _, IntPtr.Zero)
            ? (BitConverter.ToInt64(buffer, 0), BitConverter.ToInt64(buffer, 8))
            : null;
    }

    private static SafeFileHandle OpenDisk(int number) =>
        CreateFile($@"\\.\PhysicalDrive{number}", 0, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

    /// <summary>Active display outputs: connector type, signal mode, and the monitor's device instance.</summary>
    public static List<DisplayTarget> DisplayTargets()
    {
        var targets = new List<DisplayTarget>();
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return targets;
        var paths = new byte[pathCount * PathInfoSize];
        var modes = new byte[modeCount * ModeInfoSize];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return targets;

        for (var i = 0; i < pathCount; i++)
        {
            // DISPLAYCONFIG_PATH_INFO: source info (20 bytes), then target info: adapterId(8) id(4) modeInfoIdx(4)
            // outputTechnology(4) rotation(4) scaling(4) refreshRate(8) ...
            var target = i * PathInfoSize + 20;
            var adapterLow = BitConverter.ToUInt32(paths, target);
            var adapterHigh = BitConverter.ToInt32(paths, target + 4);
            var targetId = BitConverter.ToUInt32(paths, target + 8);
            var modeIndex = BitConverter.ToUInt32(paths, target + 12);
            var technology = BitConverter.ToUInt32(paths, target + 16);
            var refreshNumerator = BitConverter.ToUInt32(paths, target + 28);
            var refreshDenominator = BitConverter.ToUInt32(paths, target + 32);

            int? width = null, height = null;
            if (modeIndex < modeCount)
            {
                // DISPLAYCONFIG_MODE_INFO: infoType(4) id(4) adapterId(8), then DISPLAYCONFIG_VIDEO_SIGNAL_INFO:
                // pixelRate(8) hSyncFreq(8) vSyncFreq(8) activeSize(cx, cy) ...
                var mode = (int)modeIndex * ModeInfoSize;
                if (BitConverter.ToInt32(modes, mode) == 2) // DISPLAYCONFIG_MODE_INFO_TYPE_TARGET
                {
                    width = BitConverter.ToInt32(modes, mode + 40);
                    height = BitConverter.ToInt32(modes, mode + 44);
                }
            }

            // DISPLAYCONFIG_TARGET_DEVICE_NAME: header(20) flags(4) outputTechnology(4) edidManufactureId(2)
            // edidProductCodeId(2) connectorInstance(4) monitorFriendlyDeviceName[64] monitorDevicePath[128].
            var request = new byte[420];
            BitConverter.GetBytes(2).CopyTo(request, 0); // DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME
            BitConverter.GetBytes(request.Length).CopyTo(request, 4);
            BitConverter.GetBytes(adapterLow).CopyTo(request, 8);
            BitConverter.GetBytes(adapterHigh).CopyTo(request, 12);
            BitConverter.GetBytes(targetId).CopyTo(request, 16);
            string? friendly = null, instance = null;
            var connector = 0;
            if (DisplayConfigGetDeviceInfo(request) == 0)
            {
                connector = BitConverter.ToInt32(request, 32);
                friendly = UnicodeAt(request, 36, 64);
                instance = MonitorInstanceFromPath(UnicodeAt(request, 164, 128));
            }

            double? refresh = refreshDenominator == 0 ? null : Math.Round((double)refreshNumerator / refreshDenominator, 2);
            targets.Add(new DisplayTarget(instance, friendly, technology, connector, width, height, refresh));
        }
        return targets;
    }

    // \\?\DISPLAY#ABC1234#5&1a2b3c4d&0&UID4352#{e6f07b5f-...} → DISPLAY\ABC1234\5&1a2b3c4d&0&UID4352
    private static string? MonitorInstanceFromPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var text = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        var guid = text.LastIndexOf("#{", StringComparison.Ordinal);
        if (guid > 0) text = text[..guid];
        return text.Replace('#', '\\');
    }

    private static byte[] PropertyQuery(int propertyId)
    {
        var query = new byte[12]; // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (standard), AdditionalParameters
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        return query;
    }

    private static string? AsciiAt(byte[] buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length) return null;
        var end = Array.IndexOf(buffer, (byte)0, offset);
        if (end < 0) end = buffer.Length;
        var text = Encoding.ASCII.GetString(buffer, offset, end - offset).Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? UnicodeAt(byte[] buffer, int offset, int chars)
    {
        var text = Encoding.Unicode.GetString(buffer, offset, chars * 2);
        var end = text.IndexOf('\0');
        text = (end >= 0 ? text[..end] : text).Trim();
        return text.Length == 0 ? null : text;
    }

    /// <summary>The monitor's own model name from its EDID (for example "VX2418-P").</summary>
    public static string? MonitorName(string instanceId)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            if (key?.GetValue("EDID") is not byte[] edid || edid.Length < 128) return null;
            for (var offset = 54; offset <= 108; offset += 18)
            {
                if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 3] != 0xFC) continue;
                var text = Encoding.ASCII.GetString(edid, offset + 5, 13);
                var end = text.IndexOf('\n');
                return (end >= 0 ? text[..end] : text).Trim();
            }
        }
        catch { }
        return null;
    }

    /// <summary>The network interface GUID (NetworkInterface.Id) a network device's driver registered.</summary>
    public static string? NetInstanceId(DeviceNode node)
    {
        if (node.DriverKey is null) return null;
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{node.DriverKey}");
            return key?.GetValue("NetCfgInstanceId") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string DeviceId(uint handle)
    {
        var buffer = new char[512];
        return CM_Get_Device_IDW(handle, buffer, buffer.Length, 0) == 0 ? new string(buffer).TrimEnd('\0') : "";
    }

    private static string? Registry(uint handle, int property)
    {
        var length = 0;
        CM_Get_DevNode_Registry_PropertyW(handle, property, out _, null, ref length, 0);
        if (length <= 0) return null;
        var buffer = new byte[length];
        if (CM_Get_DevNode_Registry_PropertyW(handle, property, out _, buffer, ref length, 0) != 0) return null;
        var text = Encoding.Unicode.GetString(buffer).Split('\0')[0].Trim();
        return text.Length == 0 ? null : text;
    }

    private static string? StringProperty(uint handle, Guid set, uint id)
    {
        var key = new DevPropKey { FormatId = set, PropertyId = id };
        var size = 0;
        CM_Get_DevNode_PropertyW(handle, ref key, out _, null, ref size, 0);
        if (size <= 0) return null;
        var buffer = new byte[size];
        if (CM_Get_DevNode_PropertyW(handle, ref key, out _, buffer, ref size, 0) != 0) return null;
        var text = Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    private static uint? UIntProperty(uint handle, Guid set, uint id)
    {
        var key = new DevPropKey { FormatId = set, PropertyId = id };
        var buffer = new byte[4];
        var size = buffer.Length;
        return CM_Get_DevNode_PropertyW(handle, ref key, out _, buffer, ref size, 0) == 0 && size == 4
            ? BitConverter.ToUInt32(buffer)
            : null;
    }

    private const int CmDrpDeviceDesc = 0x01;
    private const int CmDrpService = 0x05;
    private const int CmDrpClass = 0x08;
    private const int CmDrpDriver = 0x0A;
    private const int CmDrpFriendlyName = 0x0D;
    private const int DigcfPresent = 0x02;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint IoctlUsbGetNodeInformation = 0x220408;
    private const uint IoctlUsbGetNodeConnectionDriverkeyName = 0x220420;
    private const uint IoctlUsbGetNodeConnectionInformationEx = 0x220448;
    private const uint IoctlUsbGetNodeConnectionInformationExV2 = 0x22045C;
    private const uint IoctlStorageQueryProperty = 0x2D1400;
    private const uint IoctlStorageGetDeviceNumber = 0x2D1080;
    private const uint IoctlDiskGetDriveGeometryEx = 0x700A0;
    private const uint IoctlDiskPerformance = 0x70020;
    private const uint QdcOnlyActivePaths = 0x2;
    private const int PathInfoSize = 72;
    private const int ModeInfoSize = 64;

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey { public Guid FormatId; public uint PropertyId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData { public int Size; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData { public int Size; public Guid ClassGuid; public uint DevInst; public IntPtr Reserved; }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string? deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Child(out uint child, uint devInst, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Sibling(out uint sibling, uint devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_IDW(uint devInst, char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_Registry_PropertyW(uint devInst, int property, out int type, byte[]? buffer, ref int length, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DevPropKey key, out uint type, byte[]? buffer, ref int size, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint devInst, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid classGuid, int index, ref SpDeviceInterfaceData data);

    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr set, ref SpDeviceInterfaceData data, IntPtr detail, int size, out int required, ref SpDevinfoData info);

    [DllImport("setupapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out int pathCount, out int modeCount);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref int pathCount, byte[] paths, ref int modeCount, byte[] modes, IntPtr topology);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(byte[] request);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
}
