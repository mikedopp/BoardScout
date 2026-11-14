using System.Diagnostics;
using System.Globalization;
using BoardScout.DriveHealth;

namespace BoardScout.Services;

/// <summary>
/// boardscout --drive-health: one reading of every drive now, added to the kept history, then each drive's verdict.
/// SMART needs root, so this is the one command meant to run with sudo. Under sudo the reading goes into the user's
/// own data folder (not root's) and the files are handed back to them, so the dashboard they run shows it.
/// </summary>
internal static class DriveHealthCli
{
    public static async Task<int> RunAsync(string dataRoot, bool privacy)
    {
        await DriveHealthService.RefreshAsync(dataRoot, force: true);
        HandBack(dataRoot);
        var result = DriveHealthService.LastResult;
        if (result is null) { Console.Error.WriteLine("No reading was taken."); return 1; }
        if (result.Busy) { Console.Error.WriteLine("Another reading is running; try again in a minute."); return 1; }

        Console.WriteLine($"Drive health, {result.At.LocalDateTime:d MMM yyyy HH:mm} ({result.Took.TotalSeconds:0.0} s)" +
                          (result.SmartctlVersion is { } v ? $", smartctl {v}" : ", smartctl not found"));
        Console.WriteLine();
        var rows = DriveHealthService.Reports
            .Where(r => r.Latest.Source is not "adb" and not "app")
            .OrderBy(r => r.Latest.Device ?? r.LatestFull?.Device ?? "~")
            .Select(r =>
            {
                var model = r.Latest.Model ?? "Drive";
                return (Device: (r.Latest.Device ?? r.LatestFull?.Device ?? "").Replace("/dev/", ""),
                        Model: privacy ? Privacy.Scrub(model) : model,
                        Verdict: DriveHealthService.VerdictLabel(r.Verdict),
                        Life: DriveHealthService.LifeLine(r), Report: r);
            }).ToList();
        if (rows.Count == 0) Console.WriteLine("No drives were found.");
        int wDev = Math.Max(6, rows.Select(r => r.Device.Length).DefaultIfEmpty().Max());
        int wModel = Math.Min(34, Math.Max(5, rows.Select(r => r.Model.Length).DefaultIfEmpty().Max()));
        int wVerdict = Math.Max(7, rows.Select(r => r.Verdict.Length).DefaultIfEmpty().Max());
        if (rows.Count > 0)
            Console.WriteLine($"{"Device".PadRight(wDev)}  {"Model".PadRight(wModel)}  {"Health".PadRight(wVerdict)}  Life");
        foreach (var r in rows)
        {
            var model = r.Model.Length > wModel ? r.Model[..(wModel - 1)] + "…" : r.Model;
            Console.WriteLine($"{r.Device.PadRight(wDev)}  {model.PadRight(wModel)}  {r.Verdict.PadRight(wVerdict)}  {r.Life}");
        }

        var findings = rows.SelectMany(r => r.Report.Findings
                .Where(f => f.Severity is "critical" or "warn" && f.Code != "vol-full")
                .Select(f => (r.Device, f))).ToList();
        if (findings.Count > 0)
        {
            Console.WriteLine();
            foreach (var (device, f) in findings)
            {
                Console.WriteLine($"{(f.Severity == "critical" ? "!!" : "! ")} {device}: {f.Title}");
                Console.WriteLine($"   {f.Detail}");
                if (f.Advice is { } advice) Console.WriteLine($"   {advice}");
            }
        }
        if (!Environment.IsPrivilegedProcess)
        {
            Console.WriteLine();
            Console.WriteLine("Not root: only temperatures and free space were read. sudo boardscout --drive-health reads SMART too.");
        }
        foreach (var note in result.Notes.Distinct()) Console.WriteLine("Note: " + note);
        Console.WriteLine();
        Console.WriteLine($"Kept in {DriveHealthService.Folder(dataRoot)}.");
        return rows.Any(r => r.Report.Verdict == "act") ? 3 : 0;
    }

    /// <summary>Under sudo, the invoking user's own data folder, so a root reading lands where their dashboard looks.</summary>
    public static string? SudoUserData()
    {
        if (!Environment.IsPrivilegedProcess || Environment.GetEnvironmentVariable("SUDO_USER") is not { Length: > 0 } user || user == "root") return null;
        var home = HomeOf(user);
        if (home is null || !Directory.Exists(home)) return null;
        return OperatingSystem.IsMacOS()
            ? Path.Combine(home, "Library", "Application Support", "BoardScout")
            : Path.Combine(home, ".local", "share", "boardscout");
    }

    private static string? HomeOf(string user)
    {
        if (OperatingSystem.IsMacOS()) return Path.Combine("/Users", user);
        try
        {
            foreach (var line in File.ReadLines("/etc/passwd"))
            {
                var parts = line.Split(':');
                if (parts.Length >= 6 && parts[0] == user) return parts[5];
            }
        }
        catch { }
        return null;
    }

    private static string? _createdFrom;

    /// <summary>
    /// Under sudo, before the data folder is first touched: notes the topmost folder between the user's home and it that
    /// doesn't exist yet (~/.local/share on a machine that never had one), so everything root creates goes back to the user.
    /// </summary>
    public static void NoteMissing(string dataRoot)
    {
        if (UserHome() is not { } home) return;
        var dir = Path.GetFullPath(dataRoot).TrimEnd('/');
        if (!Inside(dir, home)) return;
        string? top = null;
        while (dir.Length > home.Length && Inside(dir, home))
        {
            if (!Directory.Exists(dir)) top = dir;
            dir = Path.GetDirectoryName(dir) ?? "";
        }
        _createdFrom = top;
    }

    /// <summary>What goes back to the user after a root reading: the topmost folder root created, else the data folder.</summary>
    internal static string? HandBackTarget(string dataRoot) =>
        UserHome() is { } home && Inside(Path.GetFullPath(dataRoot), home) ? _createdFrom ?? Path.GetFullPath(dataRoot) : null;

    private static string? UserHome() =>
        Environment.IsPrivilegedProcess && Environment.GetEnvironmentVariable("SUDO_USER") is { Length: > 0 } user && user != "root" &&
        HomeOf(user) is { Length: > 1 } home ? Path.GetFullPath(home).TrimEnd('/') : null;

    internal static bool Inside(string path, string home) => path.StartsWith(home.TrimEnd('/') + "/", StringComparison.Ordinal);

    // Files and folders root wrote into the user's home go back to the user (the data folder stays 0700, theirs).
    private static void HandBack(string dataRoot)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SUDO_UID"), NumberStyles.None, CultureInfo.InvariantCulture, out var uid) || uid == 0) return;
        if (!int.TryParse(Environment.GetEnvironmentVariable("SUDO_GID"), NumberStyles.None, CultureInfo.InvariantCulture, out var gid)) return;
        // only folders inside the user's own home (the default one, or a --data folder there) go back to them
        if (HandBackTarget(dataRoot) is not { } target) return;
        try
        {
            var psi = new ProcessStartInfo("chown") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-R", $"{uid}:{gid}", target }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            p?.WaitForExit(15_000);
        }
        catch (Exception ex) { Console.Error.WriteLine("Couldn't hand the files back to you: " + ex.Message); }
    }
}
