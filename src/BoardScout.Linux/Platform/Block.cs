using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardScout.Platform;

internal sealed record BlockPartition(string Name, string DevNumber, long SizeBytes);

/// <summary>A whole disk: NVMe, SATA, USB, SD card, virtual disk, or optical drive.</summary>
internal sealed class BlockDisk
{
    public required string Name { get; init; }
    public required string SysPath { get; init; }
    public string? DevicePath { get; init; }
    public long SizeBytes { get; init; }
    public bool Rotational { get; init; }
    public bool Removable { get; init; }
    public string? Vendor { get; init; }
    public string? Model { get; init; }
    public string? Firmware { get; init; }

    /// <summary>nvme, sata, usb, mmc, virtio, scsi, or optical.</summary>
    public required string Transport { get; init; }

    public string? PciAddress { get; init; }

    /// <summary>The sysfs path of the USB device the disk hangs off, for USB drives.</summary>
    public string? UsbPath { get; init; }

    /// <summary>uas (USB Attached SCSI) or usb-storage (bulk-only), for USB drives.</summary>
    public string? UsbDriver { get; init; }

    public string? CacheType { get; init; }

    /// <summary>A SATA drive's own fastest link and spin speed, from its IDENTIFY data.</summary>
    public double? SataMaxGbps { get; init; }
    public int? Rpm { get; init; }
    public long DiscardMaxBytes { get; init; }
    public string? Scheduler { get; init; }
    public List<BlockPartition> Partitions { get; } = [];
    public int Index { get; set; }
    public double? TemperatureC { get; set; }

    public bool Optical => Transport == "optical";
    public bool Nvme => Transport == "nvme";

    /// <summary>"WDC WD10" + "JPVT-75A1YT0" → "WDC WD10JPVT-75A1YT0"; "ATA" + "ST500LT012-9WS14" → "ST500LT012-9WS14".</summary>
    public string DisplayName => Block.JoinModel(Vendor, Model) ?? (Nvme ? "NVMe SSD" : Optical ? "Optical drive" : Name);

    /// <summary>
    /// Spinning disk or not. NVMe never spins and SATA drives answer for themselves; many USB bridges don't pass
    /// the drive's answer through (the kernel then assumes it spins), so the model number decides there.
    /// </summary>
    public (bool? Spinning, bool FromModel) DriveType()
    {
        if (Nvme) return (false, false);
        if (Transport is "sata" or "virtio" or "mmc") return (Transport == "sata" ? Rotational : false, false);
        var guess = BoardScout.Services.DriveModels.SpinningFromModelNumber(Vendor, Model);
        if (guess is not null) return (guess, true);
        // Bridges that do pass the drive's answer report "not rotational" for SSDs; "rotational" is only the default.
        return Rotational ? (null, false) : (false, false);
    }
}

/// <summary>A mounted filesystem with its space, and the physical disks it lives on.</summary>
internal sealed record Volume(string MountPoint, string Source, string FileSystem, long SizeBytes, long FreeBytes, string? Label,
    IReadOnlyList<string> Disks, bool ReadOnly, bool Lvm);

/// <summary>Counters the kernel keeps per disk since boot (/proc/diskstats).</summary>
internal readonly record struct DiskCounters(long ReadOps, long ReadSectors, long WriteOps, long WriteSectors, long InFlight, long BusyMs);

/// <summary>Disks, partitions, mounts, and counters from /sys/block, /proc/self/mountinfo, and /proc/diskstats.</summary>
internal static partial class Block
{
    private static readonly HashSet<string> RealFileSystems = new(StringComparer.Ordinal)
    {
        "ext2", "ext3", "ext4", "xfs", "btrfs", "vfat", "exfat", "ntfs", "ntfs3", "fuseblk", "f2fs", "zfs", "jfs",
        "reiserfs", "hfsplus", "iso9660", "udf", "bcachefs", "nilfs2"
    };

