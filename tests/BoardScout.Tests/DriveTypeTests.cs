using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

// Hard drive or SSD from the model number, for drives whose USB bridge doesn't report a seek penalty.
// Vendor and product are split the way the drives' own descriptors split them.
public class DriveTypeTests
{
    [Theory]
    // Hard drives
    [InlineData("WDC WD10", "JPVT-75A1YT0")]   // 1 TB laptop drive behind an ASMedia bridge (the one that started this)
    [InlineData("WDC WD25", "00BEKT-75PVMT1")]
    [InlineData("WDC WD50", "00BEVT-00A0RT0")]
    [InlineData("WDC WD10", "EZEX-75WN4A0")]
    [InlineData("WDC", "WD40EFRX-68N32N0")]
    [InlineData("ST1000LM", "024 HN-M101MBB")]
    [InlineData("ST500LT0", "12-9WS142")]
    [InlineData("", "ST4000DM004-2CV104")]
    [InlineData("HGST", "HTS541010A9E680")]
    [InlineData("TOSHIBA", "MQ01ABD100")]
    [InlineData("TOSHIBA", "DT01ACA300")]
    [InlineData("SAMSUNG", "HD103SJ")]
    public void Spinning_models(string vendor, string product) =>
        Assert.True(DeviceTree.SpinningFromModelNumber(vendor, product));

    [Theory]
    // SSDs
    [InlineData("WDC WDS5", "00G2B0C-00PXH0")]
    [InlineData("", "WD Blue SN570 1TB")]
    [InlineData("", "CT2000P3PSSD8")]
    [InlineData("", "Samsung SSD 850 EVO 500GB")]
    [InlineData("", "Lexar 512GB SSD")]
    [InlineData("SAMSUNG", "MZVPW256HEGL-000")]
    [InlineData("Realtek", "RTL9210 NVME")]
    [InlineData("", "CT500MX500SSD1")]
    [InlineData("KINGSTON", "SA400S37240G")]
    public void Solid_state_models(string vendor, string product) =>
        Assert.False(DeviceTree.SpinningFromModelNumber(vendor, product));

    [Theory]
    // Enclosure names say nothing about the drive inside: no guess.
    [InlineData("WD", "Game Drive")]
    [InlineData("WD", "My Passport 0820")]
    [InlineData("WD", "My Book 25ED")]
    [InlineData("Generic", "STORAGE DEVICE")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public void Unknown_models(string? vendor, string? product) =>
        Assert.Null(DeviceTree.SpinningFromModelNumber(vendor, product));
}
