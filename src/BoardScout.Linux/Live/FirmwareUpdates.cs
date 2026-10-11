using System.Globalization;
using System.Text.Json;
using BoardScout.Platform;

namespace BoardScout.Live;

internal sealed record FirmwareDevice(string Name, string? Vendor, string? Version, bool Updatable, string? Plugin);

internal sealed record FirmwareRelease(string Device, string? Current, string Latest, string? Summary, string? Urgency, string? Url);

internal sealed record FirmwareReport(IReadOnlyList<FirmwareDevice> Devices, IReadOnlyList<FirmwareRelease> Updates, string? Note, string CheckedAt);

/// <summary>
/// Firmware versions and available updates from fwupd (LVFS), the way GNOME Software and KDE Discover see them.
/// Only asks fwupd what it already knows; downloading new metadata and installing are left to fwupd itself.
/// </summary>
internal static class FirmwareUpdates
{
    public static FirmwareReport Check()
    {
        var checkedAt = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        if (!Tools.Has("fwupdmgr"))
            return new FirmwareReport([], [], "fwupd isn't installed, so firmware can't be checked. Your distribution's fwupd package adds it.", checkedAt);

        var devices = new List<FirmwareDevice>();
        var (code, output, error) = Tools.RunFull("fwupdmgr", ["get-devices", "--json"], 20000);
        if (code == 0) devices = ParseDevices(output);
        var updates = new List<FirmwareRelease>();
        var (updateCode, updateOutput, updateError) = Tools.RunFull("fwupdmgr", ["get-updates", "--json"], 30000);
        if (updateCode == 0 || updateOutput.TrimStart().StartsWith('{')) updates = ParseUpdates(updateOutput);
        string? note = null;
        if (code != 0) note = $"fwupd didn't list devices: {First(error)}";
        else if (updateCode != 0 && updates.Count == 0)
            note = updateError.Contains("No updatable devices", StringComparison.OrdinalIgnoreCase) || updateOutput.Contains("No updatable", StringComparison.OrdinalIgnoreCase)
                ? "None of this computer's devices can be updated through fwupd."
                : updateError.Contains("metadata", StringComparison.OrdinalIgnoreCase)
                    ? "fwupd has no current update list. Its fwupd-refresh timer downloads one, or run fwupdmgr refresh."
                    : "No firmware updates are waiting.";
        else if (updates.Count == 0) note = "No firmware updates are waiting.";
        return new FirmwareReport(devices, updates, note, checkedAt);
    }

    public static List<FirmwareDevice> ParseDevices(string json)
    {
        var list = new List<FirmwareDevice>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Devices", out var devices)) return list;
            foreach (var device in devices.EnumerateArray())
            {
                var flags = device.TryGetProperty("Flags", out var f) ? f.EnumerateArray().Select(x => x.GetString()).ToList() : [];
                list.Add(new FirmwareDevice(
                    Text(device, "Name") ?? "Device",
                    Text(device, "Vendor"),
                    Text(device, "Version"),
                    flags.Contains("updatable"),
                    Text(device, "Plugin")));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    public static List<FirmwareRelease> ParseUpdates(string json)
    {
        var list = new List<FirmwareRelease>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Devices", out var devices)) return list;
            foreach (var device in devices.EnumerateArray())
            {
                if (!device.TryGetProperty("Releases", out var releases)) continue;
                var newest = releases.EnumerateArray().FirstOrDefault();
                if (newest.ValueKind != JsonValueKind.Object) continue;
                list.Add(new FirmwareRelease(
                    Text(device, "Name") ?? "Device",
                    Text(device, "Version"),
                    Text(newest, "Version") ?? "?",
                    Text(newest, "Summary"),
                    Text(newest, "Urgency"),
                    Text(newest, "DetailsUrl") ?? Text(newest, "HomepageUrl")));
            }
        }
        catch (JsonException)
        {
        }
        return list;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string First(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "no reason given";
}