    public static List<BlockDisk> ReadDisks()
    {
        var disks = new List<BlockDisk>();
        foreach (var name in Sys.Children("/sys/block"))
        {
            if (SkippedDisk().IsMatch(name)) continue;
            var path = Sys.Real($"/sys/block/{name}") ?? $"/sys/block/{name}";
            var device = Sys.Real(Path.Combine(path, "device"));
            if (device is null) continue;
            var optical = name.StartsWith("sr", StringComparison.Ordinal);
            var transport = optical ? "optical"
                : path.Contains("/nvme/", StringComparison.Ordinal) ? "nvme"
                : path.Contains("/usb", StringComparison.Ordinal) ? "usb"
                : path.Contains("/mmc_host/", StringComparison.Ordinal) || name.StartsWith("mmcblk", StringComparison.Ordinal) ? "mmc"
                : path.Contains("/virtio", StringComparison.Ordinal) ? "virtio"
                : Regex.IsMatch(path, @"/ata\d+/") ? "sata"
                : "scsi";
            if (transport == "mmc" && name.Contains("boot", StringComparison.Ordinal)) continue;

            string? usbPath = null, usbDriver = null;
            if (transport == "usb")
            {
                var parts = path.Split('/');
                var index = Array.FindLastIndex(parts, p => UsbDeviceName().IsMatch(p));
                if (index > 0)
                {
                    usbPath = string.Join('/', parts[..(index + 1)]);
                    if (index + 1 < parts.Length && parts[index + 1].Contains(':'))
                        usbDriver = Sys.LinkName(Path.Combine(usbPath, parts[index + 1], "driver"));
                }
            }

            // SATA drives publish their IDENTIFY data: full firmware revision, fastest link, and spin speed.
            var identity = transport == "sata" && KernelInfo.AtaPortOf(path) is { } port ? KernelInfo.AtaIdentify(port) : null;
            var disk = new BlockDisk
            {
                Name = name,
                SysPath = path,
                DevicePath = device,
                SizeBytes = (Sys.Long(Path.Combine(path, "size")) ?? 0) * 512,
                Rotational = Sys.Read(Path.Combine(path, "queue", "rotational")) == "1",
                Removable = Sys.Read(Path.Combine(path, "removable")) == "1",
                Vendor = Sys.Text(Path.Combine(device, "vendor")),
                Model = Sys.Text(Path.Combine(device, "model")),
                Firmware = identity?.Firmware ?? Sys.Text(Path.Combine(device, "firmware_rev")) ?? Sys.Text(Path.Combine(device, "rev")) ?? Sys.Text(Path.Combine(device, "fwrev")),
                SataMaxGbps = identity?.MaxGbps,
                Rpm = identity?.Rpm,
                Transport = transport,
                PciAddress = Pci.AddressIn(path),
                UsbPath = usbPath,
                UsbDriver = usbDriver,
                CacheType = Sys.Children(Path.Combine(device, "scsi_disk")).Select(d => Sys.Read(Path.Combine(device, "scsi_disk", d, "cache_type"))).FirstOrDefault(c => c is not null),
                DiscardMaxBytes = Sys.Long(Path.Combine(path, "queue", "discard_max_bytes")) ?? 0,
                Scheduler = Bracketed(Sys.Read(Path.Combine(path, "queue", "scheduler")))
            };
            // An SD card reader with no card in it reports a size of zero.
            if (disk.SizeBytes == 0 && !optical) continue;
            foreach (var child in Sys.Children(path))
            {
                var partition = Path.Combine(path, child);
                if (!File.Exists(Path.Combine(partition, "partition"))) continue;
                disk.Partitions.Add(new BlockPartition(child, Sys.Read(Path.Combine(partition, "dev")) ?? "", (Sys.Long(Path.Combine(partition, "size")) ?? 0) * 512));
            }
            disks.Add(disk);
        }

        // Stable numbers for the live counters: fastest bus first, then by name.
        var order = new Dictionary<string, int> { ["nvme"] = 0, ["sata"] = 1, ["virtio"] = 1, ["scsi"] = 2, ["usb"] = 3, ["mmc"] = 4, ["optical"] = 5 };
        disks = disks.OrderBy(d => order.GetValueOrDefault(d.Transport, 9)).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
        for (var i = 0; i < disks.Count; i++) disks[i].Index = i;
        return disks;
    }

