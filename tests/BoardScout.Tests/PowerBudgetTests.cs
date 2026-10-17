using BoardScout.Models;
using BoardScout.Services;
using LibreHardwareMonitor.Hardware;
using Xunit;

namespace BoardScout.Tests;

// The power breakdown: which sensor readings it uses, what an NVMe drive's power table says, and how measured,
// worked-out, estimated, and "at most" numbers are kept apart in the rows and the total.
public class PowerBudgetTests
{
    [Theory]
    [InlineData(HardwareType.Cpu, "Package", "CPU")]
    [InlineData(HardwareType.Cpu, "CPU Package", "CPU")]
    [InlineData(HardwareType.Cpu, "Core #3 (SMU)", "CPU core")]      // Ryzen per-core energy counters, added up
    [InlineData(HardwareType.Cpu, "CPU Cores", "CPU cores")]         // Intel RAPL
    [InlineData(HardwareType.Cpu, "Core Power", "CPU cores")]        // Ryzen 7000 power table
    [InlineData(HardwareType.Cpu, "SOC Power", "CPU SoC")]
    [InlineData(HardwareType.Cpu, "SoC", "CPU SoC")]                  // Ryzen 3000 power table
    [InlineData(HardwareType.Cpu, "CPU Graphics", "CPU graphics")]
    [InlineData(HardwareType.Cpu, "CPU PPT Limit", null)]
    [InlineData(HardwareType.Cpu, "Misc Power", null)]
    [InlineData(HardwareType.GpuNvidia, "GPU Package", "GPU")]
    [InlineData(HardwareType.GpuAmd, "GPU PPT", "GPU")]
    [InlineData(HardwareType.Psu, "Total watts", "PSU output")]      // Corsair
    [InlineData(HardwareType.Psu, "PSU Out", "PSU output")]          // MSI
    [InlineData(HardwareType.Psu, "Total Output", null)]             // Corsair's sum of the rails, read one by one instead
    [InlineData(HardwareType.Psu, "+12V", "PSU +12V")]
    [InlineData(HardwareType.Motherboard, "Package", null)]
    public void Sensor_names_map_to_breakdown_rows(HardwareType hardware, string name, string? device) =>
        Assert.Equal(device, SystemTelemetryService.PowerDevice(hardware, name));

    [Fact]
    public void Nvme_power_table_is_read_from_identify_data()
    {
        // WD Blue SN570 1TB, as it answers Identify Controller: five states, two of them sleep states.
        var identify = new byte[4096];
        identify[263] = 4;
        void State(int i, int mp, bool fine, bool sleep, int idle = 0, int idleScale = 0, int active = 0, int activeScale = 0)
        {
            var d = 2048 + i * 32;
            BitConverter.GetBytes((ushort)mp).CopyTo(identify, d);
            identify[d + 3] = (byte)((fine ? 1 : 0) | (sleep ? 2 : 0));
            BitConverter.GetBytes((ushort)idle).CopyTo(identify, d + 16);
            identify[d + 18] = (byte)(idleScale << 6);
            BitConverter.GetBytes((ushort)active).CopyTo(identify, d + 20);
            identify[d + 22] = (byte)(activeScale << 6);
        }
        State(0, 420, false, false, 63, 2, 370, 2);
        State(1, 270, false, false);
        State(2, 190, false, false);
        State(3, 250, true, true);
        State(4, 50, true, true);

        var table = DrivePowerTable.FromIdentify(identify);

        Assert.NotNull(table);
        Assert.Equal(4.2, table.MaxW, 3);
        Assert.Equal(0.63, table.IdleW!.Value, 3);
        Assert.Equal(3.7, table.ActiveW!.Value, 3);
        Assert.Equal(0.005, table.SleepW!.Value, 4);
        Assert.Equal(5, table.States);
    }

    [Fact]
    public void Nvme_power_table_without_power_is_ignored() =>
        Assert.Null(DrivePowerTable.FromIdentify(new byte[4096]));

    private static DiskFacts Nvme(int number) =>
        new(number, null, "WD Blue SN570 1TB", "1", 17, 1_000_000_000_000, 40, false, false, new DrivePowerTable(4.2, 0.63, 3.7, 0.005, 5));

    private static DiskFacts Sata(int number) => new(number, null, "Samsung SSD 850 EVO 500GB", "1", 11, 500_000_000_000, 35, false);

    private static DiskFacts UsbHdd(int number, string product = "Game Drive") => new(number, "WD", product, "1", 7, 8_000_000_000_000, null, true);

