using System.Net;
using System.Net.NetworkInformation;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

// The Connections map and the plan, built from a made-up machine shaped like a Dell OptiPlex 3050: an Intel
// B250 chipset, an NVMe drive on a chipset root port, integrated graphics, and a USB 3 drive bridge that came
// in at USB 2 speed on a USB 3 socket.
public class MapTests
{
    internal static HardwareCapture Machine()
    {
        const string root = "/sys/devices/pci0000:00";
        PciDevice Pci(string address, int vendor, int device, int @class, string? driver, string? name, PciLinkInfo? link = null) => new()
        {
            Address = address, SysPath = $"{root}/{address}", Vendor = vendor, Device = device, Class = @class, Driver = driver,
            DeviceName = name, VendorName = vendor == 0x8086 ? "Intel Corporation" : "Samsung Electronics Co Ltd", Link = link
        };
        var host = Pci("0000:00:00.0", 0x8086, 0x591f, 0x060000, "skl_uncore", "Host Bridge");
        var gpu = Pci("0000:00:02.0", 0x8086, 0x5912, 0x030000, "i915", "HD Graphics 630");
        var xhci = Pci("0000:00:14.0", 0x8086, 0xa2af, 0x0c0330, "xhci_hcd", "200 Series/Z370 Chipset Family USB 3.0 xHCI Controller");
        var lpc = Pci("0000:00:1f.0", 0x8086, 0xa2c8, 0x060100, null, "200 Series PCH LPC Controller (B250)");
        var peg = Pci("0000:00:01.0", 0x8086, 0x1901, 0x060400, "pcieport", "PCIe Controller (x16)");
        var port = Pci("0000:00:1b.0", 0x8086, 0xa2eb, 0x060400, "pcieport", "200 Series PCH PCI Express Root Port #21", new PciLinkInfo(8, 4, 8, 4));
        var nvme = new PciDevice
        {
            Address = "0000:02:00.0", SysPath = $"{root}/0000:00:1b.0/0000:02:00.0", ParentAddress = "0000:00:1b.0", Vendor = 0x144d, Device = 0xa804,
            Class = 0x010802, Driver = "nvme", DeviceName = "NVMe SSD Controller SM961/PM961/SM963", VendorName = "Samsung Electronics Co Ltd",
            Link = new PciLinkInfo(8, 4, 8, 4), MsiVectors = 5, Parent = port
        };
        port.Children.Add(nvme);

        var usb1 = new UsbDevice { Name = "usb1", SysPath = $"{root}/0000:00:14.0/usb1", Bus = 1, SpeedMbps = 480, Version = "2.00", DeviceClass = 9, HubPorts = 12, IsRootHub = true };
        var usb2 = new UsbDevice { Name = "usb2", SysPath = $"{root}/0000:00:14.0/usb2", Bus = 2, SpeedMbps = 5000, Version = "3.00", DeviceClass = 9, HubPorts = 6, IsRootHub = true };
        usb1.Interfaces.Add(new UsbInterface("1-0:1.0", 9, 0, 0, "hub", usb1.SysPath + "/1-0:1.0"));
        usb2.Interfaces.Add(new UsbInterface("2-0:1.0", 9, 0, 0, "hub", usb2.SysPath + "/2-0:1.0"));
        var bridge = new UsbDevice
        {
            Name = "1-5", SysPath = usb1.SysPath + "/1-5", Bus = 1, Vendor = 0x174c, Product = 0x55aa, ProductName = "AS2105", Manufacturer = "ASMedia",
            SpeedMbps = 480, Version = "2.10", SelfPowered = true, Parent = usb1
        };
        bridge.Interfaces.Add(new UsbInterface("1-5:1.0", 8, 6, 80, "usb-storage", bridge.SysPath + "/1-5:1.0"));
        usb1.Children.Add(bridge);
        var socket2 = new UsbPort { Name = "usb1-port5", Number = 5, Peer = "usb2-port4", ConnectType = "hotplug", State = "configured", Hub = usb1, Device = bridge };
        bridge.Port = socket2;
        usb1.Ports.Add(socket2);
        usb1.Ports.Add(new UsbPort { Name = "usb1-port1", Number = 1, Peer = "usb2-port1", ConnectType = "hotplug", State = "not attached", Hub = usb1 });
        usb1.Ports.Add(new UsbPort { Name = "usb1-port3", Number = 3, ConnectType = "hotplug", State = "not attached", Hub = usb1 });
        usb1.Ports.Add(new UsbPort { Name = "usb1-port10", Number = 10, ConnectType = "not used", State = "not attached", Hub = usb1 });
        usb2.Ports.Add(new UsbPort { Name = "usb2-port4", Number = 4, Peer = "usb1-port5", ConnectType = "hotplug", State = "not attached", Hub = usb2 });
        usb2.Ports.Add(new UsbPort { Name = "usb2-port1", Number = 1, Peer = "usb1-port1", ConnectType = "hotplug", State = "not attached", Hub = usb2 });

        var disks = new List<BlockDisk>
        {
            new() { Name = "nvme0n1", SysPath = $"{root}/0000:00:1b.0/0000:02:00.0/nvme/nvme0/nvme0n1", Transport = "nvme", PciAddress = "0000:02:00.0",
                Model = "SAMSUNG MZVPW256HEGL-000H1", Firmware = "CXZ74H0Q", SizeBytes = 256_060_514_304, DiscardMaxBytes = 2_199_023_255_040, Index = 0 },
            new() { Name = "sdc", SysPath = bridge.SysPath + "/1-5:1.0/host7/target7:0:0/7:0:0:0/block/sdc", Transport = "usb", PciAddress = "0000:00:14.0",
                UsbPath = bridge.SysPath, UsbDriver = "usb-storage", Vendor = "WDC WD10", Model = "JPVT-75A1YT0", Rotational = true, SizeBytes = 1_000_204_886_016, Index = 1 }
        };
        var identity = new MachineIdentity("Dell Inc.", "OptiPlex 3050", null, "Dell Inc.", "0JP3NX", "A00", "Dell Inc.", "1.2.0", "12/22/2016", 3);
        var network = new NetworkFacts([], new Dictionary<string, RouterFacts>(), new Dictionary<string, DnsFacts>(), [], null);
        return new HardwareCapture(
            new CpuFacts("Intel(R) Core(TM) i5-7500T CPU @ 2.70GHz", "GenuineIntel", 1, 4, 4, "0xf8", 3300, "powersave", "intel_pstate", "balance_performance", "x64", []),
            identity,
            new SecurityFacts(true, false, 2, null),
            new MemoryFacts(new FirmwareFacts([new DimmSlot("DIMM1", "", 8192, 2400, 2400, 1, "8ATF1G64HZ-2G3B1", "DDR4"), new DimmSlot("DIMM2", "", 0, 0, 0, 0, null, "DDR")], 2, 3, "Dell Inc.", "OptiPlex 3050"),
                new Dictionary<string, string> { ["DIMM1|"] = "Micron" }, "udev (from the SMBIOS tables)"),
            [host, peg, gpu, xhci, port, lpc, nvme],
            [new UsbBus(1, usb1, "0000:00:14.0", $"{root}/0000:00:14.0"), new UsbBus(2, usb2, "0000:00:14.0", $"{root}/0000:00:14.0")],
            disks,
            [],
            [],
            [],
            [],
            [],
            [],
            network,
            new Dictionary<(int, int), (string, string?)> { [(0x174c, 0x55aa)] = ("ASMedia", "ASM1051E SATA 6Gb/s bridge, ASM1053E SATA 6Gb/s bridge") },
            "Desktop",
            null,
            "7.0.0-38-generic",
            false,
            50,
            DateTimeOffset.Now);
    }

