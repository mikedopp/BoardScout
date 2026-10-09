using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Models;
using Microsoft.Win32;

namespace BoardScout.Services;

/// <summary>
/// Builds the Connections map: every device from the CPU outward — PCIe slots and the chipset, USB
/// controllers, hubs and what is plugged into them, drives, monitors, Bluetooth, and the network path
/// through the router to the Internet — with the link each connection actually negotiated.
/// Reads only local state (no admin needed); nothing leaves the PC.
/// </summary>
internal static partial class ConnectionsService
{
    /// <summary>Everything read from the PC for one map. Building the map from it again is cheap, so a
    /// privacy toggle or late-arriving network names do not re-read the hardware.</summary>
    internal sealed record Capture(
        DeviceNode? Tree, Dictionary<uint, int> Disks, Dictionary<int, DiskFacts> DiskFacts, List<DisplayTarget> Displays,
        NetworkFacts Network, FirmwareFacts Firmware, long CaptureMs, DateTimeOffset At);

    public static Capture Read()
    {
        var watch = Stopwatch.StartNew();
        // Everything that does not depend on the device walk runs alongside it; reverse DNS and pings in the
        // network probe are the slowest part.
        var network = Task.Run(NetworkProbe.Capture);
        var displays = Task.Run(DeviceTree.DisplayTargets);
        var firmware = Task.Run(Smbios.Read);
        var disks = Task.Run(() =>
        {
            var numbers = DeviceTree.DiskNumbers();
            var facts = new Dictionary<int, DiskFacts>();
            foreach (var number in numbers.Values.Distinct())
                if (DeviceTree.ReadDisk(number) is { } disk) facts[number] = disk;
            return (numbers, facts);
        });
        var tree = DeviceTree.Capture();
        var (diskNumbers, diskFacts) = disks.GetAwaiter().GetResult();
        return new Capture(tree, diskNumbers, diskFacts, displays.GetAwaiter().GetResult(), network.GetAwaiter().GetResult(),
            firmware.GetAwaiter().GetResult(), watch.ElapsedMilliseconds, DateTimeOffset.Now);
    }

    public static ConnectionsSnapshot Build(Capture capture, bool privacy, DiscoveryResult? discovery)
    {
        var builder = new MapBuilder(capture, privacy, discovery);
        var root = builder.Build();
        return new ConnectionsSnapshot
        {
            Root = root,
            Notes = builder.Notes,
            Privacy = privacy,
            Elevated = SystemTelemetryService.IsElevated,
            Discovery = discovery is null ? "pending" : "done",
            GatheredMs = capture.CaptureMs,
            GatheredAt = capture.At.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
        };
    }

    public static string ToJson(ConnectionsSnapshot snapshot) =>
        JsonSerializer.Serialize(snapshot, BoardScoutJson.Default.ConnectionsSnapshot);

    /// <summary>The whole map including network names, for the command line.</summary>
    public static async Task<string> GatherJsonAsync(bool privacy, bool sweep = false)
    {
        var capture = await Task.Run(Read);
        var discovery = await NetworkDiscovery.RunAsync(capture.Network, CancellationToken.None, sweep);
        return ToJson(Build(capture, privacy, discovery));
    }

    /// <summary>The key the live-rate messages use for a network interface.</summary>
    public static string NetKey(string interfaceId) => "n" + ShortHash(interfaceId);

