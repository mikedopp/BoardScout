using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;

namespace BoardScout.Views;

internal static class LinuxOverview
{
    public static OverviewPayload Build(HardwareCapture capture, LinuxConnections.BuildResult map, OptimizationPlan plan, bool privacy,
        Live.FirmwareReport? firmware = null)
    {
        var distro = Distro.Read();
        var cpu = capture.Cpu;
        var title = Title(capture);
        var subtitle = string.Join("  •  ", new[]
        {
            capture.FormFactor.ToUpperInvariant(),
            Format.ShortCpuName(cpu.Name),
            $"{cpu.Cores}C/{cpu.Threads}T",
            distro.PrettyName
        });
        var parts = BoardParts(capture, map, privacy);
        var (total, _, _, _) = Cpu.ReadMemory();
        var used = capture.Volumes.Sum(v => v.SizeBytes - v.FreeBytes);
        var details = new List<Fact>();
        if (capture.Identity.System is { } system) details.Add(new("Computer", system));
        if (capture.Identity.BiosVersion is { } bios) details.Add(new("BIOS", $"{bios}{(capture.Identity.BiosDate is { } date ? $" · {date}" : "")}"));
        var installedMb = capture.Memory.Firmware.MemorySlots.Sum(s => s.SizeMb);
        details.Add(new("Memory", (installedMb > 0 ? $"{installedMb / 1024d:0.#} GB installed, {total / 1_073_741_824d:0.#} GB usable" : $"{total / 1_073_741_824d:0.#} GB") +
            (capture.Memory.Firmware.SlotCount > 0 ? $" · {capture.Memory.Firmware.MemorySlots.Count(s => s.Populated)} of {capture.Memory.Firmware.SlotCount} slots" : "")));
        if (capture.Identity.Board is { } board && board != title) details.Add(new("Motherboard", board));
        if (capture.Memory.Firmware.MemorySlots.FirstOrDefault(s => s.Populated) is { ConfiguredMts: > 0 } stick)
            details.Add(new("Memory speed", $"{stick.ConfiguredMts} / {Math.Max(stick.SpeedMts, stick.ConfiguredMts)} MT/s"));
        details.Add(new("Storage", $"{capture.Disks.Count(d => !d.Optical)} drives · {capture.Volumes.Count} mounted filesystems"));
        details.Add(new("USB", $"{capture.UsbDevices.Count(d => !d.IsHub)} attached devices"));
        var problems = LinuxConnections.Walk(map.Snapshot.Root).Count(n => n.Problem);
        details.Add(new("Device health", problems == 0 ? "No reported errors" : $"{problems} device{(problems == 1 ? "" : "s")} need attention"));
        details.Add(new("Kernel", capture.Kernel));
        details.Add(new("Last read", capture.At.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

        return new OverviewPayload(
            privacy ? Privacy.Scrub(title) : title,
            subtitle,
            capture.FormFactor,
            privacy ? Privacy.HostPlaceholder : Environment.MachineName,
            parts,
            details,
            Drivers(capture, map, privacy),
            capture.Volumes.Select(v => new VolumeRow(
                privacy && v.MountPoint.StartsWith("/home/", StringComparison.Ordinal) ? "/home/…" : v.MountPoint,
                privacy ? null : v.Label,
                v.FileSystem, v.SizeBytes, v.FreeBytes,
                v.SizeBytes > 0 ? Math.Round((v.SizeBytes - v.FreeBytes) * 100d / v.SizeBytes, 1) : 0,
                string.Join(", ", v.Disks), v.Lvm, v.ReadOnly)).ToList(),
            capture.Disks.Select(d => new DiskRow(d.Name, privacy ? Privacy.Scrub(d.DisplayName) : d.DisplayName, d.Transport, d.SizeBytes,
                d.Optical ? "Optical" : d.DriveType().Spinning switch { true => "HDD", false => d.Nvme ? "NVMe SSD" : "SSD", _ => "Disk" },
                d.Firmware, d.TemperatureC, LinuxConnections.Walk(map.Snapshot.Root).FirstOrDefault(n => n.Disk == d.Index)?.Id)).ToList(),
            Suggestions(capture, plan, firmware),
            Math.Round(total / 1_073_741_824d, 1),
            capture.Pci.Count(d => !d.IsBridge && d.BaseClass != 0x06) + capture.UsbDevices.Count(d => !d.IsRootHub) + capture.Disks.Count,
            Math.Round(used / 1e12, 2),
            null,
            capture.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            capture.CaptureMs);
    }

    /// <summary>
    /// The board's name ("ASRock B550M Steel Legend"), or the computer's when the board only carries an OEM part
    /// number (Dell's "0JP3NX" says less than "Dell OptiPlex 3050").
    /// </summary>
    private static string Title(HardwareCapture capture)
    {
        var identity = capture.Identity;
        var partNumber = identity.BoardName is { } name && System.Text.RegularExpressions.Regex.IsMatch(name, @"^[0-9A-Z]{5,8}$") && name.Any(char.IsDigit);
        var oem = identity.SystemVendor is { } vendor && System.Text.RegularExpressions.Regex.IsMatch(vendor, @"^(Dell|HP|Hewlett|LENOVO|Lenovo|Acer|Apple|Microsoft|Fujitsu|Toshiba|Dynabook|Panasonic|Framework|Intel\(R\) Client|Google)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var system = identity.ProductName is { } product && identity.SystemVendor is { } maker
            ? $"{Format.Company(maker)} {(identity.ProductVersion is { } version && maker.StartsWith("LENOVO", StringComparison.OrdinalIgnoreCase) && !product.Contains(version, StringComparison.OrdinalIgnoreCase) ? version : product)}"
            : identity.ProductName;
        if ((partNumber || oem || identity.BoardName is null) && system is not null) return system;
        if (identity.BoardName is { } boardName) return $"{(identity.BoardVendor is { } boardVendor ? Format.Company(boardVendor) + " " : "")}{boardName}";
        return capture.DeviceTreeModel ?? system ?? "This computer";
    }

    // ---- The board ---------------------------------------------------------------------------------

    private static List<BoardPart> BoardParts(HardwareCapture capture, LinuxConnections.BuildResult map, bool privacy)
    {
        var parts = new List<BoardPart>();
        var nodes = LinuxConnections.Walk(map.Snapshot.Root).ToList();
        string Scrub(string text) => privacy ? Privacy.Scrub(text) : text;
        List<Fact> Facts(ConnectionNode? node, int take = 8) =>
            node is null ? [] : node.Facts.Take(take).Select(f => new Fact(f.Label, f.Value)).ToList();

        var cpuNode = nodes.First(n => n.Kind == "cpu");
        parts.Add(new BoardPart("cpu", "cpu", "cpu", Format.ShortCpuName(capture.Cpu.Name), $"{capture.Cpu.Cores} cores · {capture.Cpu.Threads} threads",
            "good", Facts(cpuNode, 7), Live: "cpu", Node: "cpu"));

        var slots = capture.Memory.Firmware.MemorySlots;
        if (slots.Count > 0)
        {
            foreach (var slot in slots)
            {
                var label = slot.DeviceLocator;
                var maker = capture.Memory.Makers.GetValueOrDefault(slot.DeviceLocator + "|" + slot.BankLocator);
                var facts = new List<Fact> { new("Slot", $"{slot.DeviceLocator}{(slot.BankLocator.Length > 0 ? " · " + slot.BankLocator : "")}") };
                if (slot.Populated)
                {
                    facts.Add(new("Size", $"{slot.SizeMb / 1024d:0.#} GB {slot.MemoryType}"));
                    if (slot.ConfiguredMts > 0) facts.Add(new("Speed", $"{slot.ConfiguredMts} MT/s (rated {Math.Max(slot.SpeedMts, slot.ConfiguredMts)})"));
                    if (slot.PartNumber is not null) facts.Add(new("Part number", slot.PartNumber));
                    if (maker is not null) facts.Add(new("Made by", maker));
                }
                parts.Add(new BoardPart($"dimm-{Format.ShortHash(slot.DeviceLocator + slot.BankLocator)}", "memory", "dimm",
                    slot.Populated ? $"{slot.SizeMb / 1024d:0.#} GB" : "Open", label, slot.Populated ? "good" : "open", facts, Live: "mem",
                    Empty: !slot.Populated, Node: "memory"));
            }
        }
        else
        {
            var (total, _, _, _) = Cpu.ReadMemory();
            parts.Add(new BoardPart("memory", "memory", "memory", $"{total / 1_073_741_824d:0.#} GB", "Slots not described", "info",
                [new("Memory", $"{total / 1_073_741_824d:0.#} GB"), new("Slots", "The firmware's slot list isn't readable here")], Live: "mem"));
        }

        // Expansion: every CPU and chipset root port, with what's on it (or that nothing is).
        foreach (var port in capture.Pci.Where(d => d.IsBridge && d.Parent is null))
        {
            if (port.Link is null) continue;
            var child = port.Children.FirstOrDefault(c => !c.IsBridge) ?? port.Children.FirstOrDefault();
            var node = child is null ? null : nodes.FirstOrDefault(n => n.Id == LinuxConnections.PciNodeId(child)) ??
                       (child?.Children.FirstOrDefault() is { } grandchild ? nodes.FirstOrDefault(n => n.Id == LinuxConnections.PciNodeId(grandchild)) : null);
            if (node is not null && node.Kind == "chipset") continue;
            var cpuSide = !(port.Vendor == 0x8086 && port.Slot >= 0x10);
            var width = port.Link.MaxWidth;
            var lanes = $"PCIe {Format.Generation(port.Link.MaxGen)} x{width}";
            if (child is null)
            {
                parts.Add(new BoardPart("port-" + port.Address, "slots", "slot",
                    port.SlotName is { } slotName ? $"{slotName} · open" : $"{lanes} · nothing attached",
                    $"{(cpuSide ? "CPU" : "Chipset")} lanes{(port.SlotName is null ? "" : " · " + lanes)}", "open",
                    [new("Lanes", lanes), new("Wired to", cpuSide ? "The CPU" : "The chipset"),
                     new("What it is", port.SlotName is null ? "A PCIe port with nothing on it: an empty slot or M.2 socket, or lanes the board doesn't use" : $"Slot {port.SlotName}, empty")],
                    Width: width, Empty: true));
                continue;
            }
            var zone = node?.Kind switch
            {
                "nvme" => "m2",
                "gpu" => "slots",
                "ethernet" or "wifi" => "io",
                _ => "slots"
            };
            parts.Add(new BoardPart("port-" + port.Address, zone, node?.Kind ?? "device", Scrub(node?.Name ?? child.Name),
                $"{node?.Link?.Label ?? lanes}{(cpuSide ? " · CPU" : " · chipset")}", node?.Warning is not null ? "warn" : "good",
                Facts(node), Live: node?.Disk is { } disk ? $"disk:{disk}" : node?.Net is { } net ? $"net:{net}" : null, Width: width, Node: node?.Id));
        }

        // Things on the CPU's or chipset's root bus: integrated graphics, onboard USB, SATA, audio, LAN.
        foreach (var node in nodes.Where(n => n.Link?.Bus == "internal" && n.Kind is "gpu" or "usb-controller" or "sata" or "audio" or "ethernet" or "wifi" or "bluetooth"))
        {
            if (parts.Any(p => p.Node == node.Id)) continue;
            var zone = node.Kind switch { "sata" => "sata", "usb-controller" => "io", "ethernet" or "wifi" or "bluetooth" or "audio" => "io", _ => "cpu" };
            parts.Add(new BoardPart("n-" + node.Id, zone, node.Kind, Scrub(node.Name), node.Detail, node.Warning is not null ? "warn" : "good", Facts(node),
                Live: node.Net is { } net ? $"net:{net}" : null, Node: node.Id));
        }
        foreach (var node in nodes.Where(n => n.Kind is "usb-controller" && !parts.Any(p => p.Node == n.Id)))
            parts.Add(new BoardPart("n-" + node.Id, "io", node.Kind, node.Name, node.Detail, "good", Facts(node), Node: node.Id));

        // SATA ports with what's on them.
        foreach (var controller in nodes.Where(n => n.Kind == "sata"))
            foreach (var drive in controller.Children)
                parts.Add(new BoardPart("d-" + drive.Id, "sata", drive.Kind, Scrub(drive.Name), drive.Link?.Label ?? drive.Detail,
                    drive.Warning is not null ? "warn" : "good", Facts(drive), Live: drive.Disk is { } disk ? $"disk:{disk}" : null, Node: drive.Id));

        var chipset = nodes.FirstOrDefault(n => n.Id is "chipset" or "platform");
        if (chipset is not null)
            parts.Add(new BoardPart("chipset", "chipset", "chipset", chipset.Name, chipset.Link?.Label ?? chipset.Detail, "good", Facts(chipset),
                Live: chipset.Sensor is not null ? "chipset" : null, Node: chipset.Id));

        // Display outputs and fans.
        var outputs = capture.Displays.GroupBy(o => o.Kind).Select(g => $"{g.Key}{(g.Count() > 1 ? $" ×{g.Count()}" : "")}").ToList();
        if (outputs.Count > 0)
            parts.Add(new BoardPart("outputs", "io", "monitor", "Display outputs", string.Join(" · ", outputs),
                capture.Displays.Any(o => o.Connected) ? "good" : "open",
                capture.Displays.Select(o => new Fact(o.Connector, o.Connected ? (o.Edid?.Name ?? "Monitor connected") : "Nothing connected")).ToList()));
        foreach (var (name, rpm) in Sensors.Fans(capture.Sensors))
            parts.Add(new BoardPart("fan-" + Format.ShortHash(name), "fans", "fan", name, rpm > 0 ? $"{rpm} RPM" : "stopped", rpm > 0 ? "good" : "open",
                [new("Fan", name), new("Speed", $"{rpm} RPM")], Live: "fan:" + name));
        return parts;
    }

    // ---- Drivers and firmware ----------------------------------------------------------------------

    private static List<DriverRow> Drivers(HardwareCapture capture, LinuxConnections.BuildResult map, bool privacy)
    {
        var rows = new List<DriverRow>();
        if (capture.Identity.BiosVersion is { } bios)
            rows.Add(new DriverRow(capture.Identity.Board ?? capture.Identity.System ?? "Motherboard", "Firmware", "BIOS/UEFI", bios,
                capture.Identity.BiosVendor ?? "Board maker", capture.Identity.BiosDate, "Installed", "info", null));
        foreach (var device in capture.Pci.Where(d => !d.IsBridge && !(d.BaseClass == 0x06 && d.SubClass is 0x00)))
        {
            var info = KernelInfo.Driver(Sys.LinkName(Path.Combine(device.SysPath, "driver", "module")) ?? device.Driver, capture.Kernel);
            var firmware = device.BaseClass == 0x03 ? Sys.Read(Path.Combine(device.SysPath, "vbios_version")) : null;
            rows.Add(new DriverRow($"{device.VendorShort} {device.Name}", device.ClassName, device.Driver ?? "none", info?.Version,
                info?.Source ?? "No driver attached", firmware,
                device.Driver is null ? "No driver" : info?.Proprietary == true ? "Proprietary" : "OK",
                device.Driver is null ? (device.BaseClass is 0x01 or 0x02 or 0x03 or 0x0C ? "warn" : "info") : "good",
                LinuxConnections.PciNodeId(device)));
        }
        foreach (var disk in capture.Disks.Where(d => d.Firmware is not null))
            rows.Add(new DriverRow(privacy ? Privacy.Scrub(disk.DisplayName) : disk.DisplayName, "Drive firmware", disk.Nvme ? "nvme" : disk.UsbDriver ?? "sd",
                null, "Drive maker", disk.Firmware, "Installed", "info", LinuxConnections.DiskNodeId(disk)));
        foreach (var device in capture.UsbDevices.Where(d => !d.IsRootHub))
        {
            var drivers = device.Interfaces.Select(i => i.Driver).Where(d => d is not null).Distinct().ToList();
            var names = capture.UsbNames.TryGetValue((device.Vendor, device.Product), out var found) ? found : ((string, string?)?)null;
            var name = device.ProductName ?? names?.Item2 ?? $"USB {device.Vendor:X4}:{device.Product:X4}";
            rows.Add(new DriverRow(privacy ? Privacy.Scrub(name) : name, "USB device", drivers.Count == 0 ? "none" : string.Join(", ", drivers!), null,
                drivers.Count == 0 ? "Nothing on Linux is using it" : "Kernel driver", null, drivers.Count == 0 && !device.IsHub ? "No driver" : "OK",
                drivers.Count == 0 && !device.IsHub ? "info" : "good", LinuxConnections.UsbNodeId(device)));
        }
        return rows;
    }

    // ---- Efficiency ----------------------------------------------------------------------------------

    private static List<Suggestion> Suggestions(HardwareCapture capture, OptimizationPlan plan, Live.FirmwareReport? firmware)
    {
        var list = new List<Suggestion>();
        foreach (var update in firmware?.Updates ?? [])
        {
            var system = update.Device.Contains("System Firmware", StringComparison.OrdinalIgnoreCase);
            list.Add(new Suggestion(system ? "improvement" : "info",
                system ? $"BIOS update available: {update.Current} → {update.Latest}" : $"Firmware update for {update.Device}: {update.Current} → {update.Latest}",
                update.Summary ?? "fwupd lists a newer firmware release for this device.",
                "Install it with your software center's firmware updates, or sudo fwupdmgr update. Keep the computer on mains power while it runs.",
                "Firmware"));
        }
        foreach (var section in plan.Sections)
            foreach (var item in section.Items.Where(i => i.Tone is "warn" or "improve"))
                list.Add(new Suggestion(item.Tone == "warn" ? "warning" : "improvement", item.Title, item.Detail, item.Action, section.Title));
        foreach (var volume in capture.Volumes.Where(v => v.SizeBytes > 0 && !v.ReadOnly && v.FreeBytes * 100 / v.SizeBytes is >= 10 and < 20))
            list.Add(new Suggestion("info", $"{volume.MountPoint} is {100 - volume.FreeBytes * 100 / volume.SizeBytes}% full",
                $"{Format.Bytes(volume.FreeBytes)} free of {Format.Bytes(volume.SizeBytes)}.", "Clear old files, or check sudo journalctl --disk-usage and apt's cache.", "Storage"));
        if (list.Count == 0)
            list.Add(new Suggestion("info", "Nothing to fix", "Every check in the optimization plan passed.", null, "Overview"));
        return list.OrderBy(s => s.Severity switch { "warning" => 0, "improvement" => 1, _ => 2 }).ToList();
    }

    // ---- The scan file (what the Topology view reads, and what Export saves) ---------------------------

    public static string ScanJson(HardwareCapture capture, bool privacy, bool indented = true)
    {
        var distro = Distro.Read();
        var slots = capture.Memory.Firmware.MemorySlots;
        var (total, _, _, _) = Cpu.ReadMemory();
        var components = new JsonArray();
        // "attach" and "link" tell the Topology view where a device really sits, instead of the guesses it makes for
        // scans without them: integrated, CPU lanes, or behind the chipset.
        void Component(string key, string category, string? vendor, string model, string? driver, string? source, string? firmware, string? bus = null,
            long? size = null, string? media = null, string? attach = null, string? link = null) =>
            components.Add(new JsonObject
            {
                ["component_key"] = key,
                ["category"] = category,
                ["vendor"] = vendor,
                ["model"] = model,
                ["current"] = new JsonObject { ["driver_version"] = driver, ["driver_source"] = source, ["firmware"] = firmware },
                ["lookup_hints"] = new JsonObject { ["bus_type"] = bus, ["size_bytes"] = size, ["media_type"] = media, ["attach"] = attach, ["link"] = link }
            });
        foreach (var device in capture.Pci.Where(d => !d.IsBridge))
        {
            var category = (device.BaseClass, device.SubClass) switch
            {
                (0x03, _) => "gpu",
                (0x02, _) => "network",
                (0x0C, 0x03) => "usb",
                (0x04, _) => "audio",
                (0x06, 0x01) => "chipset",
                _ => null
            };
            if (category is null) continue;
            var model = category == "chipset" ? ChipsetName(capture, device) : $"{device.VendorShort} {device.Name}";
            if (category == "network" && device.SubClass == 0x80 && !model.Contains("Wireless", StringComparison.OrdinalIgnoreCase) && !model.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase))
                model += " Wi-Fi";
            Component($"pci:{device.Vendor:x4}:{device.Device:x4}", category, device.VendorShort, model, device.Driver, "Linux kernel", null,
                attach: category == "gpu" ? Attach(capture, device) : null, link: category == "gpu" ? Link(device) : null);
        }
        if (!capture.Pci.Any(d => d.BaseClass == 0x06 && d.SubClass == 0x01) && capture.Pci.Any(d => d.Vendor == 0x1022 && d.IsBridge))
            Component("chipset", "chipset", "AMD", ChipsetName(capture, null), null, null, null);
        foreach (var disk in capture.Disks.Where(d => !d.Optical))
        {
            var controller = disk.PciAddress is null ? null : capture.Pci.FirstOrDefault(d => d.Address == disk.PciAddress);
            Component($"disk:{disk.Name}", "storage", null, privacy ? Privacy.Scrub(disk.DisplayName) : disk.DisplayName, disk.Nvme ? "nvme" : null, null, disk.Firmware,
                disk.Transport switch { "nvme" => "NVMe", "sata" => "SATA", "usb" => "USB", _ => disk.Transport }, disk.SizeBytes,
                disk.DriveType().Spinning switch { true => "HDD", false => "SSD", _ => "Unspecified" },
                attach: disk.Nvme && controller is not null ? Attach(capture, controller) : null,
                link: disk.Nvme && controller is not null ? Link(controller) : null);
        }

        var scan = new JsonObject
        {
            ["schema_version"] = "boardscout-linux-1",
            ["scan"] = new JsonObject
            {
                ["tool"] = "BoardScout for Linux",
                ["tool_version"] = AppInfo.Version,
                ["timestamp_utc"] = capture.At.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                ["hostname"] = privacy ? Privacy.HostPlaceholder : Environment.MachineName,
                ["os"] = new JsonObject { ["caption"] = distro.PrettyName, ["version"] = distro.VersionId, ["build"] = capture.Kernel, ["arch"] = capture.Cpu.Architecture }
            },
            ["system"] = new JsonObject
            {
                ["manufacturer"] = capture.Identity.SystemVendor,
                ["model"] = capture.Identity.ProductName ?? capture.DeviceTreeModel,
                ["baseboard"] = new JsonObject { ["manufacturer"] = capture.Identity.BoardVendor, ["product"] = capture.Identity.BoardName, ["version"] = capture.Identity.BoardVersion },
                ["bios"] = new JsonObject { ["vendor"] = capture.Identity.BiosVendor, ["version"] = capture.Identity.BiosVersion, ["release_date"] = capture.Identity.BiosDate },
                ["cpu"] = new JsonObject { ["name"] = capture.Cpu.Name, ["cores"] = capture.Cpu.Cores, ["threads"] = capture.Cpu.Threads }
            },
            ["components"] = components,
            ["memory"] = new JsonObject
            {
                ["total_slots"] = capture.Memory.Firmware.SlotCount,
                ["populated"] = slots.Count(s => s.Populated),
                ["total_bytes"] = slots.Any(s => s.Populated) ? slots.Sum(s => (long)s.SizeMb) * 1_048_576 : (long)total,
                ["slots"] = new JsonArray(slots.Where(s => s.Populated).Select(s => (JsonNode)new JsonObject
                {
                    ["bank"] = s.BankLocator,
                    ["locator"] = s.DeviceLocator,
                    ["capacity_gb"] = Math.Round(s.SizeMb / 1024d, 1),
                    ["speed_mhz"] = s.ConfiguredMts,
                    ["rated_mhz"] = Math.Max(s.SpeedMts, s.ConfiguredMts),
                    ["manufacturer"] = capture.Memory.Makers.GetValueOrDefault(s.DeviceLocator + "|" + s.BankLocator),
                    ["part_number"] = s.PartNumber
                }).ToArray())
            },
            ["volumes"] = new JsonArray(capture.Volumes.Select(v => (JsonNode)new JsonObject
            {
                ["letter"] = v.MountPoint,
                ["label"] = privacy ? "" : v.Label ?? "",
                ["file_system"] = v.FileSystem,
                ["size_bytes"] = v.SizeBytes,
                ["free_bytes"] = v.FreeBytes,
                ["drive_type"] = v.Disks.Any(d => capture.Disks.FirstOrDefault(k => k.Name == d)?.Transport == "usb") ? "Removable" : "Fixed",
                ["disk_model"] = v.Disks.Count == 1 ? capture.Disks.FirstOrDefault(k => k.Name == v.Disks[0])?.DisplayName : null,
                ["bus_type"] = v.Disks.Count == 1 ? capture.Disks.FirstOrDefault(k => k.Name == v.Disks[0])?.Transport : null
            }).ToArray()),
            ["dead_devices"] = new JsonArray(capture.Pci.Where(d => d.Driver is null && d.BaseClass is 0x01 or 0x02 or 0x03 or 0x0C).Select(d => (JsonNode)new JsonObject
            {
                ["name"] = $"{d.VendorShort} {d.Name}",
                ["class"] = d.ClassName,
                ["error_code"] = 28,
                ["error_desc"] = "No kernel driver attached"
            }).ToArray()),
            ["usb_devices"] = new JsonArray(capture.UsbDevices.Where(d => !d.IsRootHub).Select(d => (JsonNode)new JsonObject
            {
                ["name"] = d.ProductName ?? $"USB {d.Vendor:X4}:{d.Product:X4}",
                ["class"] = d.IsHub ? "USB" : "USBDevice",
                ["status"] = d.Interfaces.Any(i => i.Driver is not null) ? "OK" : "Unknown"
            }).ToArray()),
            ["pcie_topology"] = new JsonArray(capture.Pci.Where(d => !d.IsBridge && d.Link is not null).Select(d => (JsonNode)new JsonObject
            {
                ["name"] = $"{d.VendorShort} {d.Name}",
                ["class"] = d.ClassName,
                ["location"] = d.Address
            }).ToArray()),
            ["expansion_slots"] = new JsonArray(Sys.Children("/sys/bus/pci/slots").Select(s => (JsonNode)new JsonObject
            {
                ["designation"] = s,
                ["description"] = "PCI Express",
                ["current_usage"] = capture.Pci.Any(d => d.SlotName == s) ? 4 : 3
            }).ToArray()),
            ["form_factor"] = capture.FormFactor switch { "Laptop" => "laptop", "Desktop" => "desktop", "Server" => "server", var other => other.ToLowerInvariant() }
        };
        return scan.ToJsonString(indented ? Json.Indented : Json.Options);
    }

    /// <summary>Where a PCI device attaches: inside the CPU, on the CPU's own lanes, or behind the chipset.</summary>
    internal static string Attach(HardwareCapture capture, PciDevice device)
    {
        var top = device;
        while (top.Parent is not null) top = top.Parent;
        var intel = capture.Pci.Any(d => d.Address.EndsWith(":00:00.0", StringComparison.Ordinal) && d.Vendor == 0x8086);
        if (top == device && device.BaseClass == 0x03) return "integrated";
        if (intel) return top.Slot >= 0x10 ? "chipset" : "cpu";
        // AMD: behind the chipset's upstream switch, or inside the processor (its internal bridges).
        for (var p = device.Parent; p is not null; p = p.Parent)
        {
            if (p.Vendor == 0x1022 && p.IsBridge && p.Parent is not null && (p.DeviceName ?? "").Contains("Upstream", StringComparison.OrdinalIgnoreCase)) return "chipset";
        }
        if (device.BaseClass == 0x03 && device.Vendor == 0x1002 && device.Parent is { Bus: 0, Slot: 0x08 }) return "integrated";
        return "cpu";
    }

    private static string? Link(PciDevice device) =>
        device.Link is { } link ? $"PCIe {Format.Generation(link.Gen)} x{link.Width}" : null;

    private static string ChipsetName(HardwareCapture capture, PciDevice? lpc)
    {
        if (lpc?.DeviceName is { } name)
        {
            var match = System.Text.RegularExpressions.Regex.Match(name, @"\(([BHQZXWC]\d{3}[A-Z]?)\)|\b([BHQZXWC]\d{3}[A-Z]?)\s+Chipset");
            if (match.Success) return $"Intel {(match.Groups[1].Success ? match.Groups[1] : match.Groups[2]).Value} chipset";
            if (lpc.Vendor == 0x8086) return "Intel chipset";
        }
        var amd = System.Text.RegularExpressions.Regex.Match(capture.Identity.BoardName ?? "", @"\b([ABX][3-9][2-7]0)[A-Z]?\b");
        return amd.Success ? $"AMD {amd.Groups[1].Value} chipset" : lpc is null ? "AMD chipset" : $"{lpc.VendorShort} chipset";
    }
}
