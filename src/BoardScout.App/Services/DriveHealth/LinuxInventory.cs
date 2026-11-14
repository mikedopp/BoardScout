using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>
/// What Linux knows without asking the drives: lsblk for disks, buses, serials and mount points (through LVM, RAID
/// and partitions down to the disk underneath), statvfs for free space, and the drive temperature the kernel
/// publishes in sysfs (NVMe always, SATA with the drivetemp module). Works without root; SMART itself needs root.
/// </summary>
public static class LinuxInventory
{
    const string Columns = "NAME,PATH,PKNAME,TYPE,MODEL,VENDOR,SERIAL,SIZE,ROTA,TRAN,MOUNTPOINTS,FSTYPE,LABEL";
    static readonly HashSet<string> SkipMounts = new(StringComparer.Ordinal) { "/boot", "/boot/efi", "/efi", "[SWAP]" };

    public static async Task<Inventory> ReadAsync(CancellationToken ct = default)
    {
        var inv = new Inventory { Source = "linux", Elevated = Environment.IsPrivilegedProcess };
        try
        {
            var run = await Smartctl.RunAsync("lsblk", new[] { "-J", "-b", "-o", Columns }, TimeSpan.FromSeconds(30), ct);
            // util-linux before 2.37 knows MOUNTPOINT (one) but not MOUNTPOINTS (all)
            if (run.Exit != 0 || string.IsNullOrWhiteSpace(run.Output))
                run = await Smartctl.RunAsync("lsblk", new[] { "-J", "-b", "-o", Columns.Replace("MOUNTPOINTS", "MOUNTPOINT") }, TimeSpan.FromSeconds(30), ct);
            if (run.TimedOut || string.IsNullOrWhiteSpace(run.Output)) { inv.Error = "lsblk returned nothing"; return inv; }
            Parse(run.Output, inv, Inventory.SpaceOf, TempOf);
        }
        catch (Exception ex) { inv.Error = "lsblk: " + ex.Message; }
        return inv;
    }

    /// <summary>lsblk -J -b output into disks and volumes. Space and temperature come from callers, so tests can feed them.</summary>
    public static void Parse(string json, Inventory inv, Func<string, (long Size, long Free)?> space, Func<string, int?>? temp = null)
    {
        inv.Source = "linux";
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("blockdevices", out var devs) || devs.ValueKind != JsonValueKind.Array) return;
        int number = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var d in devs.EnumerateArray())
        {
            string name = Smartctl.Str(d, "name") ?? "";
            long size = Num(d, "size");
            if (Smartctl.Str(d, "type") != "disk" || size <= 0 || name.StartsWith("zram", StringComparison.Ordinal) || name.StartsWith("ram", StringComparison.Ordinal)) continue;
            var disk = new Inventory.Disk
            {
                Number = number++,
                Path = Smartctl.Str(d, "path") ?? "/dev/" + name,
                Name = WindowsInventory.Blank(Smartctl.Str(d, "model")) ?? WindowsInventory.Blank(Smartctl.Str(d, "vendor")) ?? name,
                Serial = WindowsInventory.Blank(Smartctl.Str(d, "serial")),
                Size = size,
                Media = Flag(d, "rota") ? "HDD" : "SSD",
                Bus = BusOf(Smartctl.Str(d, "tran"), name),
            };
            if (temp?.Invoke(name) is int t) { disk.Temp = t; disk.HasCounters = true; }
            inv.Disks.Add(disk);

            foreach (var part in SelfAndBelow(d))
                foreach (var mp in Mounts(part))
                {
                    if (SkipMounts.Contains(mp) || mp.StartsWith("/snap/", StringComparison.Ordinal) || !seen.Add(mp)) continue;
                    if (space(mp) is not (long sz, long free)) continue;
                    inv.Volumes.Add(new Inventory.Volume
                    {
                        Letter = mp, Label = WindowsInventory.Blank(Smartctl.Str(part, "label")),
                        FileSystem = WindowsInventory.Blank(Smartctl.Str(part, "fstype")), Size = sz, Free = free, Disk = disk.Number,
                    });
                }
        }
    }

    static IEnumerable<JsonElement> SelfAndBelow(JsonElement d)
    {
        yield return d;
        if (d.TryGetProperty("children", out var kids) && kids.ValueKind == JsonValueKind.Array)
            foreach (var k in kids.EnumerateArray())
                foreach (var x in SelfAndBelow(k)) yield return x;
    }

    static IEnumerable<string> Mounts(JsonElement e)
    {
        if (e.TryGetProperty("mountpoints", out var mps) && mps.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in mps.EnumerateArray())
                if (m.ValueKind == JsonValueKind.String && m.GetString() is string s && s.Length > 0) yield return s;
        }
        else if (Smartctl.Str(e, "mountpoint") is string one && one.Length > 0) yield return one;
    }

    static long Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetInt64(),
            JsonValueKind.String => long.TryParse(v.GetString(), out var l) ? l : 0,
            _ => 0,
        } : 0;

    // lsblk 2.33+ writes rota as true/false; older versions as "1"/"0"
    static bool Flag(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && v.GetString() == "1") ||
                                             (v.ValueKind == JsonValueKind.Number && v.GetInt32() == 1));

    internal static string? BusOf(string? tran, string name) => (tran?.ToLowerInvariant()) switch
    {
        "sata" or "ata" => "SATA",
        "nvme" => "NVMe",
        "usb" => "USB",
        "sas" => "SAS",
        "scsi" or "spi" => "SCSI",
        "mmc" => "SD",
        "virtio" => "Virtual",
        null or "" => name.StartsWith("nvme", StringComparison.Ordinal) ? "NVMe" : name.StartsWith("mmcblk", StringComparison.Ordinal) ? "SD"
                      : name.StartsWith("vd", StringComparison.Ordinal) ? "Virtual" : null,
        var other => other.ToUpperInvariant(),
    };

    /// <summary>The temperature the kernel publishes for this disk, in °C, without root.</summary>
    internal static int? TempOf(string name)
    {
        var places = new List<string>();
        var nvme = Regex.Match(name, @"^(nvme\d+)n\d+$");
        if (nvme.Success) places.Add("/sys/class/nvme/" + nvme.Groups[1].Value);
        places.Add($"/sys/block/{name}/device/hwmon");
        places.Add($"/sys/block/{name}/device");
        foreach (var dir in places)
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var h in Directory.EnumerateDirectories(dir, "hwmon*"))
                {
                    string f = Path.Combine(h, "temp1_input");
                    if (File.Exists(f) && long.TryParse(File.ReadAllText(f).Trim(), out long milli) && milli > 0 && milli < 150_000)
                        return (int)Math.Round(milli / 1000.0);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return null;
    }
}
