using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BoardScout.DriveHealth;

/// <summary>
/// What the operating system itself knows about the disks and where they are mounted: Windows' storage cmdlets,
/// lsblk on Linux, diskutil on macOS. smartctl says how a drive is doing; this says which disk it is, its bus, and
/// which volumes live on it. One call per pass.
/// </summary>
public sealed class Inventory
{
    public sealed class Disk
    {
        /// <summary>Windows: the disk number. Linux and macOS: the position in the OS's list (disk0 = 0 on macOS).</summary>
        public int Number { get; set; }
        /// <summary>The device path smartctl uses: /dev/sda, /dev/nvme0n1, /dev/disk0. Null on Windows (the number does it).</summary>
        public string? Path { get; set; }
        public string? Name { get; set; }
        public string? Serial { get; set; }
        public string? Media { get; set; }
        public string? Bus { get; set; }
        public string? Health { get; set; }
        public string? Status { get; set; }
        public long Size { get; set; }
        // counters the OS keeps itself (Windows' reliability counters, Linux's drive temperature); null when it has none
        public int? Temp { get; set; }
        public int? TempMax { get; set; }
        public long? PowerOnHours { get; set; }
        public int? Wear { get; set; }
        public long? ReadErrors { get; set; }
        public long? WriteErrors { get; set; }
        public long? StartStops { get; set; }
        public long? LoadCycles { get; set; }
        public bool HasCounters { get; set; }
    }

    public sealed class Volume
    {
        /// <summary>Windows: "C:". Linux and macOS: the mount point ("/", "/srv/sync", "/Volumes/Backup").</summary>
        public string Letter { get; set; } = "";
        public string? Label { get; set; }
        public string? FileSystem { get; set; }
        public long Size { get; set; }
        public long Free { get; set; }
        public string? Health { get; set; }
        public string? Status { get; set; }
        public int Disk { get; set; } = -1;
    }

    /// <summary>windows, linux or macos: which reader filled it.</summary>
    public string Source { get; set; } = "windows";
    public List<Disk> Disks { get; } = new();
    public List<Volume> Volumes { get; } = new();
    public bool Elevated { get; set; }
    public string? Error { get; set; }

    /// <summary>Who to name in notes: "Windows", "Linux", "macOS".</summary>
    public string OsName => Source switch { "linux" => "Linux", "macos" => "macOS", _ => "Windows" };

    public static async Task<Inventory?> ReadAsync(CancellationToken ct = default)
    {
        if (OperatingSystem.IsWindows()) return await WindowsInventory.ReadAsync(ct);
        if (OperatingSystem.IsLinux()) return await LinuxInventory.ReadAsync(ct);
        if (OperatingSystem.IsMacOS()) return await MacInventory.ReadAsync(ct);
        return null;
    }

    /// <summary>The disk smartctl means by this device: by number on Windows, by path elsewhere.</summary>
    public Disk? Match(ScanDevice dev)
    {
        if (Source == "windows") return dev.DiskNumber is int n ? Disks.FirstOrDefault(d => d.Number == n) : null;
        string name = Bare(dev.Name);
        // Linux: smartctl reads an NVMe drive through its controller (/dev/nvme0); lsblk lists its namespace (/dev/nvme0n1)
        var controller = Regex.Match(name, @"^(nvme\d+)$");
        foreach (var d in Disks)
        {
            if (d.Path == null) continue;
            string p = Bare(d.Path);
            if (p == name) return d;
            if (controller.Success && Regex.IsMatch(p, "^" + controller.Groups[1].Value + @"n\d+$")) return d;
        }
        return null;
    }

    static string Bare(string path) => path.StartsWith("/dev/", StringComparison.Ordinal) ? path[5..] : path;

    /// <summary>Size and free space of a mount point (statvfs underneath on Linux and macOS).</summary>
    internal static (long Size, long Free)? SpaceOf(string mount)
    {
        try
        {
            var di = new DriveInfo(mount);
            if (!di.IsReady || di.TotalSize <= 0) return null;
            return (di.TotalSize, di.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
