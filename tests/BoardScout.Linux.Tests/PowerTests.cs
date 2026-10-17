using System.Runtime.InteropServices;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Linux.Tests;

// The Linux power readings: powercap and amd_energy counters turned into watts, why the processor has no reading when it
// has none, corsair-psu and graphics readings, an NVMe drive's power table, and the breakdown and plan built from the
// made-up OptiPlex 3050 (MapTests.Machine).
public class PowerTests
{
    private const long Range = 262_143_328_850; // max_energy_range_uj on an i5-7500T

    private static Power.RawCounter Zone(string zone, string? name, long? microjoules) => new(zone, name, microjoules, Range, Hwmon: false);

    [Theory]
    [InlineData("intel-rapl:0", "package-0", "CPU")]
    [InlineData("intel-rapl:1", "package-1", "CPU")]
    [InlineData("intel-rapl:0:0", "core", "CPU cores")]
    [InlineData("intel-rapl:0:1", "uncore", "CPU graphics")]
    [InlineData("intel-rapl:0:2", "dram", "Memory")]
    [InlineData("intel-rapl:1", "psys", null)]              // a laptop's whole platform, not the processor
    [InlineData("intel-rapl-mmio:0", "package-0", null)]    // the same package through another door
    public void Powercap_zones_map_to_breakdown_rows(string zone, string name, string? device) =>
        Assert.Equal(device, Power.Device(Zone(zone, name, 0)));

    [Theory]
    [InlineData("Esocket0", "CPU")]
    [InlineData("Ecore012", "CPU cores")]
    [InlineData("Eother", null)]
    public void Amd_energy_labels_map_to_breakdown_rows(string label, string? device) =>
        Assert.Equal(device, Power.Device(new Power.RawCounter("hwmon3/energy1_input", label, 0, 0, Hwmon: true)));

    [Fact]
    public void Powercap_energy_over_time_is_watts()
    {
        // Two readings two seconds apart on an idle OptiPlex 3050 (i5-7500T).
        var before = Power.FromCounters(0,
        [
            Zone("intel-rapl:0", "package-0", 29_130_065_136), Zone("intel-rapl:0:0", "core", 22_151_575_921),
            Zone("intel-rapl:0:1", "uncore", 245_910), Zone("intel-rapl:0:2", "dram", 5_088_235_936),
            Zone("intel-rapl:1", "psys", 1_000_000), Zone("intel-rapl-mmio:0", "package-0", 5)
        ]);
        var after = Power.FromCounters(0,
        [
            Zone("intel-rapl:0", "package-0", 29_132_143_255), Zone("intel-rapl:0:0", "core", 22_152_741_506),
            Zone("intel-rapl:0:1", "uncore", 245_910), Zone("intel-rapl:0:2", "dram", 5_088_869_479),
            Zone("intel-rapl:1", "psys", 9_000_000), Zone("intel-rapl-mmio:0", "package-0", 9_000_000)
        ]);

        Assert.True(after.Present);
        Assert.False(after.Locked);
        Assert.Equal(4, after.Counters.Count);
        var watts = Power.Watts(before, after, seconds: 2).ToDictionary(r => r.Device, r => r.Watts);
        Assert.Equal(1.0, watts["CPU"]);
        Assert.Equal(0.6, watts["CPU cores"]);
        Assert.Equal(0.0, watts["CPU graphics"]);
        Assert.Equal(0.3, watts["Memory"]);
        Assert.Equal(4, watts.Count);
    }

    [Fact]
    public void Energy_counters_wrap_and_sockets_add_up()
    {
        Assert.Equal(828_850, Power.Delta(Range - 328_850, 500_000, Range));
        Assert.Equal(0, Power.Delta(5, 3, 0));   // a counter with no range that drops was reset: nothing counted
        Assert.Equal(3, Power.Delta(1, 4, 0));

        var before = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", Range - 500_000), Zone("intel-rapl:1", "package-1", 0)]);
        var after = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", 500_000), Zone("intel-rapl:1", "package-1", 1_000_000)]);
        Assert.Equal(2.0, Assert.Single(Power.Watts(before, after, seconds: 1)).Watts);
        Assert.Empty(Power.Watts(before, after, seconds: 0)); // the same moment twice
    }

    [Fact]
    public void Amd_energy_is_used_only_without_a_readable_package()
    {
        Power.RawCounter Input(int n, string label, long microjoules) => new($"hwmon3/energy{n}_input", label, microjoules, 0, Hwmon: true);

        // powercap's package is root-only here; amd_energy answers.
        var before = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", null), Input(1, "Ecore000", 1_000_000), Input(2, "Ecore001", 2_000_000), Input(17, "Esocket0", 10_000_000)]);
        var after = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", null), Input(1, "Ecore000", 2_000_000), Input(2, "Ecore001", 3_000_000), Input(17, "Esocket0", 15_000_000)]);
        var watts = Power.Watts(before, after, seconds: 1).ToDictionary(r => r.Device, r => r.Watts);
        Assert.Equal(5.0, watts["CPU"]);
        Assert.Equal(2.0, watts["CPU cores"]);

        // With a readable package, powercap wins and amd_energy is left out.
        var both = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", 7), Input(17, "Esocket0", 10_000_000)]);
        Assert.Equal("intel-rapl:0", Assert.Single(both.Counters).Key);
    }

