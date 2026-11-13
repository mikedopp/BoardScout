using System.Diagnostics;
using System.Text.Json;
using BoardScout.Views;

namespace BoardScout.Services;

/// <summary>
/// The Storage tab's "what fills a drive" scan: reads one drive's folder tree for names and sizes with the storage scanner
/// (it never opens, moves or deletes a file), sorts the bytes into buckets, and streams the totals to the page, which
/// draws them as a bar of cells. The native window and the browser dashboard both use it, each handing in how a message
/// reaches its page. One scan at a time; starting another stops the first.
/// </summary>
internal sealed class StorageFill : IDisposable
{
    private readonly Action<string, bool> _send;   // json, essential (false = a progress tick a hidden page can skip)
    private readonly Action<string> _log;
    private readonly Func<string, string> _privacy;
    private readonly object _gate = new();
    private CancellationTokenSource? _cts;

    public StorageFill(Action<string, bool> send, Action<string> log, Func<string, string>? privacy = null)
    {
        _send = send;
        _log = log;
        _privacy = privacy ?? (text => text);
    }

    /// <summary>
    /// The drive roots a scan may start on: ready fixed and removable volumes (on Linux, the mount points of real
    /// filesystems: never /proc, tmpfs, snap images or container layers). Nothing else is ever walked.
    /// </summary>
    public static IReadOnlyList<string> ScannableDrives() =>
        DriveInfo.GetDrives().Where(IsScannable).Select(d => d.Name).ToList();

    private static bool IsScannable(DriveInfo d)
    {
        try
        {
            if (!d.IsReady) return false;
            return OperatingSystem.IsWindows() ? d.DriveType is DriveType.Fixed or DriveType.Removable
                : OperatingSystem.IsMacOS() ? IsMacScannable(d.Name, d.DriveFormat, d.TotalSize)
                : IsLinuxScannable(d.Name, d.DriveFormat, d.TotalSize);
        }
        catch (Exception) { return false; } // a mount that vanished, or one this account may not look at
    }

    /// <summary>A Linux mount worth offering: a real filesystem with room on it, not a pseudo one or a package/container image.</summary>
    internal static bool IsLinuxScannable(string mount, string format, long total) =>
        total > 0
        && !BoardScout.StorageScan.Scanner.PseudoFilesystems.Contains(format)
        && !(mount.StartsWith("/proc", StringComparison.Ordinal) || mount.StartsWith("/sys", StringComparison.Ordinal)
             || mount.StartsWith("/dev", StringComparison.Ordinal) || mount.StartsWith("/run", StringComparison.Ordinal)
             || mount.StartsWith("/snap/", StringComparison.Ordinal) || mount.StartsWith("/var/lib/docker/", StringComparison.Ordinal));

    /// <summary>
    /// A Mac volume worth offering: the startup disk (/) and drives under /Volumes. macOS's own volumes
    /// (/System/Volumes/VM, Preboot, Update…) share the startup disk's space and hold nothing of the user's.
    /// </summary>
    internal static bool IsMacScannable(string mount, string format, long total) =>
        total > 0
        && !BoardScout.StorageScan.Scanner.PseudoFilesystems.Contains(format)
        && format is not ("autofs" or "devfs" or "nullfs")
        && (mount == "/" || (mount.StartsWith("/Volumes/", StringComparison.Ordinal) && mount.Length > 9));

    /// <summary>The page names a drive the way a person would ("C:", "/home"); this returns the real drive root ("C:\"), or null.</summary>
    public static string? MatchDrive(string? requested)
    {
        if (requested is null) return null;
        var wanted = Plain(requested);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return ScannableDrives().FirstOrDefault(d => string.Equals(Plain(d), wanted, comparison));
    }

    /// <summary>"C:\" and "C:" are the same drive, as are "/home/" and "/home"; "/" stays "/".</summary>
    private static string Plain(string drive) => drive.Length > 1 ? drive.TrimEnd('\\', '/') : drive;

    public void SendDrives()
    {
        var drives = ScannableDrives().Select(name =>
        {
            var info = new DriveInfo(name);
            // Linux gives no volume label here: the filesystem type says more ("ext4", "vfat")
            var label = OperatingSystem.IsWindows() ? info.VolumeLabel : info.DriveFormat;
            return new { name = Plain(name), label = _privacy(label), total = info.TotalSize, free = info.TotalFreeSpace };
        }).ToList();
        _send(JsonSerializer.Serialize(new { type = "drives", drives }, Json.Options), true);
    }

    public void Start(string? requested)
    {
        var drive = MatchDrive(requested);
        if (drive is null)
        {
            _log("STORAGE: that is not a drive BoardScout can read.");
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource();
        }

        var info = new DriveInfo(drive);
        // TotalFreeSpace, not Available: Linux keeps a reserve for root that holds no files and isn't "used"
        long total = info.TotalSize, free = info.TotalFreeSpace;
        var scanner = new BoardScout.StorageScan.Scanner();
        var clock = Stopwatch.StartNew();
        _log($"STORAGE: reading {_privacy(drive)} for what fills it (names and sizes only), at your request.");
        _send(Message("start", drive, total, free, scanner, clock), true);

        _ = Task.Run(async () =>
        {
            var run = scanner.RunAsync(drive, cts.Token);
            while (!run.IsCompleted)
            {
                await Task.WhenAny(run, Task.Delay(250));
                if (!run.IsCompleted) _send(Message("progress", drive, total, free, scanner, clock), false);
            }
            clock.Stop();
            try
            {
                await run;
                _send(Message("done", drive, total, free, scanner, clock), true);
                _log($"STORAGE: {scanner.Files:N0} files in {scanner.Directories:N0} folders read in {clock.Elapsed.TotalSeconds:0} s.");
            }
            catch (OperationCanceledException)
            {
                // a newer scan replaced this one: it reports for itself
                bool replaced;
                lock (_gate) replaced = !ReferenceEquals(_cts, cts);
                if (!replaced) _send(Message("cancelled", drive, total, free, scanner, clock), true);
            }
            catch (Exception ex)
            {
                _log("STORAGE: reading failed: " + ex.Message);
                _send(Message("error", drive, total, free, scanner, clock), true);
            }
        });
    }

    public void Cancel()
    {
        lock (_gate) _cts?.Cancel();
    }

    public void Dispose() => Cancel();

    /// <summary>One message to the page. Paths go through privacy mode like everything else on the page.</summary>
    private string Message(string phase, string drive, long total, long free, BoardScout.StorageScan.Scanner scanner, Stopwatch clock)
    {
        var caches = phase == "done"
            ? scanner.CacheRoots().Select(c => new { path = _privacy(c.Path), bytes = c.Bytes }).ToList()
            : null;
        return JsonSerializer.Serialize(new
        {
            type = "storage",
            phase,
            os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux",
            drive = _privacy(drive),
            total,
            free,
            bytes = scanner.Bytes,
            files = scanner.Files,
            skippedCloud = scanner.SkippedCloud,
            current = _privacy(scanner.Current),
            secs = clock.Elapsed.TotalSeconds,
            caches,
        }, Json.Options);
    }
}