    [Fact]
    public void Without_a_power_supply_reading_the_total_is_measured_plus_estimates()
    {
        var sensors = new List<PowerReading> { new("CPU", 36.5), new("CPU cores", 20.5), new("GPU", 21) };
        var drives = new List<PowerBudget.Drive>
        {
            new(Nvme(2), "WD Blue SN570 1TB", "n-nvme", true, false),
            new(Sata(1), "Samsung SSD 850 EVO", null, true, false),
            new(UsbHdd(5), "WD Game Drive (7.3 TB)", null, false, true)
        };
        var usb = new List<PowerBudget.UsbDevice> { new("Keyboard", 100, "k"), new("Receiver", 98, "r") };
        var busy = new Dictionary<int, double> { [2] = 40 };

        var power = PowerBudget.Build(sensors, drives, usb, busy, "NVIDIA GeForce RTX 3050", elevated: true);
        PowerRow Row(string name) => power.Rows.Single(r => r.Name == name);

        Assert.Equal("measured", Row("Processor").Kind);
        Assert.Equal(16, Row("Everything but the cores").Watts);
        Assert.Equal("derived", Row("Everything but the cores").Kind);
        Assert.True(Row("Everything but the cores").Sub);
        Assert.Equal(21, Row("NVIDIA GeForce RTX 3050").Watts);

        // SN570 at 40% busy: 0.63 + 0.4 × (3.7 − 0.63) ≈ 1.86 W; the SATA SSD isn't being read, so its idle 0.3 W.
        Assert.Equal(1.86, Row("WD Blue SN570 1TB").Watts!.Value, 2);
        Assert.Equal(0.3, Row("Samsung SSD 850 EVO").Watts!.Value, 2);
        Assert.Equal(2.2, Row("Drives").Watts);
        Assert.Equal("estimate", Row("Drives").Kind);
        Assert.Contains("WD Game Drive (7.3 TB) (own power supply)", Row("Drives").Detail);
        Assert.DoesNotContain(power.Rows, r => r.Name.StartsWith("WD Game Drive", StringComparison.Ordinal));

        Assert.Equal("ceiling", Row("USB devices").Kind);
        Assert.Equal(1.0, Row("USB devices").Watts);
        Assert.Equal("unmeasured", Row("Motherboard, memory, and fans").Kind);
        Assert.Null(Row("Motherboard, memory, and fans").Watts);

        // USB's "at most" never joins the total.
        Assert.Equal(36.5 + 21 + 2.2, power.Total!.Value, 1);
        Assert.Equal("Measured and estimated", power.TotalLabel);
        Assert.Contains("USB devices (up to 1 W)", power.Note);
    }

    [Fact]
    public void A_reporting_power_supply_shows_everything_else()
    {
        var sensors = new List<PowerReading>
        {
            new("PSU output", 80, "Corsair HX750i"), new("PSU +12V", 70, "Corsair HX750i"), new("PSU +5V", 6, "Corsair HX750i", Derived: true),
            new("CPU", 24), new("GPU", 21)
        };

        var power = PowerBudget.Build(sensors, [], [], null);

        Assert.Equal(80, power.Total);
        Assert.Equal("The power supply delivers", power.TotalLabel);
        Assert.Equal("measured", power.Rows.Single(r => r.Name == "+12V rail").Kind);
        Assert.Equal("derived", power.Rows.Single(r => r.Name == "+5V rail").Kind);
        var rest = power.Rows.Single(r => r.Name == "Everything else");
        Assert.Equal(35, rest.Watts);
        Assert.Equal("derived", rest.Kind);
        Assert.DoesNotContain(power.Rows, r => r.Kind == "unmeasured");
    }

    [Fact]
    public void A_missing_processor_reading_says_why()
    {
        var power = PowerBudget.Build([new PowerReading("GPU", 21)], [], [], null, elevated: false);

        var cpu = power.Rows.Single(r => r.Name == "Processor");
        Assert.Equal("unmeasured", cpu.Kind);
        Assert.Null(cpu.Watts);
        Assert.Contains("administrator", cpu.Detail);
        Assert.StartsWith("Not counted: the processor (no reading)", power.Note);
    }

    [Fact]
    public void Drives_without_a_power_table_use_typical_figures_for_their_kind()
    {
        Assert.Equal((4.5, 6.5), Pair(PowerBudget.Figures(new DiskFacts(0, "WDC", "WD40EFRX-68N32N0", null, 11, null, null, true), usb: false)));
        Assert.Equal((0.8, 2.0), Pair(PowerBudget.Figures(new DiskFacts(0, "WDC WD10", "JPVT-75A1YT0", null, 11, null, null, true), usb: false)));
        Assert.Equal((1.0, 2.2), Pair(PowerBudget.Figures(UsbHdd(0), usb: true)));
        Assert.Equal((0.3, 3.0), Pair(PowerBudget.Figures(Sata(0), usb: false)));
        Assert.Equal((0.63, 3.7), Pair(PowerBudget.Figures(Nvme(0), usb: false)));
        static (double, double) Pair((double Idle, double Active, double? Sleep, string Basis) f) => (f.Idle, f.Active);
    }

    [Fact]
    public void Drive_cards_say_their_power_is_an_estimate()
    {
        var fact = PowerBudget.DriveFact(Nvme(2), usb: false);
        Assert.Equal("About 0.63 W idle (0.005 W asleep), up to 3.7 W busy. An estimate: from the drive's own power table.", fact);
    }

    [Theory]
    [InlineData("WDC WD10", "JPVT-75A1YT0", true)]
    [InlineData("WDC", "WD20NMVW-11AV3S2", true)]
    [InlineData("ST1000LM", "024 HN-M101MBB", true)]
    [InlineData("HGST", "HTS541010A9E680", true)]
    [InlineData("WDC", "WD40EFRX-68N32N0", false)]
    [InlineData("HGST", "HUS728T8TALE6L4", false)]
    [InlineData("WDC", "WD60EDAZ-11U78B0", false)]
    public void Laptop_hard_drives_by_model(string vendor, string product, bool laptop) =>
        Assert.Equal(laptop, DriveModels.LaptopHardDrive(vendor, product));
}
