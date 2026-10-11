using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardScout.Platform;

/// <summary>A PCIe link: the speed and width it trained at, and the best both ends support.</summary>
internal sealed record PciLinkInfo(double SpeedGt, int Width, double MaxSpeedGt, int MaxWidth)
{
    public int Gen => GenOf(SpeedGt);
    public int MaxGen => GenOf(MaxSpeedGt);
    public bool BelowMax => Gen < MaxGen || Width < MaxWidth;

    public static int GenOf(double gigatransfers) => gigatransfers switch
    {
        <= 2.5 => 1,
        <= 5 => 2,
        <= 8 => 3,
        <= 16 => 4,
        <= 32 => 5,
        _ => 6
    };

    /// <summary>"8.0 GT/s PCIe" → 8.0; "Unknown" → null.</summary>
    public static double? ParseSpeed(string? text)
    {
        if (text is null) return null;
        var match = Regex.Match(text, @"^([\d.]+)\s*GT/s");
        return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }
}

/// <summary>One PCI function as the kernel lists it under /sys/bus/pci/devices.</summary>
internal sealed class PciDevice
{
    public required string Address { get; init; }
    public required string SysPath { get; init; }
    public string? ParentAddress { get; init; }
    public int Vendor { get; init; }
    public int Device { get; init; }
    public int SubVendor { get; init; }
    public int SubDevice { get; init; }

    /// <summary>24-bit class code: base class, subclass, programming interface.</summary>
    public int Class { get; init; }

    public string? Driver { get; init; }
    public int? Irq { get; init; }
    public int MsiVectors { get; init; }
    public string? PowerState { get; init; }
    public bool BootVga { get; init; }
    public PciLinkInfo? Link { get; init; }
    public string? SlotName { get; set; }
    public string? VendorName { get; set; }
    public string? DeviceName { get; set; }
    public string? SubsystemName { get; set; }
    public List<PciDevice> Children { get; } = [];
    public PciDevice? Parent { get; set; }

