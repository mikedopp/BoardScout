using System.Globalization;
using BoardScout.DriveHealth;

namespace BoardScout.Services;

/// <summary>
/// Drive health over time. Once a day (or when asked) every drive's own counters are read (smartctl from smartmontools
/// when it is installed; otherwise what the OS knows: Windows' storage counters, lsblk and the kernel's drive
/// temperatures on Linux, diskutil on macOS) and appended to the data folder's DriveHealth, one line per reading.
/// Each drive is then judged from its whole history, so a counter that grows shows as a trend rather than one number.
/// Read-only: nothing is written to a drive, no self-test is started, and a sleeping disk is left asleep.
/// </summary>
internal static class DriveHealthService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static IReadOnlyList<DriveReport> _reports = [];
    private static string? _loadedFrom;

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
        Analysis.NeedsAdminAdvice = OperatingSystem.IsWindows()
            ? "Start BoardScout as administrator once (right-click it, Run as administrator) and open the Storage tab; " +
              "that reading is kept, and later readings without admin rights add to it."
            : "Reading a drive's health needs root: run sudo boardscout --drive-health (once, or daily from a timer). " +
              "Its readings land in your history, and the dashboard shows them.";
    }

    public static string Folder(string dataRoot) => Path.Combine(dataRoot, "DriveHealth");

    /// <summary>Loads the kept history, then reads every drive when the last reading is older than a day (or when forced).</summary>
    public static async Task RefreshAsync(string dataRoot, bool force, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var store = new Store(Folder(dataRoot));
            if (_reports.Count == 0 || _loadedFrom != store.Folder) Load(store);
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
        _loadedFrom = store.Folder;
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
        return parts.Count > 0 ? string.Join(" · ", parts) : r.Status == "needs-admin" ? (OperatingSystem.IsWindows() ? "Needs admin rights to read" : "Needs root to read") : "";
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

    /// <summary>Drive findings that need doing something about (critical, warn), with where the drive is. Volume fill is the storage check's.</summary>
    public static IEnumerable<(DriveReport Report, Finding Finding, string Title, string Detail, string Advice)> Problems()
    {
        foreach (var report in _reports)
        {
            var name = report.Latest.Model ?? "A drive";
            var where = report.Latest.Letters is { Length: > 0 } letters ? $" ({string.Join(" ", letters)})" : "";
            foreach (var f in report.Findings.Where(f => f.Severity is "critical" or "warn" && f.Code != "vol-full"))
                yield return (report, f, $"{name}{where}: {f.Title}",
                    f.Detail + (f.Basis == "trend" ? " (from comparing its daily readings)" : ""),
                    f.Advice ?? "Keep an eye on it; the Storage tab shows its history.");
        }
    }

    /// <summary>Windows: the drive behind "Disk N".</summary>
    public static DriveReport? ForDiskNumber(int number) =>
        _reports.FirstOrDefault(r => (r.LatestFull ?? r.Latest).DiskNumber == number && r.Latest.Source is not "adb" and not "app");

    /// <summary>Linux and macOS: the drive behind a block device name (sda, nvme0n1, disk0). smartctl reads NVMe through its controller (nvme0).</summary>
    public static DriveReport? ForDevice(string name)
    {
        name = name.Replace("/dev/", "");
        foreach (var r in _reports)
        {
            var device = (r.Latest.Device ?? r.LatestFull?.Device)?.Replace("/dev/", "");
            if (device is null) continue;
            if (device == name) return r;
            if (System.Text.RegularExpressions.Regex.IsMatch(device, @"^nvme\d+$") &&
                System.Text.RegularExpressions.Regex.IsMatch(name, "^" + device + @"n\d+$")) return r;
        }
        return null;
    }

    /// <summary>True when every drive read so far was locked out (no admin rights or root), so the history holds no health yet.</summary>
    public static bool AllLocked => _reports.Count > 0 && _reports.All(r => r.LatestFull is null && r.Latest.Status == "needs-admin");
}
