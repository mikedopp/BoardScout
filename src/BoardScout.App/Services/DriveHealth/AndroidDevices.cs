using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>
/// Phones, tablets and TVs. Their flash (eMMC or UFS) keeps no SMART log, but the chip estimates its own life in
/// 10% steps and its spare-block level, and Linux counts what was written since the device started. Two ways in:
/// over adb from this computer (<see cref="ProbeAsync"/>), or the device's own app reporting to the hub
/// (<see cref="FromApp"/>). Either way a reading is one <see cref="DriveReading"/> plus the device's volumes.
/// </summary>
public static class AndroidDevices
{
    public sealed record Device(string Serial, string State, string? Model);

    public sealed class Probe
    {
        public DriveReading Reading { get; set; } = new();
        public List<VolumeReading> Volumes { get; } = new();
    }

    // ------------------------------------------------------------------ adb

    public static string? LocateAdb(string? configured = null)
    {
        string exe = OperatingSystem.IsWindows() ? "adb.exe" : "adb";
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured!);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            candidates.Add(Path.Combine(dir.Trim('"'), exe));
        foreach (var v in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
            if (Environment.GetEnvironmentVariable(v) is string sdk && sdk.Length > 0) candidates.Add(Path.Combine(sdk, "platform-tools", exe));
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", exe));
        else if (OperatingSystem.IsMacOS())
            candidates.AddRange(new[] { Path.Combine(home, "Library", "Android", "sdk", "platform-tools", exe), "/opt/homebrew/bin/adb", "/usr/local/bin/adb" });
        else
            candidates.AddRange(new[] { Path.Combine(home, "Android", "Sdk", "platform-tools", exe), "/usr/bin/adb", "/usr/lib/android-sdk/platform-tools/adb" });
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return Path.GetFullPath(c); } catch { /* a bad PATH entry */ }
        }
        return null;
    }

    /// <summary>adb devices -l: every device adb knows, with its state (device, unauthorized, offline).</summary>
    public static async Task<List<Device>> ListAsync(string adb, CancellationToken ct = default)
    {
        var run = await Smartctl.RunAsync(adb, new[] { "devices", "-l" }, TimeSpan.FromSeconds(20), ct);
        return run.TimedOut ? new() : ParseDevices(run.Output);
    }

    public static List<Device> ParseDevices(string output)
    {
        var list = new List<Device>();
        foreach (var raw in output.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("List of devices", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            var parts = Regex.Split(line, @"\s+");
            if (parts.Length < 2) continue;
            string? model = parts.Skip(2).FirstOrDefault(p => p.StartsWith("model:", StringComparison.Ordinal))?[6..].Replace('_', ' ');
            list.Add(new Device(parts[0], parts[1], model));
        }
        return list;
    }

    /// <summary>adb connect host:port, once. A TV that is off just isn't read this pass.</summary>
    public static async Task<bool> ConnectAsync(string adb, string address, CancellationToken ct = default)
    {
        var run = await Smartctl.RunAsync(adb, new[] { "connect", address }, TimeSpan.FromSeconds(12), ct);
        return !run.TimedOut && Regex.IsMatch(run.Output, @"\b(connected to|already connected)\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// One adb shell call that only reads: properties, the flash chip's life estimates, block sizes, what was written
    /// since boot, free space on /data, and storaged's last note about the chip. Nothing on the device changes.
    /// </summary>
    internal const string ProbeScript = """
        echo "manufacturer=$(getprop ro.product.manufacturer)"
        echo "model=$(getprop ro.product.model)"
        echo "name=$(settings get global device_name 2>/dev/null)"
        echo "serial=$(getprop ro.serialno)"
        echo "android=$(getprop ro.build.version.release)"
        echo "bootdevice=$(getprop ro.boot.bootdevice)"
        echo "boot_id=$(cat /proc/sys/kernel/random/boot_id 2>/dev/null)"
        for f in /sys/block/mmcblk0/device/life_time /sys/block/mmcblk0/device/pre_eol_info /sys/block/mmcblk0/device/type; do [ -r "$f" ] && echo "$f=$(cat "$f" 2>/dev/null)"; done
        for d in /sys/devices/platform/*/health_descriptor /sys/devices/platform/*/*/health_descriptor; do [ -d "$d" ] || continue; for n in life_time_estimation_a life_time_estimation_b eol_info; do [ -r "$d/$n" ] && echo "$d/$n=$(cat "$d/$n" 2>/dev/null)"; done; break; done
        for b in /sys/block/mmcblk[0-9] /sys/block/sd[a-z]; do [ -r "$b/size" ] && echo "size:${b##*/}=$(cat "$b/size")"; done
        echo "##partitions"
        cat /proc/partitions 2>/dev/null
        echo "##diskstats"
        cat /proc/diskstats 2>/dev/null
        echo "##df"
        df -k /data 2>/dev/null
        echo "##storaged"
        logcat -b events -d -s storaged_emmc_info 2>/dev/null | tail -n 1
        """;

    public static async Task<string?> ProbeAsync(string adb, string serial, CancellationToken ct = default)
    {
        var run = await Smartctl.RunAsync(adb, new[] { "-s", serial, "shell", ProbeScript.Replace("\r", "") }, TimeSpan.FromSeconds(40), ct);
        return run.TimedOut || string.IsNullOrWhiteSpace(run.Output) ? null : run.Output;
    }

    /// <summary>What <see cref="ProbeScript"/> printed, as a reading and the /data volume.</summary>
    public static Probe ParseProbe(string output, DateTimeOffset at)
    {
        var kv = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new Dictionary<string, List<string>>();
        List<string>? into = null;
        foreach (var raw in output.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("##", StringComparison.Ordinal)) { into = sections[raw[2..].Trim()] = new(); continue; }
            if (into != null) { into.Add(raw); continue; }
            int eq = raw.IndexOf('=');
            if (eq > 0) kv[raw[..eq].Trim()] = raw[(eq + 1)..].Trim();
        }

        var probe = new Probe();
        var r = probe.Reading;
        r.At = at;
        r.Source = "adb";
        string manufacturer = Pretty(kv.GetValueOrDefault("manufacturer"));
        string model = kv.GetValueOrDefault("model") ?? "";
        r.Model = (manufacturer + " " + model).Trim() is { Length: > 0 } m ? m : "Android device";
        r.Serial = Store.UsableSerial(kv.GetValueOrDefault("serial")) ? kv["serial"] : null;
        r.OsVersion = Blank(kv.GetValueOrDefault("android"));
        r.BootId = Blank(kv.GetValueOrDefault("boot_id"));
        r.Media = "SSD";

        // block devices: mmcblk0 is eMMC; sda, sdb ... are the LUNs of a UFS chip
        var sizes = kv.Where(p => p.Key.StartsWith("size:", StringComparison.Ordinal))
                      .ToDictionary(p => p.Key[5..], p => long.TryParse(p.Value, out var s) ? s * 512 : 0);
        // some builds close /sys/block/*/size to the shell (Android 17 on a Galaxy S23); /proc/partitions lists the same disks in 1 KiB blocks
        if (sections.TryGetValue("partitions", out var parts))
            foreach (var line in parts)
            {
                var f = Regex.Split(line.Trim(), @"\s+");
                if (f.Length == 4 && Regex.IsMatch(f[3], @"^(mmcblk\d+|sd[a-z]+)$") && long.TryParse(f[2], out long kb) && kb > 0)
                    sizes.TryAdd(f[3], kb * 1024);
            }
        bool emmc = sizes.ContainsKey("mmcblk0") && !(kv.GetValueOrDefault("bootdevice") ?? "").Contains("ufs", StringComparison.OrdinalIgnoreCase);
        bool ufs = !emmc && (sizes.Keys.Any(k => k.StartsWith("sd", StringComparison.Ordinal)) || kv.Keys.Any(k => k.Contains("health_descriptor")));
        r.Bus = r.Protocol = emmc ? "eMMC" : ufs ? "UFS" : null;
        r.Capacity = emmc ? sizes["mmcblk0"] : ufs ? sizes.Where(p => p.Key.StartsWith("sd", StringComparison.Ordinal)).Select(p => p.Value).DefaultIfEmpty(0).Max() : null;
        if (r.Capacity <= 0) r.Capacity = null;

        // the chip's own estimates
        Wear(kv.Where(p => p.Key.StartsWith("/sys/", StringComparison.Ordinal)), r);
        if (r.FlashLifeA == null && r.FlashLifeB == null && sections.TryGetValue("storaged", out var st))
        {
            // storaged_emmc_info: [version, pre-EOL, life A, life B]
            var mm = Regex.Match(string.Join("\n", st), @"storaged_emmc_info\s*:\s*\[(\d+),(\d+),(\d+),(\d+)\]");
            if (mm.Success)
            {
                r.FlashPreEol ??= Level(int.Parse(mm.Groups[2].Value), 3);
                r.FlashLifeA = Level(int.Parse(mm.Groups[3].Value), 11);
                r.FlashLifeB = Level(int.Parse(mm.Groups[4].Value), 11);
            }
        }
        r.PercentUsed = PercentOf(r);

        // written since this start: sectors (always 512 bytes in diskstats) on the chip's whole-disk devices
        if (sections.TryGetValue("diskstats", out var ds))
        {
            long sectors = 0; bool any = false;
            foreach (var line in ds)
            {
                var f = Regex.Split(line.Trim(), @"\s+");
                if (f.Length < 10) continue;
                bool whole = emmc ? f[2] == "mmcblk0" : Regex.IsMatch(f[2], "^sd[a-z]+$");
                if (whole && long.TryParse(f[9], out long w)) { sectors += w; any = true; }
            }
            if (any) r.WrittenSinceBoot = sectors * 512;
        }

        r.Status = r.PercentUsed != null || r.FlashPreEol != null ? "ok" : "partial";
        if (r.Status == "partial")
            r.Note = "This device doesn't show its storage chip's wear to adb; free space and what it writes are recorded.";
        r.Key = Store.KeyFor(r.Model, r.Serial, r.Capacity);

        string name = Blank(kv.GetValueOrDefault("name")) is string n && n != "null" ? n : r.Model + (r.Serial is string s2 && s2.Length >= 4 ? " " + s2[^4..] : "");
        r.Machine = name;
        // df names the mount /data on most devices and /data/user/0 on some (Android 17 on a Galaxy S23)
        if (sections.TryGetValue("df", out var df) && df.LastOrDefault(l => Regex.Split(l.Trim(), @"\s+") is { Length: >= 6 } c
                && (c[5] == "/data" || c[5].StartsWith("/data/", StringComparison.Ordinal))) is string row)
        {
            var f = Regex.Split(row.Trim(), @"\s+");
            if (f.Length >= 6 && long.TryParse(f[1], out long totalK) && long.TryParse(f[3], out long freeK) && totalK > 0)
            {
                probe.Volumes.Add(new VolumeReading { At = at, Letter = name, Label = "Internal storage", Size = totalK * 1024, Free = freeK * 1024, DriveKey = r.Key });
                r.Letters = new[] { name };
            }
        }
        return probe;
    }

    // ------------------------------------------------------------------ the device's own app (through the hub)

    /// <summary>A reading the device's app sent to the hub (schema 1). Throws FormatException on anything else.</summary>
    public static Probe FromApp(JsonElement body, DateTimeOffset received)
    {
        if (body.ValueKind != JsonValueKind.Object || Smartctl.Int(body, "schema") != 1) throw new FormatException("Not a schema 1 reading.");
        if (!body.TryGetProperty("device", out var dev) || dev.ValueKind != JsonValueKind.Object) throw new FormatException("No device.");
        string id = Smartctl.Str(dev, "id") is string i && Regex.IsMatch(i, @"^[A-Za-z0-9._-]{4,64}$") ? i : throw new FormatException("No device id.");
        DateTimeOffset at = body.TryGetProperty("at", out var a) && a.ValueKind == JsonValueKind.String &&
                            DateTimeOffset.TryParse(a.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : received;
        // a phone with a wrong clock must not write into next year
        if (at > received.AddMinutes(10)) at = received;

        var probe = new Probe();
        var r = probe.Reading;
        r.At = at;
        r.Source = "app";
        r.Model = (Pretty(Smartctl.Str(dev, "manufacturer")) + " " + Smartctl.Str(dev, "model")).Trim() is { Length: > 0 } m ? Cap(m, 80) : "Android device";
        r.OsVersion = Smartctl.Str(dev, "android") is string av ? Cap(av, 20) : null;
        r.Media = "SSD";
        string name = Smartctl.Str(dev, "name") is string dn && dn.Trim().Length > 0 ? Cap(dn.Trim(), 60) : r.Model + " " + id[^4..];
        r.Machine = name;

        if (body.TryGetProperty("io", out var io) && io.ValueKind == JsonValueKind.Object)
        {
            if (Smartctl.Long(io, "sectorsWritten") is long sw && sw >= 0) r.WrittenSinceBoot = sw * 512;
            if (Smartctl.Long(io, "bootCount") is long bc) r.BootId = "count:" + bc;
            string? block = Smartctl.Str(io, "blockDevice");
            r.Bus = r.Protocol = block == null ? null : block.StartsWith("mmcblk", StringComparison.Ordinal) ? "eMMC" : block.StartsWith("sd", StringComparison.Ordinal) ? "UFS" : null;
        }
        if (body.TryGetProperty("wear", out var wear) && wear.ValueKind == JsonValueKind.Object)
            Wear(wear.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => new KeyValuePair<string, string>(p.Name, p.Value.GetString()!)), r);
        r.PercentUsed = PercentOf(r);

        string? letter = null;
        if (body.TryGetProperty("volumes", out var vols) && vols.ValueKind == JsonValueKind.Array)
            foreach (var v in vols.EnumerateArray().Take(8))
            {
                long total = Smartctl.Long(v, "total") ?? 0, free = Smartctl.Long(v, "free") ?? -1;
                if (total <= 0 || free < 0 || free > total) continue;
                bool primary = v.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True;
                string label = Smartctl.Str(v, "label") is string l ? Cap(l, 40) : primary ? "Internal storage" : "Storage";
                string vl = primary ? name : name + ": " + label;
                probe.Volumes.Add(new VolumeReading { At = at, Letter = vl, Label = label, Size = total, Free = free });
                if (primary) { letter = vl; r.Capacity = total; }
            }

        r.Status = r.PercentUsed != null || r.FlashPreEol != null ? "ok" : "partial";
        if (r.Status == "partial")
            r.Note = "Android doesn't let apps read the storage chip's wear; free space and what it writes are recorded.";
        r.Key = Store.KeyFor(r.Model, "app-" + id, null);
        if (letter != null) r.Letters = new[] { letter };
        foreach (var v in probe.Volumes) v.DriveKey = r.Key;
        return probe;
    }

    // ------------------------------------------------------------------ shared

    /// <summary>
    /// Adds what was written since the last reading to the device's running total. A restart starts the kernel's count
    /// again, so what was written between the last reading and the restart is missed: the total runs a little low.
    /// </summary>
    public static void Accumulate(DriveReading r, Store.DeviceNote note)
    {
        if (r.WrittenSinceBoot is not long now) { r.BytesWritten = note.WrittenTotal; return; }
        long total = note.WrittenTotal ?? 0;
        // same start: add the difference. Without a boot id, a count that went down means it started again
        if (note.WrittenSinceBoot is long before && note.BootId == r.BootId && (r.BootId != null || now >= before)) total += Math.Max(0, now - before);
        else total += now;
        note.WrittenTotal = total;
        note.WrittenSinceBoot = now;
        note.BootId = r.BootId;
        r.BytesWritten = total;
    }

    /// <summary>The flash chip's estimates from sysfs (file path → its text), into the reading.</summary>
    internal static void Wear(IEnumerable<KeyValuePair<string, string>> files, DriveReading r)
    {
        foreach (var (path, value) in files)
        {
            string file = path[(path.LastIndexOf('/') + 1)..];
            var nums = Regex.Matches(value, @"0x[0-9A-Fa-f]+|\d+").Select(x => x.Value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? Convert.ToInt32(x.Value[2..], 16) : int.Parse(x.Value, CultureInfo.InvariantCulture)).ToList();
            if (nums.Count == 0) continue;
            switch (file)
            {
                case "life_time" when nums.Count >= 2: // eMMC: "0x01 0x02" = type A, type B
                    r.FlashLifeA ??= Level(nums[0], 11); r.FlashLifeB ??= Level(nums[1], 11); break;
                case "life_time_estimation_a": r.FlashLifeA ??= Level(nums[0], 11); break;
                case "life_time_estimation_b": r.FlashLifeB ??= Level(nums[0], 11); break;
                case "pre_eol_info" or "eol_info": r.FlashPreEol ??= Level(nums[0], 3); break;
            }
        }
    }

    // 0 means "not defined" in both the eMMC and UFS specs
    static int? Level(int v, int max) => v >= 1 && v <= max ? v : null;

    /// <summary>The worse of the two estimates as the lower end of its 10% step (3 = 20-30% → 20); 11 = past its rated life.</summary>
    internal static int? PercentOf(DriveReading r)
    {
        int? worst = new[] { r.FlashLifeA, r.FlashLifeB }.Where(x => x is >= 1).Max();
        return worst is int w ? (w >= 11 ? 100 : (w - 1) * 10) : null;
    }

    static string Pretty(string? s) => string.IsNullOrWhiteSpace(s) ? "" : char.ToUpperInvariant(s.Trim()[0]) + s.Trim()[1..];
    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    static string Cap(string s, int n) => s.Length > n ? s[..n] : s;
}
