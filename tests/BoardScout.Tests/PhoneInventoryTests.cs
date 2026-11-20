using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

/// <summary>What a phone's answer over adb turns into. The Galaxy text is made up in the shape Samsung answers in; the TV text is a real answer, trimmed.</summary>
public class PhoneInventoryTests
{
    private const string Tv = """
        ##props
        [ro.board.platform]: [m7632]
        [ro.build.characteristics]: [tv]
        [ro.build.version.release]: [9]
        [ro.build.version.security_patch]: [2026-03-01]
        [ro.hardware]: [m7632]
        [ro.product.brand]: [Amazon]
        [ro.product.manufacturer]: [Amazon]
        [ro.product.model]: [AFTDCT31]
        ##name
        Living room TV
        ##mem
        MemTotal:        1703428 kB
        ##cpu
        cpu0 1500000
        cpu1 1500000
        cpu2 1500000
        cpu3 1500000
        ##cpuinfo
        Hardware	: M7632
        ##battery
        Current Battery Service state:
          AC powered: true
          USB powered: false
          status: 2
          health: 2
          present: true
          level: 100
          scale: 100
          temperature: 424
          technology: 
        ##display
        Physical size: 3840x2160
        Override size: 1920x1080
          DisplayDeviceInfo{"Built-in Screen": uniqueId="local:0", 3840 x 2160, modeId 2, supportedModes [{id=1, width=1920, height=1080, fps=60.000004}, {id=2, width=3840, height=2160, fps=60.000004}]}
        ##df
        Filesystem            1K-blocks    Used Available Use% Mounted on
        /dev/block/mmcblk0p22  11497788 2862968   8487364  26% /data
        ##uptime
        72822.06 58515.54
        """;

    [Fact]
    public void A_galaxy_answer_gives_its_chip_memory_storage_display_and_battery()
    {
        var phone = PhoneInventory.Parse(PhoneInventory.SampleProbe, "x");
        Assert.Equal("Samsung", phone.Maker);
        Assert.Equal("SM-S918U", phone.Model);
        Assert.Equal("Galaxy S23 Ultra", phone.MarketName);
        Assert.Equal("Phone", phone.Kind);
        Assert.Equal("Snapdragon 8 Gen 2", phone.Chip);
        Assert.Equal("8 cores · 1 × 3.36 + 4 × 2.80 + 3 × 2.02 GHz", phone.ChipDetail);
        Assert.Equal("3088 × 1440", phone.Resolution);
        Assert.Equal(120, phone.RefreshHz);
        Assert.Equal("16", phone.Android);
        Assert.Equal("8.0", phone.OneUi);
        Assert.True(phone.HasBattery);
        Assert.Equal(78, phone.BatteryLevel);
        Assert.Equal(31.2, phone.BatteryTempC!.Value, 3);
        Assert.Equal(91, phone.BatteryHealthPercent);
        Assert.Equal(412, phone.BatteryCycles);
        Assert.Equal("Charging", phone.BatteryState);
        Assert.Equal(478150656L * 1024, phone.StorageBytes);
        Assert.Equal(291110656L * 1024, phone.StorageFreeBytes);

        var specs = PhoneInventory.Specs(phone);
        Assert.Equal(["Chip", "Memory", "Storage", "Display", "System"], specs.Select(s => s.Label));
        Assert.Equal("12 GB", specs[1].Value);
        Assert.Equal("512 GB", specs[2].Value);
        Assert.Equal("Android 16", specs[4].Value);
        Assert.Equal("One UI 8.0 · patched 2026-10-01", specs[4].Detail);
    }

    [Fact]
    public void A_tv_has_no_battery_and_is_named_by_its_maker_and_model_in_privacy_mode()
    {
        var tv = PhoneInventory.Parse(Tv, "x");
        Assert.Equal("TV", tv.Kind);
        Assert.False(tv.HasBattery);
        Assert.Null(tv.BatteryLevel);
        Assert.Null(tv.BatteryTempC);
        Assert.Equal("M7632", tv.Chip);
        Assert.Equal("4 cores · 1.50 GHz", tv.ChipDetail);
        Assert.Equal("3840 × 2160", tv.Resolution);
        Assert.Equal(60, tv.RefreshHz);
        Assert.Null(tv.OneUi);
        Assert.Equal("Living room TV", tv.Title(privacy: false));
        Assert.Equal("Amazon AFTDCT31", tv.Title(privacy: true));
        var specs = PhoneInventory.Specs(tv);
        Assert.Equal("2 GB", specs.Single(s => s.Label == "Memory").Value);
        Assert.Equal("16 GB", specs.Single(s => s.Label == "Storage").Value);
    }

    [Fact]
    public void An_answer_with_nothing_in_it_gives_a_device_with_no_plates_and_does_not_throw()
    {
        var empty = PhoneInventory.Parse("", "x");
        Assert.Empty(PhoneInventory.Specs(empty));
        Assert.Equal("Android device", empty.Title(privacy: false));
        var partial = PhoneInventory.Parse("##props\n[ro.product.model]: [Pixel 9]\n##battery\n  level: abc\n##df\ngarbage\n", "x");
        Assert.Equal("Pixel 9", partial.Model);
        Assert.False(partial.HasBattery);
        Assert.Null(partial.StorageBytes);
    }

    [Fact]
    public void A_standard_cycle_count_is_used_when_the_phone_reports_one()
    {
        var text = PhoneInventory.SampleProbe.Replace("mSavedBatteryUsage: 41250", "Cycle count: 280");
        Assert.Equal(280, PhoneInventory.Parse(text, "x").BatteryCycles);
    }

    [Theory]
    [InlineData("Samsung", "SM-S918U", "Galaxy S23 Ultra")]
    [InlineData("Samsung", "SM-S928B", "Galaxy S24 Ultra")]
    [InlineData("Samsung", "SM-T290", "Galaxy Tab A 8.0 (2019)")]
    [InlineData("Samsung", "SM-X999", null)]
    [InlineData("Google", "SM-S918U", null)]
    public void Known_galaxy_model_numbers_get_their_names(string maker, string model, string? name) =>
        Assert.Equal(name, PhoneInventory.KnownName(maker, model));

    [Theory]
    [InlineData("SM8550", "Snapdragon 8 Gen 2")]
    [InlineData("s5e9925", "Exynos 2200")]
    [InlineData("MT6789", "MT6789")]
    public void A_chip_code_reads_as_its_name_when_it_has_one(string code, string name) =>
        Assert.Equal(name, PhoneInventory.ChipName(code));

    [Theory]
    [InlineData(10.95, 12)]
    [InlineData(7.4, 8)]
    [InlineData(1.62, 2)]
    public void Memory_is_named_by_the_size_it_is_sold_as(double reported, double sold) =>
        Assert.Equal(sold, PhoneInventory.Marketed(reported, [1, 1.5, 2, 3, 4, 6, 8, 12, 16, 18, 24, 32]));
}