    public static List<Volume> ReadVolumes(IReadOnlyList<BlockDisk> disks)
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var label in Sys.Children("/dev/disk/by-label"))
            if (Sys.LinkName($"/dev/disk/by-label/{label}") is { } target)
                labels[target] = Unescape(label);

        var volumes = new List<Volume>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Sys.Lines("/proc/self/mountinfo"))
        {
            var mount = ParseMountInfo(line);
            if (mount is null || !RealFileSystems.Contains(mount.Value.FileSystem)) continue;
            var (devNumber, mountPoint, fileSystem, source, readOnly) = mount.Value;
            // The same filesystem can be mounted in several places (bind mounts, btrfs subvolumes): list it once.
            if (!seen.Add(devNumber)) continue;
            var node = Sys.Real($"/sys/dev/block/{devNumber}");
            var nodeName = node is null ? Path.GetFileName(source) : Path.GetFileName(node);
            var physical = PhysicalDisks(nodeName, disks);
            var lvm = nodeName.StartsWith("dm-", StringComparison.Ordinal);
            long size = 0, free = 0;
            try
            {
                var drive = new DriveInfo(mountPoint);
                size = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch
            {
            }
            var display = lvm ? Sys.Read($"/sys/block/{nodeName}/dm/name") ?? nodeName : nodeName;
            volumes.Add(new Volume(mountPoint, display, fileSystem, size, free, labels.GetValueOrDefault(nodeName), physical, readOnly, lvm));
        }
        return volumes.OrderBy(v => v.MountPoint == "/" ? "" : v.MountPoint, StringComparer.Ordinal).ToList();
    }

    /// <summary>The physical disks under a block device: a partition's disk, or every disk under a device-mapper (LVM, LUKS) volume.</summary>
    public static List<string> PhysicalDisks(string node, IReadOnlyList<BlockDisk> disks)
    {
        var result = new List<string>();
        void Walk(string name, int depth)
        {
            if (depth > 8) return;
            if (disks.FirstOrDefault(d => d.Name == name) is { } whole)
            {
                if (!result.Contains(whole.Name)) result.Add(whole.Name);
                return;
            }
            if (disks.FirstOrDefault(d => d.Partitions.Any(p => p.Name == name)) is { } owner)
            {
                if (!result.Contains(owner.Name)) result.Add(owner.Name);
                return;
            }
            foreach (var slave in Sys.Children($"/sys/class/block/{name}/slaves")) Walk(slave, depth + 1);
        }
        Walk(node, 0);
        return result;
    }

    /// <summary>One /proc/self/mountinfo line: device number, mount point, filesystem, source, read-only.</summary>
    public static (string DevNumber, string MountPoint, string FileSystem, string Source, bool ReadOnly)? ParseMountInfo(string line)
    {
        var dash = line.IndexOf(" - ", StringComparison.Ordinal);
        if (dash < 0) return null;
        var left = line[..dash].Split(' ');
        var right = line[(dash + 3)..].Split(' ');
        if (left.Length < 6 || right.Length < 2) return null;
        var options = left[5].Split(',');
        return (left[2], Unescape(left[4]), right[0], Unescape(right[1]), options.Contains("ro"));
    }

    public static Dictionary<string, DiskCounters> ReadCounters()
    {
        var result = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        foreach (var line in Sys.Lines("/proc/diskstats"))
        {
            if (ParseDiskStats(line) is { } entry) result[entry.Name] = entry.Counters;
        }
        return result;
    }

    public static (string Name, DiskCounters Counters)? ParseDiskStats(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 14) return null;
        long N(int i) => long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (f[2], new DiskCounters(N(3), N(5), N(7), N(9), N(11), N(12)));
    }

    /// <summary>Joins a SCSI-split model ("WDC WD10" + "JPVT-75A1YT0") and drops the generic "ATA" vendor.</summary>
    public static string? JoinModel(string? vendor, string? model)
    {
        vendor = vendor?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(vendor) ? null : vendor;
        if (string.IsNullOrEmpty(vendor) || vendor is "ATA" or "NVMe" || model.StartsWith(vendor, StringComparison.OrdinalIgnoreCase)) return model;
        // An inquiry vendor field is 8 characters; when it ends mid-word the model continues it ("WDC WD10" + "JPVT…").
        if (vendor.Length >= 7 && char.IsLetterOrDigit(vendor[^1]) && char.IsLetterOrDigit(model[0]) && Regex.IsMatch(vendor, @"\d$"))
            return vendor + model;
        return $"{vendor} {model}";
    }

    // "[mq-deadline] none" → "mq-deadline"
    private static string? Bracketed(string? text)
    {
        if (text is null) return null;
        var match = Regex.Match(text, @"\[([^\]]+)\]");
        return match.Success ? match.Groups[1].Value : text.Trim();
    }

    // Mount paths and udev labels escape spaces and odd characters as \040 (octal) or \x20 (hex).
    public static string Unescape(string text) =>
        Regex.Replace(text, @"\\(x[0-9a-fA-F]{2}|[0-7]{3})", m =>
        {
            var code = m.Groups[1].Value;
            return ((char)(code[0] == 'x'
                ? Convert.ToInt32(code[1..], 16)
                : Convert.ToInt32(code, 8))).ToString();
        });

    [GeneratedRegex(@"^(loop|ram|zram|dm-|md|nbd|fd|mtdblock|zd)")]
    private static partial Regex SkippedDisk();

    [GeneratedRegex(@"^\d+-[\d.]+$")]
    private static partial Regex UsbDeviceName();
}
