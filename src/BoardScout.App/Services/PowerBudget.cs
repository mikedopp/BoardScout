using BoardScout.Models;

namespace BoardScout.Services;

/// <summary>
/// Where the PC's power goes right now: what the sensors measure (processor, graphics card, a power supply with a USB
/// link), what each drive probably draws (its own NVMe power table, or typical figures for its kind, scaled by how busy
/// it is), and the most USB devices ask for. Every row says which of those it is; the motherboard, memory, and fans
/// have no sensor, so without a reporting power supply they stay unmeasured.
/// </summary>
internal static class PowerBudget
{
    /// <summary>A drive to estimate. FromPc: true when the PC powers it, false when it has its own power supply, null
    /// when BoardScout couldn't ask (then it's assumed to take the PC's power).</summary>
    internal sealed record Drive(DiskFacts Facts, string Name, string? Node, bool? FromPc, bool Usb);

    /// <summary>A bus-powered USB device (not a drive) and the most it asks its port for.</summary>
    internal sealed record UsbDevice(string Name, int PowerMa, string? Node);

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
        // Two drives of the same model (two "WD Game Drive"s) are told apart by capacity.
        var repeated = drives.GroupBy(d => d.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        for (var i = 0; i < drives.Count; i++)
            if (repeated.Contains(drives[i].Name) && drives[i].Facts.SizeBytes is > 0 and var size)
                drives[i] = drives[i] with { Name = $"{drives[i].Name} ({ConnectionsService.FormatBytes(size)})" };

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

    public static PowerBreakdown Build(IReadOnlyList<PowerReading> sensors, IReadOnlyList<Drive> drives, IReadOnlyList<UsbDevice> usb,
        IReadOnlyDictionary<int, double>? busy, string? gpuName = null, bool elevated = false)
    {
        double? Read(string device) => sensors.FirstOrDefault(p => p.Device == device)?.Watts;
        var breakdown = new PowerBreakdown();
        var rows = breakdown.Rows;

        // The power supply, when it reports: everything the parts inside use.
        var psu = sensors.FirstOrDefault(p => p.Device == "PSU output");
        if (psu is not null)
        {
            rows.Add(new PowerRow
            {
                Name = "Power supply output", Watts = psu.Watts, Kind = "measured",
                Detail = $"Everything the parts inside use, as {psu.Source ?? "the power supply"} reports it."
            });
            foreach (var rail in new[] { "+12V", "+5V", "+3.3V" })
                if (sensors.FirstOrDefault(p => p.Device == "PSU " + rail) is { } reading)
                    rows.Add(new PowerRow
                    {
                        Name = $"{rail} rail", Watts = reading.Watts, Kind = reading.Derived ? "derived" : "measured", Sub = true,
                        Detail = reading.Derived ? "The rail's volts times amps." : null
                    });
            if (sensors.FirstOrDefault(p => p.Device == "PSU input") is { } wall)
                rows.Add(new PowerRow
                {
                    Name = "From the wall", Watts = wall.Watts, Kind = "derived", Sub = true,
                    Detail = "The output divided by the efficiency the power supply reports: its own losses included."
                });
        }

        // The processor: the package, split into cores and the rest when it reports them.
        var package = Read("CPU");
        if (package is { } cpu)
        {
            rows.Add(new PowerRow { Name = "Processor", Watts = cpu, Kind = "measured", Node = "cpu", Detail = "The whole package, from the processor's own energy counters." });
            var cores = Read("CPU cores");
            var soc = Read("CPU SoC");
            var graphics = Read("CPU graphics");
            if (cores is { } c)
                rows.Add(new PowerRow { Name = "Cores", Watts = c, Kind = "measured", Sub = true, Detail = "The cores' own energy counters, added up." });
            if (graphics is { } g)
                rows.Add(new PowerRow { Name = "Built-in graphics", Watts = g, Kind = "measured", Sub = true });
            if (soc is { } s)
                rows.Add(new PowerRow { Name = "SoC rail", Watts = s, Kind = "measured", Sub = true, Detail = "Memory controller, fabric, and I/O, as the processor reports them." });
            else if (cores is { } split)
                rows.Add(new PowerRow
                {
                    Name = "Everything but the cores", Watts = Math.Round(Math.Max(0, cpu - split - (graphics ?? 0)), 1), Kind = "derived", Sub = true,
                    Detail = "Memory controller, fabric, I/O, and built-in graphics where there is one: the package minus the cores."
                });
        }
        else
        {
            rows.Add(new PowerRow
            {
                Name = "Processor", Kind = "unmeasured", Node = "cpu",
                Detail = elevated
                    ? "The processor doesn't report its power to the sensor library on this PC."
                    : "Reading it needs administrator rights and the PawnIO driver (the version button has the details)."
            });
        }

        var gpu = Read("GPU");
        if (gpu is { } card)
            rows.Add(new PowerRow { Name = gpuName ?? "Graphics card", Watts = card, Kind = "measured", Detail = "The whole card, as its driver reports it." });

        // Drives: estimates, never measurements.
        var powered = drives.Where(d => d.FromPc != false).ToList();
        double drivesTotal = 0;
        if (powered.Count > 0)
        {
            var own = drives.Where(d => d.FromPc == false).Select(d => d.Name).ToList();
            var driveRows = powered.Select(d => DriveRow(d, busy)).ToList();
            drivesTotal = Math.Round(driveRows.Sum(r => r.Watts ?? 0), 1);
            rows.Add(new PowerRow
            {
                Name = "Drives", Watts = drivesTotal, Kind = "estimate",
                Detail = $"{powered.Count} drive{(powered.Count == 1 ? "" : "s")}: estimates from each drive's power table or typical figures for its kind, and how busy it is. " +
                         "No drive reports what it draws." + (own.Count > 0 ? $" Not counted: {Join(own)} (own power supply)." : "")
            });
            rows.AddRange(driveRows);
        }

        // USB devices other than drives: only the most they ask for.
        var usbWatts = Math.Round(usb.Sum(d => d.PowerMa) * 5 / 1000.0, 1);
        if (usb.Count > 0)
        {
            var listed = usb.OrderByDescending(d => d.PowerMa).Take(5).ToList();
            rows.Add(new PowerRow
            {
                Name = "USB devices", Watts = usbWatts, Kind = "ceiling",
                Detail = "The most bus-powered devices (other than drives) ask their ports for, at 5 V. Most draw far less; USB doesn't report what they actually use." +
                         (usb.Count > listed.Count ? $" {usb.Count - listed.Count} smaller ones aren't listed." : "")
            });
            rows.AddRange(listed.Select(d => new PowerRow
            {
                Name = d.Name, Watts = Math.Round(d.PowerMa * 5 / 1000.0, 2), Kind = "ceiling", Sub = true, Node = d.Node,
                Detail = $"Asks for up to {d.PowerMa} mA."
            }));
        }

        if (psu is not null)
        {
            var rest = psu.Watts - (package ?? 0) - (gpu ?? 0) - drivesTotal;
            rows.Add(new PowerRow
            {
                Name = "Everything else", Watts = Math.Round(Math.Max(0, rest), 1), Kind = "derived",
                Detail = $"The motherboard, memory, fans, USB devices{(package is null ? ", the processor" : "")}, and voltage-regulator losses: " +
                         "the power supply's output minus the rows above." + (rest < 0 ? " The drive estimates run high right now." : "")
            });
            breakdown.Total = psu.Watts;
            breakdown.TotalLabel = "The power supply delivers";
            breakdown.Note = sensors.Any(p => p.Device == "PSU input")
                ? "From the wall it's a little more: the power supply's own losses."
                : "From the wall it's a little more: the power supply's own losses, which it doesn't report.";
        }
        else
        {
            rows.Add(new PowerRow
            {
                Name = "Motherboard, memory, and fans", Kind = "unmeasured",
                Detail = "No sensor on the board measures them. A power supply with a USB link (Corsair HXi, RMi, or AXi; MSI with a USB port) " +
                         "or a plug-in power meter would show the whole PC."
            });
            breakdown.Total = Math.Round((package ?? 0) + (gpu ?? 0) + drivesTotal, 1);
            breakdown.TotalLabel = "Measured and estimated";
            breakdown.Note = "Not counted: " + Join(new[]
            {
                package is null ? "the processor (no reading)" : null,
                "the motherboard, memory, and fans",
                usb.Count > 0 ? $"USB devices (up to {usbWatts:0.#} W)" : null,
                "the power supply's own losses"
            }.OfType<string>().ToList()) + ".";
        }
        return breakdown;
    }

    /// <summary>One drive's estimate: idle plus its share of the way to busy, by how busy it was over the last second.</summary>
    private static PowerRow DriveRow(Drive drive, IReadOnlyDictionary<int, double>? busy)
    {
        var (idle, active, sleep, basis) = Figures(drive.Facts, drive.Usb);
        double? percent = busy is not null && busy.TryGetValue(drive.Facts.Number, out var b) ? b : null;
        var watts = idle + (percent ?? 0) / 100 * Math.Max(0, active - idle);
        var detail = $"{basis}: {Watts(idle)} W idle{(sleep is { } s ? $", {Watts(s)} W asleep" : "")}, up to {Watts(active)} W busy. " +
                     (percent is { } p ? $"{p:0}% busy right now." : "How busy it is isn't being read right now, so this is the idle figure.") +
                     (drive.FromPc is null ? " Assumed to take its power from the PC (BoardScout couldn't ask)." : "");
        return new PowerRow { Name = drive.Name, Watts = Math.Round(watts, 2), Kind = "estimate", Detail = detail, Node = drive.Node, Sub = true };
    }

    /// <summary>
    /// Idle and busy watts for a drive, and where they come from: an NVMe drive's own power table, else typical figures
    /// for its kind. A hard drive's figures assume it's spinning.
    /// </summary>
    internal static (double Idle, double Active, double? Sleep, string Basis) Figures(DiskFacts facts, bool usb)
    {
        if (facts.PowerTable is { } table)
        {
            var active = table.ActiveW ?? table.MaxW;
            return (Math.Min(table.IdleW ?? 0.7, active), active, table.SleepW,
                table.IdleW is null ? "From the drive's own power table (idle is a typical figure)" : "From the drive's own power table");
        }
        return facts.Spinning switch
        {
            true when usb => (1.0, 2.2, null, "Typical for a portable hard drive and its USB bridge, while it spins"),
            true when DriveModels.LaptopHardDrive(facts.Vendor, facts.Product) => (0.8, 2.0, null, "Typical for a 2.5-inch hard drive, while it spins"),
            true => (4.5, 6.5, null, "Typical for a 3.5-inch hard drive, while it spins"),
            false when usb => (0.5, 3.0, null, "Typical for a portable SSD and its USB bridge"),
            false when facts.Bus == 17 => (0.7, 5.0, null, "Typical for an NVMe SSD"),
            false => (0.3, 3.0, null, "Typical for a SATA SSD"),
            null => (0.8, 3.0, null, "A rough figure: the drive doesn't say what kind it is")
        };
    }

    /// <summary>A drive card's one-line power fact: the range BoardScout estimates from.</summary>
    public static string DriveFact(DiskFacts facts, bool usb)
    {
        var (idle, active, sleep, basis) = Figures(facts, usb);
        return $"About {Watts(idle)} W idle{(sleep is { } s ? $" ({Watts(s)} W asleep)" : "")}, up to {Watts(active)} W busy. " +
               $"An estimate: {char.ToLowerInvariant(basis[0])}{basis[1..]}.";
    }

    private static string Watts(double watts) => watts switch
    {
        >= 10 => $"{watts:0}",
        >= 1 => $"{watts:0.#}",
        >= 0.1 => $"{watts:0.##}",
        _ => $"{watts:0.###}"
    };

    private static string Join(IReadOnlyList<string> items) => items.Count switch
    {
        0 => "",
        1 => items[0],
        2 => $"{items[0]} and {items[1]}",
        _ => string.Join(", ", items.Take(items.Count - 1)) + ", and " + items[^1]
    };
}
