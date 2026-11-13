using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace BoardScout.Platform.Mac;

/// <summary>
/// Reads the XML property lists that macOS tools print with -plist / -a (diskutil, ioreg): dict becomes a
/// dictionary, array a list, integer a long, real a double, true/false a bool, data a byte array, the rest a string.
/// </summary>
internal static class Plist
{
    public static object? Parse(string? xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return null;
        try
        {
            // Apple's DOCTYPE names a DTD on apple.com; never fetch it.
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            var root = XDocument.Load(reader).Root;
            var first = root?.Name.LocalName == "plist" ? root.Elements().FirstOrDefault() : root;
            return first is null ? null : Value(first);
        }
        catch (XmlException)
        {
            return null;
        }
    }

    public static Dictionary<string, object?>? Dict(string? xml) => Parse(xml) as Dictionary<string, object?>;

    private static object? Value(XElement element) => element.Name.LocalName switch
    {
        "dict" => ReadDict(element),
        "array" => element.Elements().Select(Value).ToList(),
        "integer" => long.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n
            : ulong.TryParse(element.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? (long)Math.Min(u, long.MaxValue) : 0L,
        "real" => double.TryParse(element.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0d,
        "true" => true,
        "false" => false,
        "data" => TryBase64(element.Value),
        _ => element.Value
    };

    private static Dictionary<string, object?> ReadDict(XElement element)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        string? key = null;
        foreach (var child in element.Elements())
        {
            if (child.Name.LocalName == "key") { key = child.Value; continue; }
            if (key is null) continue;
            result[key] = Value(child);
            key = null;
        }
        return result;
    }

    private static byte[] TryBase64(string text)
    {
        try { return Convert.FromBase64String(string.Concat(text.Where(c => !char.IsWhiteSpace(c)))); }
        catch (FormatException) { return []; }
    }

    // Typed lookups on a parsed dictionary.
    public static string? Str(this Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
    public static long? Long(this Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v switch { long l => l, double x => (long)x, _ => null } : null;
    public static bool? Bool(this Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is bool b ? b : null;
    public static Dictionary<string, object?>? Sub(this Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) ? v as Dictionary<string, object?> : null;
    public static List<object?> List(this Dictionary<string, object?> d, string key) => d.TryGetValue(key, out var v) && v is List<object?> l ? l : [];
}
