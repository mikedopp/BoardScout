using System.Diagnostics;
using BoardScout.Services;

namespace BoardScout.Platform;

internal sealed record BluetoothDevice(string Address, string Name, bool Connected);

internal sealed record BluetoothRadio(string Name, string? DevicePath, string? UsbPath, bool? Blocked, IReadOnlyList<BluetoothDevice> Paired, bool PairedListed);

/// <summary>
/// Everything read from the machine for one map. Building the views from it again is cheap, so a privacy toggle
/// or late-arriving network names don't re-read the hardware.
/// </summary>
internal sealed record HardwareCapture(
    CpuFacts Cpu,
    MachineIdentity Identity,
    SecurityFacts Security,
    MemoryFacts Memory,
    IReadOnlyList<PciDevice> Pci,
    IReadOnlyList<UsbBus> Usb,
    IReadOnlyList<BlockDisk> Disks,
    IReadOnlyList<Volume> Volumes,
    IReadOnlyList<NetLink> Links,
    IReadOnlyList<DisplayOutput> Displays,
    IReadOnlyList<HwmonChip> Sensors,
    IReadOnlyList<IrqLine> Irqs,
    IReadOnlyList<BluetoothRadio> Bluetooth,
    NetworkFacts Network,
    IReadOnlyDictionary<(int Vendor, int Product), (string Vendor, string? Product)> UsbNames,
    string FormFactor,
    string? DeviceTreeModel,
    string Kernel,
    bool Root,
    long CaptureMs,
    DateTimeOffset At)
{
    public IEnumerable<UsbDevice> UsbDevices => Usb.SelectMany(b => b.RootHub.Descendants());

    public static HardwareCapture Read()
    {
        var watch = Stopwatch.StartNew();
        // The network probe pings the router and asks DNS for names: run it alongside the hardware walk.
        var network = Task.Run(NetworkProbe.Capture);
        var identity = Firmware.ReadIdentity();
        var cpu = Platform.Cpu.Read();
        var pci = Platform.Pci.Read();
        var usb = Platform.Usb.Read();
        var disks = Block.ReadDisks();
        var sensors = Platform.Sensors.Read();
        var driveTemps = Platform.Sensors.Drives(sensors);
        foreach (var disk in disks)
            if (disk.DevicePath is not null && driveTemps.TryGetValue(disk.DevicePath, out var celsius)) disk.TemperatureC = celsius;
        var volumes = Block.ReadVolumes(disks);
        var links = NetLinks.Read();
        var displays = Platform.Displays.Read();
        var irqs = Interrupts.Read();
        var memory = Firmware.ReadMemory(identity);
        var security = Firmware.ReadSecurity();
        var bluetooth = ReadBluetooth();
        var usbNames = UsbIds.Resolve(usb.SelectMany(b => b.RootHub.Descendants()).Select(d => (d.Vendor, d.Product)));
        var deviceTree = Sys.Text("/sys/firmware/devicetree/base/model");
        var kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? Environment.OSVersion.Version.ToString();
        NetworkFacts facts;
        try
        {
            facts = network.GetAwaiter().GetResult();
        }
        catch
        {
            facts = new NetworkFacts([], new Dictionary<string, RouterFacts>(), new Dictionary<string, DnsFacts>(), [], null);
        }
        return new HardwareCapture(cpu, identity, security, memory, pci, usb, disks, volumes, links, displays, sensors, irqs, bluetooth,
            facts, usbNames, Firmware.FormFactor(identity, deviceTree), deviceTree, kernel, Environment.UserName == "root" || IsRoot(),
            watch.ElapsedMilliseconds, DateTimeOffset.Now);
    }

    private static bool IsRoot()
    {
        foreach (var line in Sys.Lines("/proc/self/status"))
            if (line.StartsWith("Uid:", StringComparison.Ordinal))
                return line.Split('\t', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(2) == "0";
        return false;
    }

    // Radios from /sys/class/bluetooth; paired devices from BlueZ's bluetoothctl when it's installed.
    private static List<BluetoothRadio> ReadBluetooth()
    {
        var radios = new List<BluetoothRadio>();
        var radioNames = Sys.Children("/sys/class/bluetooth").Where(n => !n.Contains(':')).ToList();
        if (radioNames.Count == 0) return radios;
        List<BluetoothDevice> paired = [];
        var listed = false;
        if (Tools.Run("bluetoothctl", ["devices", "Paired"], 3000) is { } text)
        {
            listed = true;
            var connected = (Tools.Run("bluetoothctl", ["devices", "Connected"], 3000) ?? "").Split('\n')
                .Select(l => l.Split(' ', 3)).Where(p => p.Length >= 2 && p[0] == "Device").Select(p => p[1]).ToHashSet();
            paired = text.Split('\n').Select(l => l.Trim().Split(' ', 3)).Where(p => p.Length == 3 && p[0] == "Device")
                .Select(p => new BluetoothDevice(p[1], p[2], connected.Contains(p[1]))).ToList();
        }
        foreach (var name in radioNames)
        {
            var device = Sys.Real($"/sys/class/bluetooth/{name}/device");
            bool? blocked = null;
            foreach (var rfkill in Sys.Children("/sys/class/rfkill"))
            {
                if (Sys.Read($"/sys/class/rfkill/{rfkill}/type") != "bluetooth") continue;
                blocked = Sys.Read($"/sys/class/rfkill/{rfkill}/soft") == "1" || Sys.Read($"/sys/class/rfkill/{rfkill}/hard") == "1";
            }
            string? usbPath = null;
            if (device?.Contains("/usb", StringComparison.Ordinal) == true)
            {
                var parts = device.Split('/');
                var index = Array.FindLastIndex(parts, p => System.Text.RegularExpressions.Regex.IsMatch(p, @"^\d+-[\d.]+$"));
                if (index > 0) usbPath = string.Join('/', parts[..(index + 1)]);
            }
            radios.Add(new BluetoothRadio(name, device, usbPath, blocked, radios.Count == 0 ? paired : [], listed));
        }
        return radios;
    }
}