    [Fact]
    public void The_processor_row_says_why_it_has_no_reading()
    {
        var locked = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", null), Zone("intel-rapl:0:0", "core", null)]);
        Assert.True(locked.Present);
        Assert.True(locked.Locked);
        Assert.Empty(locked.Counters);
        Assert.Contains("only root", Power.NoCpuReading(locked, [], root: false));
        Assert.Contains("sudo", Power.NoCpuReading(locked, [], root: false));
        Assert.Contains("even as root", Power.NoCpuReading(locked, [], root: true));

        var none = Power.FromCounters(0, []);
        Assert.False(none.Present);
        Assert.Contains("publishes no energy counters", Power.NoCpuReading(none, [], root: true));

        var first = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", 1)]);
        Assert.StartsWith("Measuring", Power.NoCpuReading(first, [], root: true));
        Assert.Null(Power.NoCpuReading(first, [new PowerReading("CPU", 9.5)], root: true));
    }

    [Fact]
    public void Graphics_and_power_supply_readings()
    {
        var chips = new List<HwmonChip>
        {
            new("corsairpsu", null, [], [], [new("power total", 212.04), new("power +12v", 180.5), new("power +5v", 12), new("power +3.3v", 8)]),
            new("amdgpu", "/sys/devices/pci0000:00/0000:00:08.1/0000:05:00.0", [], [], [new("slowPPT", 15)]), // an APU's whole chip
            new("amdgpu", "/sys/devices/pci0000:00/0000:00:01.1/0000:01:00.0", [], [], [new("PPT", 41.2)])
        };
        var readings = Power.Readings(null, Power.FromCounters(0, []), chips).ToDictionary(r => r.Device);
        Assert.Equal(41.2, readings["GPU"].Watts);
        Assert.Equal(212.0, readings["PSU output"].Watts);
        Assert.Equal("the Corsair power supply", readings["PSU output"].Source);
        Assert.Equal(180.5, readings["PSU +12V"].Watts);
        Assert.Equal(12, readings["PSU +5V"].Watts);
        Assert.Equal(8, readings["PSU +3.3V"].Watts);
        Assert.False(readings.ContainsKey("CPU"));
    }

    [Fact]
    public void Nvme_admin_command_matches_the_kernel()
    {
        Assert.Equal(72, Marshal.SizeOf<Power.NvmeAdminCommand>());
        Assert.Equal(24, (int)Marshal.OffsetOf<Power.NvmeAdminCommand>(nameof(Power.NvmeAdminCommand.Address)));
        Assert.Equal(36, (int)Marshal.OffsetOf<Power.NvmeAdminCommand>(nameof(Power.NvmeAdminCommand.DataLength)));
        Assert.Equal(40, (int)Marshal.OffsetOf<Power.NvmeAdminCommand>(nameof(Power.NvmeAdminCommand.Cdw10)));
        Assert.Equal(64, (int)Marshal.OffsetOf<Power.NvmeAdminCommand>(nameof(Power.NvmeAdminCommand.TimeoutMs)));
        Assert.Equal(0xC0484E41u, Power.NvmeIoctlAdminCommand);
        Assert.Null(Power.NvmePowerTable("../sda")); // only controller names
    }

    [Fact]
    public void Nvme_power_table_without_idle_figures()
    {
        // Samsung PM961 (MZVPW256HEGL), as nvme id-ctrl prints it: 6.8, 5.6, 4.4 W, then 40 mW and 5 mW sleep states, no idle or active figures.
        var identify = new byte[4096];
        identify[263] = 4;
        void State(int i, int mp, bool fine, bool sleep)
        {
            BitConverter.GetBytes((ushort)mp).CopyTo(identify, 2048 + i * 32);
            identify[2048 + i * 32 + 3] = (byte)((fine ? 1 : 0) | (sleep ? 2 : 0));
        }
        State(0, 680, false, false);
        State(1, 560, false, false);
        State(2, 440, false, false);
        State(3, 400, true, true);
        State(4, 50, true, true);

        var table = DrivePowerTable.FromIdentify(identify);

        Assert.NotNull(table);
        Assert.Equal(6.8, table.MaxW, 3);
        Assert.Null(table.IdleW);
        Assert.Null(table.ActiveW);
        Assert.Equal(0.005, table.SleepW!.Value, 4);
        Assert.Equal(5, table.States);
        var disk = new BlockDisk { Name = "nvme0n1", SysPath = "/sys/block/nvme0n1", Transport = "nvme", Model = "SAMSUNG MZVPW256HEGL-000H1", PowerTable = table };
        var (idle, active, sleep, basis) = PowerBudget.Figures(LinuxPower.Facts(disk), usb: false);
        Assert.Equal((0.7, 6.8, 0.005), (idle, active, sleep!.Value));
        Assert.Equal("From the drive's own power table (idle is a typical figure)", basis);
    }

