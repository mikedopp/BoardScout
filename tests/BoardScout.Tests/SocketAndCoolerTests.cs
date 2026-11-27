using BoardScout.Models;
using BoardScout.Services;
using BoardScout.Views;
using Xunit;

namespace BoardScout.Tests;

public sealed class SocketAndCoolerTests
{
    private static readonly CoolerDevice Cooler = new("NZXT Kraken X63",
    [
        new("Liquid", "temperature", 31.4, "°C"),
        new("Pump", "speed", 2412, "RPM"),
        new("Pump", "duty", 70, "%")
    ]);

    [Fact]
    public void ProcessorSocketComesFromSmbiosType4()
    {
        // A type 4 structure whose "Socket Designation" is its first string, then the end marker.
        byte[] table = [4, 6, 0, 0, 1, 3, (byte)'A', (byte)'M', (byte)'4', 0, 0, 127, 4, 1, 0, 0, 0];
        Assert.Equal("AM4", Smbios.Parse(table, 0).CpuSocket);
    }

    [Fact]
    public void CoolerBecomesAPartListingEveryReading()
    {
        var part = Assert.Single(BoardProfiles.Coolers([Cooler], text => text));
        Assert.Equal("cooler:0", part.Live);
        Assert.Equal("31.4 °C · 2412 RPM", part.Detail);
        Assert.Equal(["31.4 °C", "2412 RPM", "70 %"], part.Facts.Select(f => f.Value));
    }

    [Fact]
    public void UnknownBoardSaysWhenNoFanHeaderWasRead()
    {
        var parts = BoardProfiles.Apply("Some board", [], "why not", socket: "LGA1700").Parts;
        Assert.Equal("why not", Assert.Single(parts, p => p.Zone == "fans").Note);
    }

    [Fact]
    public void KnownBoardKeepsItsPrintedFanHeadersWhenFansAreRead()
    {
        var fan = new BoardPart("fan-1", "fans", "fan", "Fan #1", "900 RPM", "good", []);
        var parts = BoardProfiles.Apply("ASRock B550M Steel Legend", [fan], "why not").Parts;
        Assert.Single(parts, p => p.Zone == "fans");
        Assert.Equal(6, parts.Count(p => p.Kind == "header" && (p.Silk!.StartsWith("CHA_") || p.Silk.StartsWith("CPU"))));
    }
}
