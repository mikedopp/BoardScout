using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;

namespace BoardScout.Views;

/// <summary>
/// The power breakdown on Linux: the drives and USB devices in a capture, turned into rows by the shared PowerBudget
/// with the readings from Platform/Power.cs.
/// </summary>
internal static class LinuxPower
{
    private const string WholePc =
        "A Corsair HXi or RMi power supply (the kernel's corsair-psu driver reads it) or a plug-in power meter would show the whole PC.";

    public static PowerBreakdown Breakdown(HardwareCapture capture, PowerSample sample, string? gpuName = null,
        Func<string, string?>? nameOf = null, bool privacy = false)
    {
        var (drives, usb) = Inputs(capture, nameOf, privacy);
        return PowerBudget.Build(sample.Readings, drives, usb, sample.Busy, gpuName, capture.Root, sample.NoCpuReading, WholePc);
    }

    /// <summary>
    /// The drives and bus-powered USB devices to estimate. nameOf names a map card by its id (the plan passes the map's
    /// names); without it, names come from what the devices report, masked in privacy mode.
    /// </summary>
    public static (List<PowerBudget.Drive> Drives, List<PowerBudget.UsbDevice> Usb) Inputs(HardwareCapture capture,
        Func<string, string?>? nameOf = null, bool privacy = false)
    {
        string Masked(string name) => privacy ? Privacy.Scrub(name) : name;
        var drives = new List<PowerBudget.Drive>();
        foreach (var disk in capture.Disks.Where(Counted))
        {
            var usb = disk.UsbPath is null ? null : capture.UsbDevices.FirstOrDefault(d => d.SysPath == disk.UsbPath);
            var node = NodeOf(capture, disk, usb);
            bool? fromPc = disk.Transport != "usb" ? true : usb is null ? null : !usb.SelfPowered;
            drives.Add(new PowerBudget.Drive(Facts(disk), nameOf?.Invoke(node) ?? Masked(disk.DisplayName), node, fromPc, disk.Transport == "usb"));
        }
        PowerBudget.TellApart(drives, Format.Bytes);

        var devices = new List<PowerBudget.UsbDevice>();
        foreach (var device in capture.UsbDevices.Where(d => !d.IsRootHub && d.MaxPowerMa > 0 && !d.SelfPowered))
        {
            if (capture.Disks.Any(k => k.UsbPath == device.SysPath)) continue; // counted with the drives
            var node = LinuxConnections.UsbNodeId(device);
            var reported = device.ProductName ?? (capture.UsbNames.TryGetValue((device.Vendor, device.Product), out var names) ? names.Product?.Split(',')[0].Trim() : null);
            devices.Add(new PowerBudget.UsbDevice(nameOf?.Invoke(node) ?? Masked(reported ?? $"USB device {device.Vendor:x4}:{device.Product:x4}"), device.MaxPowerMa, node));
        }
        return (drives.OrderBy(d => d.Facts.Number).ToList(), devices);
    }

    /// <summary>The map card a drive sits on: its USB device, its NVMe controller (the map shows the two as one card), or its own.</summary>
    public static string NodeOf(HardwareCapture capture, BlockDisk disk, UsbDevice? usb) =>
        usb is not null ? LinuxConnections.UsbNodeId(usb)
        : disk.Nvme && capture.Pci.FirstOrDefault(p => p.Address == disk.PciAddress) is { } controller ? LinuxConnections.PciNodeId(controller)
        : LinuxConnections.DiskNodeId(disk);

    /// <summary>A disk as the shared estimates see it. SATA drives' "ATA" vendor is dropped so the model families match.</summary>
    public static DiskFacts Facts(BlockDisk disk)
    {
        var (spinning, fromModel) = disk.DriveType();
        return new DiskFacts(disk.Index, disk.Vendor is "ATA" or "NVMe" ? null : disk.Vendor, disk.Model, disk.Firmware, Bus(disk),
            disk.SizeBytes > 0 ? disk.SizeBytes : null, disk.TemperatureC, spinning, fromModel, disk.PowerTable);
    }

    /// <summary>How a disk connects, as the STORAGE_BUS_TYPE number the shared estimates use.</summary>
    public static int Bus(BlockDisk disk) => disk.Transport switch
    {
        "nvme" => 17,
        "sata" => 11,
        "usb" => 7,
        "mmc" => 12,
        "virtio" => 14,
        "scsi" => 1,
        _ => 0
    };

    /// <summary>Drives that draw power worth estimating: not optical drives, SD cards, or virtual disks.</summary>
    public static bool Counted(BlockDisk disk) => disk.Transport switch
    {
        "nvme" or "sata" or "usb" => true,
        "scsi" => !$"{disk.Vendor} {disk.Model}".Contains("QEMU", StringComparison.OrdinalIgnoreCase) &&
                  !$"{disk.Vendor} {disk.Model}".Contains("VMware", StringComparison.OrdinalIgnoreCase) &&
                  !$"{disk.Vendor} {disk.Model}".Contains("Virtual", StringComparison.OrdinalIgnoreCase) &&
                  disk.Vendor is not ("Msft" or "VBOX"),
        _ => false
    };
}
