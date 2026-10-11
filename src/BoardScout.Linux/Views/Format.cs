using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace BoardScout.Views;

/// <summary>Number and name formatting shared by the Linux edition's views (the same wording as the Windows edition).</summary>
internal static partial class Format
{
    public static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..10].ToLowerInvariant();

    /// <summary>The key the live-rate messages use for a network interface.</summary>
    public static string NetKey(string interfaceName) => "n" + ShortHash(interfaceName);

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => $"{bytes / 1e12:0.#} TB",
        >= 1_000_000_000 => $"{bytes / 1e9:0} GB",
        >= 1_000_000 => $"{bytes / 1e6:0} MB",
        _ => $"{bytes / 1e3:0} KB"
    };

    public static string Generation(int gen) => gen switch
    {
        1 => "1.1",
        2 => "2.0",
        3 => "3.0",
        4 => "4.0",
        5 => "5.0",
        6 => "6.0",
        _ => $"gen {gen}"
    };

    /// <summary>Usable data rate per lane after encoding overhead, in Gbps.</summary>
    public static double LaneGbps(int gen) => gen switch
    {
        1 => 2.0,
        2 => 4.0,
        3 => 7.877,
        4 => 15.754,
        5 => 31.508,
        6 => 60.5,
        _ => 0
    };

    public static string Rate(double gbps) => gbps >= 8 ? $"{gbps / 8:0.#} GB/s" : $"{gbps * 125:0} MB/s";

    public static string Latency(long milliseconds) => milliseconds < 1 ? "<1 ms" : $"{milliseconds} ms";

    public static string BitRate(long bitsPerSecond) => bitsPerSecond switch
    {
        >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#} Gbps",
        >= 1_000_000 => $"{bitsPerSecond / 1e6:0} Mbps",
        > 0 => $"{bitsPerSecond / 1e3:0} Kbps",
        _ => "unknown speed"
    };

    public static string Plural(int count, string one, string? many = null) =>
        $"{count} {(count == 1 ? one : many ?? one + "s")}";

    /// <summary>"A", "A and B", "A, B, and C".</summary>
    public static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => string.Join(", ", list.Take(list.Count - 1)) + ", and " + list[^1]
        };
    }

    /// <summary>"Dell Inc." → "Dell", "ASUSTeK COMPUTER INC." → "ASUS", "LENOVO" → "Lenovo".</summary>
    public static string Company(string name)
    {
        var trimmed = Regex.Replace(name.Trim(), @"[,\s]+(Inc\.?|INC\.?|Corporation|Corp\.?|Co\.,? ?Ltd\.?|Ltd\.?|LLC|GmbH|Limited|Incorporation|Technology Co\.?)$", "", RegexOptions.IgnoreCase).Trim();
        return trimmed.ToUpperInvariant() switch
        {
            "ASUSTEK COMPUTER" or "ASUSTEK" => "ASUS",
            "LENOVO" => "Lenovo",
            "HEWLETT-PACKARD" or "HP" => "HP",
            "MICRO-STAR INTERNATIONAL" or "MICRO-STAR INTERNATIONAL CO." => "MSI",
            "GIGABYTE TECHNOLOGY" => "Gigabyte",
            "ASROCK" => "ASRock",
            "TOSHIBA" => "Toshiba",
            _ => trimmed.Length == 0 ? name : trimmed
        };
    }

    public static string ShortCpuName(string name)
    {
        var text = CpuNoise().Replace(name, " ");
        return Regex.Replace(text, @"\s{2,}", " ").Trim();
    }

    public static bool SameSubnet(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix)) return false;
        if (network.AddressFamily != address.AddressFamily || prefix is < 0 or > 32) return false;
        var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
        var a = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray());
        var b = BitConverter.ToUInt32(network.GetAddressBytes().Reverse().ToArray());
        return (a & mask) == (b & mask);
    }

    public static string Duration(TimeSpan span) => span.TotalDays >= 1
        ? $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")}, {span.Hours} h"
        : span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min" : $"{span.Minutes} min";

    public static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    [GeneratedRegex(@"\((R|TM)\)|\bwith Radeon( Vega)? Graphics\b|\b\d+-Core Processor\b|\bCPU\b|@\s*[\d.]+\s*GHz|\bProcessor\b", RegexOptions.IgnoreCase)]
    private static partial Regex CpuNoise();
}
