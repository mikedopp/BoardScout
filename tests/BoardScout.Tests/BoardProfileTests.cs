using BoardScout.Views;
using Xunit;

namespace BoardScout.Tests;

// The Overview's board: a board BoardScout knows slot by slot gets its printed labels, its empty slots and ports,
// and its lane sharing; any other board is drawn only from what was read.
public class BoardProfileTests
{
    private const string FanHelp = "No reading.";

    private static List<BoardPart> SteelLegendParts() =>
    [
        new("cpu", "cpu", "cpu", "AMD Ryzen 7 5700G", "8 cores · 16 threads", "good", [], Live: "cpu", Side: "cpu"),
        new("dimm-1", "memory", "dimm", "8 GB", "A1", "good", [], Side: "cpu"),
        new("dimm-2", "memory", "dimm", "Open", "A2", "open", [], Empty: true, Side: "cpu"),
        new("n-ssd1", "m2", "nvme", "CT2000P3PSSD8", "PCIe 3.0 x4 · CPU", "good", [], Width: 4, Side: "cpu"),
        new("n-gpu", "slots", "gpu", "GeForce RTX 3050", "PCIe 3.0 x8 · CPU", "warn", [], Width: 8, Side: "cpu"),
        new("chipset", "chipset", "chipset", "AMD B550 chipset", "PCIe 3.0 x4", "good", [], Side: "chipset"),
        new("n-ssd2", "m2", "nvme", "WD Blue SN570 1TB", "PCIe 3.0 x2 · chipset", "warn", [], Width: 2, Side: "chipset"),
        new("n-wifi", "io", "wifi", "Wi-Fi", "Not connected", "good", [new("Adapter", "Intel(R) Wi-Fi 6E AX210 160MHz")], Side: "chipset"),
        new("n-sata", "sata", "sata", "SATA controller", null, "good", [], Side: "chipset"),
        new("d-1", "sata", "disk", "Lexar 512GB SSD", "SATA 6 Gb/s", "good", [], Side: "chipset"),
        new("d-2", "sata", "disk", "Samsung SSD 850 EVO 500GB", "SATA 6 Gb/s", "good", [], Side: "chipset")
    ];

    [Fact]
    public void SteelLegendNamesEveryPartTheWayTheBoardPrintsIt()
    {
        var result = BoardProfiles.Apply("ASRock B550M Steel Legend", SteelLegendParts(), FanHelp, 16);
        var silk = result.Parts.Where(p => p.Silk is not null).ToDictionary(p => p.Silk!, p => p);

        Assert.Equal("ASRock B550M Steel Legend", result.Layout);
        Assert.Equal("CT2000P3PSSD8", silk["M2_1"].Name);
        Assert.Equal("WD Blue SN570 1TB", silk["M2_2"].Name);
        Assert.Equal("Intel Wi-Fi 6E AX210 160MHz", silk["M2_3"].Name);
        Assert.Equal("m2", silk["M2_3"].Zone);
        Assert.Equal("GeForce RTX 3050", silk["PCIE1"].Name);
        Assert.True(silk["PCIE2"].Empty);
        Assert.True(silk["PCIE3"].Empty);
        Assert.Equal("DDR4_A1", silk["DDR4_A1"].Silk);
        Assert.Equal("AM4", silk["AM4"].Silk);
    }

    [Fact]
    public void ADriveInM2TwoTurnsSataFiveAndSixOff()
    {
        var parts = BoardProfiles.Apply("ASRock B550M Steel Legend", SteelLegendParts(), FanHelp).Parts;
        var ports = parts.Where(p => p.Silk?.StartsWith("SATA3_", StringComparison.Ordinal) == true).ToDictionary(p => p.Silk!);

        Assert.Equal(6, ports.Count);
        Assert.Equal("Lexar 512GB SSD", ports["SATA3_1"].Name);
        Assert.Equal("Samsung SSD 850 EVO 500GB", ports["SATA3_2"].Name);
        Assert.Equal("open", ports["SATA3_3"].Tone);
        Assert.Equal("off", ports["SATA3_5"].Tone);
        Assert.Equal("off", ports["SATA3_6"].Tone);

        // Without a drive in M2_2, the ports are open and M2_2 is an empty socket.
        var without = SteelLegendParts().Where(p => p.Id != "n-ssd2").ToList();
        var open = BoardProfiles.Apply("ASRock B550M Steel Legend", without, FanHelp).Parts;
        Assert.Equal("open", open.Single(p => p.Silk == "SATA3_6").Tone);
        Assert.True(open.Single(p => p.Silk == "M2_2").Empty);
    }

    [Fact]
    public void SteelLegendAddsItsHeadersAndRearIo()
    {
        var parts = BoardProfiles.Apply("ASRock B550M Steel Legend", SteelLegendParts(), FanHelp).Parts;

        Assert.Equal(6, parts.Count(p => p.Kind == "header"));
        Assert.Equal(6, parts.Count(p => p.Kind == "fan" && p.Note == FanHelp));
        Assert.Equal("rear-io", parts[0].Kind);
        // Slots top to bottom, as they sit on the board.
        Assert.Equal(["M2_1", "PCIE1", "PCIE2", "M2_3", "M2_2", "PCIE3"],
            parts.Where(p => p.Zone is "m2" or "slots").Select(p => p.Silk).ToArray());
    }

    [Fact]
    public void AnyOtherBoardIsDrawnFromWhatWasRead()
    {
        var parts = SteelLegendParts();
        var result = BoardProfiles.Apply("Dell Inc. OptiPlex 3050", parts, FanHelp, 16);

        Assert.Null(result.Layout);
        Assert.Empty(result.Notes);
        Assert.Equal(parts.Count, result.Parts.Count);
        Assert.All(result.Parts, p => Assert.Null(p.Silk));
        // Notes about what a part suits still come along.
        Assert.StartsWith("Plenty of cores", result.Parts.Single(p => p.Kind == "cpu").Note);
        Assert.StartsWith("Entry-level graphics", result.Parts.Single(p => p.Kind == "gpu").Note);
    }

    [Theory]
    [InlineData("NVIDIA GeForce RTX 4090", "High-end")]
    [InlineData("NVIDIA GeForce RTX 3070", "Performance")]
    [InlineData("AMD Radeon RX 6600 XT", "Mainstream")]
    [InlineData("NVIDIA GeForce GTX 1650", "Entry-level")]
    public void GraphicsNotesFollowTheModelTier(string name, string tier)
    {
        var gpu = new BoardPart("g", "slots", "gpu", name, "PCIe 4.0 x16 · CPU", "good", []);
        Assert.StartsWith(tier, PartNotes.For(gpu, 0));
    }
}
