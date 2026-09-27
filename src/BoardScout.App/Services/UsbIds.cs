using System.Text.RegularExpressions;

namespace BoardScout.Services;

/// <summary>
/// Vendor and product names from the USB ID Repository file DriverScout already bundles
/// (DriverScout\data\usb.ids). Only the IDs asked for are kept, so the 750 KB file is streamed once.
/// </summary>
internal static partial class UsbIds
{
    public static Dictionary<(int Vendor, int Product), (string Vendor, string? Product)> Resolve(IEnumerable<(int Vendor, int Product)> ids)
    {
        var wanted = ids.ToHashSet();
        var vendors = wanted.Select(id => id.Vendor).ToHashSet();
        var result = new Dictionary<(int, int), (string, string?)>();
        var path = Path.Combine(AppContext.BaseDirectory, "DriverScout", "data", "usb.ids");
        if (wanted.Count == 0 || !File.Exists(path)) return result;

        try
        {
            string? vendorName = null;
            var vendorId = -1;
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length < 6 || line[0] == '#') continue;
                if (line[0] != '\t')
                {
                    // The vendor list ends where the device class lists ("C 00 ...") begin.
                    if (line[1] == ' ' && !IsHex(line.AsSpan(0, 4))) break;
                    vendorId = IsHex(line.AsSpan(0, 4)) ? Convert.ToInt32(line[..4], 16) : -1;
                    vendorName = vendors.Contains(vendorId) ? CleanVendor(line[4..].Trim()) : null;
                    if (vendorName is not null)
                        foreach (var id in wanted.Where(w => w.Vendor == vendorId))
                            result.TryAdd(id, (vendorName, null));
                    continue;
                }
                if (vendorName is null || line.Length < 7 || line[1] == '\t' || !IsHex(line.AsSpan(1, 4))) continue;
                var product = Convert.ToInt32(line.Substring(1, 4), 16);
                if (wanted.Contains((vendorId, product)))
                    result[(vendorId, product)] = (vendorName, line[5..].Trim());
            }
        }
        catch (IOException)
        {
        }
        return result;
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
            if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    // "Western Digital Technologies, Inc." → "Western Digital"
    private static string CleanVendor(string name)
    {
        string previous;
        do
        {
            previous = name;
            name = CorporateSuffix().Replace(name, "").Trim().TrimEnd(',', '.').Trim();
        }
        while (name != previous && name.Length > 0);
        return name.Length == 0 ? previous : name;
    }

    [GeneratedRegex(@"[,\s]+(Inc|Corp|Corporation|Co|Ltd|Limited|LLC|GmbH|AG|S\.A|B\.V|Technologies|Technology|Semiconductor|Systems|Computer|Electronics|International|Industries)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CorporateSuffix();
}
