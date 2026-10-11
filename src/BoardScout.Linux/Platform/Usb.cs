using System.Globalization;

namespace BoardScout.Platform;

internal sealed record UsbInterface(string Name, int Class, int SubClass, int Protocol, string? Driver, string SysPath);

/// <summary>A hub port. USB 3 sockets are two ports, a USB 2 one and a SuperSpeed one, paired as "peers".</summary>
internal sealed class UsbPort
{
    public required string Name { get; init; }
    public int Number { get; init; }
    public string? Peer { get; init; }

    /// <summary>hotplug (a socket), hardwired (a soldered-on device), not used (not wired), or unknown.</summary>
    public string? ConnectType { get; init; }

    /// <summary>not attached, powered, default, addressed, configured, or suspended.</summary>
    public string? State { get; init; }

    public int OverCurrent { get; init; }
    public UsbDevice? Device { get; set; }
    public UsbDevice? Hub { get; set; }

    public bool Wired => ConnectType != "not used";
}

/// <summary>One USB device as the kernel lists it under /sys/bus/usb/devices.</summary>
internal sealed class UsbDevice
{
    public required string Name { get; init; }
    public required string SysPath { get; init; }
    public int Bus { get; init; }
    public int Vendor { get; init; }
    public int Product { get; init; }
    public string? Manufacturer { get; init; }
    public string? ProductName { get; init; }

    /// <summary>Negotiated speed in Mbps: 1.5, 12, 480, 5000, 10000, 20000.</summary>
    public double SpeedMbps { get; init; }

    /// <summary>bcdUSB, the USB version the device says it implements ("2.10", "3.20").</summary>
    public string Version { get; init; } = "";

    public int MaxPowerMa { get; init; }
    public bool SelfPowered { get; init; }
    public int DeviceClass { get; init; }
    public int HubPorts { get; init; }
    public string? RuntimeStatus { get; init; }
    public string? Removable { get; init; }
    public bool IsRootHub { get; init; }
    public List<UsbInterface> Interfaces { get; } = [];
    public List<UsbDevice> Children { get; } = [];
    public List<UsbPort> Ports { get; } = [];
    public UsbDevice? Parent { get; set; }
    public UsbPort? Port { get; set; }