    public int BaseClass => Class >> 16;
    public int SubClass => (Class >> 8) & 0xFF;
    public int ProgIf => Class & 0xFF;
    public int Bus => int.Parse(Address.AsSpan(5, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public int Slot => int.Parse(Address.AsSpan(8, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
    public int Function => Address[^1] - '0';

    /// <summary>A PCI-to-PCI bridge: a root port, a switch port, or a chipset's bridge.</summary>
    public bool IsBridge => BaseClass == 0x06 && SubClass == 0x04;

    public string VendorShort => VendorName is null ? $"Vendor {Vendor:X4}" : PciIds.CleanVendor(VendorName);

    /// <summary>The best plain name: the repository's marketing name, or the class when there is none.</summary>
    public string Name => DeviceName is not null ? PciIds.CleanDevice(DeviceName) : ClassName;

    public string ClassName => (BaseClass, SubClass) switch
    {
        (0x01, 0x08) => "NVMe controller",
        (0x01, 0x06) => "SATA controller",
        (0x01, 0x01) => "IDE controller",
        (0x01, 0x04) => "RAID controller",
        (0x01, 0x07) => "SAS controller",
        (0x01, _) => "Storage controller",
        (0x02, 0x00) => "Ethernet controller",
        (0x02, 0x80) => "Network controller",
        (0x02, _) => "Network controller",
        (0x03, _) => "Display controller",
        (0x04, 0x03) => "Audio device",
        (0x04, _) => "Multimedia controller",
        (0x05, _) => "Memory controller",
        (0x06, 0x00) => "Host bridge",
        (0x06, 0x01) => "ISA bridge",
        (0x06, 0x04) => "PCI bridge",
        (0x06, _) => "Bridge",
        (0x07, _) => "Communication controller",
        (0x08, _) => "System peripheral",
        (0x0C, 0x03) => "USB controller",
        (0x0C, 0x05) => "SMBus controller",
        (0x0C, 0x80) => "Serial bus controller",
        (0x0C, _) => "Serial bus controller",
        (0x0D, 0x11) => "Bluetooth controller",
        (0x0D, _) => "Wireless controller",
        (0x10, _) => "Encryption controller",
        (0x11, _) => "Signal processing controller",
        (0x12, _) => "Processing accelerator",
        (0x13, _) => "Non-essential instrumentation",
        _ => "PCI device"
    };

    public IEnumerable<PciDevice> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}

/// <summary>Reads every PCI function from /sys/bus/pci/devices, linked into the bus tree.</summary>
internal static partial class Pci
{
    public static List<PciDevice> Read()
    {
        var devices = new List<PciDevice>();
        const string root = "/sys/bus/pci/devices";
        foreach (var address in Sys.Children(root))
        {
            if (!PciAddress().IsMatch(address)) continue;
            var link = Path.Combine(root, address);
            var real = Sys.Real(link) ?? link;
            var parent = Path.GetFileName(Path.GetDirectoryName(real) ?? "");
            var width = Sys.Int(Path.Combine(real, "current_link_width"));
            var maxWidth = Sys.Int(Path.Combine(real, "max_link_width"));
            var speed = PciLinkInfo.ParseSpeed(Sys.Read(Path.Combine(real, "current_link_speed")));
            var maxSpeed = PciLinkInfo.ParseSpeed(Sys.Read(Path.Combine(real, "max_link_speed")));
            devices.Add(new PciDevice
            {
                Address = address,
                SysPath = real,
                ParentAddress = PciAddress().IsMatch(parent) ? parent : null,
                Vendor = Sys.Hex(Path.Combine(real, "vendor")) ?? 0,
                Device = Sys.Hex(Path.Combine(real, "device")) ?? 0,
                SubVendor = Sys.Hex(Path.Combine(real, "subsystem_vendor")) ?? 0,
                SubDevice = Sys.Hex(Path.Combine(real, "subsystem_device")) ?? 0,
                Class = Sys.Hex(Path.Combine(real, "class")) ?? 0,
                Driver = Sys.LinkName(Path.Combine(real, "driver")),
                Irq = Sys.Int(Path.Combine(real, "irq")) is { } irq and > 0 ? irq : null,
                MsiVectors = Sys.Children(Path.Combine(real, "msi_irqs")).Count(),
                PowerState = Sys.Read(Path.Combine(real, "power_state")),
                BootVga = Sys.Read(Path.Combine(real, "boot_vga")) == "1",
                Link = width is > 0 and < 64 && maxWidth is > 0 and < 64 && speed is { } s && maxSpeed is { } m
                    ? new PciLinkInfo(s, width.Value, m, maxWidth.Value)
                    : null
            });
        }

        // Slot names the firmware gives ("PCIE1", "M.2 SSD"): /sys/bus/pci/slots/<name>/address is "0000:01:00".
        foreach (var slot in Sys.Children("/sys/bus/pci/slots"))
        {
            var slotAddress = Sys.Read($"/sys/bus/pci/slots/{slot}/address");
            if (slotAddress is null) continue;
            foreach (var device in devices.Where(d => d.Address.StartsWith(slotAddress + ".", StringComparison.OrdinalIgnoreCase)))
                device.SlotName ??= slot;
        }

        var names = PciIds.Resolve(devices.Select(d => (d.Vendor, d.Device, d.SubVendor, d.SubDevice)));
        var byAddress = devices.ToDictionary(d => d.Address);
        foreach (var device in devices)
        {
            if (names.TryGetValue((device.Vendor, device.Device, device.SubVendor, device.SubDevice), out var found))
            {
                device.VendorName = found.Vendor;
                device.DeviceName = found.Device;
                device.SubsystemName = found.Subsystem;
            }
            if (device.ParentAddress is not null && byAddress.TryGetValue(device.ParentAddress, out var parent))
            {
                device.Parent = parent;
                parent.Children.Add(device);
            }
        }
        foreach (var device in devices) device.Children.Sort((a, b) => string.CompareOrdinal(a.Address, b.Address));
        return devices;
    }

    /// <summary>The PCI address nearest to a sysfs path ("/sys/devices/pci0000:00/0000:00:1c.0/0000:03:00.0/net/enp3s0" → "0000:03:00.0").</summary>
    public static string? AddressIn(string? sysPath)
    {
        if (sysPath is null) return null;
        string? last = null;
        foreach (var part in sysPath.Split('/'))
            if (PciAddress().IsMatch(part)) last = part;
        return last;
    }

    [GeneratedRegex(@"^[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]$")]
    internal static partial Regex PciAddress();
}
