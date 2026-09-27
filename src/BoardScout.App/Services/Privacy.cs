using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace BoardScout.Services;

/// <summary>
/// Privacy mode: masks what identifies this PC or its owner — the PC name, the Windows registered-owner
/// email and product ID, serial numbers, the machine ID, user paths, and volume labels — in the UI, the
/// scan log, and exports. Scans themselves stay complete on disk; only what is shown or exported changes.
/// </summary>
internal static class Privacy
{
    public const string HostPlaceholder = "THIS-PC";

    // Keys whose values identify the machine anywhere in a scan.
    private static readonly Regex MachineKey = new("serial|uuid|machine_id", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Email = new(@"[\w.+-]+@[\w-]+(\.[\w-]+)+", RegexOptions.Compiled);
    private static readonly Regex MacAddress = new(@"\b([0-9A-F]{2}[:-]){5}[0-9A-F]{2}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static List<(string Secret, string Replacement)> _secrets = [];

    public static bool Enabled => AppSettings.Current.PrivacyMode;

    /// <summary>Collects this PC's identifying values from a raw scan so they can be scrubbed from any text.</summary>
    public static void Learn(string? rawScanJson)
    {
        var found = new List<(string, string)>();
        void Add(string? value, string replacement)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value) || value.Length < 4 || IsFiller(value)) return;
            found.Add((value, replacement));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "%USERPROFILE%");
        Add(Environment.MachineName, HostPlaceholder);
        Add(Environment.UserName, "user");
        if (rawScanJson is not null)
        {
            try
            {
                Walk(JsonNode.Parse(rawScanJson), null, (parent, key, value) =>
                {
                    if (key.Equals("hostname", StringComparison.OrdinalIgnoreCase)) Add(value, HostPlaceholder);
                    else if (IsOwnerKey(parent, key) || MachineKey.IsMatch(key)) Add(value, "[redacted]");
                });
            }
            catch (JsonException) { }
        }

        lock (Gate)
        {
            _secrets = found
                .DistinctBy(s => s.Item1, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(s => s.Item1.Length)
                .ToList();
        }
    }

    /// <summary>Replaces known identifying values, email addresses, and MAC addresses in text.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        List<(string Secret, string Replacement)> secrets;
        lock (Gate) secrets = _secrets;
        foreach (var (secret, replacement) in secrets)
            text = Regex.Replace(text, $"(?<![A-Za-z0-9]){Regex.Escape(secret)}(?![A-Za-z0-9])", replacement, RegexOptions.IgnoreCase);
        text = Email.Replace(text, "[email]");
        return MacAddress.Replace(text, "[mac]");
    }

    /// <summary>
    /// Prepares scan JSON for leaving the PC. Always drops the Windows registered-owner email and product ID,
    /// which no hardware report needs. With <paramref name="full"/> it also masks the PC name and removes
    /// serial numbers, UUIDs, and the machine ID, and blanks volume labels.
    /// </summary>
    public static string SanitizeScanJson(string json, bool full)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        Clean(root, null, full);
        var text = root?.ToJsonString(Indented) ?? json;
        return full ? Scrub(text) : text;
    }

    private static void Clean(JsonNode? node, string? parentKey, bool full)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToList())
                {
                    if (IsOwnerKey(parentKey, key) || (full && MachineKey.IsMatch(key)))
                        obj.Remove(key);
                    else if (full && key.Equals("hostname", StringComparison.OrdinalIgnoreCase))
                        obj[key] = HostPlaceholder;
                    else if (full && key.Equals("label", StringComparison.OrdinalIgnoreCase) && obj[key] is JsonValue)
                        obj[key] = "";
                    else
                        Clean(obj[key], key, full);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Clean(item, parentKey, full);
                break;
        }
    }

    // The Windows owner fields sit under scan.os; a "product_id" elsewhere (a USB PID) is not personal.
    private static bool IsOwnerKey(string? parentKey, string key) =>
        string.Equals(parentKey, "os", StringComparison.OrdinalIgnoreCase) &&
        (key.Equals("registered_to", StringComparison.OrdinalIgnoreCase) ||
         key.Equals("product_id", StringComparison.OrdinalIgnoreCase));

    private static void Walk(JsonNode? node, string? parentKey, Action<string?, string, string> visit)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (value is JsonValue leaf && leaf.TryGetValue<string>(out var text)) visit(parentKey, key, text);
                    else Walk(value, key, visit);
                }
                break;
            case JsonArray array:
                foreach (var item in array) Walk(item, parentKey, visit);
                break;
        }
    }

    // Placeholder values firmware fills in when a serial is not set.
    private static bool IsFiller(string value) =>
        value.Trim('0', 'F', 'f', ' ', '-').Length == 0 ||
        value.Contains("O.E.M", StringComparison.OrdinalIgnoreCase) ||
        value is "Default string" or "System Serial Number" or "Not Specified" or "None" or "Unknown";
}
