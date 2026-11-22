using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

// A memory stick's own SPD chip: its BCD manufacture date, a blank serial, and what each slot says about
// temperature for DDR4 without a sensor, DDR4 with a TSOD and DDR5 (whose SPD5118 hub always has one).
public class DimmSpdTests
{
    private static readonly DateOnly Today = new(2026, 11, 21);

    [Theory]
    [InlineData(0x21, 0x14, 2021, 14)]
    [InlineData(0x24, 0x52, 2024, 52)]
    [InlineData(0x19, 0x01, 2019, 1)]
    public void Decodes_bcd_year_and_week(byte year, byte week, int expectedYear, int expectedWeek)
    {
        var made = DimmSpd.DecodeDate(year, week, 2026);
        Assert.Equal(new DimmMade(expectedYear, expectedWeek), made);
    }

    [Theory]
    [InlineData(0x00, 0x00)] // not filled in (the G.Skill kit on the test PC)
    [InlineData(0xFF, 0xFF)] // erased EEPROM
    [InlineData(0x21, 0x00)] // week 0
    [InlineData(0x21, 0x54)] // week 54
    [InlineData(0x2A, 0x10)] // not BCD
    [InlineData(0x30, 0x10)] // 2030, later than this year
    public void Rejects_blank_or_impossible_dates(byte year, byte week) =>
        Assert.Null(DimmSpd.DecodeDate(year, week, 2026));

    [Theory]
    [InlineData("000", null)]
    [InlineData("00000000", null)]
    [InlineData("FFFFFFFF", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("1A2B3C4D", "1A2B3C4D")]
    [InlineData("00012345", "00012345")]
    public void Blank_serials_read_as_none(string? raw, string? expected) =>
        Assert.Equal(expected, DimmSpd.CleanSerial(raw));

    [Fact]
    public void Serial_is_all_four_bytes_in_hex()
    {
        Assert.Equal("1A2B3C4D", DimmSpd.SerialFromBytes([0x1A, 0x2B, 0x3C, 0x4D]));
        Assert.Null(DimmSpd.SerialFromBytes([0, 0, 0, 0]));
        Assert.Null(DimmSpd.SerialFromBytes([0xFF, 0xFF, 0xFF, 0xFF]));
    }

    [Fact]
    public void Age_is_said_in_words()
    {
        Assert.Equal("about 6 years old", DimmSpd.AgeText(new DimmMade(2021, 14), Today));
        Assert.Equal("about a year old", DimmSpd.AgeText(new DimmMade(2025, 40), Today));
        Assert.Equal("about 3 months old", DimmSpd.AgeText(new DimmMade(2026, 33), Today));
    }

    [Fact]
    public void Ddr4_without_a_tsod_says_so_and_has_no_date()
    {
        var stick = new DimmSpdInfo(0x50, "DDR4", "G Skill Intl", "SK Hynix", "F4-3200C16-8GVKB", null, null,
            false, null, null, null, null, null, false);
        var facts = DimmSpd.Facts(stick, elevated: true, Today);
        Assert.Equal(("Temperature", "No sensor on this stick (DDR4 without a TSOD chip)"), facts[0]);
        Assert.Contains(("Memory chips", "SK Hynix"), facts);
        Assert.Contains(("Made", "Not recorded by the maker"), facts);
        Assert.DoesNotContain(facts, f => f.Label == "Serial");
    }

    [Fact]
    public void Ddr5_reports_temperature_limit_alarm_and_age()
    {
        var stick = new DimmSpdInfo(0x51, "DDR5", "Kingston", "Micron Technology", "KF560C36-16", "A1B2C3D4", new DimmMade(2024, 10),
            true, 47.25, 0, 55, 85, "above its high limit", false);
        var facts = DimmSpd.Facts(stick, elevated: true, Today);
        Assert.Equal(("Temperature", "47.3 °C (alarm at 85 °C)"), facts[0]);
        Assert.Contains(("Sensor alarm", "Stick is above its high limit"), facts);
        Assert.Contains(("Made", "week 10 of 2024 (about 3 years old)"), facts);
        Assert.Contains(("Serial", "A1B2C3D4"), facts);
    }

    [Fact]
    public void Ddr4_with_a_tsod_uses_its_high_limit()
    {
        var stick = new DimmSpdInfo(0x52, "DDR4", "Samsung", "Samsung", "M393A2K40DB3", null, new DimmMade(2020, 30),
            true, 38.5, 0, 85, null, null, true);
        Assert.Equal(("Temperature", "38.5 °C (alarm at 85 °C)"), DimmSpd.Facts(stick, true, Today)[0]);
    }

    [Fact]
    public void Without_admin_it_asks_instead_of_guessing() =>
        Assert.StartsWith("Run BoardScout as administrator", DimmSpd.Facts(null, elevated: false, Today)[0].Value);
}