    private static Dictionary<string, ConnectionNode> Nodes(ConnectionsSnapshot snapshot) =>
        LinuxConnections.Walk(snapshot.Root).ToDictionary(n => n.Id);

    [Fact]
    public void Intel_chipset_holds_its_devices()
    {
        var map = LinuxConnections.Build(Machine(), privacy: false, discovery: null).Snapshot;
        var chipset = Assert.Single(map.Root.Children, n => n.Id == "chipset");
        Assert.Equal("Intel B250 chipset", chipset.Name);
        Assert.Equal("DMI", chipset.Link?.Short);
        Assert.Contains(chipset.Children, n => n.Kind == "usb-controller");
        var nvme = Assert.Single(chipset.Children, n => n.Kind == "nvme");
        Assert.Equal("SAMSUNG MZVPW256HEGL-000H1", nvme.Name);
        Assert.Equal("PCIe 3.0 x4", nvme.Link?.Label);
        Assert.False(nvme.Link!.Degraded);
        Assert.Equal(0, nvme.Disk);
        var gpu = Assert.Single(map.Root.Children, n => n.Kind == "gpu");
        Assert.Equal("Intel HD Graphics 630", gpu.Name);
        Assert.Equal("internal", gpu.Link?.Bus);
    }

    [Fact]
    public void Usb3_bridge_at_usb2_speed_is_flagged()
    {
        var nodes = Nodes(LinuxConnections.Build(Machine(), privacy: false, discovery: null).Snapshot);
        var drive = Assert.Single(nodes.Values, n => n.Kind == "storage");
        Assert.Equal("WDC WD10JPVT-75A1YT0", drive.Name);
        Assert.True(drive.Link?.Degraded);
        Assert.Equal("480 Mbps", drive.Link?.Short);
        Assert.Contains("USB 3 socket", drive.Warning);
        Assert.Contains(drive.Facts, f => f.Label == "Drive type" && f.Value.StartsWith("Hard drive", StringComparison.Ordinal));
        Assert.Contains(drive.Facts, f => f.Label == "USB protocol" && f.Value.StartsWith("Bulk-only", StringComparison.Ordinal));
    }

