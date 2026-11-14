using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace BoardScout.DriveHealth;

public sealed class Rates
{
    public double SpanDays { get; set; }
    public double? WrittenPerDay { get; set; }
    public double? ReadPerDay { get; set; }
    public double? WriteUnitsPerDay { get; set; }
    public double? HoursPerDay { get; set; }
    public double? LoadCyclesPerDay { get; set; }
    public double? UnsafePerMonth { get; set; }
    public double? WearPerYear { get; set; }
    public int? WearOutYear { get; set; }
}

public sealed class VolumeReport
{
    public string Letter { get; set; } = "";
    public string? Label { get; set; }
    public string? FileSystem { get; set; }
    public long Size { get; set; }
    public long Free { get; set; }
    public double UsedPct { get; set; }
    public string? DriveKey { get; set; }
    public string? Health { get; set; }
    public string? Status { get; set; }
    public DateTimeOffset At { get; set; }
    /// <summary>False when a newer pass read this volume's drive and the volume wasn't in it (removed, renamed, or a
    /// mount the reading should never have counted); it keeps its history, but says nothing new.</summary>
    public bool Present { get; set; } = true;
    public double? FreePerDay { get; set; }
    public DateTimeOffset? FullAround { get; set; }
    public List<double[]> Points { get; set; } = new();
    public List<Finding> Findings { get; set; } = new();
}

public sealed class DriveReport
{
    public string Key { get; set; } = "";
    public DriveReading Latest { get; set; } = new();
    public DriveReading? LatestFull { get; set; }
    public int Readings { get; set; }
    public DateTimeOffset First { get; set; }
    public DateTimeOffset Last { get; set; }
    /// <summary>healthy | watch | attention | act | unknown</summary>
    public string Verdict { get; set; } = "unknown";
    public List<Finding> Findings { get; set; } = new();
    public List<DriveEvent> Events { get; set; } = new();
    public List<MonthRow> Months { get; set; } = new();
    public Rates Rates { get; set; } = new();
    public List<VolumeReport> Volumes { get; set; } = new();
}

