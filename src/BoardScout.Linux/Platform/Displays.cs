using System.Text;

namespace BoardScout.Platform;

/// <summary>What a monitor's EDID says about itself. The serial number is never read out of it.</summary>
internal sealed record Edid(string? Maker, string? Name, int? Year, int? Width, int? Height, double? RefreshHz, double? DiagonalInches);

/// <summary>One output of a graphics card (/sys/class/drm/card0-HDMI-A-1): its connector and what's plugged in.</summary>
internal sealed record DisplayOutput(string Card, string Connector, string Kind, bool Connected, bool Enabled, Edid? Edid,
    string? PreferredMode, string? GpuAddress);

internal static class Displays
{
    public static List<DisplayOutput> Read()
    {
        var outputs = new List<DisplayOutput>();
        foreach (var entry in Sys.Children("/sys/class/drm"))
        {
            var dash = entry.IndexOf('-');
            if (!entry.StartsWith("card", StringComparison.Ordinal) || dash < 0) continue;
            var path = $"/sys/class/drm/{entry}";
            var card = entry[..dash];
            var connector = entry[(dash + 1)..];
            if (connector.StartsWith("Writeback", StringComparison.Ordinal)) continue;
            var connected = Sys.Read(Path.Combine(path, "status")) == "connected";
            var edidBytes = connected ? Sys.Bytes(Path.Combine(path, "edid"), 32768) : null;
            outputs.Add(new DisplayOutput(
                card,
                connector,
                ConnectorKind(connector),
                connected,
                Sys.Read(Path.Combine(path, "enabled")) == "enabled",
                edidBytes is { Length: >= 128 } ? ParseEdid(edidBytes) : null,
                connected ? Sys.Lines(Path.Combine(path, "modes")).FirstOrDefault() : null,
                Pci.AddressIn(Sys.Real($"/sys/class/drm/{card}/device"))));
        }
        return outputs;
    }

    /// <summary>"HDMI-A-1" → "HDMI", "DP-2" → "DisplayPort", "eDP-1" → "eDP (built-in screen)".</summary>
    public static string ConnectorKind(string connector)
    {
        var type = connector[..Math.Max(0, connector.LastIndexOf('-'))];
        return type switch
        {
            "HDMI-A" or "HDMI-B" => "HDMI",
            "DP" => "DisplayPort",
            "eDP" => "eDP (built-in screen)",
            "DVI-D" or "DVI-I" or "DVI-A" => "DVI",
            "VGA" => "VGA",
            "LVDS" => "LVDS (built-in screen)",
            "DSI" => "DSI (built-in screen)",
            "Virtual" => "Virtual display",
            "USB" => "USB display",
            "Composite" or "SVIDEO" => "Analog TV",
            "" => connector,
            _ => type
        };
    }

    /// <summary>Decodes the base EDID block: maker, model name, year, preferred mode, and screen size.</summary>
    public static Edid? ParseEdid(byte[] edid)
    {
        if (edid.Length < 128 || edid[0] != 0x00 || edid[1] != 0xFF || edid[7] != 0x00) return null;
        // Maker: three 5-bit letters ("DEL", "SAM", "GSM").
        var id = (edid[8] << 8) | edid[9];
        var maker = new string([(char)('A' - 1 + ((id >> 10) & 0x1F)), (char)('A' - 1 + ((id >> 5) & 0x1F)), (char)('A' - 1 + (id & 0x1F))]);
        string? name = null;
        int? width = null, height = null;
        double? refresh = null;
        for (var d = 54; d <= 108; d += 18)
        {
            if (edid[d] == 0 && edid[d + 1] == 0)
            {
                // Display descriptor: 0xFC is the model name (0xFF, the serial number, is skipped on purpose).
                if (edid[d + 3] == 0xFC)
                    name = Encoding.ASCII.GetString(edid, d + 5, 13).Split('\n')[0].Trim();
            }
            else if (width is null)
            {
                // The first detailed timing is the monitor's preferred mode.
                var clock = ((edid[d + 1] << 8) | edid[d]) * 10_000.0;
                var hActive = edid[d + 2] + ((edid[d + 4] & 0xF0) << 4);
                var hBlank = edid[d + 3] + ((edid[d + 4] & 0x0F) << 8);
                var vActive = edid[d + 5] + ((edid[d + 7] & 0xF0) << 4);
                var vBlank = edid[d + 6] + ((edid[d + 7] & 0x0F) << 8);
                if (hActive > 0 && vActive > 0 && clock > 0)
                {
                    width = hActive;
                    height = vActive;
                    refresh = Math.Round(clock / ((hActive + hBlank) * (double)(vActive + vBlank)), 2);
                }
            }
        }
        double? diagonal = edid[21] > 0 && edid[22] > 0 ? Math.Round(Math.Sqrt(edid[21] * edid[21] + edid[22] * edid[22]) / 2.54, 0) : null;
        int? year = edid[17] is > 0 and < 0xFF ? 1990 + edid[17] : null;
        return new Edid(MakerName(maker), string.IsNullOrEmpty(name) ? null : name, year, width, height, refresh, diagonal);
    }

    // PNP IDs of common monitor makers.
    private static string MakerName(string code) => code switch
    {
        "DEL" => "Dell",
        "SAM" or "SEC" => "Samsung",
        "GSM" => "LG",
        "ACR" => "Acer",
        "AUS" => "ASUS",
        "BNQ" => "BenQ",
        "HWP" or "HPN" => "HP",
        "LEN" => "Lenovo",
        "AOC" => "AOC",
        "PHL" => "Philips",
        "VSC" => "ViewSonic",
        "MSI" => "MSI",
        "GBT" => "Gigabyte",
        "SNY" => "Sony",
        "VIZ" => "Vizio",
        "TCL" => "TCL",
        "HSI" or "HEC" => "Hisense",
        "APP" => "Apple",
        "AUO" => "AU Optronics",
        "BOE" => "BOE",
        "CMN" => "Innolux",
        "IVO" => "InfoVision",
        "SHP" => "Sharp",
        "LGD" => "LG Display",
        "NEC" => "NEC",
        "EIZ" or "ENC" => "EIZO",
        "IVM" => "iiyama",
        "RHT" => "Red Hat (virtual)",
        "QEM" => "QEMU (virtual)",
        _ => code
    };
}