    [Fact]
    public void Usb_sockets_are_counted_once()
    {
        var controller = LinuxConnections.Build(Machine(), privacy: false, discovery: null).Controllers.Single().Summary;
        // Two sockets with USB 3 (ports 1 and 5 paired with SuperSpeed ports), one USB 2-only socket (port 3);
        // port 10 isn't wired and doesn't count.
        Assert.Equal((2, 1, 1, 0), (controller.Usb3Ports, controller.Usb3Used, controller.Usb2Ports, controller.Usb2Used));
    }

    [Fact]
    public void Memory_band_lists_every_slot()
    {
        var nodes = Nodes(LinuxConnections.Build(Machine(), privacy: false, discovery: null).Snapshot);
        var memory = nodes["memory"];
        Assert.Equal("Memory · 8 GB DDR4", memory.Name);
        Assert.Equal(2, memory.Children.Count);
        Assert.Contains(memory.Children, d => d.Kind == "dimm-empty");
        Assert.Contains(memory.Children.SelectMany(d => d.Facts), f => f.Label == "Made by" && f.Value == "Micron");
    }

    [Fact]
    public void Plan_names_the_slow_usb_drive()
    {
        var capture = Machine();
        var map = LinuxConnections.Build(capture, privacy: false, discovery: null);
        var plan = LinuxPlan.Build(capture, map, new CoreSample([]), null, privacy: false);
        var drives = Assert.Single(plan.Sections, s => s.Id == "usb-drives");
        Assert.Contains(drives.Items, i => i.Tone == "warn" && i.Title.Contains("USB 2 speed", StringComparison.Ordinal));
        Assert.Equal("chipset", Assert.Single(plan.UsbControllers).Where);
        Assert.Contains(plan.Sections, s => s.Id == "linux");
    }

    [Fact]
    public void Privacy_masks_nothing_it_shouldnt_and_ids_stay_stable()
    {
        var capture = Machine();
        var open = Nodes(LinuxConnections.Build(capture, privacy: false, discovery: null).Snapshot);
        var masked = Nodes(LinuxConnections.Build(capture, privacy: true, discovery: null).Snapshot);
        Assert.Equal(open.Keys.Order(), masked.Keys.Order());
        Assert.True(LinuxConnections.Build(capture, privacy: true, discovery: null).Snapshot.Privacy);
    }
}
