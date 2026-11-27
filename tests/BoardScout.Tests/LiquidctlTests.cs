using BoardScout.Services;
using Xunit;

namespace BoardScout.Tests;

public sealed class LiquidctlTests
{
    // The shape "liquidctl status --json" prints (1.16.0), with made-up readings.
    private const string Json = """
        [
          {"bus": "hid", "address": "a", "description": "Corsair Hydro H100i Pro XT", "status": [
            {"key": "Liquid temperature", "value": 29.5, "unit": "°C"},
            {"key": "Fan 1 speed", "value": 840, "unit": "rpm"},
            {"key": "Pump duty", "value": 60, "unit": "%"},
            {"key": "Pump mode", "value": "balanced", "unit": ""},
            {"key": "Firmware version", "value": "2.1.0", "unit": null}
          ]},
          {"bus": "hid", "address": "b", "description": "Corsair HX750i", "status": [{"key": "Total power", "value": 90, "unit": "W"}]},
          {"bus": "hid", "address": "c", "description": "ASUS Aura LED Controller", "status": []}
        ]
        """;

    [Fact]
    public void KeepsCoolersAndTheirNumbers()
    {
        var cooler = Assert.Single(Liquidctl.Parse(Json));
        Assert.Equal("Corsair Hydro H100i Pro XT", cooler.Name);
        Assert.Equal(["temperature", "speed", "duty"], cooler.Sensors.Select(s => s.Kind));
        Assert.Equal("RPM", cooler.Sensors[1].Unit);
    }

    [Theory]
    [InlineData("NZXT Kraken X63", "NZXT Kraken X (X53, X63 or X73)", true)]
    [InlineData("x53", "NZXT Kraken X (X53, X63 or X73)", true)]
    [InlineData("d5next", "Aquacomputer D5 Next", true)]
    [InlineData("NZXT Kraken X63", "Corsair Hydro H100i Pro XT", false)]
    public void OneDeviceReadTwoWaysIsListedOnce(string known, string other, bool same) =>
        Assert.Equal(same, Liquidctl.SameDevice(known, other));
}
