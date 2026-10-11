using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;

namespace BoardScout.Views;

/// <summary>
/// Builds the Connections map for Linux: every device from the CPU outward — PCIe slots and the chipset, USB
/// controllers, hubs, ports and what is plugged into them, drives, monitors, Bluetooth, and the network path
/// through the router to the Internet — with the link each connection actually negotiated. The result is the
/// same data the Windows edition sends, so the shared Connections view renders it unchanged.
/// </summary>
internal static partial class LinuxConnections
{
    /// <summary>A USB controller's ports, for the optimization plan.</summary>
    internal sealed record ControllerPorts(UsbControllerSummary Summary, IReadOnlyList<UsbDevice> Devices);

    internal sealed record BuildResult(ConnectionsSnapshot Snapshot, IReadOnlyList<ControllerPorts> Controllers);

    public static BuildResult Build(HardwareCapture capture, bool privacy, DiscoveryResult? discovery)
    {
        var builder = new MapBuilder(capture, privacy, discovery);
        var root = builder.Build();
        return new BuildResult(new ConnectionsSnapshot
        {
            Root = root,
            Notes = builder.Notes,
            Privacy = privacy,
            Elevated = capture.Root,
            Discovery = discovery is null ? "pending" : "done",
            GatheredMs = capture.CaptureMs,
            GatheredAt = capture.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        }, builder.Controllers);
    }

    public static string ToJson(ConnectionsSnapshot snapshot) => JsonSerializer.Serialize(snapshot, Json.Options);

    public static string PciNodeId(PciDevice device) => "p" + Format.ShortHash(device.Address);
    public static string UsbNodeId(UsbDevice device) => "u" + Format.ShortHash($"{device.Name}|{device.Vendor:X4}:{device.Product:X4}");
    public static string DiskNodeId(BlockDisk disk) => "k" + Format.ShortHash(disk.Name);
    public static string LinkNodeId(NetLink link) => "n" + Format.ShortHash("if:" + link.Name);