    public bool IsHub => DeviceClass == 0x09 || Interfaces.Any(i => i.Class == 0x09);
    public double VersionNumber => double.TryParse(Version, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Says it implements USB 3 or later (it must report 2.10 instead while linked at USB 2 speed).</summary>
    public bool ReportsUsb3 => VersionNumber >= 3;

    public IEnumerable<UsbDevice> Descendants()
    {
        foreach (var child in Children)
        {
            yield return child;
            foreach (var nested in child.Descendants()) yield return nested;
        }
    }
}

/// <summary>One USB bus: a root hub and the controller it belongs to (a PCI address, or a platform device on Arm boards).</summary>
internal sealed record UsbBus(int Number, UsbDevice RootHub, string Controller, string ControllerPath);

/// <summary>Reads the USB tree from /sys/bus/usb/devices: every bus, hub, port, device, and interface.</summary>
internal static class Usb
{
    public static List<UsbBus> Read()
    {
        const string root = "/sys/bus/usb/devices";
        var devices = new Dictionary<string, UsbDevice>(StringComparer.Ordinal);
        var interfaces = new List<UsbInterface>();
        foreach (var name in Sys.Children(root))
        {
            var path = Sys.Real(Path.Combine(root, name)) ?? Path.Combine(root, name);
            if (name.Contains(':'))
            {
                interfaces.Add(new UsbInterface(name,
                    Sys.Hex(Path.Combine(path, "bInterfaceClass")) ?? 0,
                    Sys.Hex(Path.Combine(path, "bInterfaceSubClass")) ?? 0,
                    Sys.Hex(Path.Combine(path, "bInterfaceProtocol")) ?? 0,
                    Sys.LinkName(Path.Combine(path, "driver")),
                    path));
                continue;
            }
            if (!File.Exists(Path.Combine(path, "idVendor"))) continue;
            devices[name] = new UsbDevice
            {
                Name = name,
                SysPath = path,
                Bus = Sys.Int(Path.Combine(path, "busnum")) ?? 0,
                Vendor = Sys.Hex(Path.Combine(path, "idVendor")) ?? 0,
                Product = Sys.Hex(Path.Combine(path, "idProduct")) ?? 0,
                Manufacturer = Sys.Text(Path.Combine(path, "manufacturer")),
                ProductName = Sys.Text(Path.Combine(path, "product")),
                SpeedMbps = double.TryParse(Sys.Read(Path.Combine(path, "speed")), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed) ? speed : 0,
                Version = Sys.Read(Path.Combine(path, "version"))?.Trim() ?? "",
                MaxPowerMa = ParseMilliamps(Sys.Read(Path.Combine(path, "bMaxPower"))),
                SelfPowered = ((Sys.Hex(Path.Combine(path, "bmAttributes")) ?? 0) & 0x40) != 0,
                DeviceClass = Sys.Hex(Path.Combine(path, "bDeviceClass")) ?? 0,
                HubPorts = Sys.Int(Path.Combine(path, "maxchild")) ?? 0,
                RuntimeStatus = Sys.Read(Path.Combine(path, "power", "runtime_status")),
                Removable = Sys.Read(Path.Combine(path, "removable")),
                IsRootHub = name.StartsWith("usb", StringComparison.Ordinal)
            };
        }

        foreach (var usbInterface in interfaces)
        {
            var owner = usbInterface.Name[..usbInterface.Name.IndexOf(':')];
            // Root hub interfaces are named "1-0:1.0" while the hub itself is "usb1".
            if (owner.EndsWith("-0", StringComparison.Ordinal)) owner = "usb" + owner[..^2];
            if (devices.TryGetValue(owner, out var device)) device.Interfaces.Add(usbInterface);
        }

        foreach (var device in devices.Values)
        {
            if (device.IsRootHub) continue;
            var parentName = ParentName(device.Name);
            if (parentName is not null && devices.TryGetValue(parentName, out var parent))
            {
                device.Parent = parent;
                parent.Children.Add(device);
            }
        }

        foreach (var hub in devices.Values.Where(d => d.IsHub))
        {
            var hubInterface = hub.Interfaces.FirstOrDefault(i => i.Class == 0x09);
            if (hubInterface is null) continue;
            foreach (var portName in Sys.Children(hubInterface.SysPath).Where(n => n.Contains("-port", StringComparison.Ordinal)))
            {
                var portPath = Path.Combine(hubInterface.SysPath, portName);
                var port = new UsbPort
                {
                    Name = portName,
                    Number = int.TryParse(portName[(portName.LastIndexOf("port", StringComparison.Ordinal) + 4)..], out var n) ? n : 0,
                    Peer = Sys.LinkName(Path.Combine(portPath, "peer")),
                    ConnectType = Sys.Read(Path.Combine(portPath, "connect_type")),
                    State = Sys.Read(Path.Combine(portPath, "state")),
                    OverCurrent = Sys.Int(Path.Combine(portPath, "over_current_count")) ?? 0,
                    Hub = hub
                };
                var childName = hub.IsRootHub ? $"{hub.Bus}-{port.Number}" : $"{hub.Name}.{port.Number}";
                if (devices.TryGetValue(childName, out var child))
                {
                    port.Device = child;
                    child.Port = port;
                }
                hub.Ports.Add(port);
            }
            hub.Ports.Sort((a, b) => a.Number.CompareTo(b.Number));
        }

        foreach (var device in devices.Values) device.Children.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));

        return devices.Values
            .Where(d => d.IsRootHub)
            .Select(hub =>
            {
                var controllerPath = Path.GetDirectoryName(hub.SysPath) ?? "";
                return new UsbBus(hub.Bus, hub, Path.GetFileName(controllerPath), controllerPath);
            })
            .OrderBy(b => b.Number)
            .ToList();
    }

    /// <summary>"1-5.2" → "1-5"; "1-5" → "usb1".</summary>
    public static string? ParentName(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot > 0) return name[..dot];
        var dash = name.IndexOf('-');
        return dash > 0 ? "usb" + name[..dash] : null;
    }

    /// <summary>"500mA" → 500.</summary>
    public static int ParseMilliamps(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        var digits = new string(text.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out var value) ? value : 0;
    }

    /// <summary>What a negotiated speed is called: 480 → "USB 2.0 · 480 Mbps".</summary>
    public static (string Label, string Short, double Gbps) SpeedName(double mbps) => mbps switch
    {
        >= 40000 => ("USB4 · 40 Gbps", "40 Gbps", 40),
        >= 20000 => ("USB 3.2 Gen 2x2 · 20 Gbps", "20 Gbps", 20),
        >= 10000 => ("USB 3.2 Gen 2 · 10 Gbps", "10 Gbps", 10),
        >= 5000 => ("USB 3.2 Gen 1 · 5 Gbps", "5 Gbps", 5),
        >= 480 => ("USB 2.0 · 480 Mbps", "480 Mbps", 0.48),
        >= 12 => ("USB full speed · 12 Mbps", "12 Mbps", 0.012),
        > 0 => ("USB low speed · 1.5 Mbps", "1.5 Mbps", 0.0015),
        _ => ("USB", "USB", 0)
    };
}
