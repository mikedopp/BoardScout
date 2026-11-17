using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Text.Json;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

public sealed class RecordOptions
{
    /// <summary>Leave a sleeping spinning disk asleep, unless it has gone <see cref="WakeAfterDays"/> without a reading.</summary>
    public bool LeaveAsleep { get; set; } = true;
    public int WakeAfterDays { get; set; } = 7;
    public string? SmartctlPath { get; set; }
    /// <summary>The app that wrote latest.json, for whoever reads it.</summary>
    public string App { get; set; } = "";
    public string Version { get; set; } = "";
    /// <summary>Also read phones, tablets and TVs over adb: the ones plugged in, and these addresses (host:port) once each.</summary>
    public bool ReadAndroid { get; set; }
    public string? AdbPath { get; set; }
    public List<string> AndroidAddresses { get; set; } = new();
}

public sealed record PassLine(string Key, string Name, string Status, string? Note, bool Saved);

public sealed class RecordResult
{
    public DateTimeOffset At { get; set; }
    public bool Elevated { get; set; }
    public bool Busy { get; set; }
    public string? Smartctl { get; set; }
    public string? SmartctlVersion { get; set; }
    public List<PassLine> Drives { get; set; } = new();
    public int Volumes { get; set; }
    public List<string> Notes { get; set; } = new();
    public TimeSpan Took { get; set; }
}

