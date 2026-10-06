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

    /// <summary>For hubs: what each port supports (bit 0 USB 1.1, bit 1 USB 2, bit 2 USB 3) and whether it is in use.</summary>
    public List<(int Port, int Protocols, bool InUse)> PortMap { get; } = [];

    /// <summary>For hubs: powered by the port above it instead of its own adapter.</summary>
    public bool HubBusPowered { get; set; }

    /// <summary>CM_REMOVAL_POLICY: 1 not removable, 2 orderly removal (write caching on), 3 surprise removal (quick removal).</summary>
    public int? RemovalPolicy { get; init; }

    /// <summary>Most recent device power state: 0 = D0 (fully on) … 3 = D3 (off or asleep).</summary>
    public int? PowerState { get; init; }

    /// <summary>For USB devices: why BoardScout did not ask the device for its power needs (a phone or camera,
    /// or a drive that was busy), or null when it asked or had the answer already.</summary>
    public string? PowerNotAsked { get; set; }

    /// <summary>Interrupts assigned to the device; negative numbers are message-signaled (MSI/MSI-X).</summary>
    public IReadOnlyList<(int Irq, bool Shareable)> Irqs { get; init; } = [];
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

/// <summary>
/// What a USB hub reports for one of its ports. Flags are USB_NODE_CONNECTION_INFORMATION_EX_V2 flags;
/// Protocols is what the port itself supports (bit 0 USB 1.1, bit 1 USB 2, bit 2 USB 3). PowerMa and
/// SelfPowered come from the device's configuration descriptor.
/// </summary>
internal sealed record UsbPort(int Port, int Speed, int Flags, bool IsHub, int Protocols = 0, int? PowerMa = null, bool? SelfPowered = null)
{
    public bool SuperSpeedPlus => (Flags & 4) != 0;
    public bool SuperSpeed => (Flags & 1) != 0;
    public bool SuperSpeedCapable => (Flags & 2) != 0;
    public bool SuperSpeedPlusCapable => (Flags & 8) != 0;
}

/// <summary>A disk's counters since boot. The operation counts are 32-bit and wrap.</summary>
internal readonly record struct DiskCounterSample(long BytesRead, long BytesWritten, uint Reads, uint Writes, int QueueDepth,
    long IdleTime = 0, long QueryTime = 0);