    public static IEnumerable<ConnectionNode> Walk(ConnectionNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var nested in Walk(child)) yield return nested;
    }

    private sealed partial class MapBuilder(HardwareCapture capture, bool privacy, DiscoveryResult? discovery)
    {
        private const string Hidden = "Hidden (privacy mode)";

        private readonly HashSet<string> _routersPlaced = [];
        private readonly HashSet<string> _linksPlaced = new(StringComparer.Ordinal);
        private readonly HashSet<string> _disksPlaced = new(StringComparer.Ordinal);
        private readonly HashSet<string> _busesPlaced = new(StringComparer.Ordinal);
        private readonly HashSet<string> _radiosPlaced = new(StringComparer.Ordinal);
        private readonly List<string> _builtIn = [];
        private readonly List<string> _cpuBuiltIn = [];
        private readonly Dictionary<string, string> _codecs = KernelInfo.AudioCodecs();
        private ConnectionNode? _cpu;
        private ConnectionNode? _platform;
        private bool _gpuSensorTaken;

        public List<string> Notes { get; } = [];
        public List<ControllerPorts> Controllers { get; } = [];

        public ConnectionNode Build()
        {
            var cpu = capture.Cpu;
            var board = capture.Identity.Board ?? capture.DeviceTreeModel;
            _cpu = new ConnectionNode
            {
                Id = "cpu",
                Kind = "cpu",
                Name = Format.ShortCpuName(cpu.Name),
                Detail = $"{cpu.Threads} threads" + (board is null ? "" : $" · {board}"),
                Sensor = "cpu",
                Facts =
                {
                    new("Processor", cpu.Name),
                    new("Cores", cpu.Sockets > 1 ? $"{cpu.Cores} cores, {cpu.Threads} threads on {cpu.Sockets} sockets" : $"{cpu.Cores} cores, {cpu.Threads} threads")
                }
            };
            if (cpu.MaxMhz is { } mhz) _cpu.Facts.Add(new("Top speed", $"{mhz / 1000:0.0#} GHz"));
            if (cpu.Governor is not null)
                _cpu.Facts.Add(new("Frequency policy", $"{cpu.Governor}{(cpu.EnergyPreference is null ? "" : $" ({cpu.EnergyPreference.Replace('_', ' ')})")} · {cpu.ScalingDriver ?? "cpufreq"}"));
            if (cpu.Microcode is not null) _cpu.Facts.Add(new("Microcode", cpu.Microcode));
            if (board is not null) _cpu.Facts.Add(new("Motherboard", board));
            if (capture.Identity.BiosVersion is { } bios)
                _cpu.Facts.Add(new("BIOS", capture.Identity.BiosDate is { } date ? $"{bios} ({date})" : bios));
            _cpu.Facts.Add(new("Kernel", capture.Kernel));
            if (Pcie3Apu().IsMatch(cpu.Name))
            {
                _cpu.Facts.Add(new("CPU PCIe lanes", "PCIe 3.0"));
                _cpu.Note = "Ryzen 4000G and 5000G APUs run their PCIe lanes at 3.0, so PCIe 4.0 cards and drives on CPU lanes link at 3.0.";
            }

            AddPciTree();
            AddPlatformDevices();
            if (_platform is not null) _cpu.Children.Add(_platform);
            AddMemoryBand();
            AddVirtualAdapters();

            if (_builtIn.Count > 0)
            {
                var host = Walk(_cpu).FirstOrDefault(n => n.Id == "chipset") ?? _cpu;
                host.Facts.Add(new("Also built in", string.Join(", ", _builtIn.Distinct())));
            }
            if (_cpuBuiltIn.Count > 0) _cpu.Facts.Add(new("Also in the processor", string.Join(", ", _cpuBuiltIn.Distinct())));
            if (capture.Links.Any(l => l.Wireless) && !Tools.Has("iw") && !Tools.Has("nmcli"))
                Notes.Add("Install iw (or NetworkManager) for Wi-Fi signal, band, and the router's model.");
            if (capture.Pci.Count == 0)
                Notes.Add("No PCI bus here: devices hang off the system-on-chip.");
            return _cpu;
        }

        // ---- PCI -----------------------------------------------------------------------------------

        private bool IntelPlatform =>
            capture.Pci.Any(d => d.Address.EndsWith(":00:00.0", StringComparison.Ordinal) && d.Vendor == 0x8086) ||
            capture.Cpu.Vendor == "GenuineIntel";

        private void AddPciTree()
        {
            var roots = capture.Pci.Where(d => d.Parent is null).OrderBy(d => d.Address, StringComparer.Ordinal).ToList();
            ConnectionNode? pch = null;
            if (IntelPlatform)
            {
                // Intel: devices 0x10 and up on the root bus live in the chipset (PCH), which reaches the CPU over DMI.
                var pchDevices = roots.Where(d => d.Bus == 0 && d.Slot >= 0x10).ToList();
                if (pchDevices.Count > 0)
                {
                    pch = BuildIntelChipset(pchDevices);
                    foreach (var device in pchDevices)
                    {
                        if (device.IsBridge) AddBehindPort(pch, device);
                        else if (BuildEndpoint(device, pchDevices, internalLink: true) is { } node) pch.Children.Add(node);
                        else NoteBuiltIn(device, _builtIn);
                    }
                    roots = roots.Except(pchDevices).ToList();
                }
            }

            foreach (var device in roots)
            {
                if (device.IsBridge)
                {
                    AddBehindRootPort(_cpu!, device);
                    continue;
                }
                // Endpoints on the CPU's own root bus: Intel's integrated graphics, AMD's root complex and IOMMU.
                if (BuildEndpoint(device, roots, internalLink: true) is { } node)
                {
                    if (node.Kind == "gpu")
                    {
                        _cpu!.Children.Add(node);
                        continue;
                    }
                    PlatformGroup().Children.Add(node);
                }
                else NoteBuiltIn(device, _cpuBuiltIn);
            }
            if (pch is not null && pch.Children.Count > 0) _cpu!.Children.Add(pch);
        }

        private ConnectionNode BuildIntelChipset(List<PciDevice> devices)
        {
            var lpc = devices.FirstOrDefault(d => d.BaseClass == 0x06 && d.SubClass == 0x01);
            var model = lpc?.DeviceName is { } lpcName ? IntelChipsetModel().Match(lpcName) : Match.Empty;
            var chipset = new ConnectionNode
            {
                Id = "chipset",
                Kind = "chipset",
                Name = model.Success ? $"Intel {(model.Groups[1].Success ? model.Groups[1] : model.Groups[2]).Value.ToUpperInvariant()} chipset" : "Intel chipset (PCH)",
                Detail = "Connects USB, SATA, audio, and the slots it owns",
                Link = new ConnectionLink { Bus = "pcie", Label = "DMI (Direct Media Interface) to the CPU", Short = "DMI" },
                Facts = { new("Link to CPU", "DMI, Intel's chipset link (PCIe-like; Linux doesn't report its width)") }
            };
            if (Sensors.Chipset(capture.Sensors) is { } temperature)
            {
                chipset.Sensor = "chipset";
                chipset.TemperatureC = temperature;
            }
            if (lpc?.DeviceName is { } name) chipset.Facts.Add(new("Platform controller", name));
            chipset.Note = "Everything behind the chipset shares its one link to the CPU, so drives, USB, and network here can't all run flat out at once.";
            return chipset;
        }

        private void AddBehindRootPort(ConnectionNode parent, PciDevice port)
        {
            var functions = port.Children;
            if (functions.Count == 0) return;

            if (functions.Any(f => f.IsBridge && f.Vendor == 0x1022 && IsUpstreamSwitch(f)))
            {
                parent.Children.Add(BuildAmdChipset(port, functions));
                return;
            }

            if (functions.Count >= 3 && functions.All(f => f.Vendor is 0x1022 or 0x1002) &&
                functions.Any(f => f.BaseClass == 0x0C && f.SubClass == 0x03 || f.BaseClass == 0x04))
            {
                var link = functions.Select(f => f.Link).FirstOrDefault(l => l is not null);
                var integrated = new ConnectionNode
                {
                    Id = "integrated",
                    Kind = "integrated",
                    Name = "Built into the CPU",
                    Detail = "USB, audio, and security inside the processor",
                    Link = new ConnectionLink
                    {
                        Bus = "internal",
                        Label = link is null ? "Inside the processor" : $"Inside the processor (internal PCIe {Format.Generation(link.Gen)} x{link.Width})",
                        Short = "internal",
                        Gbps = link is null ? null : Format.LaneGbps(link.Gen) * link.Width
                    }
                };
                foreach (var function in functions) AddDevice(integrated, function, functions, internalLink: true);
                if (integrated.Children.Count > 0) parent.Children.Add(integrated);
                return;
            }

            foreach (var function in functions) AddDevice(parent, function, functions, internalLink: false);
        }

        // An Intel chipset root port: its devices hang off the chipset node.
        private void AddBehindPort(ConnectionNode parent, PciDevice port)
        {
            foreach (var function in port.Children) AddDevice(parent, function, port.Children, internalLink: false);
        }

        private ConnectionNode BuildAmdChipset(PciDevice port, List<PciDevice> functions)
        {
            var upstream = functions.First(f => f.IsBridge && f.Vendor == 0x1022 && IsUpstreamSwitch(f));
            var link = upstream.Link ?? port.Link;
            var model = ChipsetModel().Match(capture.Identity.BoardName ?? "");
            var chipset = new ConnectionNode
            {
                Id = "chipset",
                Kind = "chipset",
                Name = model.Success ? $"AMD {model.Groups[1].Value.ToUpperInvariant()} chipset" : "AMD chipset",
                Detail = link is null ? "Connects USB, SATA, M.2, and network" : $"Everything here shares one PCIe {Format.Generation(link.Gen)} x{link.Width} link",
                Sensor = "chipset",
                Link = link is null ? new ConnectionLink { Bus = "pcie", Label = "PCIe", Short = "PCIe" } : PciLink(link)
            };
            if (link is not null)
            {
                var rate = Format.Rate(Format.LaneGbps(link.Gen) * link.Width);
                chipset.Facts.Add(new("Link to CPU", $"PCIe {Format.Generation(link.Gen)} x{link.Width} ({rate})"));
                chipset.Note = $"Drives, USB, and network behind the chipset share this one link to the CPU — about {rate} in each direction.";
            }
            foreach (var function in functions) AddDevice(chipset, function, functions, internalLink: true);
            return chipset;
        }

        // Switch ports and bridges are pass-through: their devices attach to the same parent.
        private void AddDevice(ConnectionNode parent, PciDevice device, List<PciDevice> siblings, bool internalLink)
        {
            if (device.IsBridge)
            {
                foreach (var child in device.Children)
                {
                    if (child.IsBridge) AddDevice(parent, child, device.Children, internalLink: false);
                    else if (BuildEndpoint(child, device.Children, internalLink: false) is { } node) parent.Children.Add(node);
                    else NoteBuiltIn(child, parent.Kind == "chipset" ? _builtIn : _cpuBuiltIn);
                }
                return;
            }
            if (BuildEndpoint(device, siblings, internalLink) is { } built) parent.Children.Add(built);
            else NoteBuiltIn(device, parent.Kind == "chipset" ? _builtIn : _cpuBuiltIn);
        }

        private ConnectionNode? BuildEndpoint(PciDevice device, IReadOnlyList<PciDevice> siblings, bool internalLink)
        {
            ConnectionNode? node = (device.BaseClass, device.SubClass) switch
            {
                (0x03, _) => BuildGpu(device, siblings),
                (0x02, _) => BuildPciNetwork(device),
                (0x0C, 0x03) => BuildUsbController(device),
                (0x01, 0x08) => BuildNvme(device),
                (0x01, _) => BuildStorageController(device),
                (0x04, _) when IsGpuAudio(device, siblings) => null,
                (0x04, 0x03) or (0x04, 0x01) => BuildAudio(device),
                (0x0D, 0x11) => BuildBluetoothFor(device.SysPath, NewPciNode(device, "bluetooth", "Bluetooth")),
                _ when device.Driver is null && device.BaseClass is 0x01 or 0x02 or 0x03 or 0x04 or 0x0C or 0x0D => BuildUnknown(device),
                _ => null
            };
            if (node is null) return null;
            if (node.Link is null)
            {
                node.Link = internalLink
                    ? new ConnectionLink { Bus = "internal", Label = "Built in (no external link)", Short = "internal" }
                    : device.Link is null ? new ConnectionLink { Bus = "pcie", Label = "PCIe", Short = "PCIe" } : PciLink(device.Link);
            }
            if (!internalLink && device.SlotName is { } slot) node.Facts.Add(new("Slot", slot));
            AddInterruptFacts(node, device);
            if (device.Driver is null && node.Warning is null)
            {
                node.Problem = true;
                node.Warning = "No kernel driver is attached to this device, so Linux can't use it. Install the driver package for it, or check that its module isn't blacklisted.";
            }
            return node;
        }

        private static void NoteBuiltIn(PciDevice device, List<string> list)
        {
            if (device.IsBridge || device.BaseClass == 0x06 && device.SubClass is 0x00 or 0x01) return;
            list.Add(device.DeviceName is null ? device.ClassName : PciIds.CleanDevice(device.DeviceName));
        }

        private ConnectionNode PlatformGroup() => _platform ??= new ConnectionNode
        {
            Id = "platform",
            Kind = "chipset",
            Name = capture.Pci.Count == 0 ? "Built into the system-on-chip" : "Chipset and on-board controllers",
            Detail = capture.Pci.Count == 0 ? "Controllers inside the processor package" : "Controllers on the main bus",
            Link = new ConnectionLink { Bus = "internal", Label = "On the main bus", Short = "on-board" }
        };

        // Message-signaled interrupts (MSI/MSI-X) never conflict; legacy lines can be shared between devices.
        private void AddInterruptFacts(ConnectionNode node, PciDevice device)
        {
            var lines = capture.Irqs.Where(l => l.PciAddress == device.Address).ToList();
            if (device.MsiVectors > 0 || lines.Any(l => l.MessageSignaled))
            {
                var vectors = Math.Max(device.MsiVectors, lines.Count(l => l.MessageSignaled));
                node.Facts.Add(new("Interrupts", $"{vectors} message-signaled (MSI), the modern kind that never conflicts"));
            }
            else if (device.Irq is { } irq)
            {
                var shared = capture.Irqs.FirstOrDefault(l => l.Id == irq.ToString(CultureInfo.InvariantCulture))?.Handlers.Count > 1;
                node.Facts.Add(new("Interrupts", $"Legacy line IRQ {irq}{(shared ? ", shared with other drivers" : "")}"));
            }
        }

        // ---- Graphics and monitors ------------------------------------------------------------------

        private ConnectionNode BuildGpu(PciDevice device, IReadOnlyList<PciDevice> siblings)
        {
            var vendor = device.VendorShort;
            var fullName = device.DeviceName ?? device.ClassName;
            var name = device.Name;
            if (!name.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) && vendor is "Intel" or "AMD" or "NVIDIA" or "Matrox" or "ASPEED")
                name = name.StartsWith("Radeon", StringComparison.OrdinalIgnoreCase) || name.StartsWith("GeForce", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Arc", StringComparison.OrdinalIgnoreCase)
                    ? name
                    : $"{vendor} {name}";
            var gpu = NewPciNode(device, "gpu", name);
            var vram = Sys.Long(Path.Combine(device.SysPath, "mem_info_vram_total"));
            var integrated = device.Bus == 0 || device.Parent is { Bus: 0, Slot: 0x08 } && device.Vendor == 0x1002;
            gpu.Detail = string.Join(" · ", new[]
            {
                vendor,
                vram is > 0 ? $"{vram.Value / 1_073_741_824d:0.#} GB" : integrated ? "shares system memory" : null
            }.Where(s => s is not null));
            gpu.Facts.Add(new("Graphics", fullName));
            if (device.SubsystemName is { } board) gpu.Facts.Add(new("Card", board));
            if (vram is > 0) gpu.Facts.Add(new("Video memory", $"{vram.Value / 1_073_741_824d:0.#} GB"));
            if (Sys.Read(Path.Combine(device.SysPath, "vbios_version")) is { } vbios) gpu.Facts.Add(new("Video BIOS", vbios));
            if (device.BootVga) gpu.Facts.Add(new("Primary display", "Yes: the firmware showed its boot screen on this one"));
            AddDriverFact(gpu, device);
            if (device.PowerState is "D3cold" or "D3hot")
                gpu.Facts.Add(new("Power state", $"Asleep ({device.PowerState}): powered down while nothing uses it"));
            if (!_gpuSensorTaken && Sensors.Gpu(capture.Sensors, device.Address) is { } temperature)
            {
                gpu.Sensor = "gpu";
                gpu.TemperatureC = temperature;
                _gpuSensorTaken = true;
            }
            if (integrated)
            {
                gpu.Link = new ConnectionLink { Bus = "internal", Label = "Inside the processor", Short = "built in" };
            }
            else if (SlotLink(device) is { } link)
            {
                gpu.Link = PciLink(link);
                AddLinkFacts(gpu, link);
                if (link.BelowMax)
                    gpu.Warning = LinkWarning(link, "card") +
                        " Graphics cards also drop to a slower link when idle to save power, so check again while a game or video is running.";
            }
            foreach (var sibling in siblings.Where(s => s != device && s.Vendor == device.Vendor && s.BaseClass == 0x04))
                gpu.Facts.Add(new("Also on this card", "HDMI/DisplayPort audio"));

            var outputs = capture.Displays.Where(o => o.GpuAddress == device.Address).ToList();
            foreach (var output in outputs.Where(o => o.Connected)) gpu.Children.Add(BuildMonitor(output));
            var free = outputs.Where(o => !o.Connected).GroupBy(o => o.Kind).Select(g => g.Count() == 1 ? g.Key : $"{g.Key} ×{g.Count()}").ToList();
            if (free.Count > 0) gpu.Facts.Add(new("Free outputs", string.Join(", ", free)));
            return gpu;
        }

        // A graphics card behind its own PCIe switch (most AMD cards) reports the switch's internal link;
        // the slot's link is the one on the card's upstream switch port.
        private static PciLinkInfo? SlotLink(PciDevice device)
        {
            var link = device.Link;
            for (var p = device.Parent; p is not null && p.Vendor == device.Vendor && p.IsBridge; p = p.Parent)
                if (p.Link is not null) link = p.Link;
            return link;
        }

        private ConnectionNode BuildMonitor(DisplayOutput output)
        {
            var edid = output.Edid;
            var name = edid?.Name ?? (edid?.Maker is { } maker ? $"{maker} monitor" : "Monitor");
            var monitor = new ConnectionNode
            {
                Id = "m" + Format.ShortHash($"{output.Card}-{output.Connector}"),
                Kind = "monitor",
                Name = privacy ? Privacy.Scrub(name) : name
            };
            var mode = edid is { Width: > 0, Height: > 0 } ? $"{edid.Width}×{edid.Height}" : output.PreferredMode?.Replace('x', '×');
            var refresh = edid?.RefreshHz is > 0 ? $"{edid.RefreshHz.Value:0.##} Hz" : null;
            monitor.Detail = string.Join(" @ ", new[] { mode, refresh }.Where(s => s is not null));
            var connector = output.Kind;
            monitor.Link = new ConnectionLink
            {
                Bus = "display",
                Label = string.Join(" · ", new[] { connector, mode, refresh }.Where(s => s is not null)),
                Short = connector switch { "DisplayPort" => "DP", var k when k.StartsWith("eDP", StringComparison.Ordinal) => "eDP", var k => k.Split(' ')[0] }
            };
            monitor.Facts.Add(new("Connection", connector));
            if (mode is not null) monitor.Facts.Add(new("Best mode", string.Join(" @ ", new[] { mode, refresh }.Where(s => s is not null)) + " (what the monitor asks for)"));
            if (edid?.Maker is { } made) monitor.Facts.Add(new("Made by", made));
            if (edid?.DiagonalInches is { } inches) monitor.Facts.Add(new("Screen size", $"about {inches:0}-inch"));
            if (edid?.Year is { } year) monitor.Facts.Add(new("Made in", year.ToString(CultureInfo.InvariantCulture)));
            monitor.Facts.Add(new("GPU output", output.Connector));
            if (!output.Enabled) monitor.Note = "Connected, but no picture is being sent to it right now.";
            return monitor;
        }

        // ---- Storage ---------------------------------------------------------------------------------

        private ConnectionNode BuildNvme(PciDevice controller)
        {
            var disk = capture.Disks.FirstOrDefault(d => d.Nvme && d.PciAddress == controller.Address);
            var node = disk is null ? NewPciNode(controller, "nvme", "NVMe SSD") : BuildDisk(disk, "nvme");
            node.Id = PciNodeId(controller);
            AddDriverFact(node, controller, "Controller driver");
            if (controller.Link is { } link)
            {
                node.Link = PciLink(link);
                AddLinkFacts(node, link);
                if (link.BelowMax) node.Warning = LinkWarning(link, "drive");
            }
            return node;
        }

        private ConnectionNode BuildStorageController(PciDevice controller)
        {
            var drives = capture.Disks.Where(d => d.PciAddress == controller.Address && !d.Nvme && d.Transport != "usb").ToList();
            var sata = controller.SubClass == 0x06;
            var (ports, speeds) = sata ? KernelInfo.SataPorts(controller.SysPath) : (0, new Dictionary<int, string>());
            var node = NewPciNode(controller, "sata", sata ? "SATA controller" : controller.Name);
            node.Detail = ports > 0
                ? $"{drives.Count(d => !d.Optical) + drives.Count(d => d.Optical)} of {ports} ports in use"
                : drives.Count switch { 0 => "No drives connected", 1 => "1 drive", _ => $"{drives.Count} drives" };
            node.Facts.Add(new("Controller", controller.DeviceName is null ? controller.ClassName : $"{controller.VendorShort} {PciIds.CleanDevice(controller.DeviceName)}"));
            if (sata) node.Facts.Add(new("Mode", controller.ProgIf == 0x01 ? "AHCI" : "SATA"));
            else if (controller.SubClass == 0x04) node.Facts.Add(new("Mode", "RAID"));
            if (ports > 0) node.Facts.Add(new("Ports", $"{ports} ({drives.Count} in use)"));
            AddDriverFact(node, controller);
            foreach (var drive in drives)
            {
                var child = BuildDisk(drive, drive.Optical ? "optical" : "disk");
                var port = KernelInfo.AtaPortOf(drive.SysPath);
                if (drive.Rpm is { } rpm) child.Facts.Add(new("Spins at", $"{rpm:N0} RPM"));
                if (sata && port is { } number && speeds.TryGetValue(number, out var speed))
                {
                    var (gbps, label) = KernelInfo.SataSpeed(speed);
                    child.Link = new ConnectionLink { Bus = "sata", Label = $"{label} (port {number})", Short = $"{gbps:0.#} Gbps", Gbps = gbps };
                    if (drive.SataMaxGbps is { } best && best > gbps)
                    {
                        var (_, bestLabel) = KernelInfo.SataSpeed(best.ToString("0.0", CultureInfo.InvariantCulture));
                        child.Link.Max = bestLabel;
                        child.Link.MaxGbps = best;
                        child.Link.Degraded = true;
                        child.Warning = $"Linked at {gbps:0.#} Gbps; the drive supports {best:0.#} Gbps. The port may be an older SATA II one, or the cable is worn or loose: " +
                                        (drive.DriveType().Spinning == true ? "a hard drive barely notices, but an SSD loses half its speed." : "this SSD is running at half its speed.");
                    }
                    child.Facts.Add(new("SATA link", drive.SataMaxGbps is { } max && max <= gbps
                        ? $"{label} on port {number}, the drive's own maximum"
                        : $"{label} on port {number}"));
                }
                else
                {
                    child.Link = sata
                        ? new ConnectionLink { Bus = "sata", Label = "SATA (ports run at up to 6 Gbps)", Short = "SATA", Gbps = 6 }
                        : new ConnectionLink { Bus = "sata", Label = "Storage bus", Short = "disk" };
                }
                node.Children.Add(child);
            }
            return node;
        }

        private ConnectionNode BuildDisk(BlockDisk disk, string kind)
        {
            _disksPlaced.Add(disk.Name);
            var node = new ConnectionNode
            {
                Id = DiskNodeId(disk),
                Kind = kind,
                Name = privacy ? Privacy.Scrub(disk.DisplayName) : disk.DisplayName,
                Disk = disk.Optical ? null : disk.Index
            };
            var parts = new List<string>();
            if (disk.SizeBytes > 0) parts.Add(Format.Bytes(disk.SizeBytes));
            if (disk.Firmware is { } firmware)
            {
                parts.Add($"FW {firmware}");
                node.Facts.Add(new("Firmware", firmware));
            }
            node.Detail = string.Join(" · ", parts);
            if (disk.SizeBytes > 0) node.Facts.Add(new("Capacity", Format.Bytes(disk.SizeBytes)));
            node.Facts.Add(new("Device", $"/dev/{disk.Name}"));
            node.Facts.Add(new("Interface", disk.Transport switch
            {
                "nvme" => "NVMe",
                "sata" => "SATA",
                "usb" => "USB",
                "mmc" => "SD or eMMC",
                "virtio" => "Virtual disk (virtio)",
                "optical" => "Optical",
                _ => "SCSI/SAS"
            }));
            if (disk.TemperatureC is { } temperature)
            {
                node.TemperatureC = temperature;
                node.Facts.Add(new("Temperature", $"{temperature:0} °C"));
            }
            if (!disk.Optical)
            {
                var (spinning, fromModel) = disk.DriveType();
                node.Facts.Add(new("Drive type", spinning is { } spins
                    ? (spins ? "Hard drive (spinning disk)" : "Solid-state drive") +
                      (fromModel ? ", from its model number (the USB bridge doesn't pass the drive's own answer through)" : "")
                    : "Not reported: the drive or its USB bridge doesn't say whether it spins"));
            }
            if (disk.CacheType is { } cache)
                node.Facts.Add(new("Write cache", cache.Contains("back", StringComparison.Ordinal)
                    ? "On (write back): unmount before unplugging"
                    : "Off (write through): writes go straight to the drive"));
            if (!disk.Optical) node.Facts.Add(new("TRIM", disk.DiscardMaxBytes > 0 ? "Supported" : disk.DriveType().Spinning == true ? "Not needed (hard drive)" : "Not supported by this drive or its bridge"));
            if (disk.Scheduler is { } scheduler) node.Facts.Add(new("I/O scheduler", scheduler));
            var mounts = capture.Volumes.Where(v => v.Disks.Contains(disk.Name)).Select(v => v.MountPoint).ToList();
            if (mounts.Count > 0) node.Facts.Add(new("Mounted at", privacy ? $"{mounts.Count} place{(mounts.Count == 1 ? "" : "s")}" : string.Join(", ", mounts)));
            else if (disk.Partitions.Count > 0) node.Facts.Add(new("Mounted at", "Nothing mounted"));
            node.Facts.Add(new("Disk number", $"Disk {disk.Index}"));
            return node;
        }

        // ---- USB -------------------------------------------------------------------------------------

        private ConnectionNode BuildUsbController(PciDevice controller)
        {
            var buses = capture.Usb.Where(b => b.Controller == controller.Address).ToList();
            var node = BuildUsbControllerNode(PciNodeId(controller), buses, controller.DeviceName is null ? null : $"{controller.VendorShort} {PciIds.CleanDevice(controller.DeviceName)}");
            AddDriverFact(node, controller);
            return node;
        }

        private ConnectionNode BuildUsbControllerNode(string id, List<UsbBus> buses, string? controllerName)
        {
            foreach (var bus in buses) _busesPlaced.Add(bus.Controller);
            var fastest = buses.Select(b => b.RootHub.SpeedMbps).DefaultIfEmpty(0).Max();
            var name = fastest switch
            {
                >= 20000 => "USB 3.2 Gen 2x2 controller",
                >= 10000 => "USB 3.2 Gen 2 controller",
                >= 5000 => "USB 3 controller",
                >= 480 => "USB 2.0 controller",
                _ => "USB controller"
            };
            var node = new ConnectionNode { Id = id, Kind = "usb-controller", Name = name };
            var sockets = Sockets(buses);
            var devices = buses.SelectMany(b => b.RootHub.Children).ToList();
            var used = sockets.Count(s => s.InUse);
            node.Detail = sockets.Count > 0 ? $"{used} of {sockets.Count} ports in use" : Format.Plural(devices.Count, "device");
            if (controllerName is not null) node.Facts.Add(new("Controller", controllerName));
            if (sockets.Count > 0)
            {
                var usb3 = sockets.Count(s => s.Usb3 is not null);
                var internalPorts = sockets.Count(s => s.Hardwired);
                node.Facts.Add(new("Ports", $"{usb3} USB 3 and {sockets.Count - usb3} USB 2-only ({used} in use)" +
                    (internalPorts > 0 ? $"; {internalPorts} wired to built-in devices" : "")));
            }
            node.Facts.Add(new("Note", "Each USB 3 socket is two ports to Linux: one for USB 3 devices and one for USB 2 devices. BoardScout pairs them (the firmware says which belong together) and counts sockets."));
            node.Facts.Add(new("Plugged in but missing?", "A device that doesn't appear here at all never made a connection: the port saw nothing. " +
                "Reseat the plug, try another cable, and give bus-powered hard drives a powered port. Devices that connect but fail show up as a warning card."));
            foreach (var device in devices) node.Children.Add(BuildUsbDevice(device));
            foreach (var bus in buses) AddPortProblems(node, bus.RootHub);

            Controllers.Add(new ControllerPorts(new UsbControllerSummary
            {
                Node = id,
                Name = name,
                Where = "",
                Usb3Ports = sockets.Count(s => s.Usb3 is not null),
                Usb3Used = sockets.Count(s => s.Usb3 is not null && s.InUse),
                Usb2Ports = sockets.Count(s => s.Usb3 is null),
                Usb2Used = sockets.Count(s => s.Usb3 is null && s.InUse),
                Devices = node.Children.Where(c => !c.Problem).Select(c => c.Name).ToList(),
                PowerMa = buses.SelectMany(b => b.RootHub.Descendants()).Where(d => !d.SelfPowered).Sum(d => d.MaxPowerMa)
            }, buses.SelectMany(b => b.RootHub.Descendants()).ToList()));
            return node;
        }

        internal sealed record Socket(UsbPort? Usb2, UsbPort? Usb3)
        {
            public bool InUse => Usb2?.Device is not null || Usb3?.Device is not null;
            public bool Hardwired => (Usb2 ?? Usb3)?.ConnectType == "hardwired";
        }

        // Physical sockets: a USB 2 root port and the SuperSpeed port the firmware pairs it with count once.
        private static List<Socket> Sockets(List<UsbBus> buses)
        {
            var ports = buses.SelectMany(b => b.RootHub.Ports).Where(p => p.Wired).ToList();
            var byName = ports.ToDictionary(p => p.Name, StringComparer.Ordinal);
            var sockets = new List<Socket>();
            var taken = new HashSet<string>(StringComparer.Ordinal);
            foreach (var port in ports.Where(p => p.Hub?.SpeedMbps <= 480))
            {
                var peer = port.Peer is not null && byName.TryGetValue(port.Peer, out var p) ? p : null;
                if (peer is not null) taken.Add(peer.Name);
                sockets.Add(new Socket(port, peer));
            }
            var paired = sockets.Any(s => s.Usb3 is not null);
            foreach (var port in ports.Where(p => p.Hub?.SpeedMbps > 480 && !taken.Contains(p.Name)))
            {
                // Without pairing information, a USB 3 port's USB 2 half can't be told apart: count the USB 3 port only.
                if (!paired && sockets.Count > 0)
                {
                    var match = sockets.FirstOrDefault(s => s.Usb3 is null && s.Usb2?.Number == port.Number);
                    if (match is not null)
                    {
                        sockets[sockets.IndexOf(match)] = match with { Usb3 = port };
                        continue;
                    }
                }
                sockets.Add(new Socket(null, port));
            }
            return sockets;
        }

        private (string Vendor, string? Product)? Names(UsbDevice device) =>
            capture.UsbNames.TryGetValue((device.Vendor, device.Product), out var found) ? found : null;

        private ConnectionNode BuildUsbDevice(UsbDevice device)
        {
            var names = Names(device);
            var disk = capture.Disks.FirstOrDefault(d => d.UsbPath == device.SysPath);
            var net = capture.Links.FirstOrDefault(l => l.UsbPath == device.SysPath);
            var radio = capture.Bluetooth.FirstOrDefault(r => r.UsbPath == device.SysPath);
            var companion = device.IsHub && CompanionHub(device) is not null;

            ConnectionNode node;
            if (device.IsHub)
            {
                node = NewUsbNode(device, "usb-hub", UsbName(device, names, "USB hub"));
                var side = companion ? device.SpeedMbps >= 5000 ? "USB 3 side" : "USB 2 side" : null;
                node.Detail = string.Join(" · ", new[] { side, device.HubPorts > 0 ? $"{device.HubPorts} ports" : null, names?.Vendor }.Where(s => s is not null));
                if (companion)
                    node.Facts.Add(new("Why it appears twice", "USB 3 hubs contain a separate USB 2.0 hub for older devices; both share the same physical ports."));
                foreach (var child in device.Children) node.Children.Add(BuildUsbDevice(child));
            }
            else if (disk is not null)
            {
                node = BuildDisk(disk, "storage");
                node.Id = UsbNodeId(device);
                node.Eject = Tools.Has("udisksctl");
                node.Facts.Add(new("USB protocol", disk.UsbDriver == "uas"
                    ? "UAS (USB Attached SCSI, the faster protocol)"
                    : "Bulk-only mass storage (usb-storage driver)"));
                if (names?.Product is { } enclosure && !node.Name.Contains(enclosure, StringComparison.OrdinalIgnoreCase))
                    node.Facts.Add(new("USB bridge", $"{names.Value.Vendor} {enclosure}"));
                if (names?.Vendor is { } vendor && string.IsNullOrEmpty(node.Detail)) node.Detail = vendor;
            }
            else if (radio is not null)
            {
                node = BuildBluetoothFor(device.SysPath, NewUsbNode(device, "bluetooth", "Bluetooth"));
                if (names?.Product is { } chip) node.Facts.Insert(0, new("Radio", $"{names.Value.Vendor} {chip}"));
            }
            else if (net is not null)
            {
                node = BuildNetwork(net);
                node.Id = UsbNodeId(device);
            }
            else
            {
                var kind = UsbKind(device, names);
                node = NewUsbNode(device, kind, UsbName(device, names, FallbackName(kind)));
                node.Detail = names?.Vendor ?? device.Manufacturer;
            }

            node.Facts.Add(new("USB ID", $"{device.Vendor:X4}:{device.Product:X4}"));
            if (device.Manufacturer is { } maker && names?.Vendor is null) node.Facts.Add(new("Made by", maker));
            var link = UsbLinkFor(device, names, companion);
            node.Link = link.Link;
            if (device.Port is { } port)
            {
                var socket = port.Peer is not null ? " (a USB 3 socket" + (device.SpeedMbps <= 480 ? "; it came in on the socket's USB 2 wires)" : ")") : port.Hub?.SpeedMbps >= 5000 ? " (USB 3)" : " (USB 2)";
                node.Facts.Add(new("Hub port", $"{port.Number}{socket}"));
                if (port.ConnectType == "hardwired") node.Facts.Add(new("Built in", "Wired to this port inside the computer"));
            }
            if (device.MaxPowerMa > 0 || device.SelfPowered)
                node.Facts.Add(new("Power", $"Asks for up to {device.MaxPowerMa} mA from the port{(device.SelfPowered ? "; has its own power supply" : "")}"));
            if (device.RuntimeStatus == "suspended") node.Facts.Add(new("Power state", "Asleep: Linux suspended it while idle (USB autosuspend)"));
            if (device.IsHub) node.Facts.Add(new("Hub power", device.SelfPowered ? "Self-powered or built in" : "Bus-powered: shares one port's power with everything plugged into it"));
            var drivers = device.Interfaces.Select(i => i.Driver).Where(d => d is not null).Distinct().ToList();
            if (drivers.Count > 0) node.Facts.Add(new("Driver", string.Join(", ", drivers)));
            else if (!device.IsHub && device.Interfaces.Count > 0) node.Facts.Add(new("Driver", "None attached: nothing on Linux is using this device"));
            node.Facts.Add(new("USB version", $"{device.Version} (what the device reports at this speed)"));
            node.Facts.Add(new("USB speed", node.Link.Label));
            if (link.Warning is not null) node.Warning = link.Warning;
            else if (link.Note is not null) node.Note ??= link.Note;
            if (link.CapabilitySource is not null) node.Facts.Add(new("USB 3 capable", link.CapabilitySource));
            if (device.IsHub) AddPortProblems(node, device);
            return node;
        }

        // A USB 3 hub is two hubs: its SuperSpeed half and its USB 2 half, on ports the firmware pairs as peers.
        private UsbDevice? CompanionHub(UsbDevice hub)
        {
            var peer = hub.Port?.Peer;
            return peer is null ? null : capture.UsbDevices.FirstOrDefault(d => d != hub && d.IsHub && d.Port?.Name == peer && d.Vendor == hub.Vendor);
        }

        internal sealed record UsbLinkInfo(ConnectionLink Link, string? Warning, string? Note, string? CapabilitySource);

        private UsbLinkInfo UsbLinkFor(UsbDevice device, (string Vendor, string? Product)? names, bool companion)
        {
            var (label, shortLabel, gbps) = Usb.SpeedName(device.SpeedMbps);
            var link = new ConnectionLink { Bus = "usb", Label = label, Short = shortLabel, Gbps = gbps };
            if (companion || device.IsHub && device.SpeedMbps >= 5000) return new UsbLinkInfo(link, null, null, null);

            // A USB 3 device at USB 2 speed reports itself as USB 2.10, so its chip family tells instead.
            var capability = device.ReportsUsb3 ? "Yes: it reports USB 3" : Usb3Chip(device, names);
            var socketHasUsb3 = device.Port?.Peer is not null || device.Port?.Hub is { IsRootHub: false, SpeedMbps: >= 5000 };
            if (device.SpeedMbps <= 480 && capability is not null)
            {
                link.Max = "USB 3.2 Gen 1 · 5 Gbps";
                link.MaxGbps = 5;
                link.Degraded = true;
                var warning = socketHasUsb3
                    ? "Its USB chip is a USB 3 part, but it connected at USB 2.0 speed (480 Mbps) in a USB 3 socket: only the USB 2 wires made contact. A USB 2 cable or a worn plug is the likely cause: try a USB 3 cable and push the plug fully in (on Micro-B plugs the USB 3 contacts are on the wide half)."
                    : "Its USB chip is a USB 3 part, but it's in a USB 2-only port, so it runs at 480 Mbps. Move it to a USB 3 port (often blue or marked SS).";
                return new UsbLinkInfo(link, warning, null, capability);
            }
            if (device.SpeedMbps <= 480 && capture.Disks.Any(d => d.UsbPath == device.SysPath))
                return new UsbLinkInfo(link, null, "USB 2.0 limits this drive to roughly 40 MB/s.", null);
            return new UsbLinkInfo(link, null, null, device.ReportsUsb3 ? null : capability);
        }

        private static string? Usb3Chip(UsbDevice device, (string Vendor, string? Product)? names)
        {
            var text = $"{names?.Product} {device.ProductName}";
            if (Usb3Name().IsMatch(text)) return $"Yes: its {names?.Vendor ?? "controller"} chip ({(names?.Product ?? device.ProductName)?.Split(',')[0]}) is a USB 3 part";
            return null;
        }

        // A port where something is plugged in but Linux couldn't use it, or that tripped its over-current guard.
        private void AddPortProblems(ConnectionNode parent, UsbDevice hub)
        {
            foreach (var port in hub.Ports.Where(p => p.Device is null))
            {
                (string Title, string Warning)? problem = port.State switch
                {
                    "default" or "addressed" => ("Device failed to start", "Something is plugged into this port but didn't answer when Linux asked what it is. Unplug it and plug it back in; if it keeps failing, try another cable or port. Bus-powered hard drives often need a powered hub or a port on the back of the PC."),
                    _ when port.OverCurrent > 0 => ("Port shut off: over-current", $"A device here drew more current than the port allows, and the port switched itself off ({port.OverCurrent} time{(port.OverCurrent == 1 ? "" : "s")} since startup). Give it its own power supply or a powered hub."),
                    _ => null
                };
                if (problem is not { } found) continue;
                parent.Children.Add(new ConnectionNode
                {
                    Id = UsbNodeId(hub) + "p" + port.Number.ToString(CultureInfo.InvariantCulture),
                    Kind = "usb",
                    Name = found.Title,
                    Detail = $"Port {port.Number} · not working",
                    Problem = true,
                    Warning = found.Warning,
                    Link = new ConnectionLink { Bus = "usb", Label = "USB (the port reports a problem)", Short = "failed" },
                    Facts = { new("Hub port", port.Name), new("What the hub reports", port.State ?? "unknown") }
                });
            }
        }

        // ---- Bluetooth and audio ---------------------------------------------------------------------

        private ConnectionNode BuildBluetoothFor(string devicePath, ConnectionNode node)
        {
            var radio = capture.Bluetooth.FirstOrDefault(r => r.UsbPath == devicePath || r.DevicePath?.StartsWith(devicePath, StringComparison.Ordinal) == true);
            if (radio is null)
            {
                node.Detail = "Radio found";
                return node;
            }
            _radiosPlaced.Add(radio.Name);
            node.Facts.Add(new("Radio", radio.Name));
            if (radio.Blocked == true)
            {
                node.Detail = "Turned off";
                node.Facts.Add(new("State", "Turned off (rfkill): switch Bluetooth on in Settings to use it"));
            }
            if (!radio.PairedListed)
            {
                node.Detail ??= "Paired devices not listed";
                node.Facts.Add(new("Paired devices", "BlueZ's bluetoothctl isn't installed, so BoardScout can't list them"));
                return node;
            }
            node.Detail ??= radio.Paired.Count switch { 0 => "No paired devices", 1 => "1 paired device", _ => $"{radio.Paired.Count} paired devices" };
            foreach (var device in radio.Paired)
            {
                var looksPersonal = device.Name.Contains("'s ", StringComparison.Ordinal) || device.Name.Contains("’s ", StringComparison.Ordinal);
                node.Children.Add(new ConnectionNode
                {
                    Id = "b" + Format.ShortHash(device.Address),
                    Kind = "bt-device",
                    Name = privacy && looksPersonal ? "Bluetooth device" : privacy ? Privacy.Scrub(device.Name) : device.Name,
                    Detail = device.Connected ? "Bluetooth · connected" : "Bluetooth · paired",
                    Link = new ConnectionLink { Bus = "bluetooth", Label = "Bluetooth", Short = "BT", Gbps = 0.003 },
                    Facts = { new("Address", privacy ? Hidden : device.Address), new("Connected", device.Connected ? "Yes" : "No") }
                });
            }
            return node;
        }

        private ConnectionNode? BuildAudio(PciDevice controller)
        {
            var codec = _codecs.GetValueOrDefault(controller.Address);
            var node = NewPciNode(controller, "audio", codec ?? controller.Name);
            node.Detail = controller.Vendor is 0x1002 or 0x10DE ? "Sound over HDMI or DisplayPort" : "Audio";
            node.Facts.Add(new("Controller", controller.DeviceName is null ? controller.ClassName : $"{controller.VendorShort} {PciIds.CleanDevice(controller.DeviceName)}"));
            if (codec is not null) node.Facts.Add(new("Codec", codec));
            AddDriverFact(node, controller);
            return node;
        }

        private ConnectionNode BuildUnknown(PciDevice device)
        {
            var node = NewPciNode(device, "device", device.DeviceName is null ? device.ClassName : $"{device.VendorShort} {device.Name}");
            node.Detail = device.Driver is null ? "No driver" : device.ClassName;
            node.Facts.Add(new("Kind", device.ClassName));
            node.Facts.Add(new("PCI ID", $"{device.Vendor:x4}:{device.Device:x4}"));
            return node;
        }

        // ---- Network -------------------------------------------------------------------------------

        private ConnectionNode BuildPciNetwork(PciDevice device)
        {
            var link = capture.Links.FirstOrDefault(l => l.PciAddress == device.Address && l.Master is null) ??
                       capture.Links.FirstOrDefault(l => l.PciAddress == device.Address);
            if (link is not null)
            {
                var node = BuildNetwork(link);
                node.Id = PciNodeId(device);
                node.Facts.Insert(0, new("Adapter", $"{device.VendorShort} {device.Name}"));
                if (device.Link is { } pcie) node.Facts.Add(new("PCIe link", $"PCIe {Format.Generation(pcie.Gen)} x{pcie.Width}"));
                return node;
            }
            var wireless = device.SubClass == 0x80 || WirelessName().IsMatch(device.DeviceName ?? "");
            var orphan = NewPciNode(device, wireless ? "wifi" : "ethernet", wireless ? "Wi-Fi" : "Ethernet");
            orphan.Facts.Add(new("Adapter", $"{device.VendorShort} {device.Name}"));
            orphan.Detail = device.Driver is null ? "No driver loaded" : "No network interface";
            AddDriverFact(orphan, device);
            return orphan;
        }

        private ConnectionNode BuildNetwork(NetLink link)
        {
            _linksPlaced.Add(link.Name);
            var adapter = capture.Network.Adapters.FirstOrDefault(a => a.Id == link.Name);
            // A port enslaved to a bridge or bond carries the traffic; the addresses and routes live on the master.
            var master = link.Master is null ? null : capture.Network.Adapters.FirstOrDefault(a => a.Id == link.Master);
            if (link.Master is not null) _linksPlaced.Add(link.Master);
            var addressed = master ?? adapter;
            var wifi = link.Wireless ? capture.Network.Wifi.FirstOrDefault(w => w.InterfaceId == NetworkProbe.InterfaceGuid(link.Name)) : null;
            var node = new ConnectionNode
            {
                Id = LinkNodeId(link),
                Kind = link.Wireless ? "wifi" : "ethernet",
                Name = link.Wireless ? "Wi-Fi" : "Ethernet",
                Net = Format.NetKey(link.Name)
            };
            node.Facts.Add(new("Interface", link.Name));
            if (link.Driver is not null) node.Facts.Add(new("Driver", KernelInfo.Driver(link.Driver, capture.Kernel) is { Version: { } version } ? $"{link.Driver} {version}" : link.Driver));
            if (link.Master is not null)
                node.Facts.Add(new("Bridged", $"Part of {link.Master}: its addresses and routes are on {link.Master}"));
            var ipv4 = addressed?.IPv4.FirstOrDefault();
            var speed = link.Wireless && wifi?.ReceiveMbps is { } rx ? (long)(rx * 1_000_000) : link.SpeedMbps * 1_000_000;
            if (link.Up)
            {
                node.Detail = string.Join(" · ", new[] { speed > 0 ? Format.BitRate(speed) : null, ipv4?.Split('/')[0] }.Where(s => s is not null));
                if (link.Wireless && wifi?.Ssid is { } ssid) node.Detail = privacy ? $"Connected · {node.Detail}" : $"{ssid} · {node.Detail}";
            }
            else node.Detail = link.Wireless ? "Not connected" : "Cable unplugged or link down";

            AddProfileFacts(node, link.Name);
            node.Facts.Add(new("Status", link.Up ? "Connected" : "Disconnected"));
            if (link.SpeedMbps > 0 && link.Up)
                node.Facts.Add(new("Link speed", $"{Format.BitRate(link.SpeedMbps * 1_000_000)}{(link.Duplex is "full" or "half" ? $", {link.Duplex} duplex" : "")}"));
            if (link.Up && link.Duplex == "half")
                node.Warning = "The link negotiated half duplex: a bad cable, an old switch port, or a forced speed setting is the usual cause. It halves throughput and causes collisions.";
            if (addressed is not null) AddAddressFacts(node, addressed, link.Mac);
            else if (link.Mac is not null) node.Facts.Add(new("MAC address", privacy ? Hidden : link.Mac));
            if (link.Mtu > 0 && link.Mtu != 1500) node.Facts.Add(new("MTU", link.Mtu.ToString(CultureInfo.InvariantCulture)));
            if (wifi is not null) AddWifiFacts(node, wifi);
            if (link.Up && addressed is { Up: true, Gateways.Count: > 0 }) node.Children.Add(BuildRouter(addressed, wifi, link.Name));
            return node;
        }

        // NetworkManager's name for the connection ("Wired connection 1", or the Wi-Fi name).
        private void AddProfileFacts(ConnectionNode node, string interfaceName)
        {
            if (discovery is null || !discovery.Profiles.TryGetValue(NetworkProbe.InterfaceGuid(interfaceName), out var profile)) return;
            var generic = Regex.IsMatch(profile.Name, @"^(Wired connection \d+|Ethernet( \d+)?|netplan-.*)$", RegexOptions.IgnoreCase);
            node.Facts.Insert(1, new("Connection name", privacy && !generic ? Hidden : profile.Name));
        }

        private void AddAddressFacts(ConnectionNode node, AdapterFacts adapter, string? mac)
        {
            if (adapter.IPv4.Count > 0) node.Facts.Add(new("IPv4", string.Join(", ", adapter.IPv4)));
            if (adapter.IPv6.Count > 0)
                node.Facts.Add(new("IPv6", privacy
                    ? $"{adapter.IPv6.Count} address{(adapter.IPv6.Count == 1 ? "" : "es")} (hidden in privacy mode)"
                    : string.Join(", ", adapter.IPv6.Select(a => $"{a.Address} ({a.Scope})"))));
            if (adapter.Gateways.Count > 0) node.Facts.Add(new("Default gateway", string.Join(", ", adapter.Gateways)));
            if (adapter.DnsServers.Count > 0) node.Facts.Add(new("DNS servers", string.Join(", ", adapter.DnsServers)));
            if (adapter.DhcpServers.Count > 0) node.Facts.Add(new("DHCP server", string.Join(", ", adapter.DhcpServers)));
            if ((mac ?? adapter.Mac) is { } address) node.Facts.Add(new("MAC address", privacy ? Hidden : address));
            if (adapter.Primary) node.Facts.Add(new("Route to the Internet", "This adapter (the default route)"));
        }

        private void AddWifiFacts(ConnectionNode node, WifiFacts wifi)
        {
            if (wifi.State != "connected")
            {
                node.Facts.Add(new("Wi-Fi", $"Radio {wifi.State}"));
                return;
            }
            if (wifi.Ssid is not null) node.Facts.Add(new("Network (SSID)", privacy ? Hidden : wifi.Ssid));
            if (wifi.Bssid is not null) node.Facts.Add(new("Access point (BSSID)", privacy ? Hidden : wifi.Bssid));
            if (wifi.SignalPercent is { } signal)
                node.Facts.Add(new("Signal", wifi.RssiDbm is { } rssi ? $"{signal}% ({rssi} dBm)" : $"{signal}%"));
            var band = NetworkProbe.Band(wifi.FrequencyMhz, wifi.Channel);
            if (wifi.Channel is { } channel) node.Facts.Add(new("Channel", band is null ? $"{channel}" : $"{channel} ({band})"));
            if (wifi.Phy is not null) node.Facts.Add(new("Standard", wifi.Phy));
            if (wifi.ReceiveMbps is { } rx)
                node.Facts.Add(new("Link rate", wifi.TransmitMbps is { } tx ? $"{rx:0} Mbps down / {tx:0} Mbps up" : $"{rx:0} Mbps"));
            if (wifi.Security is not null) node.Facts.Add(new("Security", wifi.Security));
        }

        private ConnectionNode BuildRouter(AdapterFacts adapter, WifiFacts? wifi, string trafficInterface)
        {
            var gateway = adapter.Gateways[0];
            var key = gateway.ToString();
            capture.Network.Routers.TryGetValue(key, out var router);
            var link = RouterLink(adapter, wifi, trafficInterface);
            var netKey = Format.NetKey(trafficInterface);

            if (!_routersPlaced.Add(key))
            {
                return new ConnectionNode
                {
                    Id = $"router-{Format.ShortHash(key + adapter.Id)}",
                    Kind = "router",
                    Name = "Same router",
                    Detail = key,
                    Net = netKey,
                    Link = link,
                    Facts = { new("Address", key), new("Note", "This adapter reaches the same router as another adapter above.") }
                };
            }

            var node = new ConnectionNode
            {
                Id = $"router-{Format.ShortHash(key)}",
                Kind = "router",
                Name = "Router",
                Detail = router?.PingMs is { } ping ? $"{key} · {Format.Latency(ping)}" : key,
                Net = netKey,
                Link = link,
                Facts = { new("Address", key) }
            };
            if (router?.HostName is { } host)
            {
                node.Name = $"Router · {host}";
                node.Facts.Add(new("Name", host));
            }
            RouterIdentity? identity = null;
            discovery?.Routers.TryGetValue(key, out identity);
            var neighbors = (discovery?.Devices ?? [])
                .Where(d => !d.Address.Equals(gateway) && !adapter.DnsServers.Contains(d.Address) &&
                            adapter.IPv4.Any(cidr => NetworkDiscovery.InSubnet(d.Address, cidr)))
                .ToList();
            var units = neighbors.Where(d => d.Mac is not null && LanKind(d, router) == "mesh")
                .Select(d => (Mac: d.Mac!, Label: d.Address.ToString())).ToList();
            if (router?.Mac is { } routerMac) units.Add((routerMac, key));
            var radioOwners = (identity?.Radios ?? []).ToDictionary(r => r.Bssid, r => units
                .Select(u => (u.Mac, u.Label, Distance: MacDistance(r.Bssid, u.Mac)))
                .Where(u => u.Distance is <= 16)
                .OrderBy(u => u.Distance)
                .Select(u => (u.Mac, u.Label))
                .FirstOrDefault());
            if (identity is not null) AddIdentityFacts(node, identity, key, router, radioOwners);
            if (router?.Mac is { } mac) node.Facts.Add(new("MAC address", privacy ? Hidden : mac));
            if (router?.PingMs is { } latency) node.Facts.Add(new("Round trip", Format.Latency(latency)));
            if (adapter.DhcpServers.Any(d => d.Equals(gateway))) node.Facts.Add(new("Hands out addresses", "Yes (DHCP)"));
            if (adapter.DnsServers.Any(d => d.Equals(gateway))) node.Facts.Add(new("Answers DNS", "Yes"));
            if (wifi?.State == "connected" && wifi.Bssid is not null)
                node.Facts.Add(new("Wi-Fi access point", privacy ? Hidden : wifi.Bssid));

            var internet = new ConnectionNode
            {
                Id = $"internet-{Format.ShortHash(key)}",
                Kind = "internet",
                Name = "Internet",
                Detail = capture.Network.InternetAccess switch
                {
                    true => "NetworkManager reports Internet access",
                    false => "NetworkManager reports no Internet access",
                    _ => "Public IP on request"
                },
                Net = netKey,
                Link = new ConnectionLink { Bus = "wan", Label = "Your Internet connection (WAN)", Short = "WAN" },
                Facts =
                {
                    new("Connectivity check", capture.Network.InternetAccess switch
                    {
                        true => "Internet access (NetworkManager)",
                        false => "No Internet access (NetworkManager)",
                        _ => "Not checked: NetworkManager isn't running here"
                    }),
                    new("Public IP", "Not looked up yet")
                }
            };
            node.Children.Add(internet);

            var resolved = NetLinks.ReadDnsServers().Resolved;
            foreach (var dns in adapter.DnsServers.Where(d => !d.Equals(gateway)).Distinct())
            {
                capture.Network.Dns.TryGetValue(dns.ToString(), out var facts);
                var local = adapter.IPv4.Any(cidr => Format.SameSubnet(dns, cidr));
                var server = new ConnectionNode
                {
                    Id = $"dns-{Format.ShortHash(dns.ToString())}",
                    Kind = "dns",
                    Name = facts?.HostName is { } name ? $"DNS · {name}" : "DNS server",
                    Detail = dns.ToString(),
                    Link = local
                        ? new ConnectionLink { Bus = "lan", Label = "On your local network", Short = "LAN" }
                        : new ConnectionLink { Bus = "wan", Label = "Across the Internet", Short = "DNS" },
                    Facts = { new("Address", dns.ToString()), new("Where", local ? "Your local network" : "The Internet") }
                };
                if (resolved) server.Facts.Add(new("Asked through", "systemd-resolved (this computer's DNS cache)"));
                if (facts?.HostName is { } hostName)
                {
                    server.Facts.Add(new("Name", hostName));
                    if (hostName.Contains("pi.hole", StringComparison.OrdinalIgnoreCase))
                        server.Facts.Add(new("Looks like", "Pi-hole (network-wide ad blocking)"));
                }
                if (discovery?.Devices.FirstOrDefault(d => d.Address.Equals(dns)) is { } seen)
                {
                    if (seen.Vendor is not null) server.Facts.Add(new("Hardware maker", $"{seen.Vendor} (from its MAC address)"));
                    if (seen.Name is not null && !privacy) server.Facts.Add(new("Calls itself", $"{seen.Name} ({seen.NameSource})"));
                }
                (local ? node : internet).Children.Add(server);
            }

            if (neighbors.Count > 0)
            {
                var group = new ConnectionNode
                {
                    Id = $"lan-{Format.ShortHash(key)}",
                    Kind = "lan-group",
                    Name = "Devices on your network",
                    Detail = neighbors.Count == 1 ? "1 device found" : $"{neighbors.Count} devices found",
                    Link = new ConnectionLink { Bus = "lan", Label = "Your local network", Short = "LAN" },
                    Facts =
                    {
                        new("Found through", "This computer's neighbor cache, plus devices that answered multicast DNS or UPnP"),
                        new("Names from", "Multicast DNS, NetBIOS, UPnP, and your DNS server"),
                        new("Makers from", "The first half of each device's MAC address (IEEE registry)"),
                        new("Not listed", "Devices this computer has not talked to lately and that stay quiet; Find more devices pings every address to list them")
                    }
                };
                foreach (var device in neighbors) group.Children.Add(BuildLanDevice(device, router, identity, radioOwners));
                node.Children.Add(group);
            }
            return node;
        }

        private void AddIdentityFacts(ConnectionNode node, RouterIdentity identity, string address, RouterFacts? router,
            Dictionary<string, (string Mac, string Label)> radioOwners)
        {
            if (identity.DisplayName is { } model)
            {
                node.Name = model;
                node.Facts.Insert(0, new("Model", model));
                if (identity.NameSource is { } source) node.Facts.Insert(1, new("Identified from", source));
            }
            if (identity.DeviceName is { } deviceName && !privacy) node.Facts.Add(new("Calls itself", deviceName));
            if (identity.MacVendor is not null) node.Facts.Add(new("Network card maker", identity.MacVendor));
            if (identity.CertificateName is not null) node.Facts.Add(new("Web interface", $"https://{identity.CertificateName}"));
            if (identity.Ssids.Count > 0)
                node.Facts.Add(new("Wi-Fi it broadcasts", privacy ? Hidden : string.Join(", ", identity.Ssids)));
            if (identity.Radios.Count > 0)
            {
                node.Facts.Add(new("Access points in range", identity.UnitsInRange.ToString(CultureInfo.InvariantCulture)));
                node.Facts.Add(new("Radios heard from here", string.Join(", ", identity.Radios.Take(8).Select(r =>
                    $"{NetworkProbe.Band(r.FrequencyMhz, null)} {r.RssiDbm} dBm{(r.GatewayUnit ? " (this router)" : "")}"))));
                var nearest = identity.Radios[0];
                if (identity.UnitsInRange > 1)
                {
                    var owner = radioOwners.GetValueOrDefault(nearest.Bssid);
                    var which = nearest.GatewayUnit || owner.Label == address ? ": the router unit itself."
                        : owner.Label is not null ? $": the unit at {owner.Label}."
                        : ", a mesh unit rather than the router itself.";
                    node.Note = $"Your network is a mesh: {identity.UnitsInRange} access points are in range of this computer. " +
                        $"The strongest signal here is {SignalWord(nearest.RssiDbm)} ({nearest.RssiDbm} dBm, {NetworkProbe.Band(nearest.FrequencyMhz, null)}){which}";
                }
            }
            var parts = new List<string> { address };
            if (router?.PingMs is { } ping) parts.Add(Format.Latency(ping));
            if (identity.UnitsInRange > 1) parts.Add($"mesh · {identity.UnitsInRange} units");
            node.Detail = string.Join(" · ", parts);
        }

        private ConnectionNode BuildLanDevice(LanDevice device, RouterFacts? router, RouterIdentity? identity,
            Dictionary<string, (string Mac, string Label)> radioOwners)
        {
            var kind = LanKind(device, router);
            var octet = device.Address.GetAddressBytes()[3];
            var generic = device.Model ?? (kind == "mesh" && identity?.Model is { } model ? $"{model} unit" : LanKindName(kind, device.Vendor));
            var node = new ConnectionNode
            {
                Id = $"lan-{Format.ShortHash(device.Mac ?? device.Address.ToString())}",
                Kind = kind,
                Name = device.Name is not null && !privacy ? device.Name : generic,
                Detail = string.Join(" · ", new[] { device.Address.ToString(), device.Vendor ?? (device.RandomMac ? "private address" : null) }.Where(s => s is not null)),
                Link = new ConnectionLink { Bus = "lan", Label = "Your local network", Short = $".{octet}" }
            };
            node.Facts.Add(new("Address", device.Address.ToString()));
            if (device.Name is not null) node.Facts.Add(new("Calls itself", privacy ? Hidden : $"{device.Name} (from {device.NameSource})"));
            if (device.Model is not null) node.Facts.Add(new("Model", device.Model));
            if (device.Vendor is not null) node.Facts.Add(new("Made by", device.Mac is null ? device.Vendor : $"{device.Vendor} (from its MAC address)"));
            if (device.RandomMac)
                node.Facts.Add(new("Private address", "It uses a random or locally assigned MAC address (phones, tablets, laptops, and virtual machines do), so its maker can't be looked up."));
            if (device.Mac is not null) node.Facts.Add(new("MAC address", privacy ? Hidden : device.Mac));
            if (kind == "mesh" && identity is not null && device.Mac is not null)
            {
                var near = identity.Radios.Where(r => radioOwners.TryGetValue(r.Bssid, out var owner) &&
                    string.Equals(owner.Mac, device.Mac, StringComparison.OrdinalIgnoreCase)).ToList();
                if (near.Count > 0)
                {
                    var best = near.MaxBy(r => r.RssiDbm)!;
                    node.Facts.Add(new("Its Wi-Fi here", string.Join(", ", near.Select(r => $"{NetworkProbe.Band(r.FrequencyMhz, null)} {r.RssiDbm} dBm"))));
                    node.Detail = $"{node.Detail} · {SignalWord(best.RssiDbm)} signal";
                    if (identity.Radios.Count > 0 && best.RssiDbm == identity.Radios.Max(r => r.RssiDbm))
                        node.Note = "This is the access point closest to this computer.";
                }
            }
            return node;
        }

        private static int? MacDistance(string a, string b)
        {
            var x = Convert.FromHexString(new string(a.Where(char.IsAsciiHexDigit).ToArray()));
            var y = Convert.FromHexString(new string(b.Where(char.IsAsciiHexDigit).ToArray()));
            if (x.Length != 6 || y.Length != 6 || !x.AsSpan(0, 4).SequenceEqual(y.AsSpan(0, 4))) return null;
            return Math.Abs(((x[4] << 8) | x[5]) - ((y[4] << 8) | y[5]));
        }

        private static string LanKind(LanDevice device, RouterFacts? router)
        {
            var text = $"{device.Name} {device.Model} {device.Vendor}";
            if (router?.Mac is { } routerMac && device.Mac is { } mac && !device.RandomMac &&
                string.Equals(mac[..11], routerMac[..11], StringComparison.OrdinalIgnoreCase))
                return "mesh";
            if (Regex.IsMatch(text, @"Raspberry|pihole|pi-hole", RegexOptions.IgnoreCase)) return "pi";
            if (Regex.IsMatch(text, @"\bTV\b|TV$|Cast|Roku|Fire ?TV|Bravia|webOS|Tizen|Vizio|Hisense|TCL|Shield", RegexOptions.IgnoreCase)) return "tv";
            if (Regex.IsMatch(text, @"Xbox|PlayStation|PS[345]\b|Nintendo|Switch", RegexOptions.IgnoreCase)) return "console";
            if (Regex.IsMatch(text, @"Sonos|Echo|Alexa|Amazon|Google|Nest|Bose|HomePod|Speaker", RegexOptions.IgnoreCase)) return "speaker";
            if (Regex.IsMatch(text, @"Ring|Wyze|Arlo|Reolink|Hikvision|Eufy|Camera|Cam\b", RegexOptions.IgnoreCase)) return "camera";
            if (Regex.IsMatch(text, @"Printer|Brother|Epson|Canon|LaserJet|OfficeJet|DeskJet", RegexOptions.IgnoreCase)) return "printer";
            if (Regex.IsMatch(text, @"Espressif|Tuya|Shelly|Sonoff|Kasa|Wemo|Philips Lighting|Signify|LIFX|Wiz\b|smart ?plug", RegexOptions.IgnoreCase)) return "iot";
            if (Regex.IsMatch(text, @"iPhone|iPad|Galaxy|Pixel|Android|Phone", RegexOptions.IgnoreCase)) return "phone";
            if (Regex.IsMatch(text, @"pbx|server|\bnas\b|proxmox|truenas|unraid|docker|\bvm\b|ubuntu|debian", RegexOptions.IgnoreCase)) return "server";
            if (Regex.IsMatch(text, @"Micro-Star|ASUSTek|\bDell\b|Hewlett|Lenovo|\bIntel\b|Gigabyte|ASRock|Synology|QNAP|\bPC\b|Desktop|Laptop|Windows", RegexOptions.IgnoreCase)) return "pc";
            if (device.RandomMac && device.Name is null) return "phone";
            return "device";
        }

        private static string LanKindName(string kind, string? vendor) => kind switch
        {
            "mesh" => vendor is null ? "Mesh access point" : $"{vendor} access point",
            "pi" => "Raspberry Pi",
            "tv" => vendor is null ? "TV or streaming device" : $"{vendor} TV or streamer",
            "console" => vendor is null ? "Game console" : $"{vendor} game console",
            "speaker" => vendor is null ? "Smart speaker or display" : $"{vendor} smart device",
            "camera" => vendor is null ? "Camera" : $"{vendor} camera",
            "printer" => vendor is null ? "Printer" : $"{vendor} printer",
            "iot" => vendor is null ? "Smart home device" : $"{vendor} smart device",
            "phone" => "Phone, tablet, or laptop",
            "server" => "Server",
            "pc" => vendor is null ? "Computer" : $"{vendor} computer",
            _ => vendor is null ? "Device" : $"{vendor} device"
        };

        private static string SignalWord(int rssi) => rssi switch
        {
            >= -50 => "excellent",
            >= -60 => "very good",
            >= -70 => "good",
            >= -80 => "fair",
            _ => "weak"
        };

        private ConnectionLink RouterLink(AdapterFacts adapter, WifiFacts? wifi, string trafficInterface)
        {
            if (wifi?.State == "connected")
            {
                var band = NetworkProbe.Band(wifi.FrequencyMhz, wifi.Channel);
                var generation = wifi.Phy?.StartsWith("Wi-Fi", StringComparison.Ordinal) == true
                    ? string.Join(' ', wifi.Phy.Split(' ').Take(2))
                    : wifi.Phy ?? "Wi-Fi";
                var rate = wifi.ReceiveMbps is { } rx ? $"{rx:0} Mbps" : Format.BitRate(adapter.SpeedBitsPerSecond);
                return new ConnectionLink
                {
                    Bus = "wifi",
                    Label = string.Join(" · ", new[] { generation, band, rate }.Where(s => s is not null)),
                    Short = band is null ? rate : $"{band} · {rate}",
                    Gbps = (wifi.ReceiveMbps ?? adapter.SpeedBitsPerSecond / 1e6) / 1000
                };
            }
            // A bridge reports a made-up speed; the physical port underneath has the real one.
            var physical = capture.Links.FirstOrDefault(l => l.Name == trafficInterface);
            var bits = physical is { SpeedMbps: > 0 } ? physical.SpeedMbps * 1_000_000 : adapter.SpeedBitsPerSecond;
            return new ConnectionLink
            {
                Bus = "ethernet",
                Label = $"Ethernet · {Format.BitRate(bits)}",
                Short = Format.BitRate(bits),
                Gbps = bits > 0 ? bits / 1e9 : null
            };
        }

        // Interfaces with no hardware behind them: bridges, VPNs, containers, and virtual machines' links.
        private void AddVirtualAdapters()
        {
            var virtuals = capture.Links
                .Where(l => !_linksPlaced.Contains(l.Name) && l.Kind != "loopback" && l.DevicePath is null &&
                            capture.Network.Adapters.FirstOrDefault(a => a.Id == l.Name) is { Up: true, IPv4.Count: > 0 })
                .ToList();
            if (virtuals.Count == 0 || _cpu is null) return;
            var group = new ConnectionNode
            {
                Id = "virtual",
                Kind = "virtual-group",
                Name = "Virtual and VPN adapters",
                Detail = virtuals.Count == 1 ? "1 software adapter" : $"{virtuals.Count} software adapters",
                Link = new ConnectionLink { Bus = "internal", Label = "Software (no hardware link)", Short = "software" }
            };
            foreach (var link in virtuals)
            {
                var adapter = capture.Network.Adapters.First(a => a.Id == link.Name);
                var node = new ConnectionNode
                {
                    Id = LinkNodeId(link),
                    Kind = "virtual",
                    Name = link.Name,
                    Detail = adapter.IPv4.FirstOrDefault()?.Split('/')[0] ?? adapter.Description,
                    Net = Format.NetKey(link.Name),
                    Link = new ConnectionLink { Bus = "internal", Label = "Software adapter", Short = "virtual" },
                    Facts = { new("Kind", adapter.Description) }
                };
                if (link.Members.Count > 0) node.Facts.Add(new("Members", string.Join(", ", link.Members)));
                AddAddressFacts(node, adapter, link.Mac);
                if (adapter.Up && adapter.Gateways.Count > 0 && adapter.Primary && !_routersPlaced.Contains(adapter.Gateways[0].ToString()))
                    node.Children.Add(BuildRouter(adapter, null, link.Name));
                group.Children.Add(node);
            }
            _cpu.Children.Add(group);
        }

        // ---- Devices not on PCI (system-on-chip boards) ------------------------------------------------

        private void AddPlatformDevices()
        {
            foreach (var group in capture.Usb.Where(b => !_busesPlaced.Contains(b.Controller)).GroupBy(b => b.Controller))
            {
                var buses = group.ToList();
                var node = BuildUsbControllerNode("s" + Format.ShortHash(group.Key), buses, $"{group.Key} (on the system-on-chip)");
                node.Link = new ConnectionLink { Bus = "internal", Label = "Inside the system-on-chip", Short = "built in" };
                PlatformGroup().Children.Add(node);
            }
            foreach (var link in capture.Links.Where(l => l.DevicePath is not null && l.PciAddress is null && l.UsbPath is null && !_linksPlaced.Contains(l.Name) && l.Kind is "ethernet" or "wifi"))
            {
                var node = BuildNetwork(link);
                node.Link = new ConnectionLink { Bus = "internal", Label = "Built into the board", Short = "built in" };
                PlatformGroup().Children.Add(node);
            }
            foreach (var disk in capture.Disks.Where(d => !_disksPlaced.Contains(d.Name) && d.Transport != "usb"))
            {
                var node = BuildDisk(disk, disk.Optical ? "optical" : "disk");
                node.Link = disk.Transport == "mmc"
                    ? new ConnectionLink { Bus = "internal", Label = "SD card or eMMC", Short = "SD/eMMC" }
                    : new ConnectionLink { Bus = "internal", Label = "Storage", Short = "disk" };
                PlatformGroup().Children.Add(node);
            }
            foreach (var radio in capture.Bluetooth.Where(r => !_radiosPlaced.Contains(r.Name) && r.UsbPath is null && Pci.AddressIn(r.DevicePath) is null))
            {
                var node = BuildBluetoothFor(radio.DevicePath ?? radio.Name, new ConnectionNode { Id = "bt" + Format.ShortHash(radio.Name), Kind = "bluetooth", Name = "Bluetooth" });
                node.Link = new ConnectionLink { Bus = "internal", Label = "Built into the board (serial link)", Short = "built in" };
                PlatformGroup().Children.Add(node);
            }
            foreach (var card in capture.Displays.Where(o => o.GpuAddress is null).GroupBy(o => o.Card))
            {
                var driver = Sys.LinkName($"/sys/class/drm/{card.Key}/device/driver") ?? card.Key;
                var gpu = new ConnectionNode
                {
                    Id = "g" + Format.ShortHash(card.Key),
                    Kind = "gpu",
                    Name = driver switch { "vc4-drm" or "vc4_drm" => "VideoCore graphics", "v3d" => "VideoCore 3D", _ => "Built-in graphics" },
                    Detail = $"Driver {driver}",
                    Link = new ConnectionLink { Bus = "internal", Label = "Inside the system-on-chip", Short = "built in" }
                };
                foreach (var output in card.Where(o => o.Connected)) gpu.Children.Add(BuildMonitor(output));
                PlatformGroup().Children.Add(gpu);
            }
        }

        // ---- Memory --------------------------------------------------------------------------------

        private void AddMemoryBand()
        {
            var analysis = MemoryAdvice.Analyze(capture.Memory.Firmware, capture.Identity.BoardName);
            if (analysis is null || _cpu is null) return;
            var band = new ConnectionNode
            {
                Id = "memory",
                Kind = "memory",
                Name = $"Memory · {analysis.TotalGb:0.#} GB {analysis.Type}",
                Detail = string.Join(" · ", new[] { analysis.ChannelText, analysis.ConfiguredMts > 0 ? $"{analysis.ConfiguredMts} MT/s" : null }.Where(s => s is not null)),
                Link = new ConnectionLink
                {
                    Bus = "memory",
                    Label = $"{analysis.Type} memory controller inside the CPU" + (analysis.ChannelText is null ? "" : $", {analysis.ChannelText}"),
                    Short = analysis.ChannelsUsed >= 2 ? $"{analysis.ChannelsUsed} channels" : "1 channel",
                    Gbps = analysis.ConfiguredMts > 0 ? analysis.ConfiguredMts * 64d * Math.Max(1, analysis.ChannelsUsed) / 1000 : null
                },
                Facts =
                {
                    new("Installed", $"{analysis.TotalGb:0.#} GB in {analysis.Populated} of {analysis.Slots.Count} slots"),
                    new("Channels", $"{analysis.ChannelsUsed} of {analysis.ChannelsTotal} in use"),
                    new("Read from", capture.Memory.Source)
                }
            };
            if (analysis.ConfiguredMts > 0) band.Facts.Add(new("Speed", analysis.RatedMts > analysis.ConfiguredMts
                ? $"{analysis.ConfiguredMts} MT/s (rated {analysis.RatedMts})" : $"{analysis.ConfiguredMts} MT/s"));
            if (analysis.ConfiguredMts > 0)
                band.Facts.Add(new("Bandwidth", $"about {analysis.ConfiguredMts * 8d * Math.Max(1, analysis.ChannelsUsed) / 1000:0.#} GB/s"));
            var warning = analysis.Findings.FirstOrDefault(f => f.Tone is "warn" or "improve");
            if (warning is not null)
            {
                band.Warning = $"{warning.Title}. {warning.Detail}";
                if (warning.Action is not null) band.Note = warning.Action;
            }
            foreach (var placement in analysis.Slots)
            {
                var slot = placement.Slot;
                var dimm = new ConnectionNode
                {
                    Id = $"dimm-{Format.ShortHash(slot.DeviceLocator + "|" + slot.BankLocator)}",
                    Kind = slot.Populated ? "dimm" : "dimm-empty",
                    Name = slot.Populated ? $"{placement.Label} · {slot.SizeMb / 1024d:0.#} GB" : $"{placement.Label} · empty",
                    Detail = slot.Populated
                        ? string.Join(" · ", new[] { slot.ConfiguredMts > 0 ? $"{slot.ConfiguredMts} MT/s" : null, slot.PartNumber }.Where(s => s is not null))
                        : placement.Recommended ? "Recommended slot" : "Free slot",
                    Link = new ConnectionLink
                    {
                        Bus = "memory",
                        Label = slot.Channel is null ? "Memory slot" : $"Channel {slot.Channel}",
                        Short = slot.Channel is null ? "slot" : $"ch {slot.Channel}"
                    },
                    Facts = { new("Slot", $"{placement.Label} ({slot.DeviceLocator}{(slot.BankLocator.Length > 0 ? ", " + slot.BankLocator : "")})") }
                };
                if (slot.Channel is not null) dimm.Facts.Add(new("Channel", slot.Channel));
                if (slot.Populated)
                {
                    dimm.Facts.Add(new("Size", $"{slot.SizeMb / 1024d:0.#} GB {slot.MemoryType}"));
                    if (slot.ConfiguredMts > 0) dimm.Facts.Add(new("Speed", $"{slot.ConfiguredMts} MT/s now; {slot.SpeedMts} MT/s reported maximum"));
                    if (slot.Rank > 0) dimm.Facts.Add(new("Ranks", slot.Rank == 1 ? "Single rank" : $"{slot.Rank} ranks"));
                    if (slot.PartNumber is not null) dimm.Facts.Add(new("Part number", slot.PartNumber));
                    if (capture.Memory.Makers.TryGetValue(slot.DeviceLocator + "|" + slot.BankLocator, out var maker)) dimm.Facts.Add(new("Made by", maker));
                }
                if (placement.Recommended) dimm.Facts.Add(new("Placement", "Recommended slot for two sticks"));
                band.Children.Add(dimm);
            }
            _cpu.Children.Add(band);
        }

        // ---- Shared helpers ------------------------------------------------------------------------

        private ConnectionNode NewPciNode(PciDevice device, string kind, string name) => new()
        {
            Id = PciNodeId(device),
            Kind = kind,
            Name = privacy ? Privacy.Scrub(name) : name
        };

        private ConnectionNode NewUsbNode(UsbDevice device, string kind, string name) => new()
        {
            Id = UsbNodeId(device),
            Kind = kind,
            Name = privacy ? Privacy.Scrub(name) : name
        };

        private void AddDriverFact(ConnectionNode node, PciDevice device, string label = "Driver")
        {
            if (device.Driver is null)
            {
                node.Facts.Add(new(label, "None attached"));
                return;
            }
            var module = Sys.LinkName(Path.Combine(device.SysPath, "driver", "module")) ?? device.Driver;
            var info = KernelInfo.Driver(module, capture.Kernel);
            var text = info?.Version is { } version ? $"{device.Driver} {version}" : device.Driver;
            node.Facts.Add(new(label, info is null ? text : $"{text} · {info.Source}"));
        }

        private static void AddLinkFacts(ConnectionNode node, PciLinkInfo link)
        {
            node.Facts.Add(new("PCIe link now", $"PCIe {Format.Generation(link.Gen)} x{link.Width} ({Format.Rate(Format.LaneGbps(link.Gen) * link.Width)})"));
            node.Facts.Add(new("PCIe link maximum", $"PCIe {Format.Generation(link.MaxGen)} x{link.MaxWidth} ({Format.Rate(Format.LaneGbps(link.MaxGen) * link.MaxWidth)})"));
        }

        private string LinkWarning(PciLinkInfo link, string what)
        {
            var now = $"PCIe {Format.Generation(link.Gen)} x{link.Width} ({Format.Rate(Format.LaneGbps(link.Gen) * link.Width)})";
            var max = $"PCIe {Format.Generation(link.MaxGen)} x{link.MaxWidth} ({Format.Rate(Format.LaneGbps(link.MaxGen) * link.MaxWidth)})";
            var reasons = new List<string>();
            if (link.Gen < link.MaxGen)
                reasons.Add(Pcie3Apu().IsMatch(capture.Cpu.Name) && link.Gen == 3
                    ? "This APU's PCIe lanes run at 3.0."
                    : "The slot, the CPU, or a BIOS setting caps the PCIe generation.");
            if (link.Width < link.MaxWidth)
                reasons.Add(what == "card"
                    ? $"Only {link.Width} lanes trained: the card may be wired for {link.Width}, the slot may share lanes with an M.2 socket, or the card may not be fully seated."
                    : $"This M.2 socket or its lane sharing gives the drive only {link.Width} of its {link.MaxWidth} lanes.");
            return $"Linked at {now}; the {what} supports {max}. {string.Join(' ', reasons)}";
        }

        private static ConnectionLink PciLink(PciLinkInfo link) => new()
        {
            Bus = "pcie",
            Label = $"PCIe {Format.Generation(link.Gen)} x{link.Width}",
            Short = $"{Format.Generation(link.Gen)} x{link.Width}",
            Gbps = Format.LaneGbps(link.Gen) * link.Width,
            Max = link.BelowMax ? $"PCIe {Format.Generation(link.MaxGen)} x{link.MaxWidth}" : null,
            MaxGbps = link.BelowMax ? Format.LaneGbps(link.MaxGen) * link.MaxWidth : null,
            Degraded = link.BelowMax
        };

        private static bool IsGpuAudio(PciDevice device, IReadOnlyList<PciDevice> siblings) =>
            siblings.Any(s => s != device && s.BaseClass == 0x03 && s.Vendor == device.Vendor && s.Bus == device.Bus && s.Slot == device.Slot);

        private static bool IsUpstreamSwitch(PciDevice device) =>
            (device.DeviceName ?? "").Contains("Upstream", StringComparison.OrdinalIgnoreCase) ||
            (device.DeviceName ?? "").Contains("Chipset", StringComparison.OrdinalIgnoreCase) && device.Children.Any(c => c.IsBridge);

        private string UsbName(UsbDevice device, (string Vendor, string? Product)? names, string fallback)
        {
            var reported = device.ProductName;
            if (reported is not null && !GenericUsbName().IsMatch(reported) && !reported.Contains("xHCI Host Controller", StringComparison.Ordinal)) return reported;
            if (names?.Product is { } product && !GenericUsbName().IsMatch(product)) return product.Split(',')[0].Trim();
            return fallback;
        }

        private static string UsbKind(UsbDevice device, (string Vendor, string? Product)? names)
        {
            var text = $"{device.ProductName} {names?.Product}";
            var classes = device.Interfaces.Select(i => i.Class).ToHashSet();
            if (GamepadName().IsMatch(text) || device.Interfaces.Any(i => i.Driver is "xpad" or "xone" or "xpadneo")) return "gamepad";
            if (Regex.IsMatch(text, @"\b(LED|RGB|Polychrome|Aura|Mystic Light|lighting)\b", RegexOptions.IgnoreCase)) return "rgb";
            if (classes.Contains(0x0E) || Regex.IsMatch(text, "capture|webcam|camera", RegexOptions.IgnoreCase)) return "capture";
            if (classes.Contains(0x01)) return "audio";
            if (Regex.IsMatch(text, "receiver|unifying|bolt|lightspeed|dongle", RegexOptions.IgnoreCase)) return "receiver";
            var hid = device.Interfaces.Where(i => i.Class == 0x03).ToList();
            if (hid.Any(i => i.Protocol == 1)) return "keyboard";
            if (hid.Any(i => i.Protocol == 2)) return "mouse";
            if (hid.Count > 0) return "input";
            if (classes.Contains(0x07)) return "printer";
            if (classes.Contains(0x02) || classes.Contains(0x0A) || device.Interfaces.Any(i => i.Driver is "ftdi_sio" or "cp210x" or "ch341" or "pl2303" or "cdc_acm")) return "serial";
            return "usb";
        }

        private static string FallbackName(string kind) => kind switch
        {
            "gamepad" => "Game controller",
            "rgb" => "RGB lighting controller",
            "capture" => "Video capture",
            "audio" => "USB audio",
            "receiver" => "Wireless receiver",
            "keyboard" => "Keyboard",
            "mouse" => "Mouse",
            "input" => "Input device",
            "printer" => "Printer",
            "serial" => "Serial adapter",
            _ => "USB device"
        };

        [GeneratedRegex(@"\bRyzen\s+\d\s+(PRO\s+)?[45]\d{2,3}G[ET]?\b", RegexOptions.IgnoreCase)]
        private static partial Regex Pcie3Apu();

        [GeneratedRegex(@"\b([ABX][3-9][2-7]0)[A-Z]?\b", RegexOptions.IgnoreCase)]
        private static partial Regex ChipsetModel();

        // "200 Series PCH LPC Controller (B250)", "Z390 Chipset LPC/eSPI Controller", "B660 Chipset LPC/eSPI Controller".
        [GeneratedRegex(@"\(([BHQZXWC]\d{3}[A-Z]?)\)|\b([BHQZXWC]\d{3}[A-Z]?)\s+Chipset", RegexOptions.IgnoreCase)]
        private static partial Regex IntelChipsetModel();

        [GeneratedRegex(@"Wi-?Fi|Wireless|WLAN|802\.11", RegexOptions.IgnoreCase)]
        private static partial Regex WirelessName();

        [GeneratedRegex(@"^(Generic\s+)?(USB\s*)?(\d\.\d\s*)?(Receiver|(SuperSpeed\s+)?(USB\s+)?Hub|Composite Device|Input Device|Device|Mass Storage( Device)?|Storage|Keyboard|Mouse|Gamepad|Controller|Root Hub)$", RegexOptions.IgnoreCase)]
        private static partial Regex GenericUsbName();

        [GeneratedRegex(@"game\s*controller|gamepad|joystick|xbox|dualsense|dualshock|wireless controller", RegexOptions.IgnoreCase)]
        private static partial Regex GamepadName();

        // USB 3 bridge and hub chips named in the USB ID registry or the device's own product string.
        [GeneratedRegex(@"SATA 6Gb/s|USB ?3|SuperSpeed|\bASM1[01]5\d|\bASM2\d{3}|\bASM2362|\bJMS5[0-9]{2}|\bJMS58\d|\bVL7\d{2}|\bVL8\d{2}|\bRTL9210|\bRTS54\d{2}|\bGL3[0-9]{3}|\bUAS\b", RegexOptions.IgnoreCase)]
        private static partial Regex Usb3Name();
    }
}
