using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Models;

namespace BoardScout.Services;

/// <summary>
/// Coolers the sensor library and the kernel don't know, read through liquidctl when it is installed (optional).
/// Only ever runs "liquidctl status --json": no initialize and no set commands, so pump and fan settings are never
/// changed. A read takes a second or more, so it runs in the background and callers get the last result.
/// </summary>
internal static partial class Liquidctl
{
    private static readonly TimeSpan Refresh = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly object Gate = new();
    private static IReadOnlyList<CoolerDevice> _last = [];
    private static Task? _read;
    private static DateTime _readUtc = DateTime.MinValue;

    /// <summary>What the last background read found, for the coolers not already in <paramref name="known"/>.</summary>
    /// <param name="waitForFirst">Wait for the first read to finish (a one-off page, not a live sample).</param>
    public static IReadOnlyList<CoolerDevice> Merge(IReadOnlyList<CoolerDevice> known, bool waitForFirst = false)
    {
        Task? pending;
        lock (Gate)
        {
            if (_read is null or { IsCompleted: true } && DateTime.UtcNow - _readUtc > Refresh)
                _read = Task.Run(ReadAsync);
            pending = _readUtc == DateTime.MinValue ? _read : null;
        }
        if (waitForFirst) pending?.Wait(TimeSpan.FromSeconds(5));
        var extra = _last.Where(c => !known.Any(k => SameDevice(k.Name, c.Name))).ToList();
        return extra.Count == 0 ? known : [.. known, .. extra];
    }

    private static async Task ReadAsync()
    {
        IReadOnlyList<CoolerDevice> found = [];
        try
        {
            if (Locate() is { } exe)
            {
                var start = new ProcessStartInfo(exe)
                {
                    RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                };
                start.ArgumentList.Add("status");
                start.ArgumentList.Add("--json");
                // Python writes "°C" in the console's code page unless told otherwise.
                start.Environment["PYTHONIOENCODING"] = "utf-8";
                using var process = Process.Start(start)!;
                var output = process.StandardOutput.ReadToEndAsync();
                var errors = process.StandardError.ReadToEndAsync();
                using var limit = new CancellationTokenSource(Timeout);
                try
                {
                    await process.WaitForExitAsync(limit.Token);
                    var json = await output;
                    await errors;
                    if (process.ExitCode == 0) found = Parse(json);
                }
                catch (OperationCanceledException)
                {
                    process.Kill(true);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or JsonException)
        {
        }
        lock (Gate)
        {
            _last = found;
            _readUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// liquidctl on PATH. With administrator or root rights, only a copy in a folder ordinary programs can't write to
    /// is run, so that a planted file can't borrow those rights.
    /// </summary>
    public static string? Locate()
    {
        var exe = OperatingSystem.IsWindows() ? "liquidctl.exe" : "liquidctl";
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var path = Path.Combine(folder.Trim('"'), exe);
                if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) continue;
                if (!Environment.IsPrivilegedProcess || Protected(path)) return path;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static bool Protected(string path)
    {
        var full = Path.GetFullPath(path);
        string[] roots = OperatingSystem.IsWindows()
            ? [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)]
            : ["/usr/bin", "/usr/sbin", "/usr/local/bin", "/bin"];
        return roots.Any(r => r.Length > 0 && full.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// liquidctl's JSON: [{ bus, address, description, status: [{ key, value, unit }] }]. Numbers are kept; text values
    /// (pump modes, firmware versions) are left out. Power supplies are skipped: BoardScout reads those itself.
    /// </summary>
    public static List<CoolerDevice> Parse(string json)
    {
        var coolers = new List<CoolerDevice>();
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return coolers;
        foreach (var device in document.RootElement.EnumerateArray())
        {
            if (Text(device, "description") is not { Length: > 0 } name || PowerSupply().IsMatch(name)) continue;
            if (!device.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.Array) continue;
            var sensors = new List<CoolerSensor>();
            foreach (var entry in status.EnumerateArray())
            {
                if (Text(entry, "key") is not { } key || !entry.TryGetProperty("value", out var value) ||
                    value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number)) continue;
                var unit = Text(entry, "unit") ?? "";
                if (unit == "rpm") unit = "RPM";
                var kind = unit switch
                {
                    "°C" => "temperature",
                    "RPM" => "speed",
                    "%" => key.Contains("duty", StringComparison.OrdinalIgnoreCase) ? "duty" : "level",
                    "L/h" => "flow",
                    "V" => "voltage",
                    "A" => "current",
                    "W" => "power",
                    _ => "other"
                };
                sensors.Add(new CoolerSensor(key, kind, Math.Round(number, 2), unit));
            }
            if (sensors.Count > 0) coolers.Add(new CoolerDevice(name, sensors));
        }
        return coolers;

        static string? Text(JsonElement element, string property) =>
            element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    /// <summary>
    /// Whether two names are the same family of device, read two ways: the library's "NZXT Kraken X63" or the kernel's
    /// "x53", and liquidctl's "NZXT Kraken X (X53, X63 or X73)".
    /// </summary>
    public static bool SameDevice(string known, string other)
    {
        static string? Family(string name)
        {
            var text = name.Replace(" ", "").ToLower(CultureInfo.InvariantCulture);
            if (text is "x53" or "z53" || text.Contains("kraken")) return "kraken";
            foreach (var family in new[] { "d5next", "octo", "quadro", "coreliquid", "aquaero", "farbwerk", "highflow", "waterforce", "ryujin", "commander", "smartdevice", "arctic" })
                if (text.Contains(family)) return family;
            return text.StartsWith("corsaircpro", StringComparison.Ordinal) ? "commander" : null;
        }
        return Family(known) is { } a && a == Family(other);
    }

    [GeneratedRegex(@"\bPSU\b|\b(HX|RM|AX)\d{3,4}i\b|\bNZXT E\d{3}\b", RegexOptions.IgnoreCase)]
    private static partial Regex PowerSupply();
}