/// <summary>What a disk reports about itself. Bus is the STORAGE_BUS_TYPE (7 USB, 11 SATA, 17 NVMe);
/// Spinning is true for hard drives (the disk reports a seek penalty).</summary>
internal sealed record DiskFacts(int Number, string? Vendor, string? Product, string? Firmware, int Bus, long? SizeBytes,
    double? TemperatureC, bool? Spinning = null);

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
            RemovalPolicy = IntRegistry(handle, CmDrpRemovalPolicy),
            PowerState = ReadPowerState(handle),
            Irqs = ReadIrqs(handle),
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

    // Asks every USB hub which device sits on which port, at what speed, and what each port supports. The hub
    // driver answers these from what it already knows; nothing reaches the devices except the power question
    // below, which phones and cameras are never asked and drives are asked only while idle.
    private static void AttachUsbPorts(DeviceNode tree)
    {
        var hubs = new Dictionary<uint, DeviceNode>();
        foreach (var node in tree.Descendants())
            if (node.IdStarts(@"USB\")) hubs[node.Handle] = node;
        var idle = new DriveIdleCheck();

        foreach (var (devInst, path) in InterfacePaths(UsbHubInterface))
        {
            if (!hubs.TryGetValue(devInst, out var hub)) continue;
            using var handle = CreateFile(path, GenericWrite, FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (handle.IsInvalid) continue;
            var (ports, busPowered) = HubInformation(handle);
            hub.HubPorts = ports;
            hub.HubBusPowered = busPowered;
            for (var port = 1; port <= ports; port++)
            {
                var connection = ConnectionInfo(handle, port, out var protocols);
                hub.PortMap.Add((port, protocols, connection is not null));
                if (connection is null) continue;
                var driverKey = PortDriverKey(handle, port);
                var child = hub.Children.FirstOrDefault(c =>
                    driverKey is not null && string.Equals(c.DriverKey, driverKey, StringComparison.OrdinalIgnoreCase));
                if (child is null) continue;
                var key = $"{child.InstanceId}|{connection.SuperSpeed}";
                if (!PowerCache.TryGetValue(key, out var power))
                {
                    child.PowerNotAsked = idle.WhyNotAsk(child);
                    if (child.PowerNotAsked is not null)
                    {
                        child.Usb = connection;
                        continue;
                    }
                    PowerCache[key] = power = DevicePower(handle, port, connection.SuperSpeed);
                }
                child.Usb = connection with { PowerMa = power.Power, SelfPowered = power.Self };
            }
        }
    }

    // How much current a USB device asks for, from its configuration descriptor. Reading the descriptor is
    // a request to the device itself, so each device is asked once per BoardScout session, never on a timer.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (int? Power, bool? Self)> PowerCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Decides whether a USB device may be asked for its power needs. Phones and cameras never are: they move
    /// files over MTP/PTP, and BoardScout stays off that link entirely. Drives are asked only when their
    /// read and write counters (kept by Windows, not the drive) haven't moved for a quarter second.
    /// </summary>
    private sealed class DriveIdleCheck
    {
        private static readonly string[] PhoneClasses = ["WPD", "Image", "Modem", "AndroidUsbDeviceClass"];
        private static readonly string[] StorageServices = ["USBSTOR", "UASPStor"];
        private readonly System.Diagnostics.Stopwatch _since = new();
        private Dictionary<uint, int>? _numbers;
        private Dictionary<int, DiskCounterSample?>? _before;

        public string? WhyNotAsk(DeviceNode device)
        {
            var functions = device.Descendants().Append(device).ToList();
            if (functions.Any(f => PhoneClasses.Any(f.IsClass)))
                return "phone or camera: BoardScout never sends requests to devices that transfer files over MTP or PTP";
            var disks = functions.Where(f => f.IsClass("DiskDrive") || f.IsClass("CDROM")).ToList();
            var storage = disks.Count > 0 || functions.Any(f => StorageServices.Any(s => string.Equals(f.Service, s, StringComparison.OrdinalIgnoreCase)));
            if (!storage) return null;

            // Baseline every disk once, then compare this drive's counters a quarter second or more later.
            if (_before is null)
            {
                _numbers = DiskNumbers();
                _before = _numbers.Values.Distinct().ToDictionary(n => n, DiskCounters);
                _since.Start();
            }
            var numbers = disks.Select(d => _numbers!.TryGetValue(d.Handle, out var n) ? n : -1).Where(n => n >= 0).ToList();
            if (numbers.Count == 0) return "drive still starting up: asked once it is ready";
            var wait = 250 - (int)_since.ElapsedMilliseconds;
            if (wait > 0) Thread.Sleep(wait);
            foreach (var number in numbers)
            {
                var then = _before.GetValueOrDefault(number);
                var now = DiskCounters(number);
                if (then is null || now is null) return "drive activity unknown: not asked";
                if (now.Value.Reads != then.Value.Reads || now.Value.Writes != then.Value.Writes || now.Value.QueueDepth > 0)
                    return "drive was busy: BoardScout asks drives only while they are idle";
            }
            return null;
        }
    }

    private static (int? Power, bool? Self) DevicePower(SafeFileHandle hub, int port, bool superSpeed)
    {
        // USB_DESCRIPTOR_REQUEST: ConnectionIndex, then the setup packet GET_DESCRIPTOR(CONFIGURATION, 0), 9 bytes.
        var request = new byte[12 + 9];
        BitConverter.GetBytes(port).CopyTo(request, 0);
        request[4] = 0x80;
        request[5] = 6;
        request[7] = 2;
        BitConverter.GetBytes((ushort)9).CopyTo(request, 10);
        (int?, bool?) result = (null, null);
        if (DeviceIoControl(hub, IoctlUsbGetDescriptorFromNodeConnection, request, request.Length, request, request.Length, out var returned, IntPtr.Zero) &&
            returned >= 21 && request[13] == 2)
        {
            var attributes = request[12 + 7];
            var maxPower = request[12 + 8];
            // bMaxPower counts 8 mA units on SuperSpeed and 2 mA units on USB 2.
            result = (maxPower * (superSpeed ? 8 : 2), (attributes & 0x40) != 0);
        }
        return result;
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

    // USB_NODE_INFORMATION: NodeType (4) + USB_HUB_DESCRIPTOR (bLength, bDescriptorType, bNumberOfPorts, …, 71 bytes)
    // + HubIsBusPowered (1) at offset 75.
    private static (int Ports, bool BusPowered) HubInformation(SafeFileHandle hub)
    {
        var buffer = new byte[80];
        return DeviceIoControl(hub, IoctlUsbGetNodeInformation, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero)
            ? (buffer[6], buffer[75] != 0)
            : (0, false);
    }

    private static UsbPort? ConnectionInfo(SafeFileHandle hub, int port, out int protocols)
    {
        // The _V2 query tells what the port supports (in the SupportedUsbProtocols field it returns) and, when
        // something is plugged in, whether it runs at SuperSpeed — EX keeps reporting "high speed" for USB 3 devices.
        var v2 = new byte[16];
        BitConverter.GetBytes(port).CopyTo(v2, 0);
        BitConverter.GetBytes(16).CopyTo(v2, 4);
        BitConverter.GetBytes(7).CopyTo(v2, 8); // Usb110 | Usb200 | Usb300
        var haveV2 = DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationExV2, v2, v2.Length, v2, v2.Length, out _, IntPtr.Zero);
        protocols = haveV2 ? BitConverter.ToInt32(v2, 8) : 0;
        var flags = haveV2 ? BitConverter.ToInt32(v2, 12) : 0;

        // USB_NODE_CONNECTION_INFORMATION_EX is packed: ConnectionIndex(4) + USB_DEVICE_DESCRIPTOR(18) +
        // CurrentConfigurationValue(1) + Speed(1) + DeviceIsHub(1) + DeviceAddress(2) + NumberOfOpenPipes(4) + ConnectionStatus(4).
        var buffer = new byte[512];
        BitConverter.GetBytes(port).CopyTo(buffer, 0);
        if (!DeviceIoControl(hub, IoctlUsbGetNodeConnectionInformationEx, buffer, buffer.Length, buffer, buffer.Length, out _, IntPtr.Zero))
            return null;
        if (BitConverter.ToInt32(buffer, 31) != 1) return null; // DeviceConnected
        return new UsbPort(port, buffer[23], flags, buffer[24] != 0, protocols);
    }

    private static int? IntRegistry(uint handle, int property)
    {
        var buffer = new byte[4];
        var length = buffer.Length;
        return CM_Get_DevNode_Registry_PropertyW(handle, property, out _, buffer, ref length, 0) == 0 && length == 4
            ? BitConverter.ToInt32(buffer)
            : null;
    }

    // CM_POWER_DATA: PD_Size, PD_MostRecentPowerState (DEVICE_POWER_STATE: 1 = D0 … 4 = D3), …
    private static int? ReadPowerState(uint handle)
    {
        var buffer = new byte[64];
        var length = buffer.Length;
        if (CM_Get_DevNode_Registry_PropertyW(handle, CmDrpDevicePowerData, out _, buffer, ref length, 0) != 0 || length < 8) return null;
        var state = BitConverter.ToInt32(buffer, 4);
        return state is >= 1 and <= 4 ? state - 1 : null;
    }

    // The interrupts Windows assigned (the device's allocated resources); needs no admin rights.
    private static List<(int Irq, bool Shareable)> ReadIrqs(uint handle)
    {
        var irqs = new List<(int, bool)>();
        if (CM_Get_First_Log_Conf(out var configuration, handle, AllocLogConf) != 0) return irqs;
        try
        {
            var current = configuration;
            for (var i = 0; i < 64 && CM_Get_Next_Res_Des(out var next, current, ResTypeIrq, out _, 0) == 0; i++)
            {
                if (current != configuration) CM_Free_Res_Des_Handle(current);
                current = next;
                if (CM_Get_Res_Des_Data_Size(out var size, next, 0) != 0 || size < 16) continue;
                var data = new byte[size];
                if (CM_Get_Res_Des_Data(next, data, size, 0) != 0) continue;
                // IRQ_DES: IRQD_Count, IRQD_Type, IRQD_Flags (bit 0 = shareable), IRQD_Alloc_Num, IRQD_Affinity.
                irqs.Add((BitConverter.ToInt32(data, 12), (BitConverter.ToUInt32(data, 8) & 1) != 0));
            }
            if (current != configuration) CM_Free_Res_Des_Handle(current);
        }
        finally
        {
            CM_Free_Log_Conf_Handle(configuration);
        }
        return irqs;
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

        // Temperature only from internal drives (SCSI, ATA, RAID, SAS, SATA, NVMe): USB and card-reader bridges
        // rarely answer it, and BoardScout doesn't send them questions they may have to pass to the drive.
        double? temperature = null;
        var thermal = new byte[512];
        if (bus is 1 or 3 or 8 or 10 or 11 or 17 &&
            DeviceIoControl(handle, IoctlStorageQueryProperty, PropertyQuery(52), 12, thermal, thermal.Length, out _, IntPtr.Zero) &&
            BitConverter.ToUInt16(thermal, 12) > 0)
        {
            // STORAGE_TEMPERATURE_DATA_DESCRIPTOR: InfoCount @12, first STORAGE_TEMPERATURE_INFO @24 (Temperature at +2).
            var celsius = BitConverter.ToInt16(thermal, 26);
            if (celsius is > 0 and < 120) temperature = celsius;
        }

        // DEVICE_SEEK_PENALTY_DESCRIPTOR: Version, Size, IncursSeekPenalty (a hard drive's moving heads).
        bool? spinning = null;
        var seek = new byte[12];
        if (DeviceIoControl(handle, IoctlStorageQueryProperty, PropertyQuery(7), 12, seek, seek.Length, out var seekLength, IntPtr.Zero) && seekLength >= 9)
            spinning = seek[8] != 0;
        return new DiskFacts(number, vendor, product, firmware, bus, size, temperature, spinning);
    }

    /// <summary>Cumulative bytes and operations since boot plus the current queue depth, or null when the
    /// disk keeps no counters.</summary>
    public static DiskCounterSample? DiskCounters(int number)
    {
        using var handle = OpenDisk(number);
        if (handle.IsInvalid) return null;
        // DISK_PERFORMANCE: BytesRead @0, BytesWritten @8, ReadTime, WriteTime, IdleTime @32,
        // ReadCount @40, WriteCount @44, QueueDepth @48, SplitCount, QueryTime @56 (100 ns units).
        var buffer = new byte[88];
        return DeviceIoControl(handle, IoctlDiskPerformance, [], 0, buffer, buffer.Length, out _, IntPtr.Zero)
            ? new DiskCounterSample(BitConverter.ToInt64(buffer, 0), BitConverter.ToInt64(buffer, 8),
                BitConverter.ToUInt32(buffer, 40), BitConverter.ToUInt32(buffer, 44), BitConverter.ToInt32(buffer, 48),
                BitConverter.ToInt64(buffer, 32), BitConverter.ToInt64(buffer, 56))
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
    private const int CmDrpDevicePowerData = 0x1F;
    private const int CmDrpRemovalPolicy = 0x20;
    private const int AllocLogConf = 2;
    private const int ResTypeIrq = 4;
    private const uint IoctlUsbGetDescriptorFromNodeConnection = 0x220410;
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

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_First_Log_Conf(out IntPtr logConf, uint devInst, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Next_Res_Des(out IntPtr next, IntPtr current, int forResource, out int resourceId, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data_Size(out int size, IntPtr resDes, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_Res_Des_Data(IntPtr resDes, byte[] buffer, int size, int flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Res_Des_Handle(IntPtr resDes);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Free_Log_Conf_Handle(IntPtr logConf);

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