/// <summary>
/// Turns a drive's readings into what a person wants to know: is it all right, what changed, how fast is it wearing,
/// and what probably caused a problem. Single readings give "today" findings; two or more give trends.
/// </summary>
public static class Analysis
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>What to do about a drive that needs admin rights to read; each app names its own way to get them.</summary>
    public static string NeedsAdminAdvice { get; set; } =
        "Use \"Read USB drives\" (one admin prompt), or turn on the daily reading task, which runs with admin rights.";

    /// <summary>
    /// Added to the advice for unreadable sectors: where to find the files sitting on them. Empty unless the app knows a
    /// tool for that and sets it (the engine itself names no other app).
    /// </summary>
    public static string DamagedFilesHint { get; set; } = "";

    static string S(long n) => n == 1 ? "" : "s";

    static string WithHint(string advice) =>
        string.IsNullOrWhiteSpace(DamagedFilesHint) ? advice : advice + " " + DamagedFilesHint.Trim();

    public static bool IsFull(DriveReading r) => r.Status is "ok" or "partial";

    public static DriveReport Analyze(string key, IReadOnlyList<DriveReading> history, IReadOnlyList<VolumeReading>? volumes = null, DateTimeOffset? now = null)
    {
        var list = history.OrderBy(r => r.At).ToList();
        var full = list.Where(IsFull).ToList();
        var nowAt = now ?? DateTimeOffset.Now;
        var rep = new DriveReport
        {
            Key = key,
            Latest = list[^1],
            LatestFull = full.Count > 0 ? CarriedForward(full) : null,
            Readings = full.Count,
            First = list[0].At,
            Last = list[^1].At,
        };
        rep.Rates = RatesOf(full);
        rep.Events = EventsOf(full);
        rep.Months = MonthsOf(full);
        if (volumes != null) rep.Volumes = VolumesOf(volumes, nowAt).Where(v => v.DriveKey == key).ToList();
        rep.Findings = FindingsOf(rep, full, nowAt);
        // the verdict is about the drive's health: a full or filling volume is listed, but it doesn't make a drive failing
        // (a file system Windows says needs repair does count)
        int worst = rep.Findings.Where(f => !f.Code.StartsWith("vol-", StringComparison.Ordinal) || f.Code == "vol-repair")
                                .Select(f => Finding.Rank(f.Severity)).DefaultIfEmpty(0).Max();
        rep.Verdict = rep.LatestFull == null ? "unknown" : worst switch { 4 => "act", 3 => "attention", 2 => "watch", _ => "healthy" };
        return rep;
    }

    /// <summary>
    /// The newest full reading, with each counter it lacks taken from the last reading that had it. A thin pass (a USB
    /// adapter that answered only with Windows' counters once) must never make earlier damage disappear.
    /// </summary>
    static DriveReading CarriedForward(List<DriveReading> full)
    {
        var e = full[^1].Copy();
        for (int i = full.Count - 2; i >= 0; i--)
        {
            var p = full[i];
            e.SmartPassed ??= p.SmartPassed; e.CriticalWarning ??= p.CriticalWarning;
            e.PowerOnHours ??= p.PowerOnHours; e.PowerCycles ??= p.PowerCycles; e.UnsafeShutdowns ??= p.UnsafeShutdowns;
            e.TempLimit ??= p.TempLimit; e.TempLifetimeMax ??= p.TempLifetimeMax;
            e.BytesWritten ??= p.BytesWritten; e.BytesRead ??= p.BytesRead;
            if (e.WriteUnits == null && p.WriteUnits != null) { e.WriteUnits = p.WriteUnits; e.WriteUnit = p.WriteUnit; }
            e.PercentUsed ??= p.PercentUsed; e.Spare ??= p.Spare; e.SpareThreshold ??= p.SpareThreshold;
            e.MediaErrors ??= p.MediaErrors; e.ErrorLogEntries ??= p.ErrorLogEntries; e.AtaErrors ??= p.AtaErrors;
            e.WarningTempMinutes ??= p.WarningTempMinutes; e.CriticalTempMinutes ??= p.CriticalTempMinutes;
            e.LoadCycles ??= p.LoadCycles; e.StartStops ??= p.StartStops;
            e.Reallocated ??= p.Reallocated; e.Pending ??= p.Pending; e.Uncorrectable ??= p.Uncorrectable; e.CrcErrors ??= p.CrcErrors;
            if (e.SelfTest == null && p.SelfTest != null) { e.SelfTest = p.SelfTest; e.SelfTestPassed = p.SelfTestPassed; e.SelfTestHours = p.SelfTestHours; }
            e.Attributes ??= p.Attributes;
            e.FlashLifeA ??= p.FlashLifeA; e.FlashLifeB ??= p.FlashLifeB; e.FlashPreEol ??= p.FlashPreEol;
        }
        return e;
    }

    // ------------------------------------------------------------------ rates

    static double? PerDay(List<DriveReading> full, Func<DriveReading, double?> pick, double windowDays = 30)
    {
        var withValue = full.Where(r => pick(r) != null).ToList();
        if (withValue.Count < 2) return null;
        var last = withValue[^1];
        var from = withValue.FirstOrDefault(r => (last.At - r.At).TotalDays <= windowDays && (last.At - r.At).TotalDays >= 0.9)
                   ?? withValue.LastOrDefault(r => (last.At - r.At).TotalDays >= 0.9);
        if (from == null) return null;
        double days = (last.At - from.At).TotalDays;
        double delta = pick(last)!.Value - pick(from)!.Value;
        if (days < 0.9 || delta < 0) return null; // a counter that went down was reset; no rate across it
        return delta / days;
    }

    public static Rates RatesOf(List<DriveReading> full)
    {
        var r = new Rates();
        if (full.Count >= 2) r.SpanDays = (full[^1].At - full[0].At).TotalDays;
        r.WrittenPerDay = PerDay(full, x => x.BytesWritten);
        r.ReadPerDay = PerDay(full, x => x.BytesRead);
        r.WriteUnitsPerDay = PerDay(full, x => x.WriteUnits);
        r.HoursPerDay = PerDay(full, x => x.PowerOnHours);
        r.LoadCyclesPerDay = PerDay(full, x => x.LoadCycles);
        if (PerDay(full, x => x.UnsafeShutdowns, 90) is double u) r.UnsafePerMonth = u * 30.44;

        // wear: the percentage moves in whole steps, so use the whole history and only once it has moved
        var wear = full.Where(x => x.PercentUsed != null).ToList();
        if (wear.Count >= 2)
        {
            double years = (wear[^1].At - wear[0].At).TotalDays / 365.25;
            int moved = wear[^1].PercentUsed!.Value - wear[0].PercentUsed!.Value;
            if (years >= 30 / 365.25 && moved >= 1)
            {
                r.WearPerYear = moved / years;
                double left = (100 - wear[^1].PercentUsed!.Value) / r.WearPerYear.Value;
                if (left < 200) r.WearOutYear = wear[^1].At.AddDays(left * 365.25).Year;
            }
        }
        return r;
    }

    // ------------------------------------------------------------------ findings

    static Finding F(string sev, string code, string title, string detail, string? advice = null, string basis = "today", DateTimeOffset? since = null) =>
        new() { Severity = sev, Code = code, Title = title, Detail = detail, Advice = advice, Basis = basis, Since = since };

    static List<Finding> FindingsOf(DriveReport rep, List<DriveReading> full, DateTimeOffset now)
    {
        var f = new List<Finding>();
        var L = rep.LatestFull;
        var latest = rep.Latest;

        if (L == null)
        {
            f.Add(latest.Status switch
            {
                "needs-admin" => F("info", "needs-admin", "Not read yet", "This drive is on USB, and Windows only lets a program read a USB drive's health with admin rights.", NeedsAdminAdvice),
                "no-smart" => F("info", "no-smart", "No health readings through this adapter", "The drive's USB adapter doesn't pass SMART readings through, and Windows has no counters for it either.", "Only its free space is tracked. Plugged in directly (SATA), the drive would report everything."),
                "asleep" => F("info", "asleep", "Asleep at every reading so far", "The disk was spun down, and it was left asleep rather than woken for a reading.", "It will be read anyway once it has gone a week without a reading."),
                "no-tool" => F("info", "no-tool", "smartmontools isn't installed", "Drive health is read through smartctl, part of smartmontools.", "Install smartmontools (smartmontools.org), then read again."),
                _ => F("info", "error", "Couldn't read this drive", latest.Note ?? "smartctl couldn't read it."),
            });
            return f;
        }

        bool hdd = L.Media == "HDD";
        string name = L.Model ?? rep.Key;

        // the drive's own verdicts
        if (L.SmartPassed == false)
            f.Add(F("critical", "smart-failed", "The drive reports it is failing", "Its overall SMART health check says FAILED.", "Copy anything you need off it now, then replace it."));
        if (L.CriticalWarning is int cw && cw != 0)
        {
            var bits = new List<string>();
            if ((cw & 0x01) != 0) bits.Add("spare blocks below the safe level");
            if ((cw & 0x02) != 0) bits.Add("temperature out of range");
            if ((cw & 0x04) != 0) bits.Add("reliability degraded by media or internal errors");
            if ((cw & 0x08) != 0) bits.Add("switched itself to read-only");
            if ((cw & 0x10) != 0) bits.Add("power-loss backup failed");
            if ((cw & 0x20) != 0) bits.Add("persistent memory read-only");
            f.Add(F("critical", "nvme-critical", "The drive raised a critical warning", "NVMe critical warning: " + string.Join(", ", bits) + ".", "Copy anything you need off it now."));
        }
        foreach (var a in L.Attributes ?? new())
        {
            if (a.WhenFailed == "now") f.Add(F("critical", "attr-failing-" + a.Id, $"{Pretty(a.Name)} is at its failure threshold", $"Attribute {a.Id} reads {a.Value}, threshold {a.Thresh}.", "The drive's maker counts this as failing. Copy anything you need off it now."));
            else if (a.WhenFailed == "past") f.Add(F("warn", "attr-failed-" + a.Id, $"{Pretty(a.Name)} reached its threshold in the past", $"Attribute {a.Id} once fell to its threshold ({a.Thresh}); its worst value is {a.Worst}.", "Back up and keep an eye on it."));
        }
        if (L.SelfTestPassed == false)
            f.Add(F("critical", "selftest-failed", "Its last self-test failed", L.SelfTest + (L.SelfTestHours is long h ? $" at {h:N0} hours" : "") + ".", "Copy anything you need off it now."));

        // wear and spare (SSDs)
        if (L.PercentUsed is int used)
        {
            string pace = rep.Rates.WearOutYear is int y ? $" At its pace so far it reaches 100% around {y}." : "";
            // a phone's or TV's flash chip estimates its life in 10% steps: 70 means 70-80%
            string share = L.FlashLifeA != null || L.FlashLifeB != null ? (used >= 100 ? "100%" : $"{used}-{used + 10}%") : $"{used}%";
            if (used >= 100) f.Add(F("critical", "worn-out", "Past its rated life", $"{share} of its rated write life is used.{pace}", "It may keep working, but treat it as a drive that can stop any day: keep a copy of everything on it."));
            else if (used >= 90) f.Add(F("warn", "wear-high", $"{share} of its write life used", $"The maker's rated write life is nearly used up.{pace}", "Plan its replacement."));
            else if (used >= 70) f.Add(F("watch", "wear", $"{share} of its write life used", "Well into its rated write life." + pace));
            else if (rep.Rates.WearOutYear is int yy && yy - now.Year < 3)
                f.Add(F("watch", "wear-fast", "Wearing fast", $"{used}% used, rising {rep.Rates.WearPerYear:0.#} points a year: about 100% around {yy}.", "Check what writes so much to it.", "trend"));
        }
        if (L.Spare is int sp && L.SpareThreshold is int st && st > 0 && L.Media != "HDD")
        {
            if (sp <= st) f.Add(F("critical", "spare-out", "Out of spare blocks", $"Spare blocks at {sp}%, at or below the drive's limit of {st}%.", "Copy anything you need off it now."));
            else if (sp < 100) f.Add(F(sp - st < 20 ? "warn" : "watch", "spare", $"Using its spare blocks ({sp}% left)", $"It has replaced worn-out blocks from its reserve; its limit is {st}%."));
        }

        // flash in phones, tablets and TVs: the chip's own spare-block level (pre-EOL)
        if (L.FlashPreEol is int eol && eol >= 2)
            f.Add(eol >= 3
                ? F("critical", "flash-eol", "Its storage chip is nearly out of spare blocks", "The flash reports its reserve as urgent (pre-EOL level 3).", "Copy anything you need off it now: the device may slow down, turn read-only or stop starting.")
                : F("warn", "flash-eol", "Its storage chip has used most of its spare blocks", "The flash reports 80% of its reserve used (pre-EOL level 2).", "Back up the device and plan its replacement."));

        // damage counters
        if (L.MediaErrors is long me && me > 0)
            f.Add(F("warn", "media-errors", $"{me:N0} data integrity error{S(me)}", "The drive found data it couldn't read back correctly (NVMe media errors).", "Back up now; if the number grows, replace it."));
        if (L.Pending is long pe && pe > 0)
            f.Add(F("warn", "pending", $"{pe:N0} sector{S(pe)} it couldn't read", "Sectors waiting to be re-tested or replaced (pending). Files sitting on them may already be damaged.", WithHint("Back up now.")));
        if (L.Uncorrectable is long un && un > 0)
            f.Add(F("warn", "uncorrectable", $"{un:N0} uncorrectable error{S(un)}", "Reads the drive could not correct, over its life.", "Back up now and watch whether the number grows."));
        if (L.Reallocated is long re && re > 0)
            f.Add(F(re >= 100 ? "warn" : "watch", "reallocated", $"{re:N0} sector{S(re)} replaced", "The drive retired bad areas and moved their data to spares.", re >= 100 ? "Many replaced sectors: back up and plan a replacement." : "A few are normal for an old drive; what matters is whether the number grows."));
        if (L.AtaErrors is long ae && ae > 0)
            f.Add(F("watch", "ata-errors", $"{ae:N0} error{S(ae)} in the drive's error log", "Commands that failed inside the drive, over its life.", "If the number grows, back up and check the cable or adapter."));
        if (L.CrcErrors is long crc && crc > 0)
            f.Add(F("info", "crc", $"{crc:N0} cable error{S(crc)} over its life", "Data garbled between the drive and the PC: a cable, connector or USB adapter problem, not the disk itself.", "Only matters if it keeps growing."));
        if (L.ErrorLogEntries is long el && el > 0 && (L.MediaErrors ?? 0) == 0)
            f.Add(F("info", "error-log", $"{el:N0} entries in the NVMe error log", "With no media errors, these are usually commands the drive turned down (Windows asks for features some drives don't support), not damage."));

        // heat
        int? limit = L.TempLimit ?? (hdd ? 60 : 70);
        if (L.Temp is int t)
        {
            if (t >= limit) f.Add(F("warn", "hot", $"Running hot: {t} °C", $"At or above its {limit} °C limit.", "Check airflow around it."));
            else if (t >= limit - 5) f.Add(F("watch", "warm", $"Warm: {t} °C", $"Close to its {limit} °C limit.", "Check airflow around it."));
            else if (hdd && t >= 50) f.Add(F("watch", "warm", $"Warm: {t} °C", $"Below its {limit} °C limit, but hard disks last longer under 50 °C.", "Check airflow around it."));
        }
        if (L.CriticalTempMinutes is long ctm && ctm > 0)
            f.Add(F("watch", "temp-critical-time", "Has run above its critical temperature", $"{ctm:N0} minutes in total over its life."));
        else if (L.WarningTempMinutes is long wtm && wtm > 0)
            f.Add(F("info", "temp-warning-time", "Has run above its warning temperature", $"{wtm:N0} minutes in total over its life."));

        // how it is treated
        if (L.UnsafeShutdowns is long us && L.PowerCycles is long pc && pc >= 20)
        {
            double share = (double)us / pc;
            bool repairNeeded = rep.Volumes.Any(v => v.Present && (v.Status ?? "").Contains("Repair", StringComparison.OrdinalIgnoreCase));
            string what = hdd ? "emergency head retracts (power cut while spinning)" : "unsafe power-offs";
            string link = repairNeeded ? " That fits Windows reporting that its file system needs repair." : "";
            if (us > pc)
                f.Add(F("info", "retract-count", $"Its retract counter ({us:N0}) is above its power cycles ({pc:N0})",
                    "On this drive the counter also counts head retracts that aren't power losses, so it can't say how many power-offs were unsafe."));
            else if (share >= 0.5) f.Add(F("warn", "unsafe-power", $"{us:N0} of {pc:N0} power-offs were unsafe", $"{Math.Round(share * 100):0}% of the times it lost power were {what}: unplugged without ejecting, or an enclosure that cuts power before the drive finishes.{link}", "Eject it before unplugging. If you already do, the enclosure or hub may be cutting power: try another one."));
            else if (share >= 0.15) f.Add(F("watch", "unsafe-power", $"{us:N0} of {pc:N0} power-offs were unsafe", $"{Math.Round(share * 100):0}% of power-offs were {what}.{link}", "Eject it before unplugging."));
        }
        if (hdd && L.LoadCycles is long lc)
        {
            double perHour = L.PowerOnHours is long poh && poh > 0 ? (double)lc / poh : 0;
            string rate = perHour > 0 ? $" About {perHour:0.#} times per powered-on hour." : "";
            if (lc >= 600_000) f.Add(F("warn", "load-cycles", $"Heads have parked {lc:N0} times", "Past the 600,000 load cycles many drives are rated for." + rate, "Back up; the head mechanism is well past its rating."));
            else if (lc >= 300_000) f.Add(F("watch", "load-cycles", $"Heads have parked {lc:N0} times", "Many laptop-size drives are rated for 300,000 to 600,000 load cycles." + rate));
            else if (perHour >= 6) f.Add(F("info", "load-cycles", $"Parks its heads often ({lc:N0} so far)", "Aggressive head parking (a power-saving setting) wears the head mechanism over time." + rate));
        }
        if (hdd && L.PowerOnHours is long hours && hours >= 43_800)
            f.Add(F("watch", "old", $"Powered on for {Years(hours)}", "Spinning disks fail more often after about five years of running.", "Keep a second copy of anything only it holds."));

        if (L.WindowsHealth is string wh && wh != "Healthy")
            f.Add(F("warn", "windows-health", $"Windows reports {wh}", "Windows' own storage check doesn't call this disk healthy."));
        if (L.WriteUnit == "unknown" && L.WriteUnits != null)
            f.Add(F("info", "write-unit", "Counts writes in an unknown unit", $"smartctl's drive database doesn't know this model, so its write counter ({L.WriteUnits:N0}) has no known unit. The trend still shows how fast it grows."));

        // trends between readings
        if (full.Count >= 2) f.AddRange(TrendFindings(full, now));

        // the drive is gone or asleep lately
        if ((now - rep.Last).TotalDays > 3)
            f.Add(F("info", "stale", "No recent reading", $"Last read {rep.Last.LocalDateTime:d MMM yyyy}. Unplugged, or the reading task isn't running."));
        else if (latest.Status == "needs-admin" && L.At != latest.At)
            f.Add(F("info", "needs-admin", "Last pass couldn't read it", $"The newest pass ran without admin rights; the figures here are from {L.At.LocalDateTime:d MMM}."));

        foreach (var v in rep.Volumes) f.AddRange(v.Findings);
        return f.OrderByDescending(x => Finding.Rank(x.Severity)).ToList();
    }

    static IEnumerable<Finding> TrendFindings(List<DriveReading> full, DateTimeOffset now)
    {
        var first = full[0];
        (string Code, string Label, Func<DriveReading, long?> Pick, string Sev, string Advice)[] counters =
        {
            ("grow-media", "data integrity errors", r => r.MediaErrors, "critical", "Copy anything you need off it now."),
            ("grow-pending", "unreadable sectors", r => r.Pending, "critical", WithHint("Copy anything you need off it now.")),
            ("grow-uncorrectable", "uncorrectable errors", r => r.Uncorrectable, "critical", "Copy anything you need off it now."),
            ("grow-reallocated", "replaced sectors", r => r.Reallocated, "warn", "The surface is wearing: back up, and replace it if the count keeps climbing."),
            ("grow-crc", "cable errors", r => r.CrcErrors, "warn", "Reseat or replace the cable or USB adapter; the disk itself is probably fine."),
            ("grow-ata", "drive error log entries", r => r.AtaErrors, "watch", "Back up and check the cable or adapter."),
        };
        foreach (var c in counters)
        {
            long? a = full.Select(c.Pick).FirstOrDefault(v => v != null);
            long? b = full.Select(c.Pick).LastOrDefault(v => v != null);
            if (a == null || b == null || b <= a) continue;
            var (firstRise, lastRise) = Rises(full, c.Pick);
            // still climbing counts as recent, however long ago it started
            bool recent = lastRise != null && (now - lastRise.Value).TotalDays <= 45;
            string sev = recent ? c.Sev : c.Sev == "critical" ? "warn" : "watch";
            string when = firstRise is DateTimeOffset fr
                ? $", first rise seen {fr.LocalDateTime:d MMM yyyy}" + (lastRise is DateTimeOffset lr && lr != fr ? $", latest {lr.LocalDateTime:d MMM yyyy}." : ".")
                : ".";
            yield return F(sev, c.Code, $"More {c.Label}: {a:N0} → {b:N0}", $"Up {b - a:N0} since {first.At.LocalDateTime:d MMM yyyy}" + when, c.Advice, "trend", firstRise);
        }
        if (full.Zip(full.Skip(1)).FirstOrDefault(p => p.First.PowerOnHours is long x && p.Second.PowerOnHours is long y && y + 1 < x) is var (before, after) && before != null)
            yield return F("warn", "reset", "Its hour counter went backwards", $"{before.PowerOnHours:N0} h on {before.At.LocalDateTime:d MMM}, {after.PowerOnHours:N0} h on {after.At.LocalDateTime:d MMM}. The SMART data was reset, or a different drive now has this one's name and serial.", "Readings before that date may describe another drive.", "trend", after.At);
        if (full.Select(r => r.Spare).FirstOrDefault(v => v != null) is int s0 && full.Select(r => r.Spare).LastOrDefault(v => v != null) is int s1 && s1 < s0)
            yield return F("warn", "spare-drop", $"Spare blocks {s0}% → {s1}%", "It is drawing on its reserve of spare blocks.", "Back up and watch it.", "trend");
        if (full.FirstOrDefault(r => r.UnsafeShutdowns != null) is { UnsafeShutdowns: long u0 } ufirst &&
            full.LastOrDefault(r => r.UnsafeShutdowns != null) is { UnsafeShutdowns: long u1 } ulast && u1 > u0 && (ulast.At - ufirst.At).TotalDays >= 1)
            yield return F("info", "unsafe-recent", $"{u1 - u0:N0} unsafe power-offs since {first.At.LocalDateTime:d MMM}", "Power was cut without the drive being told first.", "Eject before unplugging.", "trend");
    }

    static (DateTimeOffset? First, DateTimeOffset? Last) Rises(List<DriveReading> full, Func<DriveReading, long?> pick)
    {
        long? prev = null;
        DateTimeOffset? firstRise = null, lastRise = null;
        foreach (var r in full)
        {
            var v = pick(r);
            if (v == null) continue;
            if (prev != null && v > prev) { firstRise ??= r.At; lastRise = r.At; }
            prev = v;
        }
        return (firstRise, lastRise);
    }

    // ------------------------------------------------------------------ events

    public static List<DriveEvent> EventsOf(List<DriveReading> full)
    {
        var events = new List<DriveEvent>();
        if (full.Count == 0) return events;
        events.Add(new DriveEvent { At = full[0].At, Text = "First reading" + (full[0].PowerOnHours is long h ? $": {h:N0} hours on, {Bytes(full[0].BytesWritten)} written" : "") });
        foreach (var (a, b) in full.Zip(full.Skip(1)))
        {
            void Rise(Func<DriveReading, long?> pick, string label, string sev)
            {
                if (pick(a) is long x && pick(b) is long y && y > x) events.Add(new DriveEvent { At = b.At, Severity = sev, Text = $"{label} {x:N0} → {y:N0} (+{y - x:N0})" });
            }
            if (a.Firmware != null && b.Firmware != null && a.Firmware != b.Firmware) events.Add(new DriveEvent { At = b.At, Text = $"Firmware {a.Firmware} → {b.Firmware}" });
            if (a.PowerOnHours is long h0 && b.PowerOnHours is long h1 && h1 + 1 < h0) events.Add(new DriveEvent { At = b.At, Severity = "warn", Text = $"Hour counter went backwards ({h0:N0} → {h1:N0})" });
            if (a.SmartPassed == true && b.SmartPassed == false) events.Add(new DriveEvent { At = b.At, Severity = "critical", Text = "SMART health check changed to FAILED" });
            if ((a.CriticalWarning ?? 0) == 0 && (b.CriticalWarning ?? 0) != 0) events.Add(new DriveEvent { At = b.At, Severity = "critical", Text = $"Critical warning raised (code {b.CriticalWarning})" });
            Rise(r => r.MediaErrors, "Data integrity errors", "critical");
            Rise(r => r.Pending, "Unreadable sectors", "critical");
            Rise(r => r.Uncorrectable, "Uncorrectable errors", "critical");
            Rise(r => r.Reallocated, "Replaced sectors", "warn");
            Rise(r => r.CrcErrors, "Cable errors", "warn");
            Rise(r => r.AtaErrors, "Drive error log", "watch");
            Rise(r => r.UnsafeShutdowns, "Unsafe power-offs", "info");
            if (a.Spare is int s0 && b.Spare is int s1 && s1 < s0) events.Add(new DriveEvent { At = b.At, Severity = "warn", Text = $"Spare blocks {s0}% → {s1}%" });
            if (a.PercentUsed is int p0 && b.PercentUsed is int p1 && p1 > p0) events.Add(new DriveEvent { At = b.At, Text = $"Wear {p0}% → {p1}%" });
            if (b.Temp is int t && b.TempLimit is int lim && t >= lim && !(a.Temp >= lim)) events.Add(new DriveEvent { At = b.At, Severity = "warn", Text = $"Reached {t} °C (limit {lim} °C)" });
            if (b.SelfTestHours != null && b.SelfTestHours != a.SelfTestHours && b.SelfTest != null)
                events.Add(new DriveEvent { At = b.At, Severity = b.SelfTestPassed == false ? "critical" : "info", Text = "Self-test: " + b.SelfTest });
            var la = string.Join(" ", a.Letters ?? Array.Empty<string>());
            var lb = string.Join(" ", b.Letters ?? Array.Empty<string>());
            if (la != lb && lb.Length > 0) events.Add(new DriveEvent { At = b.At, Text = la.Length > 0 ? $"Letter {la} → {lb}" : $"Now at {lb}" });
        }
        return events.OrderByDescending(e => e.At).Take(300).ToList();
    }

    // ------------------------------------------------------------------ months

    public static List<MonthRow> MonthsOf(List<DriveReading> full)
    {
        var rows = new List<MonthRow>();
        DriveReading? before = null;
        foreach (var g in full.GroupBy(r => r.At.LocalDateTime.ToString("yyyy-MM", Inv)))
        {
            var items = g.ToList();
            var start = before ?? items[0];
            var end = items[^1];
            long? D(Func<DriveReading, long?> pick) => pick(end) is long y && pick(start) is long x && y >= x ? y - x : null;
            long problems = 0;
            foreach (var pick in new Func<DriveReading, long?>[] { r => r.MediaErrors, r => r.Pending, r => r.Uncorrectable, r => r.Reallocated, r => r.CrcErrors, r => r.AtaErrors })
                problems += D(pick) ?? 0;
            rows.Add(new MonthRow
            {
                Month = g.Key,
                Readings = items.Count,
                Hours = D(r => r.PowerOnHours),
                Written = D(r => r.BytesWritten),
                Read = D(r => r.BytesRead),
                WriteUnits = D(r => r.WriteUnits),
                WearFrom = start.PercentUsed,
                WearTo = end.PercentUsed,
                TempMax = items.Select(r => r.Temp).Where(t => t != null).DefaultIfEmpty(null).Max(),
                NewProblems = problems,
                UnsafeShutdowns = D(r => r.UnsafeShutdowns),
                LoadCycles = D(r => r.LoadCycles),
            });
            before = end;
        }
        rows.Reverse();
        return rows;
    }

    // ------------------------------------------------------------------ volumes

    public static List<VolumeReport> VolumesOf(IReadOnlyList<VolumeReading> all, DateTimeOffset now)
    {
        var reports = new List<VolumeReport>();
        // the newest pass that saw each drive: a volume missing from it is gone (an unplugged drive keeps its volumes,
        // since no newer pass saw that drive at all). Passes stamp their volumes alike; a phone's own clock may be a
        // little off, hence the two minutes. A volume tied to no drive (a phone's SD card, a volume Windows maps to no
        // disk) comes from passes that don't all see it, so it is never called gone.
        var newest = all.Where(v => v.DriveKey != null).GroupBy(v => v.DriveKey!).ToDictionary(g => g.Key, g => g.Max(v => v.At));
        foreach (var g in all.GroupBy(v => v.Letter, StringComparer.OrdinalIgnoreCase))
        {
            var list = g.OrderBy(v => v.At).ToList();
            var last = list[^1];
            // only the stretch that belongs to the volume there now (a letter can move to another disk)
            var same = list.Where(v => v.DriveKey == last.DriveKey && v.Size == last.Size).ToList();
            var rep = new VolumeReport
            {
                Letter = last.Letter, Label = last.Label, FileSystem = last.FileSystem, Size = last.Size, Free = last.Free,
                UsedPct = last.Size > 0 ? Math.Round(100.0 * (last.Size - last.Free) / last.Size, 1) : 0,
                DriveKey = last.DriveKey, Health = last.Health, Status = last.Status, At = last.At,
                Present = last.DriveKey == null || last.At >= newest[last.DriveKey].AddMinutes(-2),
                Points = same.Select(v => new[] { v.At.ToUnixTimeMilliseconds(), v.Size > 0 ? Math.Round(100.0 * (v.Size - v.Free) / v.Size, 2) : 0 }).ToList(),
            };
            // a volume that is gone keeps its history but warns about nothing: it can't fill up or need a repair now
            if (!rep.Present) { reports.Add(rep); continue; }
            var recent = same.Where(v => (last.At - v.At).TotalDays <= 60).ToList();
            if (recent.Count >= 2 && (recent[^1].At - recent[0].At).TotalDays >= 1)
            {
                rep.FreePerDay = Slope(recent);
                if (rep.FreePerDay < -1e6) // losing more than a megabyte a day
                {
                    double days = last.Free / -rep.FreePerDay.Value;
                    if (days < 3650) rep.FullAround = last.At.AddDays(days);
                }
            }
            string name = last.Letter + (last.Label != null ? $" ({last.Label})" : "");
            if (rep.UsedPct >= 99) rep.Findings.Add(F("critical", "vol-full", $"{name} is full", $"{rep.UsedPct:0.#}% used, {Bytes(last.Free)} free.", "Windows and apps misbehave on a full drive: move or remove something now."));
            else if (rep.UsedPct >= 95) rep.Findings.Add(F("warn", "vol-full", $"{name} is {rep.UsedPct:0.#}% full", $"{Bytes(last.Free)} free.", "Make room before it fills."));
            else if (rep.UsedPct >= 90) rep.Findings.Add(F("watch", "vol-full", $"{name} is {rep.UsedPct:0.#}% full", $"{Bytes(last.Free)} free."));
            // a forecast is only worth saying for a volume still being read (not an unplugged drive) and a date still ahead
            if (rep.FullAround is DateTimeOffset fa && rep.UsedPct < 99 && fa > now && (now - last.At).TotalDays <= 7)
            {
                double days = (fa - now).TotalDays;
                if (days <= 30) rep.Findings.Add(F("warn", "vol-filling", $"{name} fills up around {fa.LocalDateTime:d MMM}", $"Losing {Bytes((long)-rep.FreePerDay!.Value)} a day lately.", "Make room, or move what keeps landing there.", "trend"));
                else if (days <= 120) rep.Findings.Add(F("watch", "vol-filling", $"{name} fills up around {fa.LocalDateTime:d MMM yyyy}", $"Losing {Bytes((long)-rep.FreePerDay!.Value)} a day lately.", null, "trend"));
            }
            if ((last.Status ?? "").Contains("Repair", StringComparison.OrdinalIgnoreCase))
                rep.Findings.Add(F("warn", "vol-repair", $"Windows says {name} needs a file system repair", $"Volume status: {last.Status}.", "Copy anything important off first; a repair (chkdsk) can lose damaged files."));
            else if (last.Health is string h && h != "Healthy")
                rep.Findings.Add(F("watch", "vol-health", $"Windows reports {name} as {h}", $"Volume status: {last.Status}."));
            reports.Add(rep);
        }
        return reports.OrderBy(r => r.Letter).ToList();
    }

    /// <summary>Least-squares change in free bytes per day.</summary>
    static double Slope(List<VolumeReading> pts)
    {
        double t0 = pts[0].At.ToUnixTimeSeconds();
        double n = pts.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var p in pts)
        {
            double x = (p.At.ToUnixTimeSeconds() - t0) / 86400.0, y = p.Free;
            sx += x; sy += y; sxx += x * x; sxy += x * y;
        }
        double den = n * sxx - sx * sx;
        return Math.Abs(den) < 1e-9 ? 0 : (n * sxy - sx * sy) / den;
    }

    // ------------------------------------------------------------------ wording

    public static string Bytes(long? b)
    {
        if (b is not long v) return "?";
        string[] units = { "B", "KB", "MB", "GB", "TB", "PB" };
        double x = v; int i = 0;
        while (Math.Abs(x) >= 1000 && i < units.Length - 1) { x /= 1000; i++; }
        return (i == 0 ? x.ToString("0", Inv) : x.ToString(x >= 100 ? "0" : "0.#", Inv)) + " " + units[i];
    }

    public static string Years(long hours)
    {
        double y = hours / 8766.0;
        return y >= 1 ? $"{y:0.#} years" : $"{hours / 24.0:0} days";
    }

    static string Pretty(string attr) => attr.Replace('_', ' ');
}
