using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardScout.Platform;

/// <summary>Vendor, device, and subsystem names for one PCI function, from the PCI ID Repository.</summary>
internal sealed record PciNames(string? Vendor, string? Device, string? Subsystem);

/// <summary>
/// Names from the PCI ID Repository file BoardScout bundles (DriverScout/data/pci.ids), falling back to the
/// system's copy. Only the IDs asked for are kept, so the 1.6 MB file is streamed once per map.
/// </summary>
internal static partial class PciIds
{
    public static Dictionary<(int Vendor, int Device, int SubVendor, int SubDevice), PciNames> Resolve(
        IEnumerable<(int Vendor, int Device, int SubVendor, int SubDevice)> ids, string? path = null)
    {
        var wanted = ids.ToHashSet();
        var result = new Dictionary<(int, int, int, int), PciNames>();
        path ??= new[]
        {
            Path.Combine(AppContext.BaseDirectory, "DriverScout", "data", "pci.ids"),
            "/usr/share/hwdata/pci.ids",
            "/usr/share/misc/pci.ids"
        }.FirstOrDefault(File.Exists);
        if (wanted.Count == 0 || path is null) return result;
        var vendors = wanted.Select(w => w.Vendor).ToHashSet();
        var devices = wanted.Select(w => (w.Vendor, w.Device)).ToHashSet();

        try
        {
            string? vendorName = null, deviceName = null;
            int vendor = -1, device = -1;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length < 5 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    // The vendor list ends where the class lists ("C 00  Unclassified device") begin.
                    if (line.StartsWith("C ", StringComparison.Ordinal)) break;
                    vendor = ParseHex(line.AsSpan(0, 4));
                    vendorName = vendors.Contains(vendor) ? line[4..].Trim() : null;
                    device = -1;
                    deviceName = null;
                    if (vendorName is not null)
                        foreach (var id in wanted.Where(w => w.Vendor == vendor))
                            result.TryAdd(id, new PciNames(vendorName, null, null));
                    continue;
                }
                if (vendorName is null) continue;
                if (line.Length > 1 && line[1] != '\t')
                {
                    device = ParseHex(line.AsSpan(1, 4));
                    deviceName = devices.Contains((vendor, device)) ? line[5..].Trim() : null;
                    if (deviceName is not null)
                        foreach (var id in wanted.Where(w => w.Vendor == vendor && w.Device == device))
                            result[id] = new PciNames(vendorName, deviceName, null);
                    continue;
                }
                if (deviceName is null || line.Length < 13) continue;
                // "\t\tsubvendor subdevice  name"
                var subVendor = ParseHex(line.AsSpan(2, 4));
                var subDevice = ParseHex(line.AsSpan(7, 4));
                if (wanted.Contains((vendor, device, subVendor, subDevice)))
                    result[(vendor, device, subVendor, subDevice)] = new PciNames(vendorName, deviceName, line[11..].Trim());
            }
        }
        catch (IOException)
        {
        }
        return result;
    }

    /// <summary>"Intel Corporation" → "Intel", "Advanced Micro Devices, Inc. [AMD/ATI]" → "AMD".</summary>
    public static string CleanVendor(string name)
    {
        var bracket = Bracketed().Match(name);
        if (bracket.Success)
        {
            var inner = bracket.Groups[1].Value;
            return inner.Contains('/') ? inner.Split('/')[0] : inner;
        }
        string previous;
        do
        {
            previous = name;
            name = CorporateSuffix().Replace(name, "").Trim().TrimEnd(',', '.').Trim();
        }
        while (name != previous && name.Length > 0);
        return name.Length == 0 ? previous : name;
    }

    /// <summary>The marketing name when the repository gives one in brackets ("GA106 [GeForce RTX 3050]").</summary>
    public static string CleanDevice(string name)
    {
        var bracket = Bracketed().Match(name);
        return bracket.Success && bracket.Groups[1].Value.Length >= 4 ? bracket.Groups[1].Value : name;
    }

    private static int ParseHex(ReadOnlySpan<char> text) =>
        int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : -1;

    [GeneratedRegex(@"\[([^\]]+)\]\s*$")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[,\s]+(Inc|Corp|Corporation|Co|Ltd|Limited|LLC|GmbH|AG|S\.A|B\.V|Technologies|Technology|Semiconductor|Systems|Electronics|International|Industries)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CorporateSuffix();
}
