using System.Drawing;
using BoardScout.Services;
using BoardScout.UI;
using Xunit;

namespace BoardScout.Tests;

/// <summary>The words the Drivers table shows, the colours the instruments use, and the per-thread load figures.</summary>
public class HeaderAndTableTests
{
    [Theory]
    [InlineData("update-available", "Update available")]
    [InlineData("current", "Up to date")]
    [InlineData("manual-check", "Check by hand")]
    [InlineData("error", "Check failed")]
    [InlineData("unknown", "Could not tell")]
    [InlineData("not checked", "Not checked")]
    [InlineData(null, "Not checked")]
    [InlineData("some-new-state", "Some new state")]
    public void A_driver_status_code_reads_as_words(string? code, string shown) =>
        Assert.Equal(shown, DriverLabels.Status(code));

    [Theory]
    [InlineData("bios", "BIOS")]
    [InlineData("gpu", "Graphics")]
    [InlineData("network", "Network")]
    [InlineData("peripheral", "Peripheral")]
    [InlineData("", "—")]
    public void A_driver_category_code_reads_as_words(string code, string shown) =>
        Assert.Equal(shown, DriverLabels.Category(code));

    [Theory]
    [InlineData("ms-catalog", "Microsoft Update Catalog")]
    [InlineData("ssd-vendor", "Drive maker")]
    [InlineData("nvidia", "NVIDIA")]
    [InlineData("asrock", "ASRock")]
    [InlineData("logitech", "Logitech")]
    [InlineData(null, "—")]
    [InlineData("—", "—")]
    public void Where_a_driver_was_found_reads_as_a_name(string? code, string shown) =>
        Assert.Equal(shown, DriverLabels.Source(code));

    [Fact]
    public void No_label_still_shows_a_code_with_dashes_in_it()
    {
        foreach (var code in new[] { "amd", "asrock", "dell-catalog", "fwupd", "lenovo-catalog", "mikrotik-api", "ms-catalog", "nvidia", "oem-bios", "router-firmware", "ssd-vendor", "ubiquiti-api" })
            Assert.DoesNotContain('-', DriverLabels.Source(code));
        foreach (var code in new[] { "current", "error", "manual-check", "unknown", "update-available" })
            Assert.DoesNotContain('-', DriverLabels.Status(code));
    }

    [Fact]
    public void A_ramp_has_a_colour_for_every_reading_and_clamps_outside_the_range()
    {
        foreach (var kind in Enum.GetValues<GaugeKind>()) Check(kind);
    }

    private static void Check(GaugeKind kind)
    {
        Assert.Equal(Heat.At(kind, 0f), Heat.At(kind, -3f));
        Assert.Equal(Heat.At(kind, 1f), Heat.At(kind, 7f));
        Assert.Equal(Heat.At(kind, 0f), Heat.At(kind, float.NaN));
        for (var i = 0; i <= 100; i++)
            Assert.Equal(255, Heat.At(kind, i / 100f).A);
    }

    [Fact]
    public void A_full_cpu_memory_or_drive_is_red_and_an_idle_one_is_not()
    {
        foreach (var kind in new[] { GaugeKind.Cpu, GaugeKind.Memory, GaugeKind.Disk })
        {
            var full = Heat.At(kind, 1f);
            var idle = Heat.At(kind, 0.05f);
            Assert.True(full.R > 200 && full.G < 100 && full.B < 100, $"{kind} at 100% should be red, was {full}");
            Assert.True(idle.R < 160, $"{kind} at 5% should be cool, was {idle}");
        }
    }

    [Fact]
    public void A_colour_between_two_stops_is_between_them()
    {
        // The CPU ramp runs cyan (34, 211, 238) at 0 to blue (59, 130, 246) at 45%.
        var mid = Heat.At(GaugeKind.Cpu, 0.225f);
        Assert.InRange(mid.R, 34, 59);
        Assert.InRange(mid.G, 130, 211);
        Assert.InRange(mid.B, 238, 246);
    }

    [Fact]
    public void Each_thread_gets_the_share_of_the_interval_it_was_not_idle()
    {
        // Three processors over one second (10,000,000 ticks): idle all of it, busy all of it, busy a quarter of it.
        // Kernel time includes idle time, as Windows reports it.
        var before = new byte[CoreLoads.EntrySize * 3];
        var after = new byte[CoreLoads.EntrySize * 3];
        void Set(byte[] buffer, int core, long idle, long kernel, long user)
        {
            BitConverter.GetBytes(idle).CopyTo(buffer, core * CoreLoads.EntrySize);
            BitConverter.GetBytes(kernel).CopyTo(buffer, core * CoreLoads.EntrySize + 8);
            BitConverter.GetBytes(user).CopyTo(buffer, core * CoreLoads.EntrySize + 16);
        }
        Set(before, 0, 500, 900, 100);
        Set(after, 0, 500 + 10_000_000, 900 + 10_000_000, 100);
        Set(before, 1, 500, 900, 100);
        Set(after, 1, 500, 900 + 4_000_000, 100 + 6_000_000);
        Set(before, 2, 500, 900, 100);
        Set(after, 2, 500 + 7_500_000, 900 + 8_000_000, 100 + 2_000_000);

        var loads = CoreLoads.Between(before, after);
        Assert.Equal(3, loads.Length);
        Assert.Equal(0, loads[0], 6);
        Assert.Equal(1, loads[1], 6);
        Assert.Equal(0.25, loads[2], 6);
    }

