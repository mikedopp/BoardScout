namespace BoardScout.Services;

/// <summary>
/// Maker of a network device from the first three bytes of its MAC address, using the IEEE MA-L listing
/// bundled as DriverScout\data\oui.tsv. Only the prefixes asked for are kept, so the file is streamed once.
/// </summary>
internal static class Oui
{
    public static Dictionary<string, string> Resolve(IEnumerable<string> macs)
    {
        var wanted = macs.Select(Prefix).Where(p => p is not null).Select(p => p!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(AppContext.BaseDirectory, "DriverScout", "data", "oui.tsv");
        if (wanted.Count == 0 || !File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (line.Length < 8 || line[0] == '#' || line[6] != '\t') continue;
                var prefix = line[..6];
                if (wanted.Contains(prefix)) result[prefix] = Clean(line[7..]);
            }
        }
        catch (IOException)
        {
        }
        return result;
    }

    /// <summary>"E0:D3:62:12:34:56" → "E0D362", or null for a randomized (locally administered) address.</summary>
    public static string? Prefix(string? mac)
    {
        if (mac is null) return null;
        var hex = new string(mac.Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length < 6) return null;
        var first = Convert.ToInt32(hex[..2], 16);
        return (first & 0x02) != 0 ? null : hex[..6].ToUpperInvariant();
    }

    /// <summary>Phones and some PCs use a random MAC per network; its maker cannot be looked up.</summary>
    public static bool IsRandomized(string? mac)
    {
        if (mac is null) return false;
        var hex = new string(mac.Where(char.IsAsciiHexDigit).ToArray());
        return hex.Length >= 2 && (Convert.ToInt32(hex[..2], 16) & 0x02) != 0;
    }

    // "TP-Link Systems Inc." → "TP-Link Systems", "Hui Zhou Gaoshengda Technology Co.,LTD" → "Hui Zhou Gaoshengda"
    private static string Clean(string name)
    {
        var text = name.Trim();
        foreach (var suffix in new[] { ", Inc.", ", Inc", " Inc.", " Inc", " Co.,LTD", " Co., Ltd.", " Co.,Ltd.", " Co., Ltd", " Co.,Ltd",
                     " CO.,LTD", " CO., LTD.", " LTD.", " Ltd.", " Ltd", " LLC", " Corporation", " Corp.", " GmbH", " S.A.", " AG", " B.V.",
                     " Technology", " Technologies", " Electronics", " International", " INTL" })
        {
            if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) text = text[..^suffix.Length].TrimEnd(',', ' ', '.');
        }
        return text.Length == 0 ? name.Trim() : text;
    }
}
