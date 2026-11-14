using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace BoardScout.DriveHealth;

/// <summary>Apple's XML property lists (diskutil -plist) as plain objects: dictionaries, lists, strings, longs, doubles, bools.</summary>
public static class Plist
{
    public static object? Parse(string xml)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null };
        using var reader = XmlReader.Create(new StringReader(xml), settings);
        var doc = XDocument.Load(reader);
        var top = doc.Root?.Elements().FirstOrDefault();
        return top == null ? null : Value(top);
    }

    static object? Value(XElement e) => e.Name.LocalName switch
    {
        "dict" => Dict(e),
        "array" => e.Elements().Select(Value).ToList(),
        "string" => e.Value,
        "integer" => long.TryParse(e.Value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null,
        "real" => double.TryParse(e.Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null,
        "true" => true,
        "false" => false,
        "date" or "data" => e.Value.Trim(),
        _ => null,
    };

    static Dictionary<string, object?> Dict(XElement e)
    {
        var d = new Dictionary<string, object?>();
        string? key = null;
        foreach (var c in e.Elements())
        {
            if (c.Name.LocalName == "key") key = c.Value;
            else if (key != null) { d[key] = Value(c); key = null; }
        }
        return d;
    }

    public static string? Str(this Dictionary<string, object?>? d, string key) => d != null && d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;
    public static long? Long(this Dictionary<string, object?>? d, string key) => d != null && d.TryGetValue(key, out var v) && v is long l ? l : null;
    public static bool? Bool(this Dictionary<string, object?>? d, string key) => d != null && d.TryGetValue(key, out var v) && v is bool b ? b : null;
    public static IEnumerable<Dictionary<string, object?>> Dicts(this Dictionary<string, object?>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is List<object?> list ? list.OfType<Dictionary<string, object?>>() : Enumerable.Empty<Dictionary<string, object?>>();
    public static IEnumerable<string> Strings(this Dictionary<string, object?>? d, string key) =>
        d != null && d.TryGetValue(key, out var v) && v is List<object?> list ? list.OfType<string>() : Enumerable.Empty<string>();
}