    /// <summary>The map card id for a device (a hash of its instance, never the instance itself).</summary>
    internal static string DeviceNodeId(DeviceNode device) => "d" + ShortHash(device.InstanceId);

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1_000_000_000_000 => $"{bytes / 1e12:0.#} TB",
        >= 1_000_000_000 => $"{bytes / 1e9:0} GB",
        _ => $"{bytes / 1e6:0} MB"
    };

    internal static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..10].ToLowerInvariant();

    private sealed partial class MapBuilder
    {
        private const string Hidden = "Hidden (privacy mode)";

        private readonly DeviceNode? _tree;
        private readonly Dictionary<uint, int> _disks;
        private readonly Dictionary<int, DiskFacts> _diskFacts;
        private readonly List<DisplayTarget> _displays;
        private readonly NetworkFacts _network;
        private readonly FirmwareFacts _firmware;
        private readonly DiscoveryResult? _discovery;
        private readonly bool _privacy;
        private readonly Dictionary<(int Vendor, int Product), (string Vendor, string? Product)> _usbNames;
        private readonly string _cpuName;
        private readonly bool _pcie3Cpu;
        private readonly HashSet<string> _routersPlaced = [];
        private readonly HashSet<string> _adaptersPlaced = new(StringComparer.OrdinalIgnoreCase);
        private ConnectionNode? _cpu;
        private ConnectionNode? _platform;
        private bool _gpuSensorTaken;

        public MapBuilder(Capture capture, bool privacy, DiscoveryResult? discovery)
        {
            var tree = capture.Tree;
            _tree = tree;
            _disks = capture.Disks;
            _diskFacts = capture.DiskFacts;
            _displays = capture.Displays;
            _network = capture.Network;
            _firmware = capture.Firmware;
            _discovery = discovery;
            _privacy = privacy;
            _cpuName = ReadMachineString(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0", "ProcessorNameString") ?? "Processor";
            // Ryzen 4000G/5000G APUs run every CPU PCIe lane at 3.0, even on B550/X570 boards.
            _pcie3Cpu = Pcie3Apu().IsMatch(_cpuName);
            _usbNames = UsbIds.Resolve(tree?.Descendants()
                .Select(n => UsbId(n.InstanceId))
                .Where(id => id is not null)
                .Select(id => id!.Value) ?? []);
        }

        public List<string> Notes { get; } = [];

        public ConnectionNode Build()
        {
            var board = string.Join(' ', new[]
            {
                ReadMachineString(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardManufacturer"),
                ReadMachineString(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct")
            }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            _cpu = new ConnectionNode
            {
                Id = "cpu",
                Kind = "cpu",
                Name = ShortCpuName(_cpuName),
                Detail = $"{Environment.ProcessorCount} threads" + (board.Length > 0 ? $" · {board}" : ""),
                Sensor = "cpu",
                Facts =
                {
                    new("Processor", _cpuName.Trim()),
                    new("Logical processors", Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture))
                }
            };
            if (board.Length > 0) _cpu.Facts.Add(new("Motherboard", board));
            if (_pcie3Cpu)
            {
                _cpu.Facts.Add(new("CPU PCIe lanes", "PCIe 3.0"));
                _cpu.Note = "Ryzen 4000G and 5000G APUs run their PCIe lanes at 3.0, so PCIe 4.0 cards and drives on CPU lanes link at 3.0.";
            }

            if (_tree is not null)
            {
                foreach (var root in _tree.Descendants().Where(n => n.IdStarts(@"ACPI\PNP0A08") || n.IdStarts(@"ACPI\PNP0A03")))
                    foreach (var device in root.Children.Where(IsPci))
                        AddFromRootBus(device);
            }
            if (_platform is not null) _cpu.Children.Add(_platform);
            AddMemoryBand();
            AddVirtualAdapters();

            if (!SystemTelemetryService.IsElevated && Walk(_cpu).Any(n => n.Disk is not null && n.TemperatureC is null))
                Notes.Add("Only some drives report their temperature to Windows without administrator rights.");
            return _cpu;
        }

        // ---- Memory --------------------------------------------------------------------------

        // The memory controller is inside the CPU: one band, one card per slot (empty ones too).
        private void AddMemoryBand()
        {
            var analysis = MemoryAdvice.Analyze(_firmware, ReadMachineString(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct"));
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
                    // Each 64-bit channel moves 8 bytes per transfer.
                    Gbps = analysis.ConfiguredMts > 0 ? analysis.ConfiguredMts * 64d * Math.Max(1, analysis.ChannelsUsed) / 1000 : null
                },
                Facts =
                {
                    new("Installed", $"{analysis.TotalGb:0.#} GB in {analysis.Populated} of {analysis.Slots.Count} slots"),
                    new("Channels", $"{analysis.ChannelsUsed} of {analysis.ChannelsTotal} in use"),
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
                    Id = $"dimm-{ShortHash(slot.DeviceLocator + "|" + slot.BankLocator)}",
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
                }
                if (placement.Recommended) dimm.Facts.Add(new("Placement", "Recommended slot for two sticks"));
                band.Children.Add(dimm);
            }
            _cpu.Children.Add(band);
        }

        // ---- PCI -----------------------------------------------------------------------------

        private void AddFromRootBus(DeviceNode device)
        {
            if (IsBridge(device))
            {
                AddBehindRootPort(_cpu!, device);
                return;
            }
            // Integrated endpoints on the root bus (Intel platform controllers, for example).
            var node = BuildEndpoint(device, device.Parent?.Children ?? [], internalLink: true);
            if (node is null) return;
            _platform ??= new ConnectionNode
            {
                Id = "platform",
                Kind = "chipset",
                Name = "Chipset and on-board controllers",
                Detail = "Controllers on the main PCI bus",
                Link = new ConnectionLink { Bus = "internal", Label = "On the main PCI bus", Short = "on-board" }
            };
            _platform.Children.Add(node);
        }

        private void AddBehindRootPort(ConnectionNode parent, DeviceNode port)
        {
            var functions = port.Children.Where(IsPci).ToList();
            if (functions.Count == 0) return;

            if (functions.Any(f => IsUpstreamSwitch(f) && VendorOf(f) == 0x1022))
            {
                parent.Children.Add(BuildChipset(functions));
                return;
            }

            if (functions.Count >= 3 && functions.All(f => VendorOf(f) is 0x1022 or 0x1002) &&
                functions.Any(f => f.IsClass("USB") || f.Name.Contains("Audio", StringComparison.OrdinalIgnoreCase)))
            {
                var link = functions.Select(f => f.Pci).FirstOrDefault(l => l is not null);
                var integrated = new ConnectionNode
                {
                    Id = "integrated",
                    Kind = "integrated",
                    Name = "Built into the CPU",
                    Detail = "USB, audio, and security inside the processor",
                    Link = new ConnectionLink
                    {
                        Bus = "internal",
                        Label = link is null ? "Inside the processor" : $"Inside the processor (internal PCIe {Generation(link.Speed)} x{link.Width})",
                        Short = "internal",
                        Gbps = link is null ? null : LaneGbps(link.Speed) * link.Width
                    }
                };
                foreach (var function in functions) AddDevice(integrated, function, functions, internalLink: true);
                if (integrated.Children.Count > 0) parent.Children.Add(integrated);
                return;
            }

            foreach (var function in functions) AddDevice(parent, function, functions, internalLink: false);
        }

        private ConnectionNode BuildChipset(List<DeviceNode> functions)
        {
            var link = functions.Select(f => f.Pci).FirstOrDefault(l => l is not null);
            var board = ReadMachineString(@"HARDWARE\DESCRIPTION\System\BIOS", "BaseBoardProduct") ?? "";
            var model = ChipsetModel().Match(board);
            var chipset = new ConnectionNode
            {
                Id = "chipset",
                Kind = "chipset",
                Name = model.Success ? $"AMD {model.Groups[1].Value.ToUpperInvariant()} chipset" : "AMD chipset",
                Detail = link is null ? "Connects USB, SATA, M.2, and network" : $"Everything here shares one PCIe {Generation(link.Speed)} x{link.Width} link",
                Sensor = "chipset",
                Link = link is null ? new ConnectionLink { Bus = "pcie", Label = "PCIe", Short = "PCIe" } : PciLink(link)
            };
            if (link is not null)
            {
                chipset.Facts.Add(new("Link to CPU", $"PCIe {Generation(link.Speed)} x{link.Width} ({Rate(LaneGbps(link.Speed) * link.Width)})"));
                chipset.Note = $"Drives, USB, and network behind the chipset share this one link to the CPU — about {Rate(LaneGbps(link.Speed) * link.Width)} in each direction.";
            }
            foreach (var function in functions) AddDevice(chipset, function, functions, internalLink: true);
            return chipset;
        }

        // Switch ports and bridges are pass-through: their devices attach to the same parent.
        private void AddDevice(ConnectionNode parent, DeviceNode device, List<DeviceNode> siblings, bool internalLink)
        {
            if (IsBridge(device))
            {
                var downstream = device.Children.Where(IsPci).ToList();
                foreach (var child in downstream)
                {
                    if (IsBridge(child)) AddDevice(parent, child, downstream, internalLink: false);
                    else if (BuildEndpoint(child, downstream, internalLink: false) is { } node) parent.Children.Add(node);
                }
                return;
            }
            if (BuildEndpoint(device, siblings, internalLink) is { } built) parent.Children.Add(built);
        }

        private ConnectionNode? BuildEndpoint(DeviceNode device, IReadOnlyList<DeviceNode> siblings, bool internalLink)
        {
            ConnectionNode? node;
            if (device.IsClass("Display")) node = BuildGpu(device, siblings);
            else if (device.IsClass("Net")) node = BuildNetwork(device);
            else if (device.IsClass("USB") && !device.IdStarts(@"USB\")) node = BuildUsbController(device);
            else if (IsNvme(device)) node = BuildNvme(device);
            else if (device.IsClass("HDC") || device.IsClass("SCSIAdapter")) node = BuildStorageController(device);
            else if (IsAudioController(device)) node = IsGpuAudio(device, siblings) ? null : BuildAudio(device);
            else if (device.IsClass("SecurityDevices"))
            {
                _cpu?.Facts.Add(new("Security processor", device.Name));
                node = null;
            }
            else if (device.IsClass("Bluetooth")) node = BuildBluetooth(device);
            else if (device.Problem != 0 || device.Class is null) node = BuildUnknown(device);
            else node = null;

            if (node is null) return null;
            if (device.IdStarts(@"PCI\") && node.Link is null)
            {
                node.Link = internalLink
                    ? new ConnectionLink { Bus = "internal", Label = "Built in (no external link)", Short = "internal" }
                    : device.Pci is null ? new ConnectionLink { Bus = "pcie", Label = "PCIe", Short = "PCIe" } : PciLink(device.Pci);
            }
            AddInterruptFacts(node, device, siblings);
            AddProblem(node, device);
            return node;
        }

        // Message-signaled interrupts (MSI/MSI-X) never conflict; legacy lines can be shared between devices.
        private static void AddInterruptFacts(ConnectionNode node, DeviceNode device, IReadOnlyList<DeviceNode> siblings)
        {
            var irqs = device.Irqs.Concat(siblings.Where(s => s != device && node.Facts.Any(f => f.Label == "Also on this card") &&
                                                                VendorOf(s) == VendorOf(device)).SelectMany(s => s.Irqs)).ToList();
            if (irqs.Count == 0) return;
            var msi = irqs.Count(i => i.Irq < 0);
            var lines = irqs.Where(i => i.Irq >= 0).Select(i => i.Irq).Distinct().ToList();
            node.Facts.Add(new("Interrupts", lines.Count == 0
                ? $"{msi} message-signaled (MSI), the modern kind that never conflicts"
                : msi == 0 ? $"Legacy line{(lines.Count == 1 ? "" : "s")} IRQ {string.Join(", ", lines)}"
                : $"{msi} message-signaled, plus legacy IRQ {string.Join(", ", lines)}"));
        }

        private ConnectionNode BuildGpu(DeviceNode device, IReadOnlyList<DeviceNode> siblings)
        {
            // "NVIDIA GeForce RTX 3050" → "GeForce RTX 3050", with the vendor on the second line.
            var vendor = VendorName(VendorOf(device));
            var name = vendor is not null && device.Name.StartsWith(vendor + " ", StringComparison.OrdinalIgnoreCase)
                ? device.Name[(vendor.Length + 1)..]
                : device.Name;
            var gpu = NewNode(device, "gpu", name);
            var vram = VideoMemory(device);
            gpu.Detail = string.Join(" · ", new[] { vendor, vram is null ? null : $"{vram.Value / 1_073_741_824d:0.#} GB" }.Where(s => s is not null));
            gpu.Facts.Add(new("Graphics card", device.Name));
            if (vram is not null) gpu.Facts.Add(new("Video memory", $"{vram.Value / 1_073_741_824d:0.#} GB"));
            if (!_gpuSensorTaken)
            {
                gpu.Sensor = "gpu";
                _gpuSensorTaken = true;
            }
            AddDriverFact(gpu, device);
            if (device.Pci is { } link)
            {
                gpu.Link = PciLink(link);
                AddLinkFacts(gpu, link);
                if (link.BelowMax)
                    gpu.Warning = LinkWarning(link, "card") +
                        " Graphics cards also drop to a slower link when idle to save power, so check again while a game or video is running.";
            }
            foreach (var sibling in siblings.Where(s => s != device && VendorOf(s) == VendorOf(device) && IsAudioController(s)))
                gpu.Facts.Add(new("Also on this card", HdAudioCodecName(sibling) ?? "HDMI/DisplayPort audio"));

            foreach (var monitor in device.Children.Where(c => c.IdStarts(@"DISPLAY\")))
                gpu.Children.Add(BuildMonitor(monitor));
            return gpu;
        }

        private ConnectionNode BuildMonitor(DeviceNode device)
        {
            var target = _displays.FirstOrDefault(t => string.Equals(t.MonitorInstance, device.InstanceId, StringComparison.OrdinalIgnoreCase));
            var name = target?.FriendlyName ?? DeviceTree.MonitorName(device.InstanceId) ?? "Monitor";
            var monitor = NewNode(device, "monitor", name);
            if (target is null)
            {
                monitor.Detail = "Not showing a picture";
                monitor.Link = new ConnectionLink { Bus = "display", Label = "Display (inactive)", Short = "inactive" };
                return monitor;
            }

            var connector = ConnectorName(target.Technology);
            var mode = target.Width is > 0 && target.Height is > 0 ? $"{target.Width}×{target.Height}" : null;
            var refresh = target.RefreshHz is > 0 ? $"{target.RefreshHz.Value:0.##} Hz" : null;
            monitor.Detail = string.Join(" @ ", new[] { mode, refresh }.Where(s => s is not null));
            monitor.Link = new ConnectionLink
            {
                Bus = "display",
                Label = string.Join(" · ", new[] { connector, mode, refresh }.Where(s => s is not null)),
                Short = connector == "DisplayPort" ? "DP" : connector
            };
            monitor.Facts.Add(new("Connection", connector));
            if (mode is not null) monitor.Facts.Add(new("Signal", string.Join(" @ ", new[] { mode, refresh }.Where(s => s is not null))));
            monitor.Facts.Add(new("GPU output", $"#{target.Connector + 1}"));
            return monitor;
        }

        private ConnectionNode BuildNvme(DeviceNode controller)
        {
            var disk = controller.Children.FirstOrDefault(c => c.IsClass("DiskDrive"));
            var node = disk is null ? NewNode(controller, "nvme", "NVMe SSD") : BuildDisk(disk, "nvme");
            node.Id = NodeId(controller);
            AddDriverFact(node, controller, "Controller driver");
            if (controller.Pci is { } link)
            {
                node.Link = PciLink(link);
                AddLinkFacts(node, link);
                if (link.BelowMax) node.Warning = LinkWarning(link, "drive");
            }
            return node;
        }

        private ConnectionNode? BuildStorageController(DeviceNode controller)
        {
            var drives = controller.Children.Where(c => c.IsClass("DiskDrive") || c.IsClass("CDROM")).ToList();
            var sata = controller.IsClass("HDC") || controller.Name.Contains("SATA", StringComparison.OrdinalIgnoreCase) ||
                       controller.Name.Contains("AHCI", StringComparison.OrdinalIgnoreCase);
            var node = NewNode(controller, "sata", sata ? "SATA controller" : controller.Name);
            node.Detail = drives.Count switch
            {
                0 => "No drives connected",
                1 => "1 drive",
                _ => $"{drives.Count} drives"
            };
            node.Facts.Add(new("Controller", controller.Name));
            AddDriverFact(node, controller);
            foreach (var drive in drives)
            {
                var child = drive.IsClass("CDROM") ? NewNode(drive, "optical", CleanDiskName(drive.Name)) : BuildDisk(drive, "disk");
                child.Link = sata
                    ? new ConnectionLink { Bus = "sata", Label = "SATA (ports run at up to 6 Gbps)", Short = "SATA", Gbps = 6 }
                    : new ConnectionLink { Bus = "sata", Label = "Storage bus", Short = "disk" };
                node.Children.Add(child);
            }
            return node;
        }

        private ConnectionNode BuildDisk(DeviceNode disk, string kind)
        {
            var node = NewNode(disk, kind, CleanDiskName(disk));
            if (!_disks.TryGetValue(disk.Handle, out var number)) return node;

            node.Disk = number;
            var facts = _diskFacts.GetValueOrDefault(number);
            var parts = new List<string>();
            if (facts?.SizeBytes is > 0) parts.Add(FormatBytes(facts.SizeBytes.Value));
            if (facts?.Firmware is { } firmware)
            {
                parts.Add($"FW {firmware}");
                node.Facts.Add(new("Firmware", firmware));
            }
            node.Detail = string.Join(" · ", parts);
            if (facts?.SizeBytes is > 0) node.Facts.Add(new("Capacity", FormatBytes(facts.SizeBytes.Value)));
            node.Facts.Add(new("Disk number", $"Disk {number}"));
            if (facts is not null) node.Facts.Add(new("Interface", BusName(facts.Bus)));
            if (facts?.TemperatureC is { } temperature)
            {
                node.TemperatureC = temperature;
                node.Facts.Add(new("Temperature", $"{temperature:0} °C"));
            }
            if (facts?.Spinning is { } spinning)
                node.Facts.Add(new("Drive type", (spinning ? "Hard drive (spinning disk)" : "Solid-state drive") +
                    (facts.SpinningFromModel ? ", from its model number (the USB bridge doesn't pass the drive's own answer through)" : "")));
            else
                node.Facts.Add(new("Drive type", "Not reported: the drive or its USB bridge doesn't say whether it spins"));
            if (disk.RemovalPolicy is 2 or 3)
                node.Facts.Add(new("Write caching", disk.RemovalPolicy == 2
                    ? "On (Better performance): use Safely Remove before unplugging"
                    : "Off (Quick removal): safe to unplug any time, slower writes"));
            return node;
        }

        private ConnectionNode BuildUsbController(DeviceNode controller)
        {
            var roots = controller.Children.Where(c => c.IdStarts(@"USB\ROOT_HUB")).ToList();
            var devices = roots.SelectMany(r => r.Children).Where(d => d.IdStarts(@"USB\")).ToList();
            var ports = roots.Sum(r => r.HubPorts ?? 0);
            var version = UsbVersion().Match(controller.Name);
            var node = NewNode(controller, "usb-controller", version.Success ? $"USB {version.Groups[1].Value}.{version.Groups[2].Value} controller" : "USB controller");
            node.Detail = ports > 0
                ? $"{devices.Count} of {ports} ports in use"
                : devices.Count == 1 ? "1 device" : $"{devices.Count} devices";
            node.Facts.Add(new("Controller", controller.Name));
            if (ports > 0) node.Facts.Add(new("Root hub ports", $"{ports} ({devices.Count} in use)"));
            node.Facts.Add(new("Note", "USB 3 controllers list each physical port twice: once for USB 3 and once for USB 2 devices."));
            node.Facts.Add(new("Plugged in but missing?", "A device that doesn't appear here at all never made a connection: the port saw nothing. " +
                "Reseat the plug, try another cable, and give bus-powered hard drives a powered port. Devices that connect but fail show up as a warning card."));
            AddDriverFact(node, controller);
            foreach (var device in devices) node.Children.Add(BuildUsbDevice(device));
            foreach (var root in roots) AddPortProblems(node, root);
            return node;
        }

        private ConnectionNode BuildUsbDevice(DeviceNode device)
        {
            var id = UsbId(device.InstanceId);
            var names = id is { } key && _usbNames.TryGetValue(key, out var found) ? found
                : id is { } missing && ExtraUsbVendor(missing.Vendor) is { } extra ? (extra, null)
                : ((string Vendor, string? Product)?)null;
            var interfaces = device.Children.Where(c => c.IdStarts(@"USB\") && c.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
            var inside = interfaces.Count > 0 ? interfaces.Concat(interfaces.SelectMany(i => i.Children)).Append(device).ToList() : device.Descendants().Append(device).ToList();
            var disk = device.Descendants().FirstOrDefault(d => d.IsClass("DiskDrive"));
            // A USB network adapter is the device itself or one of its interfaces (not, say, Bluetooth's PAN service).
            var net = device.IsClass("Net") ? device : interfaces.FirstOrDefault(i => i.IsClass("Net"));
            var isHub = device.Usb?.IsHub == true || device.HubPorts is > 0;

            // A USB 3 hub shows up twice: a SuperSpeed hub and a USB 2.0 companion hub on the same ports.
            var companion = isHub && id is { } hubId && device.Parent?.Children.Any(s =>
                s != device && (s.Usb?.IsHub == true || s.HubPorts is > 0) && UsbId(s.InstanceId)?.Vendor == hubId.Vendor &&
                s.Usb?.SuperSpeed != device.Usb?.SuperSpeed) == true;

            ConnectionNode node;
            if (isHub)
            {
                node = NewNode(device, "usb-hub", UsbName(device, names, "USB hub"));
                var side = companion ? device.Usb?.SuperSpeed == true ? "USB 3 side" : "USB 2 side" : null;
                node.Detail = string.Join(" · ", new[] { side, device.HubPorts is > 0 ? $"{device.HubPorts} ports" : null, names?.Vendor }.Where(s => s is not null));
                if (companion)
                    node.Facts.Add(new("Why it appears twice", "USB 3 hubs contain a separate USB 2.0 hub for older devices; both share the same physical ports."));
                foreach (var child in device.Children.Where(c => c.IdStarts(@"USB\") && !c.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
                    node.Children.Add(BuildUsbDevice(child));
            }
            else if (disk is not null)
            {
                node = BuildDisk(disk, "storage");
                node.Id = NodeId(device);
                // The card's Eject button stops this USB device, the one Safely Remove would.
                node.Eject = device.Usb is not null;
                var uas = device.Service?.Equals("UASPStor", StringComparison.OrdinalIgnoreCase) == true ||
                          device.Descendants().Any(d => d.Service?.Equals("UASPStor", StringComparison.OrdinalIgnoreCase) == true);
                node.Facts.Add(new("USB protocol", uas ? "UAS (USB Attached SCSI, the faster protocol)" : "Bulk-only mass storage"));
                if (names?.Product is { } enclosure && !node.Name.Contains(enclosure, StringComparison.OrdinalIgnoreCase))
                    node.Facts.Add(new("USB bridge", $"{names.Value.Vendor} {enclosure}"));
                if (names?.Vendor is { } vendor && string.IsNullOrEmpty(node.Detail)) node.Detail = vendor;
            }
            else if (device.IsClass("Bluetooth"))
            {
                node = BuildBluetooth(device);
                if (names?.Product is { } radio) node.Facts.Insert(0, new("Radio", $"{names.Value.Vendor} {radio}"));
            }
            else if (net is not null && BuildNetwork(net) is { } adapter)
            {
                node = adapter;
                node.Id = NodeId(device);
            }
            else
            {
                var kind = UsbKind(device, inside);
                node = NewNode(device, kind, UsbName(device, names, FallbackName(kind)));
                node.Detail = names?.Vendor;
            }

            if (id is { } usb) node.Facts.Add(new("USB ID", $"{usb.Vendor:X4}:{usb.Product:X4}"));
            if (device.Usb is { } port)
            {
                // A USB 3 socket is two connections, listed as two hub ports: its USB 2 wires and its USB 3 wires.
                // The number is the half the device came in on, not a USB 2-only socket.
                node.Facts.Add(new("Hub port", (port.Protocols & 4) != 0
                    ? $"{port.Port} (USB 3 connection)"
                    : port.SuperSpeedCapable && !port.SuperSpeed
                        ? $"{port.Port} (USB 2 connection: only the socket's USB 2 wires made contact)"
                        : $"{port.Port} (USB 2 connection)"));
                if (port.PowerMa is { } power)
                    node.Facts.Add(new("Power", $"Asks for up to {power} mA from the port{(port.SelfPowered == true ? "; has its own power supply" : "")}"));
                else if (device.PowerNotAsked is { } reason)
                    node.Facts.Add(new("Power", "Not asked: " + reason));
            }
            if (device.PowerState is > 0) node.Facts.Add(new("Power state", $"Asleep (D{device.PowerState}): Windows suspended it while idle"));
            if (isHub) node.Facts.Add(new("Hub power", device.HubBusPowered ? "Bus-powered: shares one port's power with everything plugged into it" : "Self-powered or built in"));
            AddDriverFact(node, interfaces.FirstOrDefault(i => i.IsClass("MEDIA")) ?? device);
            node.Link = UsbLink(device.Usb);
            if (companion && node.Link.Degraded)
            {
                node.Link.Degraded = false;
                node.Link.Max = null;
                node.Link.MaxGbps = null;
            }
            if (device.Usb is { } negotiated)
            {
                node.Facts.Add(new("USB speed", node.Link.Label));
                if (companion)
                {
                    // The USB 2.0 half of a USB 3 hub is expected; nothing to fix.
                }
                else if (negotiated.SuperSpeedCapable && !negotiated.SuperSpeed)
                    node.Warning = "This USB 3 device connected at USB 2.0 speed (480 Mbps): only the USB 2 wires made contact. Use a USB 3 port (often blue or marked SS). " +
                                   "If it already is one, the cable or the device's socket is the likely fault: try another USB 3 cable and push the plug fully in (on Micro-B plugs the USB 3 contacts are on the wide half).";
                else if (negotiated.SuperSpeedPlusCapable && !negotiated.SuperSpeedPlus)
                    node.Warning = "This device supports 10 Gbps but connected at 5 Gbps. A USB 3.2 Gen 2 port and cable would give it full speed.";
                else if (node.Kind == "storage" && !negotiated.SuperSpeed && negotiated.Speed <= 2)
                    node.Note = "USB 2.0 limits this drive to roughly 40 MB/s.";
            }
            AddProblem(node, device);
            if (isHub) AddPortProblems(node, device);
            return node;
        }

        // A port where something is plugged in but Windows couldn't use it. The hub sees the device even when
        // Device Manager has no entry for it, so this is the only place a dead or underpowered drive shows up.
        private static void AddPortProblems(ConnectionNode parent, DeviceNode hub)
        {
            foreach (var (port, status) in hub.PortProblems)
            {
                var (title, warning) = status switch
                {
                    2 => ("Device failed to start", "Something is plugged into this port but didn't answer when Windows asked what it is. Unplug it and plug it back in; if it keeps failing, try another cable or port. Bus-powered hard drives often need a powered hub or a port on the back of the PC."),
                    4 => ("Port shut off: over-current", "The device drew more current than this port allows, so the port switched itself off. Unplug it; give it its own power supply or a powered hub."),
                    5 => ("Not enough power", "The device needs more power than this port gives. Use a powered hub or a port on the back of the PC."),
                    6 => ("Not enough bandwidth", "The controller has no bandwidth left for this device. Move it, or a busy device beside it, to another USB controller."),
                    7 => ("Hubs nested too deeply", "There are too many hubs between this device and the PC. Plug it in closer to the PC."),
                    8 => ("Stuck behind a USB 1.1 hub", "This device is on an old USB 1.1 hub that can't run it. Plug it straight into the PC."),
                    _ => ("Device failed", "Something is plugged into this port but stopped working. Unplug it and plug it back in, or try another cable or port.")
                };
                parent.Children.Add(new ConnectionNode
                {
                    Id = NodeId(hub) + "p" + port.ToString(CultureInfo.InvariantCulture),
                    Kind = "usb",
                    Name = title,
                    Detail = $"Port {port} · not working",
                    Problem = true,
                    Warning = warning,
                    Link = new ConnectionLink { Bus = "usb", Label = "USB (the port reports a problem)", Short = "failed" },
                    Facts =
                    {
                        new("Hub port", port.ToString(CultureInfo.InvariantCulture)),
                        new("What the hub reports", title)
                    }
                });
            }
        }

        private ConnectionNode BuildBluetooth(DeviceNode radio)
        {
            var node = NewNode(radio, "bluetooth", "Bluetooth");
            var paired = radio.Descendants()
                .Where(d => d.IdStarts(@"BTHLE\DEV_") || d.IdStarts(@"BTHENUM\DEV_"))
                .ToList();
            node.Detail = paired.Count switch
            {
                0 => "No paired devices",
                1 => "1 paired device",
                _ => $"{paired.Count} paired devices"
            };
            AddDriverFact(node, radio);
            foreach (var device in paired)
            {
                var low = device.IdStarts(@"BTHLE\");
                var child = NewNode(device, "bt-device", BluetoothName(device.Name));
                child.Detail = low ? "Bluetooth Low Energy · paired" : "Bluetooth · paired";
                child.Link = new ConnectionLink
                {
                    Bus = "bluetooth",
                    Label = low ? "Bluetooth Low Energy (about 1–2 Mbps)" : "Bluetooth Classic (up to 3 Mbps)",
                    Short = low ? "BLE" : "BT",
                    Gbps = low ? 0.002 : 0.003
                };
                AddProblem(child, device);
                node.Children.Add(child);
            }
            return node;
        }

        private ConnectionNode? BuildAudio(DeviceNode controller)
        {
            var codec = HdAudioCodecName(controller);
            if (codec is null && !controller.IsClass("MEDIA")) return null;
            var node = NewNode(controller, "audio", codec ?? controller.Name);
            // AMD/NVIDIA/Intel HDMI audio functions only carry sound over a display cable.
            node.Detail = VendorOf(controller) is 0x1002 or 0x10DE || controller.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase)
                ? "Sound over HDMI or DisplayPort"
                : "Audio";
            node.Facts.Add(new("Controller", controller.Name));
            AddDriverFact(node, controller.Children.FirstOrDefault(c => c.IdStarts(@"HDAUDIO\")) ?? controller);
            return node;
        }

        private ConnectionNode BuildUnknown(DeviceNode device)
        {
            var node = NewNode(device, "device", device.Name);
            node.Detail = device.Class is null ? "No driver" : device.Class;
            return node;
        }

        // ---- Network -------------------------------------------------------------------------

        private ConnectionNode? BuildNetwork(DeviceNode device)
        {
            var interfaceId = DeviceTree.NetInstanceId(device);
            var adapter = interfaceId is null ? null
                : _network.Adapters.FirstOrDefault(a => string.Equals(a.Id, interfaceId, StringComparison.OrdinalIgnoreCase));
            var wifi = interfaceId is not null && Guid.TryParse(interfaceId, out var guid)
                ? _network.Wifi.FirstOrDefault(w => w.InterfaceId == guid)
                : null;
            var isWifi = wifi is not null || WirelessName().IsMatch(device.Name);
            var node = NewNode(device, isWifi ? "wifi" : "ethernet", isWifi ? "Wi-Fi" : "Ethernet");
            node.Facts.Add(new("Adapter", device.Name));
            AddDriverFact(node, device);
            if (adapter is null)
            {
                node.Detail = "Not set up in Windows";
                return node;
            }

            _adaptersPlaced.Add(adapter.Id);
            node.Net = NetKey(adapter.Id);
            node.Facts.Insert(0, new("Windows name", adapter.Name));
            var ipv4 = adapter.IPv4.FirstOrDefault();
            if (adapter.Up)
            {
                node.Detail = string.Join(" · ", new[] { adapter.SpeedBitsPerSecond > 0 ? BitRate(adapter.SpeedBitsPerSecond) : null, ipv4?.Split('/')[0] }.Where(s => s is not null));
                if (isWifi && wifi?.Ssid is { } ssid) node.Detail = _privacy ? $"Connected · {node.Detail}" : $"{ssid} · {node.Detail}";
            }
            else node.Detail = isWifi ? "Not connected" : "Cable unplugged or link down";

            AddProfileFacts(node, adapter);
            AddAddressFacts(node, adapter);
            if (wifi is not null) AddWifiFacts(node, wifi);
            if (adapter.Up && adapter.Gateways.Count > 0) node.Children.Add(BuildRouter(adapter, wifi));
            return node;
        }

        // Windows' own name for the network ("Network 5", or the Wi-Fi name) and whether it is Public.
        private void AddProfileFacts(ConnectionNode node, AdapterFacts adapter)
        {
            if (_discovery is null || !Guid.TryParse(adapter.Id, out var id) || !_discovery.Profiles.TryGetValue(id, out var profile)) return;
            var generic = GenericNetworkName().IsMatch(profile.Name);
            node.Facts.Insert(1, new("Windows network name", _privacy && !generic ? Hidden : profile.Name));
            node.Facts.Insert(2, new("Network type", profile.Category switch
            {
                "Public" => "Public: Windows hides this PC from other devices here and turns off sharing",
                "Private" => "Private: other devices here can find this PC and use its shared folders and printers",
                "Domain" => "Domain: managed by your organization",
                _ => profile.Category
            }));
            if (profile.FirstConnected is { } first)
                node.Facts.Insert(3, new("First connected", first.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        private void AddAddressFacts(ConnectionNode node, AdapterFacts adapter)
        {
            node.Facts.Add(new("Status", adapter.Up ? "Connected" : "Disconnected"));
            if (adapter.SpeedBitsPerSecond > 0 && adapter.Up) node.Facts.Add(new("Link speed", BitRate(adapter.SpeedBitsPerSecond)));
            if (adapter.IPv4.Count > 0) node.Facts.Add(new("IPv4", string.Join(", ", adapter.IPv4)));
            if (adapter.IPv6.Count > 0)
            {
                node.Facts.Add(new("IPv6", _privacy
                    ? $"{adapter.IPv6.Count} address{(adapter.IPv6.Count == 1 ? "" : "es")} (hidden in privacy mode)"
                    : string.Join(", ", adapter.IPv6.Select(a => $"{a.Address} ({a.Scope})"))));
            }
            if (adapter.Gateways.Count > 0) node.Facts.Add(new("Default gateway", string.Join(", ", adapter.Gateways)));
            if (adapter.DnsServers.Count > 0) node.Facts.Add(new("DNS servers", string.Join(", ", adapter.DnsServers)));
            if (adapter.DhcpServers.Count > 0) node.Facts.Add(new("DHCP server", string.Join(", ", adapter.DhcpServers)));
            if (adapter.Mac is not null) node.Facts.Add(new("MAC address", _privacy ? Hidden : adapter.Mac));
            if (adapter.Primary) node.Facts.Add(new("Route to the Internet", "This adapter (Windows' preferred route)"));
        }

        private void AddWifiFacts(ConnectionNode node, WifiFacts wifi)
        {
            if (wifi.State != "connected")
            {
                node.Facts.Add(new("Wi-Fi", $"Radio {wifi.State}"));
                return;
            }
            if (wifi.Ssid is not null) node.Facts.Add(new("Network (SSID)", _privacy ? Hidden : wifi.Ssid));
            if (wifi.Bssid is not null) node.Facts.Add(new("Access point (BSSID)", _privacy ? Hidden : wifi.Bssid));
            if (wifi.SignalPercent is { } signal)
                node.Facts.Add(new("Signal", wifi.RssiDbm is { } rssi ? $"{signal}% ({rssi} dBm)" : $"{signal}%"));
            var band = NetworkProbe.Band(wifi.FrequencyMhz, wifi.Channel);
            if (wifi.Channel is { } channel) node.Facts.Add(new("Channel", band is null ? $"{channel}" : $"{channel} ({band})"));
            if (wifi.Phy is not null) node.Facts.Add(new("Standard", wifi.Phy));
            if (wifi.ReceiveMbps is { } rx && wifi.TransmitMbps is { } tx)
                node.Facts.Add(new("Link rate", $"{rx:0} Mbps down / {tx:0} Mbps up"));
            if (wifi.Security is not null) node.Facts.Add(new("Security", wifi.Security));
        }

        private ConnectionNode BuildRouter(AdapterFacts adapter, WifiFacts? wifi)
        {
            var gateway = adapter.Gateways[0];
            var key = gateway.ToString();
            _network.Routers.TryGetValue(key, out var router);
            var link = RouterLink(adapter, wifi);

            if (!_routersPlaced.Add(key))
            {
                return new ConnectionNode
                {
                    Id = $"router-{ShortHash(key + adapter.Id)}",
                    Kind = "router",
                    Name = "Same router",
                    Detail = key,
                    Net = NetKey(adapter.Id),
                    Link = link,
                    Facts = { new("Address", key), new("Note", "This adapter reaches the same router as another adapter above.") }
                };
            }

            var node = new ConnectionNode
            {
                Id = $"router-{ShortHash(key)}",
                Kind = "router",
                Name = "Router",
                Detail = router?.PingMs is { } ping ? $"{key} · {Latency(ping)}" : key,
                Net = NetKey(adapter.Id),
                Link = link,
                Facts = { new("Address", key) }
            };
            if (router?.HostName is { } host)
            {
                node.Name = $"Router · {host}";
                node.Facts.Add(new("Name", host));
            }
            RouterIdentity? identity = null;
            _discovery?.Routers.TryGetValue(key, out identity);
            // Everything else this PC can see on the same network.
            var neighbors = (_discovery?.Devices ?? [])
                .Where(d => !d.Address.Equals(gateway) && !adapter.DnsServers.Contains(d.Address) &&
                            adapter.IPv4.Any(cidr => NetworkDiscovery.InSubnet(d.Address, cidr)))
                .ToList();
            // Each radio heard belongs to the mesh unit (or the router) whose LAN MAC is numerically closest.
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
            if (router?.Mac is { } mac) node.Facts.Add(new("MAC address", _privacy ? Hidden : mac));
            if (router?.PingMs is { } latency) node.Facts.Add(new("Round trip", Latency(latency)));
            if (adapter.DhcpServers.Any(d => d.Equals(gateway))) node.Facts.Add(new("Hands out addresses", "Yes (DHCP)"));
            if (adapter.DnsServers.Any(d => d.Equals(gateway))) node.Facts.Add(new("Answers DNS", "Yes"));
            if (wifi?.State == "connected" && wifi.Bssid is not null)
                node.Facts.Add(new("Wi-Fi access point", _privacy ? Hidden : wifi.Bssid));

            var internet = new ConnectionNode
            {
                Id = $"internet-{ShortHash(key)}",
                Kind = "internet",
                Name = "Internet",
                Detail = _network.InternetAccess switch
                {
                    true => "Windows reports Internet access",
                    false => "Windows reports no Internet access",
                    _ => "Public IP on request"
                },
                Net = NetKey(adapter.Id),
                Link = new ConnectionLink { Bus = "wan", Label = "Your Internet connection (WAN)", Short = "WAN" },
                Facts =
                {
                    new("Windows connectivity check", _network.InternetAccess switch { true => "Internet access", false => "No Internet access", _ => "Unknown" }),
                    new("Public IP", "Not looked up yet")
                }
            };
            node.Children.Add(internet);

            foreach (var dns in adapter.DnsServers.Where(d => !d.Equals(gateway)).Distinct())
            {
                _network.Dns.TryGetValue(dns.ToString(), out var facts);
                var local = adapter.IPv4.Any(cidr => SameSubnet(dns, cidr));
                var server = new ConnectionNode
                {
                    Id = $"dns-{ShortHash(dns.ToString())}",
                    Kind = "dns",
                    Name = facts?.HostName is { } name ? $"DNS · {name}" : "DNS server",
                    Detail = dns.ToString(),
                    Link = local
                        ? new ConnectionLink { Bus = "lan", Label = "On your local network", Short = "LAN" }
                        : new ConnectionLink { Bus = "wan", Label = "Across the Internet", Short = "DNS" },
                    Facts = { new("Address", dns.ToString()), new("Where", local ? "Your local network" : "The Internet") }
                };
                if (facts?.HostName is { } hostName)
                {
                    server.Facts.Add(new("Name", hostName));
                    if (hostName.Contains("pi.hole", StringComparison.OrdinalIgnoreCase))
                        server.Facts.Add(new("Looks like", "Pi-hole (network-wide ad blocking)"));
                }
                if (_discovery?.Devices.FirstOrDefault(d => d.Address.Equals(dns)) is { } seen)
                {
                    if (seen.Vendor is not null) server.Facts.Add(new("Hardware maker", $"{seen.Vendor} (from its MAC address)"));
                    if (seen.Name is not null && !_privacy) server.Facts.Add(new("Calls itself", $"{seen.Name} ({seen.NameSource})"));
                }
                (local ? node : internet).Children.Add(server);
            }

            if (neighbors.Count > 0)
            {
                var group = new ConnectionNode
                {
                    Id = $"lan-{ShortHash(key)}",
                    Kind = "lan-group",
                    Name = "Devices on your network",
                    Detail = neighbors.Count == 1 ? "1 device found" : $"{neighbors.Count} devices found",
                    Link = new ConnectionLink { Bus = "lan", Label = "Your local network", Short = "LAN" },
                    Facts =
                    {
                        new("Found through", "This PC's list of recent neighbors, plus devices that answered multicast DNS or UPnP"),
                        new("Names from", "Multicast DNS, NetBIOS, UPnP, and your DNS server"),
                        new("Makers from", "The first half of each device's MAC address (IEEE registry)"),
                        new("Not listed", "Devices this PC has not talked to lately and that stay quiet; Find more devices pings every address to list them")
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
            if (identity.DeviceName is { } deviceName && !_privacy) node.Facts.Add(new("Calls itself", deviceName));
            if (identity.MacVendor is not null) node.Facts.Add(new("Network card maker", identity.MacVendor));
            if (identity.CertificateName is not null) node.Facts.Add(new("Web interface", $"https://{identity.CertificateName}"));
            if (identity.Ssids.Count > 0)
                node.Facts.Add(new("Wi-Fi it broadcasts", _privacy ? Hidden : string.Join(", ", identity.Ssids)));
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
                    node.Note = $"Your network is a mesh: {identity.UnitsInRange} access points are in range of this PC. " +
                        $"The strongest signal here is {SignalWord(nearest.RssiDbm)} ({nearest.RssiDbm} dBm, {NetworkProbe.Band(nearest.FrequencyMhz, null)}){which}";
                }
            }
            var parts = new List<string> { address };
            if (router?.PingMs is { } ping) parts.Add(Latency(ping));
            if (identity.UnitsInRange > 1) parts.Add($"mesh · {identity.UnitsInRange} units");
            node.Detail = string.Join(" · ", parts);
        }

        private ConnectionNode BuildLanDevice(LanDevice device, RouterFacts? router, RouterIdentity? identity,
            Dictionary<string, (string Mac, string Label)> radioOwners)
        {
            var kind = LanKind(device, router);
            var octet = device.Address.GetAddressBytes()[3];
            // A model ("Xbox One") names the device well without saying whose it is. Mesh units are named
            // after the router model they belong to.
            var generic = device.Model ?? (kind == "mesh" && identity?.Model is { } model ? $"{model} unit" : LanKindName(kind, device.Vendor));
            var node = new ConnectionNode
            {
                Id = $"lan-{ShortHash(device.Mac ?? device.Address.ToString())}",
                Kind = kind,
                Name = device.Name is not null && !_privacy ? device.Name : generic,
                Detail = string.Join(" · ", new[]
                {
                    device.Address.ToString(),
                    device.Vendor ?? (device.RandomMac ? "private address" : null)
                }.Where(s => s is not null)),
                Link = new ConnectionLink { Bus = "lan", Label = "Your local network", Short = $".{octet}" }
            };
            node.Facts.Add(new("Address", device.Address.ToString()));
            if (device.Name is not null) node.Facts.Add(new("Calls itself", _privacy ? Hidden : $"{device.Name} (from {device.NameSource})"));
            if (device.Model is not null) node.Facts.Add(new("Model", device.Model));
            if (device.Vendor is not null) node.Facts.Add(new("Made by", device.Mac is null ? device.Vendor : $"{device.Vendor} (from its MAC address)"));
            if (device.RandomMac)
                node.Facts.Add(new("Private address", "It uses a random or locally assigned MAC address (phones, tablets, laptops, and virtual machines do), so its maker can't be looked up."));
            if (device.Mac is not null) node.Facts.Add(new("MAC address", _privacy ? Hidden : device.Mac));

            // A mesh unit's Wi-Fi radios have BSSIDs right next to its LAN MAC: that tells how well this PC hears it.
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
                        node.Note = "This is the access point closest to this PC.";
                }
            }
            return node;
        }

        // How far apart two MAC addresses are as numbers, when their first four bytes match.
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
            // Mesh units share the router's MAC block (same first four bytes), unlike other gadgets from its maker.
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
            // A random MAC with no name: almost always a phone, tablet, or laptop with privacy addressing on.
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

        private static ConnectionLink RouterLink(AdapterFacts adapter, WifiFacts? wifi)
        {
            if (wifi?.State == "connected")
            {
                var band = NetworkProbe.Band(wifi.FrequencyMhz, wifi.Channel);
                var generation = wifi.Phy?.StartsWith("Wi-Fi", StringComparison.Ordinal) == true
                    ? string.Join(' ', wifi.Phy.Split(' ').Take(2))
                    : wifi.Phy ?? "Wi-Fi";
                var rate = wifi.ReceiveMbps is { } rx ? $"{rx:0} Mbps" : BitRate(adapter.SpeedBitsPerSecond);
                return new ConnectionLink
                {
                    Bus = "wifi",
                    Label = string.Join(" · ", new[] { generation, band, rate }.Where(s => s is not null)),
                    Short = band is null ? rate : $"{band} · {rate}",
                    Gbps = (wifi.ReceiveMbps ?? adapter.SpeedBitsPerSecond / 1e6) / 1000
                };
            }
            return new ConnectionLink
            {
                Bus = "ethernet",
                Label = $"Ethernet · {BitRate(adapter.SpeedBitsPerSecond)}",
                Short = BitRate(adapter.SpeedBitsPerSecond),
                Gbps = adapter.SpeedBitsPerSecond / 1e9
            };
        }

        // Adapters with no hardware behind them: Hyper-V and WSL switches, VPNs, and tunnels.
        private void AddVirtualAdapters()
        {
            // Only adapters that carry an address: packet-filter layers and WAN miniports enumerate too.
            var virtuals = _network.Adapters
                .Where(a => a.Up && a.IPv4.Count > 0 && !_adaptersPlaced.Contains(a.Id) && a.Type != NetworkInterfaceType.Loopback)
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
            foreach (var adapter in virtuals)
            {
                var node = new ConnectionNode
                {
                    Id = $"v{ShortHash(adapter.Id)}",
                    Kind = "virtual",
                    Name = adapter.Name,
                    Detail = adapter.IPv4.FirstOrDefault()?.Split('/')[0] ?? adapter.Description,
                    Net = NetKey(adapter.Id),
                    Link = new ConnectionLink { Bus = "internal", Label = "Software adapter", Short = "virtual" },
                    Facts = { new("Description", adapter.Description) }
                };
                AddAddressFacts(node, adapter);
                if (adapter.Up && adapter.Gateways.Count > 0 && adapter.Primary)
                    node.Children.Add(BuildRouter(adapter, null));
                group.Children.Add(node);
            }
            _cpu.Children.Add(group);
        }

        // ---- Shared helpers ------------------------------------------------------------------

        private ConnectionNode NewNode(DeviceNode device, string kind, string name) => new()
        {
            Id = NodeId(device),
            Kind = kind,
            Name = _privacy ? Privacy.Scrub(name) : name
        };

        private static string NodeId(DeviceNode device) => "d" + ShortHash(device.InstanceId);

        private static void AddProblem(ConnectionNode node, DeviceNode device)
        {
            if (device.Problem == 0) return;
            // CM_PROB_HELD_FOR_EJECT: stopped by an eject and waiting to be unplugged. Expected, not a fault.
            if (device.Problem == 47)
            {
                node.Detail = "Stopped · safe to unplug";
                node.Note = "Windows stopped this device so it can be unplugged safely. Unplug it, or unplug and reconnect it to use it again.";
                return;
            }
            node.Problem = true;
            node.Warning = device.Problem switch
            {
                22 => "Disabled in Device Manager (code 22).",
                28 => "No driver is installed for this device (code 28).",
                10 => "Windows could not start this device (code 10).",
                43 => "Windows stopped this device because it reported a problem (code 43).",
                _ => $"Windows reports a problem with this device (code {device.Problem})."
            };
        }

        private static void AddDriverFact(ConnectionNode node, DeviceNode device, string label = "Driver")
        {
            if (device.DriverKey is null) return;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{device.DriverKey}");
                var version = key?.GetValue("DriverVersion") as string;
                if (string.IsNullOrWhiteSpace(version)) return;
                var date = key?.GetValue("DriverDate") as string;
                node.Facts.Add(new(label, string.IsNullOrWhiteSpace(date) ? version : $"{version} ({date})"));
            }
            catch
            {
            }
        }

        private static void AddLinkFacts(ConnectionNode node, PciLink link)
        {
            node.Facts.Add(new("PCIe link now", $"PCIe {Generation(link.Speed)} x{link.Width} ({Rate(LaneGbps(link.Speed) * link.Width)})"));
            node.Facts.Add(new("PCIe link maximum", $"PCIe {Generation(link.MaxSpeed)} x{link.MaxWidth} ({Rate(LaneGbps(link.MaxSpeed) * link.MaxWidth)})"));
        }

        private string LinkWarning(PciLink link, string what)
        {
            var now = $"PCIe {Generation(link.Speed)} x{link.Width} ({Rate(LaneGbps(link.Speed) * link.Width)})";
            var max = $"PCIe {Generation(link.MaxSpeed)} x{link.MaxWidth} ({Rate(LaneGbps(link.MaxSpeed) * link.MaxWidth)})";
            var reasons = new List<string>();
            if (link.Speed < link.MaxSpeed)
                reasons.Add(_pcie3Cpu && link.Speed == 3
                    ? "This APU's PCIe lanes run at 3.0."
                    : "The slot, the CPU, or a BIOS setting caps the PCIe generation.");
            if (link.Width < link.MaxWidth)
                reasons.Add(what == "card"
                    ? $"Only {link.Width} lanes trained: the card may be wired for {link.Width}, the slot may share lanes with an M.2 socket, or the card may not be fully seated."
                    : $"This M.2 socket or its lane sharing gives the drive only {link.Width} of its {link.MaxWidth} lanes.");
            return $"Linked at {now}; the {what} supports {max}. {string.Join(' ', reasons)}";
        }

        private static ConnectionLink PciLink(PciLink link) => new()
        {
            Bus = "pcie",
            Label = $"PCIe {Generation(link.Speed)} x{link.Width}",
            Short = $"{Generation(link.Speed)} x{link.Width}",
            Gbps = LaneGbps(link.Speed) * link.Width,
            Max = link.BelowMax ? $"PCIe {Generation(link.MaxSpeed)} x{link.MaxWidth}" : null,
            MaxGbps = link.BelowMax ? LaneGbps(link.MaxSpeed) * link.MaxWidth : null,
            Degraded = link.BelowMax
        };

        private static ConnectionLink UsbLink(UsbPort? port)
        {
            if (port is null) return new ConnectionLink { Bus = "usb", Label = "USB", Short = "USB" };
            var (label, shortLabel, gbps) = port switch
            {
                { SuperSpeedPlus: true } => ("USB 3.2 Gen 2 · 10 Gbps", "10 Gbps", 10d),
                { SuperSpeed: true } or { Speed: 3 } => ("USB 3.2 Gen 1 · 5 Gbps", "5 Gbps", 5d),
                { Speed: 2 } => ("USB 2.0 · 480 Mbps", "480 Mbps", 0.48),
                { Speed: 1 } => ("USB full speed · 12 Mbps", "12 Mbps", 0.012),
                _ => ("USB low speed · 1.5 Mbps", "1.5 Mbps", 0.0015)
            };
            var link = new ConnectionLink { Bus = "usb", Label = label, Short = shortLabel, Gbps = gbps };
            if (port.SuperSpeedCapable && !port.SuperSpeed)
            {
                link.Max = "USB 3.2 Gen 1 · 5 Gbps";
                link.MaxGbps = 5;
                link.Degraded = true;
            }
            else if (port.SuperSpeedPlusCapable && !port.SuperSpeedPlus)
            {
                link.Max = "USB 3.2 Gen 2 · 10 Gbps";
                link.MaxGbps = 10;
                link.Degraded = true;
            }
            return link;
        }

        private string UsbName(DeviceNode device, (string Vendor, string? Product)? names, string fallback)
        {
            var reported = device.BusReportedName;
            // Windows' own name can be fuller than the USB product string ("Game Capture HD60" vs "Game Capture HD").
            if (reported is not null && !GenericUsbName().IsMatch(device.Name) &&
                device.Name.Length > reported.Length && device.Name.Contains(reported, StringComparison.OrdinalIgnoreCase))
                return device.Name;
            if (reported is not null && !GenericUsbName().IsMatch(reported)) return reported;
            if (names?.Product is { } product && !GenericUsbName().IsMatch(product)) return product;
            return GenericUsbName().IsMatch(device.Name) ? fallback : device.Name;
        }

        private static string UsbKind(DeviceNode device, List<DeviceNode> inside)
        {
            var text = string.Join(' ', inside.Select(n => n.Name).Append(device.BusReportedName ?? ""));
            // Not a bare "controller": receivers expose a "HID-compliant system controller" too.
            if (inside.Any(n => n.IsClass("XboxComposite")) || GamepadName().IsMatch(text))
                return "gamepad";
            if (Regex.IsMatch(text, @"\b(LED|RGB|Polychrome|Aura|Mystic Light|lighting)\b", RegexOptions.IgnoreCase)) return "rgb";
            if (Regex.IsMatch(text, "capture|webcam|camera", RegexOptions.IgnoreCase) || inside.Any(n => n.IsClass("Camera") || n.IsClass("Image")))
                return "capture";
            if (inside.Any(n => n.IsClass("MEDIA") || n.IsClass("AudioEndpoint"))) return "audio";
            if (Regex.IsMatch(text, "receiver|unifying|bolt|lightspeed|dongle", RegexOptions.IgnoreCase)) return "receiver";
            if (inside.Any(n => n.IsClass("Keyboard"))) return "keyboard";
            if (inside.Any(n => n.IsClass("Mouse"))) return "mouse";
            if (inside.Any(n => n.IsClass("HIDClass"))) return "input";
            if (inside.Any(n => n.IsClass("Printer"))) return "printer";
            if (inside.Any(n => n.IsClass("Ports"))) return "serial";
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

        private string BluetoothName(string name)
        {
            if (!_privacy) return name;
            // Phones and headphones are often named after their owner ("Sam's AirPods").
            return name.Contains("'s ", StringComparison.Ordinal) || name.Contains("’s ", StringComparison.Ordinal)
                ? "Bluetooth device"
                : Privacy.Scrub(name);
        }

        private static string? HdAudioCodecName(DeviceNode controller) =>
            controller.Children.FirstOrDefault(c => c.IdStarts(@"HDAUDIO\"))?.Name;

        private static bool IsAudioController(DeviceNode device) =>
            device.IsClass("MEDIA") || device.Name.Contains("High Definition Audio", StringComparison.OrdinalIgnoreCase);

        private static bool IsGpuAudio(DeviceNode device, IReadOnlyList<DeviceNode> siblings) =>
            siblings.Any(s => s != device && s.IsClass("Display") && VendorOf(s) == VendorOf(device));

        private static bool IsNvme(DeviceNode device) =>
            device.IdStarts(@"PCI\") &&
            (device.Name.Contains("NVM Express", StringComparison.OrdinalIgnoreCase) ||
             device.Name.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
             (device.Service?.Contains("nvme", StringComparison.OrdinalIgnoreCase) ?? false));

        private static bool IsPci(DeviceNode device) => device.IdStarts(@"PCI\");

        private static bool IsBridge(DeviceNode device) =>
            device.IdStarts(@"PCI\") && device.Children.Any(IsPci);

        private static bool IsUpstreamSwitch(DeviceNode device) =>
            device.Name.Contains("Upstream", StringComparison.OrdinalIgnoreCase) && device.Name.Contains("Switch", StringComparison.OrdinalIgnoreCase);

        private static int VendorOf(DeviceNode device)
        {
            var match = PciVendor().Match(device.InstanceId);
            return match.Success ? Convert.ToInt32(match.Groups[1].Value, 16) : 0;
        }

        private static (int Vendor, int Product)? UsbId(string instanceId)
        {
            var match = UsbVidPid().Match(instanceId);
            return match.Success && instanceId.StartsWith(@"USB\", StringComparison.OrdinalIgnoreCase)
                ? (Convert.ToInt32(match.Groups[1].Value, 16), Convert.ToInt32(match.Groups[2].Value, 16))
                : null;
        }

        // Vendors the bundled usb.ids snapshot does not list yet.
        private static string? ExtraUsbVendor(int vendor) => vendor switch
        {
            0x26CE => "ASRock",
            _ => null
        };

        private static string? VendorName(int vendor) => vendor switch
        {
            0x10DE => "NVIDIA",
            0x1002 or 0x1022 => "AMD",
            0x8086 => "Intel",
            _ => null
        };

        private static long? VideoMemory(DeviceNode device)
        {
            if (device.DriverKey is null) return null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Control\Class\{device.DriverKey}");
                return key?.GetValue("HardwareInformation.qwMemorySize") switch
                {
                    long bytes when bytes > 0 => bytes,
                    byte[] raw when raw.Length >= 8 => BitConverter.ToInt64(raw),
                    _ => key?.GetValue("HardwareInformation.MemorySize") switch
                    {
                        int bytes when bytes > 0 => (uint)bytes,
                        byte[] raw when raw.Length >= 4 => BitConverter.ToUInt32(raw),
                        _ => null
                    }
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ConnectorName(uint technology) => technology switch
        {
            0 => "VGA",
            4 => "DVI",
            5 => "HDMI",
            6 => "LVDS",
            10 => "DisplayPort",
            11 => "eDP",
            12 or 13 => "UDI",
            15 => "Miracast",
            16 => "USB display",
            17 => "Virtual display",
            0x80000000 => "Internal",
            _ => "Display"
        };

        private static string BusName(int bus) => bus switch
        {
            1 => "SCSI",
            3 => "ATA",
            7 => "USB",
            8 => "RAID",
            10 => "SAS",
            11 => "SATA",
            12 => "SD card",
            13 => "MMC",
            14 or 15 => "Virtual disk",
            16 => "Storage Spaces",
            17 => "NVMe",
            _ => "Other"
        };

        private static string CleanDiskName(DeviceNode disk)
        {
            var split = SplitScsiModel().Match(disk.InstanceId);
            if (split.Success && char.IsLetterOrDigit(split.Groups[1].Value[^1]) && char.IsDigit(split.Groups[2].Value[0]))
                return (split.Groups[1].Value + split.Groups[2].Value).Replace('_', ' ').Trim();
            return CleanDiskName(disk.Name);
        }

        private static string CleanDiskName(string name) =>
            DiskSuffix().Replace(name, "").Trim();

        private static string ShortCpuName(string name)
        {
            var text = CpuNoise().Replace(name, " ");
            return Regex.Replace(text, @"\s{2,}", " ").Trim();
        }

        private static string Generation(int speed) => speed switch
        {
            1 => "1.1",
            2 => "2.0",
            3 => "3.0",
            4 => "4.0",
            5 => "5.0",
            6 => "6.0",
            _ => $"gen {speed}"
        };

        // Usable data rate per lane after encoding overhead, in Gbps.
        private static double LaneGbps(int speed) => speed switch
        {
            1 => 2.0,
            2 => 4.0,
            3 => 7.877,
            4 => 15.754,
            5 => 31.508,
            6 => 60.5,
            _ => 0
        };

        private static string Rate(double gbps) => gbps >= 8
            ? $"{gbps / 8:0.#} GB/s"
            : $"{gbps * 125:0} MB/s";

        private static string Latency(long milliseconds) => milliseconds < 1 ? "<1 ms" : $"{milliseconds} ms";

        private static string BitRate(long bitsPerSecond) => bitsPerSecond switch
        {
            >= 1_000_000_000 => $"{bitsPerSecond / 1e9:0.#} Gbps",
            >= 1_000_000 => $"{bitsPerSecond / 1e6:0} Mbps",
            > 0 => $"{bitsPerSecond / 1e3:0} Kbps",
            _ => "unknown speed"
        };

        private static bool SameSubnet(IPAddress address, string cidr)
        {
            var parts = cidr.Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefix)) return false;
            if (network.AddressFamily != address.AddressFamily || prefix is < 0 or > 32) return false;
            var mask = prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);
            var a = BitConverter.ToUInt32(address.GetAddressBytes().Reverse().ToArray());
            var b = BitConverter.ToUInt32(network.GetAddressBytes().Reverse().ToArray());
            return (a & mask) == (b & mask);
        }

        private static string? ReadMachineString(string path, string name)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(path);
                var value = (key?.GetValue(name) as string)?.Trim();
                return string.IsNullOrEmpty(value) || value is "Default string" or "To be filled by O.E.M." ? null : value;
            }
            catch
            {
                return null;
            }
        }

        private static IEnumerable<ConnectionNode> Walk(ConnectionNode node)
        {
            yield return node;
            foreach (var child in node.Children)
                foreach (var nested in Walk(child)) yield return nested;
        }

        [GeneratedRegex(@"\bRyzen\s+\d\s+(PRO\s+)?[45]\d{2,3}G[ET]?\b", RegexOptions.IgnoreCase)]
        private static partial Regex Pcie3Apu();

        // AMD desktop chipsets: A320 … X870. "B550M" is a board size, so only the chipset part is kept.
        [GeneratedRegex(@"\b([ABX][3-9][2-7]0)[A-Z]?\b", RegexOptions.IgnoreCase)]
        private static partial Regex ChipsetModel();

        [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
        private static partial Regex PciVendor();

        [GeneratedRegex(@"VID_([0-9A-F]{4})&PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
        private static partial Regex UsbVidPid();

        [GeneratedRegex(@"Wi-?Fi|Wireless|WLAN|802\.11", RegexOptions.IgnoreCase)]
        private static partial Regex WirelessName();

        // Names Windows makes up ("Network", "Network 5"); anything else may be a Wi-Fi name.
        [GeneratedRegex(@"^(Unidentified network|Network( \d+)?)$", RegexOptions.IgnoreCase)]
        private static partial Regex GenericNetworkName();

        [GeneratedRegex(@"^(Generic\s+)?(USB\s*)?(\d\.\d\s*)?(Receiver|(SuperSpeed\s+)?(USB\s+)?Hub|Composite Device|Input Device|Device|Mass Storage( Device)?|Storage|Keyboard|Mouse|Gamepad|Controller)$", RegexOptions.IgnoreCase)]
        private static partial Regex GenericUsbName();

        [GeneratedRegex(@"game\s*controller|gamepad|joystick|xbox|dualsense|dualshock|wireless controller", RegexOptions.IgnoreCase)]
        private static partial Regex GamepadName();

        // SCSI inquiry splits a long model into an 8-character vendor and a product: VEN_WDC_WDS5&PROD_00G2B0C-00PXH0.
        [GeneratedRegex(@"\\DISK&VEN_([^&]{8})&PROD_([^&\\]+)", RegexOptions.IgnoreCase)]
        private static partial Regex SplitScsiModel();

        [GeneratedRegex(@"USB (\d)\.(\d)", RegexOptions.IgnoreCase)]
        private static partial Regex UsbVersion();

        [GeneratedRegex(@"\s+(USB Device|SCSI Disk Device|ATA Device)$", RegexOptions.IgnoreCase)]
        private static partial Regex DiskSuffix();

        [GeneratedRegex(@"\((R|TM)\)|\bwith Radeon( Vega)? Graphics\b|\b\d+-Core Processor\b|\bCPU\b|@\s*[\d.]+\s*GHz|\bProcessor\b", RegexOptions.IgnoreCase)]
        private static partial Regex CpuNoise();
    }
}