    [Fact]
    public void Linux_disks_become_drive_facts()
    {
        var capture = MapTests.Machine();
        var nvme = LinuxPower.Facts(capture.Disks[0]);
        Assert.Equal((17, false, 0), (nvme.Bus, nvme.Spinning, nvme.Number));
        var usb = LinuxPower.Facts(capture.Disks[1]);
        Assert.Equal((7, true, true), (usb.Bus, usb.Spinning, usb.SpinningFromModel));

        // SATA drives say "ATA" as their vendor; dropped, the model still reads as a 2.5-inch drive.
        var sata = new BlockDisk { Name = "sda", SysPath = "/sys/block/sda", Transport = "sata", Vendor = "ATA", Model = "ST500LT012-9WS14", Rotational = true };
        var facts = LinuxPower.Facts(sata);
        Assert.Null(facts.Vendor);
        Assert.Equal(11, facts.Bus);
        var (idle, active, _, _) = PowerBudget.Figures(facts, usb: false);
        Assert.Equal((0.8, 2.0), (idle, active));

        Assert.False(LinuxPower.Counted(new BlockDisk { Name = "sda", SysPath = "/sys/block/sda", Transport = "scsi", Vendor = "QEMU", Model = "QEMU HARDDISK" }));
        Assert.False(LinuxPower.Counted(new BlockDisk { Name = "mmcblk0", SysPath = "/sys/block/mmcblk0", Transport = "mmc" }));
        Assert.True(LinuxPower.Counted(new BlockDisk { Name = "sdb", SysPath = "/sys/block/sdb", Transport = "scsi", Vendor = "SEAGATE", Model = "ST4000NM0023" }));
    }

    private static HardwareCapture WithKeyboard()
    {
        var capture = MapTests.Machine();
        var root = capture.Usb[0].RootHub;
        root.Children.Add(new UsbDevice
        {
            Name = "1-3", SysPath = root.SysPath + "/1-3", Bus = 1, Vendor = 0x046d, Product = 0xc31c, ProductName = "USB Keyboard",
            SpeedMbps = 1.5, Version = "1.10", MaxPowerMa = 100, Parent = root
        });
        return capture;
    }

    private static PowerSample Sample() => new(
        [new PowerReading("CPU", 12.5), new PowerReading("CPU cores", 6.0), new PowerReading("CPU graphics", 0.5), new PowerReading("Memory", 1.2)],
        new Dictionary<int, double> { [0] = 40, [1] = 0 }, null);

    [Fact]
    public void Breakdown_keeps_measured_estimated_and_at_most_apart()
    {
        var capture = WithKeyboard();
        var power = LinuxPower.Breakdown(capture, Sample());
        var rows = power.Rows.ToDictionary(r => r.Name);

        Assert.Equal(("measured", 12.5, "cpu"), (rows["Processor"].Kind, rows["Processor"].Watts!.Value, rows["Processor"].Node));
        Assert.Equal(6.0, rows["Cores"].Watts);
        Assert.True(rows["Cores"].Sub);
        Assert.Equal(0.5, rows["Built-in graphics"].Watts);
        Assert.Equal(("derived", 6.0), (rows["Everything but the cores"].Kind, rows["Everything but the cores"].Watts!.Value));
        Assert.EndsWith("the package minus the cores and the built-in graphics.", rows["Everything but the cores"].Detail);

        // Memory from the processor's DRAM counter is an estimate, never a measurement.
        Assert.Equal(("estimate", 1.2), (rows["Memory"].Kind, rows["Memory"].Watts!.Value));

        // Only the NVMe drive takes the PC's power: the USB drive's bridge has its own supply.
        Assert.Equal(("estimate", 2.4), (rows["Drives"].Kind, rows["Drives"].Watts!.Value));
        Assert.Contains("Not counted: WDC WD10JPVT-75A1YT0 (own power supply).", rows["Drives"].Detail);
        var nvme = rows["SAMSUNG MZVPW256HEGL-000H1"];
        Assert.Equal(2.42, nvme.Watts!.Value, 2); // 0.7 W idle + 40% of the way to 5 W
        Assert.Equal(LinuxConnections.PciNodeId(capture.Pci.Single(p => p.Address == "0000:02:00.0")), nvme.Node); // the controller's card
        Assert.Contains("40% busy right now", nvme.Detail);

        Assert.Equal(("ceiling", 0.5), (rows["USB devices"].Kind, rows["USB devices"].Watts!.Value));
        Assert.Equal("ceiling", rows["USB Keyboard"].Kind);

        Assert.Equal("unmeasured", rows["Motherboard and fans"].Kind);
        Assert.Contains("corsair-psu", rows["Motherboard and fans"].Detail);
        Assert.False(rows.ContainsKey("Motherboard, memory, and fans"));

        Assert.Equal(16.1, power.Total);  // processor + memory + drives; never the "at most" USB figure
        Assert.Equal("Measured and estimated", power.TotalLabel);
        Assert.Equal("Not counted: the motherboard and fans, USB devices (up to 0.5 W), and the power supply's own losses.", power.Note);
        Assert.Equal("Not counted: the motherboard and fans, and the power supply's own losses.",
            LinuxPower.Breakdown(MapTests.Machine(), Sample()).Note); // no bus-powered USB devices
    }