/// <summary>One pass: read every drive once, append what it said to the log, and refresh latest.json.</summary>
public static class Recorder
{
    public static async Task<RecordResult> RecordAsync(Store store, RecordOptions opt, Action<string>? progress = null, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var result = new RecordResult { At = started };
        using var gate = store.TryLock();
        if (gate == null) { result.Busy = true; result.Notes.Add("Another reading is already running."); return result; }

        if (OperatingSystem.IsWindows())
        {
            try { await SaveWindowsDiskEventsAsync(store, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Notes.Add(ex.Message); }
        }
        string os = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux";
        progress?.Invoke($"Asking {os} about the disks");
        Inventory? inv = await Inventory.ReadAsync(ct);
        if (inv?.Error != null) result.Notes.Add($"{inv.OsName}'s disk list: " + inv.Error);
        string source = inv?.Source ?? "os";
        result.Elevated = inv?.Elevated ?? Environment.IsPrivilegedProcess;
        var notes = store.LoadDevices();
        string machine = Environment.MachineName;
        var found = new List<(DriveReading Reading, string? Raw)>();
        var covered = new HashSet<Inventory.Disk>();

        string? exe = Smartctl.Locate(opt.SmartctlPath);
        result.Smartctl = exe;
        if (exe == null) result.Notes.Add($"smartctl not found: only {os}'s own disk details were recorded.");
        else
        {
            progress?.Invoke("Finding drives");
            var (version, devices) = await Smartctl.ScanAsync(exe, ct);
            result.SmartctlVersion = version;
            foreach (var dev in devices)
            {
                ct.ThrowIfCancellationRequested();
                var win = inv?.Match(dev);
                if (win != null) covered.Add(win);
                var known = KnownFor(notes, win);
                try
                {
                if (dev.OpenError != null)
                {
                    var r0 = FromOs(win, source, started, dev.NeedsAdmin ? "needs-admin" : "error", dev.NeedsAdmin ? NeedsAdminNote : dev.OpenError);
                    r0.Device = dev.Name; r0.DeviceType = dev.Type; r0.Protocol = dev.Protocol;
                    r0.Key = known.Key ?? r0.Key;
                    found.Add((r0, null));
                    continue;
                }

                string type = known.Note?.Type is string kt && dev.Type == "scsi" ? kt : dev.Type;
                bool spinningOrUnknown = win?.Media != "SSD" && !string.Equals(dev.Protocol, "NVMe", StringComparison.OrdinalIgnoreCase);
                bool readRecently = known.Note?.LastFullReading is DateTimeOffset lf && (started - lf).TotalDays < opt.WakeAfterDays;
                bool leaveAsleep = opt.LeaveAsleep && spinningOrUnknown && readRecently;
                progress?.Invoke("Reading " + (win?.Name ?? dev.InfoName ?? dev.Name));
                var run = await Smartctl.ReadAsync(exe, dev.Name, type, leaveAsleep, ct);
                DriveReading r;
                string? raw = run.Output;
                if (run.TimedOut || string.IsNullOrWhiteSpace(run.Output))
                {
                    r = FromOs(win, source, started, "error", run.TimedOut ? "smartctl timed out" : "smartctl returned nothing");
                    raw = null;
                }
                else
                {
                    try { r = Smartctl.Parse(run.Output, started); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        r = FromOs(win, source, started, "error", "smartctl's reply couldn't be read: " + ex.Message);
                        raw = null;
                    }
                }

                // a USB bridge that only answered as plain SCSI: try the pass-through types once a week until one works
                if (r.Status == "no-smart" && dev.Type == "scsi" && type == "scsi" &&
                    !(known.Note is { ProbeFailed: true, ProbedAt: DateTimeOffset pa } && (started - pa).TotalDays < 7))
                {
                    foreach (var t in Smartctl.BridgeTypes)
                    {
                        progress?.Invoke($"Trying {t} on {win?.Name ?? dev.Name}");
                        var probe = await Smartctl.ReadAsync(exe, dev.Name, t, false, ct);
                        if (probe.TimedOut || string.IsNullOrWhiteSpace(probe.Output)) continue;
                        DriveReading pr;
                        try { pr = Smartctl.Parse(probe.Output, started); } catch { continue; }
                        if (pr.Status == "ok" && pr.PowerOnHours != null) { r = pr; raw = probe.Output; type = t; break; }
                    }
                    var note = known.Note ?? new Store.DeviceNote();
                    note.ProbedAt = started;
                    note.ProbeFailed = r.Status == "no-smart";
                    if (known.Key != null) notes[known.Key] = note;
                    else if (UsableWindowsSerial(win) is string ws) notes["win_" + Store.KeyFor(win!.Name, ws, win.Size)] = note;
                }

                r.DeviceType = type;
                r.Device = dev.Name;
                if (r.Status == "asleep" && win != null) { r.Model ??= win.Name; }
                Merge(r, win);
                // the asleep / failed reading keeps the drive's own key so it lands in the same history
                r.Key = r.Serial != null && r.Status is "ok" or "partial"
                    ? Store.KeyFor(r.Model, r.Serial, r.Capacity)
                    : known.Key ?? Store.KeyFor(r.Model, r.Serial ?? win?.Serial, r.Capacity ?? win?.Size);
                found.Add((r, raw));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // one drive that can't be read never costs the others their reading
                    var bad = FromOs(win, source, started, "error", ex.Message);
                    bad.Device = dev.Name;
                    bad.Key = known.Key ?? bad.Key;
                    found.Add((bad, null));
                }
            }
        }

        // disks the OS sees that smartctl didn't list
        if (inv != null)
            foreach (var win in inv.Disks.Where(d => !covered.Contains(d)))
            {
                string status = win.HasCounters ? "partial" : exe == null ? "no-tool" : result.Elevated ? "no-smart" : "needs-admin";
                string counters = source == "windows" ? "hours, temperature, start/stops" : "temperature";
                string? note = exe == null
                    ? "smartctl isn't installed" + (win.HasCounters ? $", so only {inv.OsName}'s own counters ({counters}) are known." : ".")
                    : !win.HasCounters ? "smartctl didn't list this disk."
                    : source == "windows" ? null : $"smartctl didn't list this disk; only the temperature {inv.OsName} shows is known.";
                var r = FromOs(win, source, started, status, note);
                r.Key = KnownFor(notes, win).Key ?? r.Key;
                found.Add((r, null));
            }

        // phones, tablets and TVs over adb
        var volumes = new List<VolumeReading>();
        if (opt.ReadAndroid) await ReadAndroidAsync(opt, started, found, volumes, result, progress, ct);

        // letters (mount points elsewhere) and volumes
        if (inv != null)
        {
            foreach (var (r, _) in found)
                if (r.DiskNumber is int d && r.Source != "adb")
                {
                    var letters = inv.Volumes.Where(v => v.Disk == d && v.Letter.Length > 0).Select(v => v.Letter).OrderBy(x => x).ToArray();
                    if (letters.Length > 0) r.Letters = letters;
                }
            foreach (var v in inv.Volumes.Where(v => v.Letter.Length > 0 && v.Size > 0))
                volumes.Add(new VolumeReading
                {
                    At = started, Letter = v.Letter, Label = v.Label, FileSystem = v.FileSystem, Size = v.Size, Free = v.Free,
                    DiskNumber = v.Disk >= 0 ? v.Disk : null, Health = v.Health, Status = v.Status,
                    DriveKey = found.FirstOrDefault(f => f.Reading.DiskNumber == v.Disk && v.Disk >= 0).Reading?.Key,
                });
        }
        else
        {
            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady && d.DriveType == DriveType.Fixed && d.TotalSize > 1_000_000_000))
                volumes.Add(new VolumeReading { At = started, Letter = d.Name, Label = d.VolumeLabel, FileSystem = d.DriveFormat, Size = d.TotalSize, Free = d.AvailableFreeSpace });
        }

