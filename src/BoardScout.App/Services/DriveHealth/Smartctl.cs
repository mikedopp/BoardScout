using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>A device smartctl found with --scan-open.</summary>
public sealed record ScanDevice(string Name, string Type, string? Protocol, string? InfoName, string? OpenError)
{
    /// <summary>Windows: /dev/sdc = PhysicalDrive2. Linux: null (the disk number means nothing there).</summary>
    public int? DiskNumber => Smartctl.DiskNumberOf(Name, OpenError);
    public bool NeedsAdmin => OpenError != null && Regex.IsMatch(OpenError, @"Error=5\b|Permission denied|Operation not permitted", RegexOptions.IgnoreCase);
}

/// <summary>
/// Reads drives through smartctl (smartmontools) with JSON output (-j), and turns its JSON into a <see cref="DriveReading"/>.
/// Plain C#, no Windows calls: the same code reads a Linux server's drives.
/// </summary>
public static class Smartctl
{
    /// <summary>USB bridges that answer only as plain SCSI often pass SMART through with one of these.</summary>
    public static readonly string[] BridgeTypes = { "sat", "sat,12", "usbjmicron", "usbcypress", "usbsunplus", "usbprolific" };

    public static string? Locate(string? configured = null)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured!);
        string exe = OperatingSystem.IsWindows() ? "smartctl.exe" : "smartctl";
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), exe));
        if (OperatingSystem.IsWindows())
        {
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "smartmontools", "bin", exe));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "smartmontools", "bin", exe));
        }
        else candidates.AddRange(new[] { "/usr/sbin/smartctl", "/usr/bin/smartctl", "/usr/local/sbin/smartctl", "/opt/homebrew/bin/smartctl" });
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { /* a bad PATH entry */ }
        }
        return null;
    }

    public sealed record Run(int Exit, string Output, bool TimedOut);

    public static async Task<Run> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("smartctl did not start");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try { await p.WaitForExitAsync(cts.Token); }
        catch (OperationCanceledException)
        {
            try { p.Kill(true); } catch { }
            return new Run(-1, "", true);
        }
        string output = await stdout;
        await stderr;
        return new Run(p.ExitCode, output, false);
    }

    public static async Task<(string Version, List<ScanDevice> Devices)> ScanAsync(string exe, CancellationToken ct = default)
    {
        var run = await RunAsync(exe, new[] { "--scan-open", "-j" }, TimeSpan.FromSeconds(90), ct);
        if (run.TimedOut) throw new TimeoutException("smartctl --scan-open took more than 90 s");
        return ParseScan(run.Output);
    }

    public static (string Version, List<ScanDevice> Devices) ParseScan(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var list = new List<ScanDevice>();
        if (root.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
            foreach (var d in devs.EnumerateArray())
                list.Add(new ScanDevice(Str(d, "name") ?? "", Str(d, "type") ?? "auto", Str(d, "protocol"), Str(d, "info_name"), Str(d, "open_error")));
        return (VersionOf(root), list);
    }

    public static string VersionOf(JsonElement root)
    {
        if (Get(root, "smartctl", "version") is { ValueKind: JsonValueKind.Array } v)
            return string.Join(".", v.EnumerateArray().Select(x => x.ToString()));
        return "";
    }

    /// <summary>-x reads everything the drive offers. -n standby,0 leaves a sleeping disk asleep (exit 0, no identity in the reply).</summary>
    public static Task<Run> ReadAsync(string exe, string device, string type, bool leaveAsleep, CancellationToken ct = default)
    {
        var args = new List<string> { "-j", "-x" };
        if (leaveAsleep) { args.Add("-n"); args.Add("standby,0"); }
        args.Add(device); args.Add("-d"); args.Add(type);
        return RunAsync(exe, args, TimeSpan.FromSeconds(75), ct);
    }

    public static int? DiskNumberOf(string name, string? openError = null)
    {
        if (openError != null)
        {
            var m = Regex.Match(openError, @"PhysicalDrive(\d+)", RegexOptions.IgnoreCase);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        if (!OperatingSystem.IsWindows()) return null;
        var s = Regex.Match(name, @"^/dev/sd([a-z]+)$");
        if (s.Success)
        {
            int n = 0;
            foreach (char c in s.Groups[1].Value) n = n * 26 + (c - 'a' + 1);
            return n - 1;
        }
        var pd = Regex.Match(name, @"^/dev/pd(\d+)$");
        return pd.Success ? int.Parse(pd.Groups[1].Value) : null;
    }

    // ------------------------------------------------------------------ parsing

    /// <summary>True when smartctl says the disk is in standby and it left it alone.</summary>
    public static bool IsAsleep(JsonElement root) =>
        Str(root, "model_name") == null && Messages(root).Any(m => Regex.IsMatch(m, @"\b(STANDBY|SLEEP|IDLE)\b", RegexOptions.IgnoreCase));

    public static IEnumerable<string> Messages(JsonElement root)
    {
        if (Get(root, "smartctl", "messages") is { ValueKind: JsonValueKind.Array } msgs)
            foreach (var m in msgs.EnumerateArray())
                if (Str(m, "string") is string s) yield return s;
    }

    public static DriveReading Parse(string json, DateTimeOffset at)
    {
        using var doc = JsonDocument.Parse(json);
        return Parse(doc.RootElement, at);
    }

    public static DriveReading Parse(JsonElement j, DateTimeOffset at)
    {
        var r = new DriveReading { At = at, Source = "smartctl" };
        r.Device = Str(j, "device", "name");
        r.DeviceType = Str(j, "device", "type");
        r.Protocol = Str(j, "device", "protocol");
        r.Model = Str(j, "model_name") ?? Str(j, "scsi_model_name");
        r.Family = Str(j, "model_family");
        r.Serial = Str(j, "serial_number")?.Trim();
        r.Firmware = Str(j, "firmware_version") ?? Str(j, "scsi_revision");
        r.Capacity = Long(j, "user_capacity", "bytes") ?? Long(j, "nvme_total_capacity");
        r.InDriveDatabase = Get(j, "in_smartctl_database") is { ValueKind: JsonValueKind.True };
        r.Rpm = Int(j, "rotation_rate");
        bool nvme = string.Equals(r.Protocol, "NVMe", StringComparison.OrdinalIgnoreCase);
        bool spinning = r.Rpm is > 1;
        r.Media = nvme || r.Rpm == 0 ? "SSD" : spinning ? "HDD" : null;

        r.SmartPassed = Get(j, "smart_status", "passed") is { } p && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : null;
        r.PowerOnHours = Long(j, "power_on_time", "hours");
        r.PowerCycles = Long(j, "power_cycle_count");
        int? temp = Int(j, "temperature", "current");
        r.Temp = temp is > 0 and < 150 ? temp : null;
        r.TempLimit = Int(j, "temperature", "op_limit_max") is int lim and > 0 ? lim : null;
        r.TempLifetimeMax = Int(j, "temperature", "lifetime_max") is int lm and > 0 ? lm : null;
        if (!spinning)
        {
            r.PercentUsed = Int(j, "endurance_used", "current_percent");
            r.Spare = Int(j, "spare_available", "current_percent");
            r.SpareThreshold = Int(j, "spare_available", "threshold_percent");
        }

        if (Get(j, "nvme_smart_health_information_log") is { ValueKind: JsonValueKind.Object } nv)
        {
            r.CriticalWarning = Int(nv, "critical_warning");
            r.PercentUsed ??= Int(nv, "percentage_used");
            r.Spare ??= Int(nv, "available_spare");
            r.SpareThreshold ??= Int(nv, "available_spare_threshold");
            // an NVMe "data unit" is 1000 sectors of 512 bytes
            if (Long(nv, "data_units_written") is long w) r.BytesWritten = w * 512_000;
            if (Long(nv, "data_units_read") is long rd) r.BytesRead = rd * 512_000;
            r.UnsafeShutdowns = Long(nv, "unsafe_shutdowns");
            r.MediaErrors = Long(nv, "media_errors");
            r.ErrorLogEntries = Long(nv, "num_err_log_entries");
            r.WarningTempMinutes = Long(nv, "warning_temp_time");
            r.CriticalTempMinutes = Long(nv, "critical_comp_time");
            r.PowerOnHours ??= Long(nv, "power_on_hours");
            r.PowerCycles ??= Long(nv, "power_cycles");
            r.Temp ??= Int(nv, "temperature");
        }

        if (Get(j, "ata_smart_attributes", "table") is { ValueKind: JsonValueKind.Array } table)
        {
            r.Attributes = new List<AtaAttribute>();
            foreach (var a in table.EnumerateArray())
            {
                var attr = new AtaAttribute
                {
                    Id = Int(a, "id") ?? 0,
                    Name = Str(a, "name") ?? "",
                    Value = Int(a, "value") ?? 0,
                    Worst = Int(a, "worst") ?? 0,
                    Thresh = Int(a, "thresh"),
                    Raw = Long(a, "raw", "value") ?? 0,
                    RawText = Str(a, "raw", "string"),
                    WhenFailed = Str(a, "when_failed") is string wf && wf.Length > 0 ? wf : null,
                };
                r.Attributes.Add(attr);
            }
            ApplyAttributes(r);
        }

        // ATA device statistics: the standard place for sectors written/read on newer drives
        if (Get(j, "ata_device_statistics", "pages") is { ValueKind: JsonValueKind.Array } pages)
        {
            long block = Long(j, "logical_block_size") ?? 512;
            foreach (var page in pages.EnumerateArray())
            {
                if (Get(page, "table") is not { ValueKind: JsonValueKind.Array } rows) continue;
                foreach (var row in rows.EnumerateArray())
                {
                    if (Get(row, "valid") is { ValueKind: JsonValueKind.False }) continue;
                    long? v = Long(row, "value");
                    switch (Str(row, "name"))
                    {
                        case "Logical Sectors Written" when v is long sw: r.BytesWritten = sw * block; r.WriteUnits = null; r.WriteUnit = null; break;
                        case "Logical Sectors Read" when v is long sr: r.BytesRead = sr * block; break;
                        case "Percentage Used Endurance Indicator" when v is long pu && !spinning: r.PercentUsed ??= (int)pu; break;
                        case "Highest Temperature" when v is long ht && ht > 0: r.TempLifetimeMax ??= (int)ht; break;
                        case "Number of Reported Uncorrectable Errors" when v is long ue: r.Uncorrectable ??= ue; break;
                    }
                }
            }
        }

        r.AtaErrors = Long(j, "ata_smart_error_log", "extended", "count") ?? Long(j, "ata_smart_error_log", "summary", "count");

        // most recent self-test that finished one way or the other (an aborted one says nothing)
        foreach (var tablePath in new[] { new[] { "ata_smart_self_test_log", "extended", "table" }, new[] { "ata_smart_self_test_log", "standard", "table" } })
        {
            if (r.SelfTest != null || Get(j, tablePath) is not { ValueKind: JsonValueKind.Array } tests) continue;
            foreach (var t in tests.EnumerateArray())
            {
                if (Get(t, "status", "passed") is not { } pass || pass.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                r.SelfTest = $"{Str(t, "type", "string")}: {Str(t, "status", "string")}";
                r.SelfTestPassed = pass.GetBoolean();
                r.SelfTestHours = Long(t, "lifetime_hours");
                break;
            }
        }
        if (r.SelfTest == null && Get(j, "nvme_self_test_log", "table") is { ValueKind: JsonValueKind.Array } nt)
        {
            foreach (var t in nt.EnumerateArray())
            {
                int? result = Int(t, "self_test_result", "value");
                // 1-4, 8, 9: aborted (by a command, a reset, a removed namespace, a format, an unknown reason, a sanitize);
                // 5-7: failed segments; 0: passed; 0xF: unused entry
                if (result is null or 1 or 2 or 3 or 4 or 8 or 9 or > 9) continue;
                r.SelfTest = $"{Str(t, "self_test_code", "string")}: {Str(t, "self_test_result", "string")}";
                r.SelfTestPassed = result == 0;
                r.SelfTestHours = Long(t, "power_on_hours");
                break;
            }
        }

        r.Status = Classify(j, r, out string? note);
        r.Note = note;
        return r;
    }

    static string Classify(JsonElement j, DriveReading r, out string? note)
    {
        note = null;
        if (IsAsleep(j)) { note = "The disk was asleep and was left that way."; return "asleep"; }
        int exit = Int(j, "smartctl", "exit_status") ?? 0;
        var msgs = Messages(j).ToList();
        if ((exit & 2) != 0 && r.Model == null)
        {
            note = msgs.FirstOrDefault() ?? "smartctl could not open the drive";
            return note.Contains("Error=5") || note.Contains("ermission") ? "needs-admin" : "error";
        }
        if (Get(j, "smart_support", "available") is { ValueKind: JsonValueKind.False } && r.PowerOnHours == null)
        {
            note = "The drive or its USB adapter doesn't pass SMART readings through.";
            return "no-smart";
        }
        if (r.PowerOnHours == null && r.SmartPassed == null) { note = msgs.FirstOrDefault(); return "partial"; }
        return "ok";
    }

    /// <summary>Picks the counters that matter out of the ATA attribute table, by name first (smartctl's drive database names them) and by number second.</summary>
    static void ApplyAttributes(DriveReading r)
    {
        AtaAttribute? ById(params int[] ids) => r.Attributes!.FirstOrDefault(a => ids.Contains(a.Id));
        AtaAttribute? ByName(params string[] names) => r.Attributes!.FirstOrDefault(a => names.Contains(a.Name, StringComparer.OrdinalIgnoreCase));

        r.Reallocated = ById(5)?.Raw;
        r.Pending = ById(197)?.Raw;
        // 198 counts surface defects the drive's own scan found; 187 counts reads it could not correct
        long? offline = ById(198)?.Raw, reported = ById(187)?.Raw;
        if (offline != null || reported != null) r.Uncorrectable = (offline ?? 0) + (reported ?? 0);
        r.CrcErrors = ById(199)?.Raw;
        r.LoadCycles = (ByName("Load_Cycle_Count") ?? ById(193))?.Raw;
        r.StartStops = ById(4)?.Raw;
        r.UnsafeShutdowns = ByName("Unexpect_Power_Loss_Ct", "Unsafe_Shutdown_Count", "POR_Recovery_Count", "Unexpected_Power_Loss", "Power-Off_Retract_Count")?.Raw;

        // data written: the unit is in the attribute's name when smartctl knows the drive
        (string Name, long Unit)[] writes =
        {
            ("Host_Writes_32MiB", 32L << 20), ("Host_Writes_GiB", 1L << 30), ("Total_Writes_GiB", 1L << 30), ("Lifetime_Writes_GiB", 1L << 30),
            ("Host_Writes_MiB", 1L << 20), ("Total_Host_Writes_GiB", 1L << 30), ("Total_LBAs_Written", 512),
        };
        (string Name, long Unit)[] reads =
        {
            ("Host_Reads_32MiB", 32L << 20), ("Host_Reads_GiB", 1L << 30), ("Total_Reads_GiB", 1L << 30), ("Lifetime_Reads_GiB", 1L << 30),
            ("Host_Reads_MiB", 1L << 20), ("Total_LBAs_Read", 512),
        };
        foreach (var (name, unit) in writes)
        {
            if (ByName(name) is not { } a) continue;
            // a drive smartctl doesn't know still gets the default name "Total_LBAs_Written", whatever unit it counts in
            if (name == "Total_LBAs_Written" && !r.InDriveDatabase) { r.WriteUnits = a.Raw; r.WriteUnit = "unknown"; break; }
            r.BytesWritten = a.Raw * unit;
            break;
        }
        foreach (var (name, unit) in reads)
        {
            if (ByName(name) is not { } a) continue;
            if (name == "Total_LBAs_Read" && !r.InDriveDatabase) break;
            r.BytesRead = a.Raw * unit;
            break;
        }
    }

    // ------------------------------------------------------------------ JSON helpers

    public static JsonElement? Get(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e;
    }

    public static string? Str(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.String } s ? s.GetString() : null;

    public static long? Long(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt64(out long v) ? v : null;

    public static int? Int(JsonElement e, params string[] path) =>
        Get(e, path) is { ValueKind: JsonValueKind.Number } n && n.TryGetInt32(out int v) ? v : null;
}
