using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>
/// What macOS knows without asking the drives: diskutil for the physical disks (name, size, bus, SSD or not, and
/// its own SMART verdict) and which volumes live on each, through APFS containers to the disk underneath.
/// </summary>
public static class MacInventory
{
    const string Diskutil = "/usr/sbin/diskutil";

    public static async Task<Inventory> ReadAsync(CancellationToken ct = default)
    {
        var inv = new Inventory { Source = "macos", Elevated = Environment.IsPrivilegedProcess };
        try
        {
            var physical = await Smartctl.RunAsync(Diskutil, new[] { "list", "-plist", "physical" }, TimeSpan.FromSeconds(30), ct);
            var all = await Smartctl.RunAsync(Diskutil, new[] { "list", "-plist" }, TimeSpan.FromSeconds(30), ct);
            var infos = new Dictionary<string, string>();
            foreach (var id in WholeDisks(physical.Output))
            {
                var info = await Smartctl.RunAsync(Diskutil, new[] { "info", "-plist", id }, TimeSpan.FromSeconds(30), ct);
                if (!info.TimedOut) infos[id] = info.Output;
            }
            Parse(physical.Output, all.Output, infos, inv, Inventory.SpaceOf);
        }
        catch (Exception ex) { inv.Error = "diskutil: " + ex.Message; }
        return inv;
    }

    static List<string> WholeDisks(string physicalList) =>
        (Plist.Parse(physicalList) as Dictionary<string, object?>).Strings("WholeDisks").ToList();

    /// <summary>
    /// diskutil list -plist physical, diskutil list -plist, and diskutil info -plist for each physical disk.
    /// Only "/" and /Volumes/* are kept as volumes: the rest of an APFS container (Data, Preboot, VM) shares its space.
    /// </summary>
    public static void Parse(string physicalList, string fullList, IReadOnlyDictionary<string, string> infos, Inventory inv,
                             Func<string, (long Size, long Free)?> space)
    {
        inv.Source = "macos";
        foreach (var id in WholeDisks(physicalList))
        {
            infos.TryGetValue(id, out var xml);
            var info = xml == null ? null : Plist.Parse(xml) as Dictionary<string, object?>;
            inv.Disks.Add(new Inventory.Disk
            {
                Number = int.TryParse(Regex.Match(id, @"\d+").Value, out var n) ? n : inv.Disks.Count,
                Path = "/dev/" + id,
                Name = info.Str("MediaName") ?? info.Str("IORegistryEntryName") ?? id,
                Size = info.Long("TotalSize") ?? info.Long("Size") ?? 0,
                Media = info.Bool("SolidState") switch { true => "SSD", false => "HDD", _ => null },
                Bus = BusOf(info.Str("BusProtocol")),
                Health = info.Str("SMARTStatus") switch { "Verified" => "Healthy", "Failing" => "Unhealthy", _ => null },
            });
        }

        var root = Plist.Parse(fullList) as Dictionary<string, object?>;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in root.Dicts("AllDisksAndPartitions"))
        {
            // an APFS container is a synthesized disk; its volumes live on the physical disk(s) holding its stores
            var stores = entry.Dicts("APFSPhysicalStores").Select(s => WholeOf(s.Str("DeviceIdentifier"))).Where(s => s != null).ToList();
            var owners = stores.Count > 0 ? stores : new List<string?> { WholeOf(entry.Str("DeviceIdentifier")) };
            int diskNo = inv.Disks.FirstOrDefault(d => owners.Contains(d.Path![5..]))?.Number ?? -1;
            var vols = entry.Dicts("APFSVolumes").Concat(entry.Dicts("Partitions")).Append(entry);
            foreach (var v in vols)
            {
                // the sealed system volume is mounted at / as a snapshot; its Data sibling shares the same space
                string? mp = v.Str("MountPoint") ?? v.Dicts("MountedSnapshots").Select(s => s.Str("SnapshotMountPoint")).FirstOrDefault(s => s != null);
                if (mp == "/System/Volumes/Data") mp = "/";
                if (mp == null || !(mp == "/" || mp.StartsWith("/Volumes/", StringComparison.Ordinal)) || !seen.Add(mp)) continue;
                if (space(mp) is not (long sz, long free)) continue;
                inv.Volumes.Add(new Inventory.Volume
                {
                    Letter = mp, Label = v.Str("VolumeName") is string vn ? vn.Replace(" - Data", "") : null,
                    FileSystem = stores.Count > 0 ? "APFS" : FileSystemOf(v.Str("Content")),
                    Size = sz, Free = free, Disk = diskNo,
                });
            }
        }
    }

    static string? WholeOf(string? id) => id == null ? null : Regex.Match(id, @"^(disk\d+)").Groups[1].Value is { Length: > 0 } w ? w : null;

    static string? FileSystemOf(string? content) => content switch
    {
        "Apple_HFS" or "Apple_HFSX" => "HFS+",
        "Windows_NTFS" => "NTFS",
        "DOS_FAT_32" => "FAT32",
        "Apple_APFS" => "APFS",
        _ => null,
    };

    internal static string? BusOf(string? protocol) => protocol switch
    {
        null or "" => null,
        "Apple Fabric" => "Internal",
        "PCI-Express" or "PCI" => "PCIe",
        var p => p,
    };
}