        // write
        foreach (var (r, raw) in found)
        {
            r.Machine = machine;
            r.Elevated = result.Elevated;
            notes.TryGetValue(r.Key, out var note);
            bool full = Analysis.IsFull(r);
            // without admin rights, a USB drive that an admin pass already reads in full adds only noise
            bool skip = r.Status is "needs-admin" && note?.LastFullReading != null;
            note ??= new Store.DeviceNote();
            if (r.Source == "adb") AndroidDevices.Accumulate(r, note);
            if (!skip)
            {
                store.Append(r);
                if (raw != null && r.Status != "asleep") store.AppendRaw(r.Key, started, raw);
            }
            if (full)
            {
                note.LastFullReading = started;
                note.Type = r.DeviceType;
            }
            note.WindowsSerial = r.WindowsSerial ?? note.WindowsSerial;
            note.DiskNumber = r.DiskNumber;
            note.Capacity = r.Capacity ?? note.Capacity;
            notes[r.Key] = note;
            result.Drives.Add(new PassLine(r.Key, Label(r), r.Status, r.Note, !skip));
        }
        store.AppendVolumes(volumes);
        result.Volumes = volumes.Count;
        store.SaveDevices(notes);
        result.Took = DateTimeOffset.Now - started;
        // the readings are saved; another app holding latest.json open only delays its refresh
        try { WriteLatest(store, opt, result); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.Notes.Add("latest.json wasn't replaced: " + ex.Message); }
        return result;
    }

    static string Label(DriveReading r) =>
        (r.Model ?? "Unknown drive") + (r.Letters is { Length: > 0 } l ? " (" + string.Join(" ", l) + ")" : "");

    /// <summary>The key and notes of a drive seen before through this Windows disk (matched by Windows' serial).</summary>
    internal static (string? Key, Store.DeviceNote? Note) KnownFor(Dictionary<string, Store.DeviceNote> notes, Inventory.Disk? win)
    {
        if (win == null) return (null, null);
        // a junk serial ("0123456789ABCDEF", all zeros) shared by two enclosures must never merge their drives; such a
        // drive is known instead by where it is and its size: the same disk number and capacity as a drive read in full
        if (UsableWindowsSerial(win) is not string serial)
        {
            var bySlot = notes.Where(kv => !kv.Key.StartsWith("win_") && kv.Value.LastFullReading != null && !Store.UsableSerial(kv.Value.WindowsSerial) &&
                                           kv.Value.DiskNumber == win.Number && SameSize(kv.Value.Capacity, win.Size))
                              .OrderByDescending(kv => kv.Value.LastFullReading).FirstOrDefault();
            return bySlot.Key != null ? (bySlot.Key, bySlot.Value) : (null, null);
        }
        // prefer the key that has had a full reading
        var match = notes.Where(kv => kv.Value.WindowsSerial == serial && !kv.Key.StartsWith("win_"))
                         .OrderByDescending(kv => kv.Value.LastFullReading ?? DateTimeOffset.MinValue).FirstOrDefault();
        if (match.Key != null) return (match.Key, match.Value);
        notes.TryGetValue("win_" + Store.KeyFor(win!.Name, serial, win.Size), out var probeNote);
        return (null, probeNote);
    }

    // Windows and the drive can count a few sectors differently; 1% apart is the same drive
    internal static bool SameSize(long? a, long? b) =>
        a is long x && b is long y && x > 0 && y > 0 && Math.Abs(x - y) <= Math.Max(x, y) / 100;

    static string? UsableWindowsSerial(Inventory.Disk? win) =>
        win != null && Store.UsableSerial(win.Serial) ? win.Serial!.Trim() : null;

    const string AdapterNote = "From Windows' own counters: the USB adapter doesn't pass SMART readings through, so only hours, temperature and start/stop counts are known.";

    static string NeedsAdminNote => OperatingSystem.IsWindows() ? "Needs admin rights to read." : "Needs root to read (run the reading with sudo).";

    /// <summary>A reading from what the OS knows alone (source: windows, linux or macos).</summary>
    static DriveReading FromOs(Inventory.Disk? win, string source, DateTimeOffset at, string status, string? note)
    {
        var r = new DriveReading { At = at, Source = source, Status = status, Note = note };
        Merge(r, win);
        r.Model = win?.Name;
        r.Capacity = win?.Size;
        r.Device ??= win?.Path;
        r.Key = Store.KeyFor(win?.Name ?? "disk", win?.Serial, win?.Size);
        if (status == "partial" && note == null && source == "windows") r.Note = AdapterNote;
        return r;
    }

    static async Task ReadAndroidAsync(RecordOptions opt, DateTimeOffset started, List<(DriveReading Reading, string? Raw)> found,
                                       List<VolumeReading> volumes, RecordResult result, Action<string>? progress, CancellationToken ct)
    {
        string? adb = AndroidDevices.LocateAdb(opt.AdbPath);
        if (adb == null) { result.Notes.Add("adb not found: phones, tablets and TVs weren't read."); return; }
        foreach (var address in opt.AndroidAddresses.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct())
        {
            progress?.Invoke("Connecting to " + address);
            if (!await AndroidDevices.ConnectAsync(adb, address, ct)) result.Notes.Add($"{address}: no answer over adb (off, asleep or not allowing debugging).");
        }
        foreach (var dev in await AndroidDevices.ListAsync(adb, ct))
        {
            string who = dev.Model ?? dev.Serial;
            if (dev.State != "device")
            {
                result.Notes.Add($"{who}: adb says {dev.State}" + (dev.State == "unauthorized" ? " (allow this computer on the device's screen)." : "."));
                continue;
            }
            progress?.Invoke("Reading " + who);
            try
            {
                string? output = await AndroidDevices.ProbeAsync(adb, dev.Serial, ct);
                if (output == null) { result.Notes.Add($"{who}: no reply over adb."); continue; }
                var probe = AndroidDevices.ParseProbe(output, started);
                // two connections to one device (USB and Wi-Fi) are one reading
                if (found.Any(f => f.Reading.Key == probe.Reading.Key)) continue;
                found.Add((probe.Reading, null));
                volumes.AddRange(probe.Volumes);
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { result.Notes.Add($"{who}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// A reading a device's own app sent to the hub. Waits briefly for a pass that holds the store; false when it
    /// stays busy (the app retries later).
    /// </summary>
    public static async Task<bool> RecordAppAsync(Store store, RecordOptions opt, AndroidDevices.Probe probe, CancellationToken ct = default)
    {
        FileStream? gate = null;
        for (int i = 0; i < 10 && (gate = store.TryLock()) == null; i++) await Task.Delay(500, ct);
        if (gate == null) return false;
        using (gate)
        {
            var notes = store.LoadDevices();
            var r = probe.Reading;
            notes.TryGetValue(r.Key, out var note);
            note ??= new Store.DeviceNote();
            AndroidDevices.Accumulate(r, note);
            store.Append(r);
            store.AppendVolumes(probe.Volumes);
            if (Analysis.IsFull(r)) note.LastFullReading = r.At;
            note.Capacity = r.Capacity ?? note.Capacity;
            notes[r.Key] = note;
            store.SaveDevices(notes);
            try { WriteLatest(store, opt, null); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* refreshed on the next pass */ }
        }
        return true;
    }

    /// <summary>Windows knows the bus, the media type, its own health verdict and, with admin rights, a few counters.</summary>
    static void Merge(DriveReading r, Inventory.Disk? win)
    {
        if (win == null) return;
        r.DiskNumber = win.Number;
        r.Bus = win.Bus;
        if (win.Media is "SSD" or "HDD") r.Media = win.Media;
        r.WindowsSerial = UsableWindowsSerial(win);
        r.WindowsHealth = win.Health;
        r.Capacity ??= win.Size > 0 ? win.Size : null;
        if (win.HasCounters && r.PowerOnHours == null)
        {
            r.PowerOnHours = win.PowerOnHours;
            r.Temp ??= win.Temp;
            r.StartStops ??= win.StartStops;
            r.LoadCycles ??= win.LoadCycles;
            if (win.ReadErrors is long re && re > 0) r.Uncorrectable ??= re;
            // Windows' counters stand in for SMART; a temperature alone (Linux) doesn't
            if (r.Status == "no-smart" && OperatingSystem.IsWindows())
            {
                r.Status = "partial";
                r.Source = "windows";
                r.Note = AdapterNote;
            }
        }
    }

    // ------------------------------------------------------------------ latest.json

    // The normal record pass already holds this lock. The event-only command uses the
    // same lock and reads no disk devices.
    internal static async Task<int> SaveWindowsDiskEventsAsync(Store store, CancellationToken ct = default)
    {
        var incidents = await WindowsInventory.ReadDiskEventsAsync(ct);
        store.SaveDiskIncidents(incidents);
        return incidents.Sum(i => i.Count);
    }

    public static List<DriveReport> Reports(Store store, DateTimeOffset now) =>
        Reports(store.LoadAll(), store.LoadVolumes(), now, store.LoadDiskIncidents(now));

    /// <summary>Every drive judged, from one load of the files (a pass that adds a drive meanwhile can't split them).</summary>
    public static List<DriveReport> Reports(Dictionary<string, List<DriveReading>> all, IReadOnlyList<VolumeReading> volumes,
        DateTimeOffset now, IReadOnlyList<DiskIncident>? incidents = null)
    {
        // a drive first seen without admin rights under Windows' name, later read in full under its own name: show it once
        var fullSerials = all.Where(kv => kv.Value.Any(Analysis.IsFull)).Select(kv => kv.Value.Last(Analysis.IsFull).WindowsSerial)
                             .Where(Store.UsableSerial).ToHashSet();
        var fullSlots = all.Where(kv => kv.Value.Any(Analysis.IsFull)).Select(kv => kv.Value.Last(Analysis.IsFull))
                           .Where(r => r.DiskNumber != null).Select(r => (r.DiskNumber, r.Capacity)).ToList();
        var reports = new List<DriveReport>();
        foreach (var (key, list) in all)
        {
            var last = list[^1];
            bool shadow = !list.Any(Analysis.IsFull) &&
                          (last.WindowsSerial is string ws && Store.UsableSerial(ws)
                              ? fullSerials.Contains(ws)
                              : last.DiskNumber != null && fullSlots.Any(s => s.DiskNumber == last.DiskNumber && SameSize(s.Capacity, last.Capacity)));
            if (shadow) continue;
            reports.Add(Analysis.Analyze(key, list, volumes, now));
        }
        // one finding per drive and event ID, even when a replug gave the drive a different disk number
        if (incidents != null)
            foreach (var g in incidents.Select(i => (match: MatchDiskIncident(reports, i), i)).Where(x => x.match != null)
                                       .GroupBy(x => (x.match!, x.i.EventId)))
            {
                var same = g.Select(x => x.i).ToList();
                Analysis.AddDiskIncident(g.Key.Item1, same.Count == 1 ? same[0] : new DiskIncident
                {
                    EventId = g.Key.EventId, DiskNumber = same[0].DiskNumber, Model = same[0].Model, WindowsSerial = same[0].WindowsSerial,
                    Count = same.Sum(i => i.Count), First = same.Min(i => i.First), Last = same.Max(i => i.Last),
                });
            }
        return reports.OrderBy(r => r.Latest.Letters?.FirstOrDefault() ?? "~").ThenBy(r => r.Latest.Model).ToList();
    }

    internal static DriveReport? MatchDiskIncident(IReadOnlyList<DriveReport> reports, DiskIncident incident)
    {
        if (!Store.UsableSerial(incident.WindowsSerial)) return null;
        var matches = reports.Where(r => string.Equals(r.Latest.WindowsSerial ?? r.LatestFull?.WindowsSerial,
            incident.WindowsSerial, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return matches.Count == 1 ? matches[0] : null;
    }

    public static object LatestDocument(Store store, RecordOptions opt, RecordResult? pass, DateTimeOffset now)
    {
        var incidents = store.LoadDiskIncidents(now);
        var readings = store.LoadVolumes();
        var reports = Reports(store.LoadAll(), readings, now, incidents);
        var volumes = Analysis.VolumesOf(readings, now);
        return new
        {
            app = opt.App,
            version = opt.Version,
            written = now,
            machine = Environment.MachineName,
            pass = pass == null ? null : new { at = pass.At, elevated = pass.Elevated, smartctl = pass.SmartctlVersion, seconds = Math.Round(pass.Took.TotalSeconds, 1) },
            windowsDiskIncidents = incidents.Select(i => new { i.DiskNumber, i.Model, i.EventId, i.Description, i.Count, i.First, i.Last, i.Severity }),
            drives = reports.Select(r =>
            {
                var L = r.LatestFull ?? r.Latest;
                return new
                {
                    key = r.Key,
                    model = L.Model,
                    serial = L.Serial,
                    windowsSerial = L.WindowsSerial,
                    letters = r.Latest.Letters ?? L.Letters,
                    bus = L.Bus,
                    media = L.Media,
                    capacity = L.Capacity,
                    verdict = r.Verdict,
                    status = r.Latest.Status,
                    lastReading = r.Last,
                    lastFullReading = r.LatestFull?.At,
                    readings = r.Readings,
                    powerOnHours = L.PowerOnHours,
                    bytesWritten = L.BytesWritten,
                    bytesRead = L.BytesRead,
                    writeUnits = L.WriteUnits,
                    readUnits = L.ReadUnits,
                    rpm = L.Rpm,
                    startStops = L.StartStops,
                    loadCycles = L.LoadCycles,
                    percentUsed = L.PercentUsed,
                    percentUsedEstimated = L.PercentUsedEstimated,
                    spare = L.Spare,
                    spareThreshold = L.SpareThreshold,
                    temp = L.Temp,
                    tempLifetimeMax = L.TempLifetimeMax,
                    warningTempMinutes = L.WarningTempMinutes,
                    criticalTempMinutes = L.CriticalTempMinutes,
                    controllerBusyMinutes = L.ControllerBusyMinutes,
                    reallocated = L.Reallocated,
                    pending = L.Pending,
                    uncorrectable = L.Uncorrectable,
                    crcErrors = L.CrcErrors,
                    mediaErrors = L.MediaErrors,
                    errorLogEntries = L.ErrorLogEntries,
                    unsafeShutdowns = L.UnsafeShutdowns,
                    powerCycles = L.PowerCycles,
                    selfTest = L.SelfTest,
                    selfTestPassed = L.SelfTestPassed,
                    selfTestHours = L.SelfTestHours,
                    writtenPerDay = r.Rates.WrittenPerDay,
                    findings = r.Findings.Where(f => !f.Code.StartsWith("vol-")).Select(f => new { severity = f.Severity, code = f.Code, title = f.Title, detail = f.Detail, advice = f.Advice, basis = f.Basis }),
                };
            }),
            volumes = volumes.Select(v => new
            {
                letter = v.Letter, label = v.Label, size = v.Size, free = v.Free, usedPct = v.UsedPct, driveKey = v.DriveKey, present = v.Present, at = v.At,
                freePerDay = v.FreePerDay, fullAround = v.FullAround, status = v.Status, health = v.Health,
                findings = v.Findings.Select(f => new { severity = f.Severity, code = f.Code, title = f.Title, detail = f.Detail, advice = f.Advice }),
            }),
        };
    }

    public static void WriteLatest(Store store, RecordOptions opt, RecordResult? pass) =>
        store.WriteLatest(LatestDocument(store, opt, pass, DateTimeOffset.Now));
}
