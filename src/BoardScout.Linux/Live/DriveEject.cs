using BoardScout.Platform;

namespace BoardScout.Live;

/// <summary>
/// Stops a USB drive so it can be unplugged, the way the desktop's eject button does: udisks unmounts each of its
/// filesystems (writing out anything cached, and refusing while a file is open), then powers the drive off.
/// Only USB drives, only when the user asks twice, and only the drive that is still at the same USB port.
/// </summary>
internal static class DriveEject
{
    public static (bool Ok, string Message) Run(BlockDisk disk)
    {
        if (disk.Transport != "usb") return (false, "Only USB drives can be ejected here.");
        if (!Tools.Has("udisksctl"))
            return (false, "udisksctl (part of udisks2) isn't installed, so BoardScout can't stop the drive. Unmount it, then unplug it.");
        // The same drive must still be at the same port: a different drive may have taken its name since.
        if (Sys.Real($"/sys/block/{disk.Name}") != disk.SysPath) return (false, "That drive is no longer connected.");

        var nodes = disk.Partitions.Select(p => (Name: p.Name, DevNumber: p.DevNumber)).Append((Name: disk.Name, DevNumber: Sys.Read($"/sys/block/{disk.Name}/dev") ?? "")).ToList();
        // Encrypted or LVM volumes sit on top of the drive; closing them is a job for the desktop or the terminal.
        var holders = nodes.SelectMany(n => Sys.Children($"/sys/class/block/{n.Name}/holders")).ToList();
        if (holders.Count > 0)
            return (false, $"It holds an encrypted or LVM volume ({string.Join(", ", holders)}). Close that first, then eject.");

        foreach (var line in Sys.Lines("/proc/self/mountinfo"))
        {
            var mount = Block.ParseMountInfo(line);
            if (mount is not { } m) continue;
            var node = nodes.FirstOrDefault(n => n.DevNumber == m.DevNumber);
            if (node.Name is null) continue;
            var (code, _, error) = Tools.RunFull("udisksctl", ["unmount", "-b", $"/dev/{node.Name}", "--no-user-interaction"], 30000);
            if (code == 0) continue;
            if (error.Contains("busy", StringComparison.OrdinalIgnoreCase))
                return (false, $"A program has a file open on {m.MountPoint}, so nothing was stopped. Close it and try again.");
            if (error.Contains("Not authorized", StringComparison.OrdinalIgnoreCase) || error.Contains("NotAuthorized", StringComparison.OrdinalIgnoreCase))
                return (false, $"Linux won't let a regular user unmount {m.MountPoint} (the system mounted it). Run sudo umount {m.MountPoint}, then eject.");
            return (false, $"Couldn't unmount {m.MountPoint}: {FirstLine(error)}");
        }

        var (off, _, offError) = Tools.RunFull("udisksctl", ["power-off", "-b", $"/dev/{disk.Name}", "--no-user-interaction"], 30000);
        return off == 0
            ? (true, "Stopped: safe to unplug.")
            : (false, offError.Contains("Not authorized", StringComparison.OrdinalIgnoreCase)
                ? "Its filesystems are unmounted, so it's safe to unplug, but Linux didn't allow powering it off without an administrator."
                : $"Its filesystems are unmounted; powering it off failed ({FirstLine(offError)}). It's safe to unplug.");
    }

    private static string FirstLine(string text) => text.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "no reason given";
}