    [Fact]
    public void A_thread_with_no_time_between_readings_counts_as_idle_and_a_live_reading_is_in_range()
    {
        var same = new byte[CoreLoads.EntrySize * 2];
        Assert.All(CoreLoads.Between(same, same), load => Assert.Equal(0, load));

        var first = CoreLoads.Read();
        Thread.Sleep(120);
        var second = CoreLoads.Read();
        Assert.NotNull(first);
        Assert.NotNull(second);
        var loads = CoreLoads.Between(first!, second!);
        Assert.Equal(Environment.ProcessorCount, loads.Length);
        Assert.All(loads, load => Assert.InRange(load, 0, 1));
    }

    private static BoardScout.Models.ScanManifest Rig() => new()
    {
        FormFactor = "micro-atx",
        Scan = new() { Os = new() { Caption = "Microsoft Windows 10 Pro" } },
        SystemInfo = new()
        {
            CpuElement = System.Text.Json.JsonDocument.Parse("""{"name":"AMD Ryzen 7 5700G with Radeon Graphics","cores":8,"threads":16}""").RootElement
        },
        Components =
        [
            new() { Category = "gpu", Model = "AMD Radeon(TM) Graphics" },
            new() { Category = "gpu", Model = "NVIDIA GeForce RTX 3050" },
            new() { Category = "audio", Model = "Realtek High Definition Audio" }
        ],
        Memory = new()
        {
            TotalBytes = 17_066_950_656, // what Windows reports for two 8 GB sticks with some held back for graphics
            Slots = [new() { CapacityGb = 8, SpeedMhz = 3200 }, new() { CapacityGb = 0 }, new() { CapacityGb = 8, SpeedMhz = 3200 }, new() { CapacityGb = 0 }]
        },
        Volumes = [new() { Letter = "C:", SizeBytes = 512L << 30 }, new() { Letter = "D:", SizeBytes = 2048L << 30 }, new() { Letter = "X:", SizeBytes = 0 }]
    };

    [Fact]
    public void The_spec_plates_name_the_rigs_headline_parts_from_its_scan()
    {
        var specs = RigSpecs.For(Rig());
        Assert.Equal(["CPU", "Graphics", "Memory", "Storage", "System"], specs.Select(s => s.Label));
        Assert.Contains("5700G", specs[0].Value);
        Assert.Equal("8 cores · 16 threads", specs[0].Detail);
        // The card in a slot is named, not the processor's own graphics, and without its maker's name.
        Assert.Equal("GeForce RTX 3050", specs[1].Value);
        Assert.Equal("+ 1 more", specs[1].Detail);
        Assert.Equal("16 GB", specs[2].Value);
        Assert.Equal("2 × 8 GB · 3200 MHz", specs[2].Detail);
        Assert.Equal("2.5 TB", specs[3].Value);
        Assert.Equal("2 volumes", specs[3].Detail);
        Assert.Equal("Windows 10 Pro", specs[4].Value);
        Assert.Equal("MICRO-ATX", specs[4].Detail);
    }

    [Fact]
    public void A_part_the_scan_has_nothing_for_gets_no_plate()
    {
        Assert.Empty(RigSpecs.For(new BoardScout.Models.ScanManifest()));
        var rig = Rig();
        rig.Components.Clear();
        rig.Memory = new();
        Assert.Equal(["CPU", "Storage", "System"], RigSpecs.For(rig).Select(s => s.Label));
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 3050", "GeForce RTX 3050")]
    [InlineData("AMD Radeon(TM) Graphics", "Radeon Graphics")]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", "Arc A770 Graphics")]
    public void A_graphics_card_is_named_without_its_maker_or_trademark_marks(string model, string shown) =>
        Assert.Equal(shown, RigSpecs.ShortGraphicsName(model));

    [Theory]
    [InlineData(500L << 30, "500 GB")]
    [InlineData(1024L << 30, "1 TB")]
    [InlineData(2560L << 30, "2.5 TB")]
    public void Storage_is_shown_in_the_units_the_rest_of_the_app_uses(long bytes, string shown) =>
        Assert.Equal(shown, RigSpecs.Size(bytes));

    [Fact]
    public void A_command_button_is_wider_with_its_label_than_as_an_icon_and_a_chevron_adds_width()
    {
        using var plain = new CommandButton { Glyph = CommandGlyph.Drivers, Text = "&Drivers" };
        using var menu = new CommandButton { Glyph = CommandGlyph.Drivers, Text = "&Drivers", DropDown = true };
        Assert.True(plain.FullWidth > plain.CompactWidth);
        Assert.True(menu.FullWidth > plain.FullWidth);
        Assert.True(menu.CompactWidth > plain.CompactWidth);
        Assert.Equal(plain.FullWidth, plain.Width);
        plain.Compact = true;
        Assert.Equal(plain.CompactWidth, plain.Width);
        Assert.True(plain.CompactWidth >= CommandButton.CommandHeight);
    }

    [Fact]
    public void The_access_key_marker_is_not_measured_or_announced_as_text()
    {
        using var with = new CommandButton { Text = "&Scan now" };
        using var without = new CommandButton { Text = "Scan now" };
        Assert.Equal(without.FullWidth, with.FullWidth);
    }
}
