using System.Globalization;
using BoardScout.DriveHealth;
using BoardScout.Models;

namespace BoardScout.Services;

/// <summary>
/// Drive health over time. Once a day (or when asked) every drive's own counters are read (smartctl from smartmontools
/// when it is installed, Windows' storage counters otherwise) and appended to Data\DriveHealth, one line per reading.
/// Each drive is then judged from its whole history, so a counter that grows shows as a trend rather than one number.
/// Read-only: nothing is written to a drive, no self-test is started, and a sleeping disk is left asleep.
/// </summary>
internal static class DriveHealthService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<DriveReport> _reports = [];

    /// <summary>A new reading is taken when the last one is older than this.</summary>
    public static TimeSpan ReadEvery { get; } = TimeSpan.FromHours(20);

    public static IReadOnlyList<DriveReport> Reports => _reports;
    public static DateTimeOffset? LastPass { get; private set; }
    public static RecordResult? LastResult { get; private set; }
    public static bool Reading { get; private set; }
    public static string? Smartctl => DriveHealth.Smartctl.Locate(null);

    /// <summary>Raised on the thread pool after a reading or a reload.</summary>
    public static event EventHandler? Changed;

    static DriveHealthService()
    {
        Analysis.NeedsAdminAdvice =
            "Start BoardScout as administrator once (right-click it, Run as administrator) and open the Storage tab; " +
            "that reading is kept, and later readings without admin rights add to it.";
    }

    public static string Folder(string dataRoot) => Path.Combine(dataRoot, "DriveHealth");

    /// <summary>Loads the kept history, then reads every drive when the last reading is older than a day (or when forced).</summary>
    public static async Task RefreshAsync(string dataRoot, bool force, CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows()) return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var store = new Store(Folder(dataRoot));
            if (_reports.Count == 0) Load(store);
            LastPass = LastPassAt(store);
            if (!force && LastPass is { } last && DateTimeOffset.Now - last < ReadEvery) return;
            Reading = true;
            Changed?.Invoke(null, EventArgs.Empty);
            LastResult = await Task.Run(() => Recorder.RecordAsync(store,
                new RecordOptions { App = "BoardScout", Version = AppInfo.Version }, null, cancellationToken), cancellationToken);
            Load(store);
            LastPass = LastPassAt(store);
        }
        finally
        {
            Reading = false;
            Gate.Release();
            Changed?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void Load(Store store)
    {
        try { _reports = Directory.Exists(store.DrivesFolder) ? Recorder.Reports(store, DateTimeOffset.Now) : []; }
        catch { _reports = []; }
    }

    // Every pass appends free space per volume, so that file's time is the last pass, whichever drives answered.
    private static DateTimeOffset? LastPassAt(Store store) =>
        File.Exists(store.VolumesPath) ? new DateTimeOffset(File.GetLastWriteTime(store.VolumesPath)) : null;

    /// <summary>The drive that holds a volume letter ("C:"), from its newest reading.</summary>
    public static DriveReport? ForLetter(string letter) =>
        _reports.FirstOrDefault(r => r.Latest.Letters?.Any(l => string.Equals(l, letter, StringComparison.OrdinalIgnoreCase)) == true);

    public static string VerdictLabel(string verdict) => verdict switch
    {
        "healthy" => "Healthy",
        "watch" => "Watch",
        "attention" => "Needs a look",
        "act" => "Act now",
        _ => "Not read"
    };

    /// <summary>One line on a drive's life: hours, wear, data written, temperature (whatever its counters give).</summary>
    public static string LifeLine(DriveReport report)
    {
        var r = report.LatestFull ?? report.Latest;
        var parts = new List<string>();
        if (r.PowerOnHours is long hours) parts.Add($"{hours.ToString("N0", CultureInfo.InvariantCulture)} h on");
        if (r.PercentUsed is int used) parts.Add($"{used}% worn");
        if (r.BytesWritten is long written) parts.Add($"{Analysis.Bytes(written)} written");
        if (report.Rates.WrittenPerDay is double perDay && report.Readings > 1) parts.Add($"{Analysis.Bytes((long)perDay)}/day");
        if (r.Temp is int temp) parts.Add($"{temp} °C");
        return parts.Count > 0 ? string.Join(" · ", parts) : r.Status == "needs-admin" ? "Needs admin rights to read" : "";
    }

    /// <summary>The drive's findings worth showing, worst first, as tooltip text.</summary>
    public static string Tooltip(DriveReport report)
    {
        var lines = new List<string> { $"{report.Latest.Model}: {VerdictLabel(report.Verdict)}" };
        foreach (var f in report.Findings.OrderByDescending(f => Finding.Rank(f.Severity)).Take(6))
            lines.Add($"• {f.Title}" + (f.Basis == "trend" ? " (trend)" : ""));
        lines.Add(report.Readings == 1
            ? "One reading so far; trends start with the next day's reading."
            : $"{report.Readings} readings since {report.First.LocalDateTime:d MMM yyyy}.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Drive findings that need doing something about, as suggestions. Volume fill is already the storage check's.</summary>
    public static IEnumerable<EfficiencySuggestion> Suggestions()
    {
        foreach (var report in _reports)
        {
            var name = report.Latest.Model ?? "A drive";
            var where = report.Latest.Letters is { Length: > 0 } letters ? $" ({string.Join(" ", letters)})" : "";
            foreach (var f in report.Findings.Where(f => f.Severity is "critical" or "warn" && f.Code != "vol-full"))
                yield return new EfficiencySuggestion(
                    f.Severity == "critical" ? SuggestionSeverity.Critical : SuggestionSeverity.Warning,
                    $"{name}{where}: {f.Title}",
                    f.Detail + (f.Basis == "trend" ? " (from comparing its daily readings)" : ""),
                    f.Advice ?? "Keep an eye on it; the Storage tab shows its history.",
                    "Drive health");
        }
    }
}
