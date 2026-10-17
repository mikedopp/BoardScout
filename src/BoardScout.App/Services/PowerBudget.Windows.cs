namespace BoardScout.Services;

// The Windows edition's inputs to the power breakdown: drives and USB devices from the device tree and the map's capture.
// The Linux edition builds its own from /sys (Views/LinuxPower.cs).
internal static partial class PowerBudget
{
    /// <summary>
    /// The drives and USB devices in a capture. nameOf names a drive by its USB device (for USB drives) or its disk;
    /// nodeOf finds a drive's map card by disk number. Without them, drives are named by what they report.
    /// </summary>
    public static (List<Drive> Drives, List<UsbDevice> Usb) Inputs(ConnectionsService.Capture capture,
        Func<DeviceNode, string?>? nameOf = null, Func<int, string?>? nodeOf = null)
    {
        var all = capture.Tree?.Descendants().ToList() ?? [];
        var byHandle = new Dictionary<uint, DeviceNode>();
        foreach (var device in all) byHandle.TryAdd(device.Handle, device);
        var drives = new List<Drive>();
        foreach (var (handle, number) in capture.Disks)
        {
            if (!capture.DiskFacts.TryGetValue(number, out var facts) || !byHandle.TryGetValue(handle, out var disk)) continue;
            // SD cards, virtual disks, and Storage Spaces draw nothing of their own worth counting.
            if (facts.Bus is 12 or 13 or 14 or 15 or 16) continue;
            var usb = facts.Bus == 7 ? UsbDeviceOf(disk) : null;
            if (facts.Bus == 7 && usb is null) continue;
            var reported = $"{facts.Vendor} {facts.Product}".Trim();
            var name = nameOf?.Invoke(usb ?? disk) ?? (reported.Length > 0 ? reported : disk.Name);
            bool? fromPc = usb is null ? true : usb.Usb?.SelfPowered is { } own ? !own : null;
            drives.Add(new Drive(facts, name, nodeOf?.Invoke(number), fromPc, usb is not null));
        }
        TellApart(drives, ConnectionsService.FormatBytes);

        var devices = new List<UsbDevice>();
        foreach (var device in all.Where(d => d.IdStarts(@"USB\VID_") && !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
        {
            if (device.Usb is not { PowerMa: > 0 } port || port.SelfPowered == true) continue;
            if (device.Descendants().Any(d => d.IsClass("DiskDrive"))) continue; // counted with the drives
            devices.Add(new UsbDevice(nameOf?.Invoke(device) ?? device.BusReportedName ?? device.Name, port.PowerMa.Value, ConnectionsService.DeviceNodeId(device)));
        }
        return (drives.OrderBy(d => d.Facts.Number).ToList(), devices);
    }

    private static DeviceNode? UsbDeviceOf(DeviceNode disk)
    {
        for (var node = disk.Parent; node is not null; node = node.Parent)
            if (node.IdStarts(@"USB\VID_") && !node.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)) return node;
        return null;
    }
}