    [Fact]
    public void Without_root_the_processor_row_says_so()
    {
        var locked = Power.FromCounters(0, [Zone("intel-rapl:0", "package-0", null)]);
        var readings = new List<PowerReading> { new("GPU", 21) };
        var power = LinuxPower.Breakdown(MapTests.Machine(), new PowerSample(readings, null, Power.NoCpuReading(locked, readings, root: false)), "AMD Radeon RX 6600");

        var cpu = Assert.Single(power.Rows, r => r.Name == "Processor");
        Assert.Equal("unmeasured", cpu.Kind);
        Assert.Null(cpu.Watts);
        Assert.Contains("Start BoardScout with sudo", cpu.Detail);
        Assert.Equal(("measured", 21.0), (power.Rows.Single(r => r.Name == "AMD Radeon RX 6600").Kind, power.Rows.Single(r => r.Name == "AMD Radeon RX 6600").Watts!.Value));
        Assert.Contains("idle figure", power.Rows.Single(r => r.Name == "SAMSUNG MZVPW256HEGL-000H1").Detail); // nothing sampled busy yet
        Assert.Contains("the processor (no reading)", power.Note);
    }

    [Fact]
    public void Plan_power_section_has_the_table()
    {
        var capture = WithKeyboard();
        var map = LinuxConnections.Build(capture, privacy: false, discovery: null);

        var plan = LinuxPlan.Build(capture, map, new CoreSample([]), null, privacy: false, Sample());

        Assert.NotNull(plan.Power);
        var section = Assert.Single(plan.Sections, s => s.Id == "power");
        Assert.StartsWith("Measured and estimated: about 16 W right now.", section.Summary);
        Assert.DoesNotContain(section.Items, i => i.Title == "Biggest power user right now");
        Assert.Contains(section.Items, i => i.Title == "Root sees more of the power"); // the fixture isn't root
        // Rows are named and linked like the map's cards.
        var nodes = LinuxConnections.Walk(map.Snapshot.Root).ToDictionary(n => n.Id);
        var keyboard = Assert.Single(plan.Power.Rows, r => r.Node == LinuxConnections.UsbNodeId(capture.UsbDevices.Single(d => d.Name == "1-3")));
        Assert.Equal(nodes[keyboard.Node!].Name, keyboard.Name);
        Assert.Equal(nodes.Values.Single(n => n.Disk == 0).Id, plan.Power.Rows.Single(r => r.Name == "SAMSUNG MZVPW256HEGL-000H1").Node);
        Assert.Contains("\"power\":{\"rows\":[", System.Text.Json.JsonSerializer.Serialize(plan, Json.Options));

        // The Overview's quick build has no power sample, so no table.
        Assert.Null(LinuxPlan.Build(capture, map, new CoreSample([]), null, privacy: false).Power);
    }

    [Fact]
    public void Drive_cards_show_the_estimate()
    {
        var capture = MapTests.Machine();
        var nodes = LinuxConnections.Walk(LinuxConnections.Build(capture, privacy: false, discovery: null).Snapshot.Root).ToDictionary(n => n.Id);

        var nvme = nodes.Values.Single(n => n.Disk == 0);
        Assert.Equal("About 0.7 W idle, up to 5 W busy. An estimate: typical for an NVMe SSD.", nvme.Facts.Single(f => f.Label == "Power (estimate)").Value);
        var usb = nodes[LinuxConnections.UsbNodeId(capture.UsbDevices.Single(d => d.Name == "1-5"))];
        Assert.Equal("About 1 W idle, up to 2.2 W busy. An estimate: typical for a portable hard drive and its USB bridge, while it spins.",
            usb.Facts.Single(f => f.Label == "Power (estimate)").Value);
    }
}
