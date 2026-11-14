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

        WindowsInventory? inv = null;
        if (OperatingSystem.IsWindows())
        {
            progress?.Invoke("Asking Windows about the disks");
            inv = await WindowsInventory.ReadAsync(ct);
            if (inv.Error != null) result.Notes.Add("Windows storage cmdlets: " + inv.Error);
        }
        result.Elevated = inv?.Elevated ?? Environment.IsPrivilegedProcess;
        var notes = store.LoadDevices();
        string machine = Environment.MachineName;
        var found = new List<(DriveReading Reading, string? Raw)>();
        var covered = new HashSet<int>();

        string? exe = Smartctl.Locate(opt.SmartctlPath);
        result.Smartctl = exe;
        if (exe == null) result.Notes.Add("smartctl not found: only Windows' own disk details were recorded.");
        else
        {
            progress?.Invoke("Finding drives");
            var (version, devices) = await Smartctl.ScanAsync(exe, ct);
            result.SmartctlVersion = version;
            foreach (var dev in devices)
            {
                ct.ThrowIfCancellationRequested();
                int? diskNo = dev.DiskNumber;
                var win = inv?.Disks.FirstOrDefault(d => d.Number == diskNo);
                if (diskNo is int n) covered.Add(n);
                var known = KnownFor(notes, win);
                try
                {
                if (dev.OpenError != null)
                {
                    var r0 = FromWindows(win, started, dev.NeedsAdmin ? "needs-admin" : "error", dev.NeedsAdmin ? "Needs admin rights to read." : dev.OpenError);
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
                    r = FromWindows(win, started, "error", run.TimedOut ? "smartctl timed out" : "smartctl returned nothing");
                    raw = null;
                }
                else
                {
                    try { r = Smartctl.Parse(run.Output, started); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
                    {
                        r = FromWindows(win, started, "error", "smartctl's reply couldn't be read: " + ex.Message);
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
                    var bad = FromWindows(win, started, "error", ex.Message);
                    bad.Device = dev.Name;
                    bad.Key = known.Key ?? bad.Key;
                    found.Add((bad, null));
                }
            }
        }

        // disks Windows sees that smartctl didn't list
        if (inv != null)
            foreach (var win in inv.Disks.Where(d => !covered.Contains(d.Number)))
            {
                string status = win.HasCounters ? "partial" : exe == null ? "no-tool" : result.Elevated ? "no-smart" : "needs-admin";
                string? note = exe == null
                    ? "smartctl isn't installed" + (win.HasCounters ? ", so only Windows' own counters (hours, temperature, start/stops) are known." : ".")
                    : win.HasCounters ? null : "smartctl didn't list this disk.";
                var r = FromWindows(win, started, status, note);
                r.Key = KnownFor(notes, win).Key ?? r.Key;
                found.Add((r, null));
            }

        // letters and volumes
        var volumes = new List<VolumeReading>();
        if (inv != null)
        {
            foreach (var (r, _) in found)
                if (r.DiskNumber is int d)
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
            if (!skip)
            {
                store.Append(r);
                if (raw != null && r.Status != "asleep") store.AppendRaw(r.Key, started, raw);
            }
            note ??= new Store.DeviceNote();
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
    internal static (string? Key, Store.DeviceNote? Note) KnownFor(Dictionary<string, Store.DeviceNote> notes, WindowsInventory.Disk? win)
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

    static string? UsableWindowsSerial(WindowsInventory.Disk? win) =>
        win != null && Store.UsableSerial(win.Serial) ? win.Serial!.Trim() : null;

    const string AdapterNote = "From Windows' own counters: the USB adapter doesn't pass SMART readings through, so only hours, temperature and start/stop counts are known.";

    static DriveReading FromWindows(WindowsInventory.Disk? win, DateTimeOffset at, string status, string? note)
    {
        var r = new DriveReading { At = at, Source = "windows", Status = status, Note = note };
        Merge(r, win);
        r.Model = win?.Name;
        r.Capacity = win?.Size;
        r.Key = Store.KeyFor(win?.Name ?? "disk", win?.Serial, win?.Size);
        if (status == "partial" && note == null) r.Note = AdapterNote;
        return r;
    }

    /// <summary>Windows knows the bus, the media type, its own health verdict and, with admin rights, a few counters.</summary>
    static void Merge(DriveReading r, WindowsInventory.Disk? win)
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
            if (r.Status == "no-smart")
            {
                r.Status = "partial";
                r.Source = "windows";
                r.Note = AdapterNote;
            }
        }
    }

    // ------------------------------------------------------------------ latest.json

    public static List<DriveReport> Reports(Store store, DateTimeOffset now) => Reports(store.LoadAll(), store.LoadVolumes(), now);

    /// <summary>Every drive judged, from one load of the files (a pass that adds a drive meanwhile can't split them).</summary>
    public static List<DriveReport> Reports(Dictionary<string, List<DriveReading>> all, IReadOnlyList<VolumeReading> volumes, DateTimeOffset now)
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
        return reports.OrderBy(r => r.Latest.Letters?.FirstOrDefault() ?? "~").ThenBy(r => r.Latest.Model).ToList();
    }

    public static object LatestDocument(Store store, RecordOptions opt, RecordResult? pass, DateTimeOffset now)
    {
        var reports = Reports(store, now);
        var volumes = Analysis.VolumesOf(store.LoadVolumes(), now);
        return new
        {
            app = opt.App,
            version = opt.Version,
            written = now,
            machine = Environment.MachineName,
            pass = pass == null ? null : new { at = pass.At, elevated = pass.Elevated, smartctl = pass.SmartctlVersion, seconds = Math.Round(pass.Took.TotalSeconds, 1) },
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
                    percentUsed = L.PercentUsed,
                    temp = L.Temp,
                    reallocated = L.Reallocated,
                    pending = L.Pending,
                    mediaErrors = L.MediaErrors,
                    unsafeShutdowns = L.UnsafeShutdowns,
                    powerCycles = L.PowerCycles,
                    writtenPerDay = r.Rates.WrittenPerDay,
                    findings = r.Findings.Where(f => !f.Code.StartsWith("vol-")).Select(f => new { severity = f.Severity, code = f.Code, title = f.Title, detail = f.Detail, advice = f.Advice, basis = f.Basis }),
                };
            }),
            volumes = volumes.Select(v => new
            {
                letter = v.Letter, label = v.Label, size = v.Size, free = v.Free, usedPct = v.UsedPct, driveKey = v.DriveKey,
                freePerDay = v.FreePerDay, fullAround = v.FullAround, status = v.Status, health = v.Health,
                findings = v.Findings.Select(f => new { severity = f.Severity, code = f.Code, title = f.Title, detail = f.Detail, advice = f.Advice }),
            }),
        };
    }

    public static void WriteLatest(Store store, RecordOptions opt, RecordResult? pass) =>
        store.WriteLatest(LatestDocument(store, opt, pass, DateTimeOffset.Now));
}
