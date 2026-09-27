using System.Diagnostics;
using BoardScout.Models;
using Microsoft.Win32;

namespace BoardScout.Services;

/// <summary>
/// Turns what the Connections map found into a plan: where each USB drive and device should be plugged
/// in, how to make every drive faster on the port or slot it uses, whether memory is placed correctly,
/// which devices conflict or use the most interrupt time, and which draw the most power.
/// </summary>
internal static class PlanService
{
    public static OptimizationPlan Build(ConnectionsService.Capture capture, ConnectionsSnapshot map,
        IReadOnlyList<CoreInterruptLoad> cores, InterruptProfile? profile, bool privacy, IReadOnlyList<PowerReading>? power = null)
    {
        var watch = Stopwatch.StartNew();
        var context = new Context(capture, map, privacy);
        var plan = new OptimizationPlan
        {
            FormFactor = capture.Firmware.FormFactor,
            Elevated = SystemTelemetryService.IsElevated,
            UsbControllers = context.Controllers.Select(c => c.Summary).ToList(),
            Cores = cores.Select(c => new CoreLoadRow
            {
                Core = c.Core,
                InterruptPercent = c.InterruptPercent,
                DpcPercent = c.DpcPercent,
                InterruptsPerSecond = c.InterruptsPerSecond,
                DpcsPerSecond = c.DpcsPerSecond
            }).ToList()
        };

        plan.Sections.Add(UsbPorts(context));
        plan.Sections.Add(UsbDrives(context));
        plan.Sections.Add(InsideThePc(context));
        var memory = MemoryAdvice.Analyze(capture.Firmware, Board());
        plan.Sections.Add(Memory(memory, plan));
        plan.Sections.Add(Interrupts(context, cores, profile, plan));
        plan.Sections.Add(Power(context, power ?? []));
        plan.Sections.Add(Compatibility(context, memory));
        plan.GatheredMs = watch.ElapsedMilliseconds;
        return plan;
    }

    /// <summary>The whole plan, for the command line: capture, a one-second interrupt sample, and (when
    /// elevated and asked) a ten-second driver profile.</summary>
    public static async Task<string> GatherJsonAsync(bool privacy, bool profile)
    {
        var capture = await Task.Run(ConnectionsService.Read);
        var map = ConnectionsService.Build(capture, privacy, null);
        var cores = await InterruptStats.SampleAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        InterruptProfile? drivers = profile && SystemTelemetryService.IsElevated
            ? await InterruptProfiler.MeasureAsync(TimeSpan.FromSeconds(10), capture.Tree, CancellationToken.None)
            : null;
        return ToJson(Build(capture, map, cores, drivers, privacy));
    }

    public static string ToJson(OptimizationPlan plan) =>
        System.Text.Json.JsonSerializer.Serialize(plan, BoardScoutJson.Default.OptimizationPlan);

    // ---- What the plan works from ------------------------------------------------------------------

    private sealed class Controller
    {
        public required DeviceNode Device { get; init; }
        public required ConnectionNode Node { get; init; }
        public required UsbControllerSummary Summary { get; init; }
        public required List<(int Port, int Protocols, bool InUse)> Ports { get; init; }
    }

    private sealed class UsbStorage
    {
        public required DeviceNode Device { get; init; }
        public required DeviceNode Disk { get; init; }
        public required string Name { get; init; }
        public required Controller? Controller { get; init; }
        public DiskFacts? Facts { get; init; }
        public bool Uas { get; init; }
        public bool OnHub { get; init; }
        public string Id => ConnectionsService.DeviceNodeId(Device);
        public bool Ssd => Facts?.Spinning == false;
        public bool Hdd => Facts?.Spinning == true;
    }

    private sealed class Context
    {
        public Context(ConnectionsService.Capture capture, ConnectionsSnapshot map, bool privacy)
        {
            Capture = capture;
            Privacy = privacy;
            Walk(map.Root, null);
            All = capture.Tree?.Descendants().ToList() ?? [];

            foreach (var device in All.Where(d => d.IdStarts(@"PCI\") && d.IsClass("USB")))
            {
                var nodeId = ConnectionsService.DeviceNodeId(device);
                if (!Nodes.TryGetValue(nodeId, out var node)) continue;
                var parent = Parents.GetValueOrDefault(nodeId);
                var where = parent?.Kind switch
                {
                    "chipset" => "chipset",
                    "integrated" => "CPU",
                    _ => "add-in card"
                };
                var roots = device.Children.Where(c => c.IdStarts(@"USB\ROOT_HUB")).ToList();
                var counts = roots.Select(r => CountPorts(r.PortMap)).ToList();
                var direct = roots.SelectMany(r => r.Children).Where(c => c.Usb is not null).ToList();
                Controllers.Add(new Controller
                {
                    Device = device,
                    Node = node,
                    Ports = roots.SelectMany(r => r.PortMap).ToList(),
                    Summary = new UsbControllerSummary
                    {
                        Node = nodeId,
                        Name = where == "CPU" ? "CPU's USB controller" : where == "chipset" ? "Chipset's USB controller" : node.Name,
                        Where = where,
                        Usb3Ports = counts.Sum(c => c.Usb3),
                        Usb3Used = counts.Sum(c => c.Usb3Used),
                        Usb2Ports = counts.Sum(c => c.Usb2),
                        Usb2Used = counts.Sum(c => c.Usb2Used),
                        PowerMa = direct.Where(d => d.Usb?.SelfPowered != true).Sum(d => d.Usb?.PowerMa ?? 0)
                    }
                });
            }
            // Number repeated controllers so the plan can tell them apart.
            foreach (var group in Controllers.GroupBy(c => c.Summary.Name).Where(g => g.Count() > 1))
            {
                var i = 1;
                foreach (var controller in group) controller.Summary.Name += $" #{i++}";
            }

            var found = new List<(DeviceNode Device, DeviceNode Disk, string Name, DiskFacts? Facts)>();
            foreach (var device in All.Where(d => d.IdStarts(@"USB\VID_") && !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)))
            {
                var disk = device.Descendants().FirstOrDefault(d => d.IsClass("DiskDrive"));
                if (disk is null || device.Usb?.IsHub == true || device.HubPorts is > 0) continue;
                var facts = capture.Disks.TryGetValue(disk.Handle, out var number) ? capture.DiskFacts.GetValueOrDefault(number) : null;
                found.Add((device, disk, NameOf(device), facts));
            }
            // Two drives of the same model (two "WD Game Drive"s) are told apart by capacity.
            var repeated = found.GroupBy(f => f.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
            foreach (var (device, disk, name, facts) in found)
            {
                var display = repeated.Contains(name) && facts?.SizeBytes is > 0 ? $"{name} ({ConnectionsService.FormatBytes(facts.SizeBytes.Value)})" : name;
                _names[device] = display;
                Storage.Add(new UsbStorage
                {
                    Device = device,
                    Disk = disk,
                    Name = display,
                    Controller = ControllerOf(device),
                    Facts = facts,
                    Uas = device.Service?.Equals("UASPStor", StringComparison.OrdinalIgnoreCase) == true ||
                          device.Descendants().Any(d => d.Service?.Equals("UASPStor", StringComparison.OrdinalIgnoreCase) == true),
                    OnHub = device.Parent is { } hub && !hub.IdStarts(@"USB\ROOT_HUB")
                });
            }

            // What each controller carries, with a hub's two halves (USB 3 and USB 2) listed once with their devices.
            var byId = _names.ToDictionary(n => ConnectionsService.DeviceNodeId(n.Key), n => n.Value);
            string Label(ConnectionNode n) => byId.GetValueOrDefault(n.Id) ?? n.Name;
            foreach (var controller in Controllers)
            {
                var devices = new List<string>();
                foreach (var group in controller.Node.Children.GroupBy(c => c.Kind == "usb-hub" ? "hub:" + c.Name : "id:" + c.Id))
                {
                    var first = group.First();
                    if (first.Kind != "usb-hub")
                    {
                        devices.Add(Label(first));
                        continue;
                    }
                    var inside = group.SelectMany(h => h.Children).Select(Label).ToList();
                    var hub = first.Name.EndsWith("hub", StringComparison.OrdinalIgnoreCase) ? first.Name : first.Name + " hub";
                    devices.Add(inside.Count == 0 ? $"{hub} (empty)" : $"{hub}: {string.Join(", ", inside)}");
                }
                controller.Summary.Devices = devices;
            }
        }

        private readonly Dictionary<DeviceNode, string> _names = [];

        public ConnectionsService.Capture Capture { get; }
        public bool Privacy { get; }
        public List<DeviceNode> All { get; }
        public Dictionary<string, ConnectionNode> Nodes { get; } = [];
        public Dictionary<string, ConnectionNode> Parents { get; } = [];
        public List<Controller> Controllers { get; } = [];
        public List<UsbStorage> Storage { get; } = [];

        public Controller? ControllerOf(DeviceNode device)
        {
            for (var node = device.Parent; node is not null; node = node.Parent)
                if (Controllers.FirstOrDefault(c => c.Device == node) is { } controller) return controller;
            return null;
        }

        public string NameOf(DeviceNode device) =>
            _names.TryGetValue(device, out var name) ? name
            : Nodes.TryGetValue(ConnectionsService.DeviceNodeId(device), out var node) ? node.Name
            : device.BusReportedName ?? device.Name;

        /// <summary>A hub's name, or what kind of hub it is when Windows only calls it "USB hub".</summary>
        public string HubName(DeviceNode hub)
        {
            var name = NameOf(hub);
            return name.Equals("USB hub", StringComparison.OrdinalIgnoreCase) || name.Contains("Generic", StringComparison.OrdinalIgnoreCase)
                ? hub.Usb?.SuperSpeed == true ? "a USB 3 hub" : "a USB 2 hub"
                : name;
        }

        private void Walk(ConnectionNode node, ConnectionNode? parent)
        {
            Nodes[node.Id] = node;
            if (parent is not null) Parents[node.Id] = parent;
            foreach (var child in node.Children) Walk(child, node);
        }
    }

    // A USB 3 connector shows up on its root hub twice: once as a SuperSpeed port and once as a USB 2 port for
    // older devices. Windows says which ports pair up only when the firmware describes its connectors, which
    // many boards don't, so ports pair in order (the first SuperSpeed port with the first USB 2 port), the
    // layout xHCI controllers commonly use. A connector is in use when either half is; USB 2 ports left over
    // are USB 2-only connectors or internal headers.
    private static (int Usb3, int Usb3Used, int Usb2, int Usb2Used) CountPorts(List<(int Port, int Protocols, bool InUse)> ports)
    {
        var super = ports.Where(p => (p.Protocols & 4) != 0).OrderBy(p => p.Port).ToList();
        var legacy = ports.Where(p => (p.Protocols & 4) == 0).OrderBy(p => p.Port).ToList();
        var paired = Math.Min(super.Count, legacy.Count);
        var used = super.Where((p, i) => p.InUse || (i < paired && legacy[i].InUse)).Count();
        var only2 = legacy.Skip(paired).ToList();
        return (super.Count, used, only2.Count, only2.Count(p => p.InUse));
    }

    // ---- USB ports -------------------------------------------------------------------------------

    private static PlanSection UsbPorts(Context context)
    {
        var section = new PlanSection { Id = "usb-ports", Title = "USB ports" };
        var free3 = context.Controllers.Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        section.Summary = context.Controllers.Count == 0
            ? "No USB controllers found."
            : $"{context.Controllers.Count} USB controllers with {context.Controllers.Sum(c => c.Summary.Usb3Ports)} USB 3 ports ({free3} free) and " +
              $"{context.Controllers.Sum(c => c.Summary.Usb2Ports)} USB 2-only ports ({context.Controllers.Sum(c => c.Summary.Usb2Ports - c.Summary.Usb2Used)} free). " +
              "USB 2-only ports include internal headers, where Bluetooth and lighting controllers usually sit.";

        // Where the fast ports are.
        var cpuFree = context.Controllers.Where(c => c.Summary.Where == "CPU").Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        var chipsetFree = context.Controllers.Where(c => c.Summary.Where == "chipset").Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        if (free3 > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"{free3} free USB 3 port{(free3 == 1 ? "" : "s")}: {cpuFree} on the CPU's controllers, {chipsetFree} on the chipset's",
                Detail = "Ports on the CPU's own USB controllers reach the processor directly; the chipset's ports share the chipset's one link " +
                         "with its drives and network, which only matters when several of them are busy at once. Windows can't say where a " +
                         "port is on the case, so match them by plugging a device in and watching which controller it appears under on this map.",
                Nodes = context.Controllers.Where(c => c.Summary.Usb3Ports > c.Summary.Usb3Used).Select(c => c.Summary.Node).ToList()
            });
        }

        // USB 3 devices (other than drives, covered below) stuck on USB 2 links.
        foreach (var device in context.All.Where(d => d.Usb is { SuperSpeedCapable: true, SuperSpeed: false, IsHub: false } &&
                                                      d.HubPorts is null or 0 && context.Storage.All(s => s.Device != d)))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"Move {context.NameOf(device)} to a USB 3 port",
                Detail = $"It supports USB 3 (5 Gbps or more) but is connected at USB 2 speed (480 Mbps)" +
                         (device.Parent is { } hub && !hub.IdStarts(@"USB\ROOT_HUB") ? $", through {context.HubName(hub)}" : "") + ".",
                Action = cpuFree + chipsetFree > 0 ? "Plug it straight into a free USB 3 port (often blue, or marked SS)." : "Use a USB 3 port or hub.",
                Nodes = [ConnectionsService.DeviceNodeId(device)]
            });
        }

        // Several busy devices sharing one hub link.
        foreach (var hub in context.All.Where(d => d.HubPorts is > 0 && d.IdStarts(@"USB\VID_")))
        {
            var drives = context.Storage.Where(s => s.Device.Parent == hub).ToList();
            if (drives.Count >= 2)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drives.Count} drives share {context.HubName(hub)}",
                    Detail = $"{Join(drives.Select(d => d.Name))} reach the computer through one hub link. " +
                             (drives.All(d => d.Hdd)
                                 ? "Hard drives rarely fill a 5 Gbps link, so this is fine for everyday use; copying between them at once shares the link."
                                 : "Copying between them, or to both at once, splits the link's speed between them."),
                    Nodes = drives.Select(d => d.Id).Append(ConnectionsService.DeviceNodeId(hub)).ToList()
                });
            }
        }

        if (section.Items.Count == 0)
            section.Items.Add(new PlanItem { Tone = "good", Title = "Every USB device is on a port that matches its speed" });
        return section;
    }

    // ---- USB drives --------------------------------------------------------------------------------

    private static PlanSection UsbDrives(Context context)
    {
        var section = new PlanSection { Id = "usb-drives", Title = "USB drives" };
        var drives = context.Storage;
        section.Summary = drives.Count == 0
            ? "No USB drives are connected."
            : $"{drives.Count} USB drive{(drives.Count == 1 ? "" : "s")}: {drives.Count(d => d.Ssd)} SSD, {drives.Count(d => d.Hdd)} hard drive" +
              $"{(drives.Count(d => d.Hdd) == 1 ? "" : "s")}.";
        var atBest = new List<UsbStorage>();

        foreach (var drive in drives)
        {
            var port = drive.Device.Usb;
            if (port is null) continue;
            if (port.SuperSpeedCapable && !port.SuperSpeed)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "warn",
                    Title = $"{drive.Name} is running at USB 2 speed",
                    Detail = $"It supports USB 3 but is connected at 480 Mbps, about 40 MB/s: {(drive.Ssd ? "a tenth" : "a quarter")} of what it can do.",
                    Action = "Plug it into a USB 3 port, with a USB 3 cable, and not through a USB 2 hub.",
                    Nodes = [drive.Id]
                });
            }
            else if (!port.SuperSpeed && port.Speed <= 2)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} is a USB 2 drive",
                    Detail = "USB 2 limits it to about 40 MB/s whichever port it uses.",
                    Nodes = [drive.Id]
                });
            }
            else if (drive.Ssd && port.SuperSpeedPlusCapable && !port.SuperSpeedPlus)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "improve",
                    Title = $"{drive.Name} could run twice as fast",
                    Detail = "This SSD supports 10 Gbps but connected at 5 Gbps, so it tops out near 450 MB/s instead of about 1 GB/s.",
                    Action = "Use a 10 Gbps port (USB 3.2 Gen 2, often red or teal, or a USB-C port) and a 10 Gbps cable.",
                    Nodes = [drive.Id]
                });
            }
            else
            {
                atBest.Add(drive);
            }

            if (drive.Ssd && !drive.Uas)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} uses the older USB storage protocol",
                    Detail = "Its enclosure talks Bulk-Only Transport instead of UAS, which costs SSDs speed on small files. A UAS enclosure fixes it.",
                    Nodes = [drive.Id]
                });
            }
        }

        if (atBest.Count > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = atBest.Count == 1 ? $"{atBest[0].Name} already runs at full speed" : $"{atBest.Count} drives already run at full speed",
                Detail = string.Join("; ", atBest.GroupBy(d => Rate(d.Device.Usb!)).Select(g => $"{Join(g.Select(d => d.Name))} at {g.Key}")) + ". " +
                         (atBest.Any(d => d.Hdd)
                             ? "Each is linked at the fastest speed it supports. A hard drive can't move more than about 2 Gbps (250 MB/s), so any USB 3 port suits it."
                             : "Each is linked at the fastest speed it supports."),
                Nodes = atBest.Select(d => d.Id).ToList()
            });
        }

        // Write caching: worth turning on for drives that live on the desk (their own power supply or behind a hub).
        var stationary = drives.Where(d => d.Disk.RemovalPolicy == 3 && (d.Device.Usb?.SelfPowered == true || d.OnHub)).ToList();
        var portable = drives.Where(d => d.Disk.RemovalPolicy == 3 && !stationary.Contains(d)).ToList();
        if (stationary.Count > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"Turn on write caching for {Join(stationary.Select(d => d.Name))}",
                Detail = "Windows uses Quick removal for USB drives, which turns write caching off so they can be pulled at any time. For drives " +
                         "that stay connected, Better performance makes writes faster, but they must be ejected with Safely Remove Hardware first.",
                Action = "Device Manager → Disk drives → the drive → Properties → Policies → Better performance.",
                Nodes = stationary.Select(d => d.Id).ToList()
            });
        }
        if (portable.Count > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"Quick removal suits {Join(portable.Select(d => d.Name))}",
                Detail = "Bus-powered drives you carry around are safest with write caching off: they can be unplugged at any time.",
                Nodes = portable.Select(d => d.Id).ToList()
            });
        }

        if (drives.Count > 0 && section.Items.All(i => i.Tone == "good"))
            section.Items.Insert(0, new PlanItem { Tone = "good", Title = "Every USB drive runs at the speed its port and cable allow" });
        return section;
    }

    // ---- Inside the PC: drives, graphics, and the chipset link ------------------------------------

    private static PlanSection InsideThePc(Context context)
    {
        var section = new PlanSection { Id = "inside", Title = "Inside the PC" };
        var nvme = context.Nodes.Values.Where(n => n.Kind == "nvme").ToList();
        var sata = context.Nodes.Values.Where(n => n.Kind == "disk").ToList();
        var gpus = context.Nodes.Values.Where(n => n.Kind == "gpu").ToList();
        section.Summary = $"{nvme.Count} NVMe and {sata.Count} SATA drive{(nvme.Count + sata.Count == 1 ? "" : "s")}" +
                          (gpus.Count == 0 ? "." : $", and {(gpus.Count == 1 ? "a graphics card" : $"{gpus.Count} graphics cards")}.");
        var steel = Board()?.Contains("B550M Steel Legend", StringComparison.OrdinalIgnoreCase) == true;
        var cpuName = context.Nodes.GetValueOrDefault("cpu")?.Facts.FirstOrDefault(f => f.Label == "Processor")?.Value ?? "";
        var apu = System.Text.RegularExpressions.Regex.IsMatch(cpuName, @"\bRyzen\s+\d\s+(PRO\s+)?[45]\d{2,3}G[ET]?\b",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        foreach (var gpu in gpus.Where(g => g.Link?.Degraded == true))
            section.Items.Add(GraphicsLink(gpu, apu));

        foreach (var drive in nvme.Where(n => n.Link?.Degraded == true))
        {
            var link = drive.Link!;
            var lanes = link.Label.Split('x').Last();
            var maxLanes = link.Max?.Split('x').Last();
            if (lanes != maxLanes)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "improve",
                    Title = $"{drive.Name} gets {lanes} of its {maxLanes} PCIe lanes",
                    Detail = $"Linked at {link.Label}; the drive supports {link.Max}. " + (steel
                        ? "On this board the M2_2 socket is wired for 2 lanes. The PCIE3 slot (full length, x4 from the chipset) would give it all four with an M.2-to-PCIe adapter card, roughly doubling its speed."
                        : "The M.2 socket or its lane sharing gives it fewer lanes. A free x4 slot with an M.2-to-PCIe adapter card, or an M.2 socket wired for x4, would give it all four."),
                    Action = steel ? "Move it to an M.2-to-PCIe x4 adapter card in PCIE3, or keep it where it is at about 1.7 GB/s." : null,
                    Nodes = [drive.Id]
                });
            }
            else
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} runs at {link.Label} instead of {link.Max}",
                    Detail = apu
                        ? "Ryzen 5000G and 4000G processors run their PCIe lanes at 3.0, so a PCIe 4.0 drive links at 3.0 (about 3.9 GB/s instead of 7.9). A Ryzen 5000 processor without the G would run it at 4.0."
                        : "The slot, the processor, or a BIOS setting limits the PCIe generation.",
                    Nodes = [drive.Id]
                });
            }
        }

        // The drive that can go fastest belongs on the CPU's own lanes.
        double Potential(ConnectionNode n) => n.Link?.MaxGbps ?? n.Link?.Gbps ?? 0;
        bool BehindChipset(ConnectionNode n)
        {
            for (var p = context.Parents.GetValueOrDefault(n.Id); p is not null; p = context.Parents.GetValueOrDefault(p.Id))
                if (p.Kind == "chipset") return true;
            return false;
        }
        var cpuDrives = nvme.Where(n => !BehindChipset(n)).ToList();
        var chipsetDrives = nvme.Where(BehindChipset).ToList();
        if (cpuDrives.Count > 0 && chipsetDrives.Count > 0)
        {
            var bestBehind = chipsetDrives.MaxBy(Potential)!;
            var slowestOnCpu = cpuDrives.MinBy(Potential)!;
            section.Items.Add(Potential(bestBehind) > Potential(slowestOnCpu) * 1.2
                ? new PlanItem
                {
                    Tone = "improve",
                    Title = $"Swap {bestBehind.Name} and {slowestOnCpu.Name}",
                    Detail = $"{bestBehind.Name} can go faster ({bestBehind.Link?.Max ?? bestBehind.Link?.Label}) but sits behind the chipset, while " +
                             $"{slowestOnCpu.Name} ({slowestOnCpu.Link?.Max ?? slowestOnCpu.Link?.Label}) has the CPU's own M.2 socket.",
                    Action = "Swap the two drives' M.2 sockets; Windows finds them again on its own.",
                    Nodes = [bestBehind.Id, slowestOnCpu.Id]
                }
                : new PlanItem
                {
                    Tone = "good",
                    Title = $"{cpuDrives.MaxBy(Potential)!.Name} has the CPU's own M.2 socket",
                    Detail = "The fastest drive is on the CPU's lanes, where it doesn't share bandwidth with the chipset's other devices.",
                    Nodes = cpuDrives.Select(n => n.Id).ToList()
                });
        }

        if (context.Nodes.GetValueOrDefault("chipset") is { } chipset)
        {
            var behind = Descendants(chipset).Where(n => n.Kind is "nvme" or "disk" or "ethernet" or "wifi").ToList();
            var usbDrives = Descendants(chipset).Count(n => n.Kind == "storage");
            if (behind.Count + usbDrives >= 2)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = "Drives and network behind the chipset share one link",
                    Detail = $"{string.Join(", ", behind.Select(n => n.Name))}" +
                             (usbDrives > 0 ? $"{(behind.Count > 0 ? ", and " : "")}{usbDrives} USB drive{(usbDrives == 1 ? "" : "s")} on the chipset's USB ports" : "") +
                             $" reach the CPU through the chipset's {chipset.Link?.Label ?? "single link"}" +
                             (chipset.Link?.Gbps is { } gbps ? $" (about {gbps / 8:0.#} GB/s)" : "") + ". Big copies between two of them at once compete for it; " +
                             "drives on the CPU's own M.2 socket or USB ports don't.",
                    Nodes = behind.Select(n => n.Id).Append(chipset.Id).ToList()
                });
            }
        }

        var trim = ReadTrim();
        section.Items.Add(trim == false
            ? new PlanItem { Tone = "warn", Title = "TRIM is turned off", Detail = "SSDs slow down over time without TRIM.", Action = "Run fsutil behavior set DisableDeleteNotify 0 in an administrator terminal." }
            : new PlanItem { Tone = "good", Title = "TRIM is on", Detail = "Windows tells your SSDs which blocks are free, which keeps them fast." });
        if (sata.Count > 0)
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"SATA drive{(sata.Count == 1 ? "" : "s")} already at the interface limit",
                Detail = "SATA III tops out near 550 MB/s; SATA SSDs reach it on any SATA port, so there's nothing to move.",
                Nodes = sata.Select(n => n.Id).ToList()
            });
        return section;
    }

    // ---- Memory ------------------------------------------------------------------------------------

    private static PlanSection Memory(MemoryAnalysis? memory, OptimizationPlan plan)
    {
        var section = new PlanSection { Id = "memory", Title = "Memory" };
        if (memory is null)
        {
            section.Summary = "The firmware didn't describe the memory slots.";
            return section;
        }
        section.Summary = $"{memory.TotalGb:0.#} GB {memory.Type} in {memory.Populated} of {memory.Slots.Count} slots" +
                          (memory.ChannelText is null ? "" : $", {memory.ChannelText}") +
                          (memory.ConfiguredMts > 0 ? $", {memory.ConfiguredMts} MT/s." : ".");
        plan.MemorySlots = memory.Slots.Select(p => new MemorySlotRow
        {
            Slot = p.Label,
            Locator = $"{p.Slot.DeviceLocator}{(p.Slot.BankLocator.Length > 0 ? " / " + p.Slot.BankLocator : "")}",
            Channel = p.Slot.Channel,
            Populated = p.Slot.Populated,
            SizeGb = Math.Round(p.Slot.SizeMb / 1024d, 1),
            Speed = p.Slot.SpeedMts,
            Configured = p.Slot.ConfiguredMts,
            Part = p.Slot.PartNumber,
            Recommended = p.Recommended
        }).ToList();
        foreach (var finding in memory.Findings)
            section.Items.Add(new PlanItem { Tone = finding.Tone, Title = finding.Title, Detail = finding.Detail, Action = finding.Action, Nodes = ["memory"] });
        return section;
    }

    // ---- Conflicts and interrupts -----------------------------------------------------------------

    private static PlanSection Interrupts(Context context, IReadOnlyList<CoreInterruptLoad> cores, InterruptProfile? profile, OptimizationPlan plan)
    {
        var section = new PlanSection { Id = "interrupts", Title = "Conflicts and interrupts" };
        var devices = context.All;

        // Devices Windows reports a problem with (a resource conflict is code 12).
        foreach (var device in devices.Where(d => d.Problem != 0 && !d.IdStarts(@"SWD\") && !d.IdStarts(@"ROOT\")))
        {
            section.Items.Add(new PlanItem
            {
                Tone = device.Problem == 12 ? "warn" : "improve",
                Title = device.Problem == 12 ? $"{context.NameOf(device)} has a resource conflict" : $"{context.NameOf(device)} isn't working",
                Detail = device.Problem switch
                {
                    12 => "Windows couldn't give it the resources it needs because another device uses them (code 12).",
                    22 => "It's disabled in Device Manager (code 22).",
                    28 => "No driver is installed for it (code 28).",
                    10 => "Windows couldn't start it (code 10).",
                    43 => "Windows stopped it after it reported a problem (code 43).",
                    45 => "It isn't connected right now (code 45).",
                    _ => $"Windows reports problem code {device.Problem}."
                },
                Nodes = [ConnectionsService.DeviceNodeId(device)]
            });
        }

        // Legacy interrupt lines shared by more than one device (message-signaled interrupts can't be shared).
        var lines = devices.Where(d => !d.IdStarts(@"ACPI_HAL\") && !d.IdStarts(@"ROOT\"))
            .SelectMany(d => d.Irqs.Where(i => i.Irq >= 0).Select(i => (i.Irq, Device: d)))
            .GroupBy(x => x.Irq)
            .ToList();
        var shared = lines.Where(g => g.Select(x => x.Device).Distinct().Count() > 1).ToList();
        var pci = devices.Where(d => d.IdStarts(@"PCI\") && d.Irqs.Count > 0).ToList();
        var msi = pci.Count(d => d.Irqs.Any(i => i.Irq < 0));
        var legacy = pci.Where(d => d.Irqs.All(i => i.Irq >= 0)).ToList();
        foreach (var group in shared)
        {
            var names = group.Select(x => context.NameOf(x.Device)).Distinct().ToList();
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"IRQ {group.Key} is shared by {Join(names)}",
                Detail = "Sharing a legacy interrupt line works, but each interrupt makes every driver on the line check whether it's theirs, which adds latency.",
                Nodes = group.Select(x => ConnectionsService.DeviceNodeId(x.Device)).Distinct().ToList()
            });
        }
        if (shared.Count == 0 && devices.All(d => d.Problem != 12))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = "No conflicts: nothing shares an interrupt line",
                Detail = $"{msi} PCI device{(msi == 1 ? " uses" : "s use")} message-signaled interrupts, which can't conflict" +
                         (legacy.Count > 0 ? $"; {legacy.Count} still use{(legacy.Count == 1 ? "s" : "")} a legacy line of {(legacy.Count == 1 ? "its" : "their")} own ({string.Join(", ", legacy.Select(context.NameOf))})." : "."),
                Nodes = legacy.Select(ConnectionsService.DeviceNodeId).ToList()
            });
        }

        if (cores.Count > 0)
        {
            var total = cores.Sum(c => c.InterruptsPerSecond);
            var busiest = cores.MaxBy(c => c.InterruptsPerSecond)!;
            var dpc = cores.Average(c => c.DpcPercent);
            var isr = cores.Average(c => c.InterruptPercent);
            section.Summary = $"{total:N0} interrupts per second across {cores.Count} logical processors; interrupts and DPCs take {isr + dpc:0.##}% of processor time.";
            section.Items.Add(new PlanItem
            {
                Tone = isr + dpc > 5 ? "improve" : "good",
                Title = isr + dpc > 5 ? "Interrupts are using a noticeable share of the processor" : "Interrupt load is light",
                Detail = $"Processor {busiest.Core} handles the most: {busiest.InterruptsPerSecond:N0} of the {total:N0} per second. " +
                         "Windows spreads device interrupts across processors on its own; a busy processor 0 is normal."
            });
        }

        if (profile is not null)
        {
            plan.ProfileSeconds = Math.Round(profile.Seconds, 1);
            plan.ProfileError = profile.Error;
            plan.EventsLost = profile.EventsLost;
        }
        if (profile is { Error: null })
        {
            plan.Drivers = profile.Drivers.Take(12).Select(d => new DriverLoadRow
            {
                Driver = d.Driver,
                Description = d.Description,
                Devices = d.Devices.Select(name => context.Privacy ? Privacy.Scrub(name) : name).ToList(),
                IsrCount = d.IsrCount,
                IsrMs = d.IsrTotalMs,
                IsrMaxUs = d.IsrMaxUs,
                DpcCount = d.DpcCount,
                DpcMs = d.DpcTotalMs,
                DpcMaxUs = d.DpcMaxUs,
                CpuPercent = profile.Seconds > 0 ? Math.Round(d.TotalMs / (profile.Seconds * 1000 * profile.Cores) * 100, 3) : 0
            }).ToList();
            var slow = profile.Drivers.Where(d => d.DpcMaxUs > 1000 || d.IsrMaxUs > 500).ToList();
            foreach (var driver in slow)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = driver.DpcMaxUs > 2000 || driver.IsrMaxUs > 1000 ? "warn" : "improve",
                    Title = $"{driver.Driver} ran for up to {Math.Max(driver.DpcMaxUs, driver.IsrMaxUs) / 1000:0.##} ms at a time",
                    Detail = $"{driver.Description ?? "This driver"} held a processor that long in one {(driver.DpcMaxUs >= driver.IsrMaxUs ? "DPC" : "interrupt")}. " +
                             "Stretches over about 1 ms can cause audio crackles, dropped frames, or input lag.",
                    Action = "Update that device's driver from the maker; for network and graphics drivers, the newest version usually fixes long DPCs."
                });
            }
            if (profile.Drivers.Count > 0)
            {
                var top = profile.Drivers[0];
                section.Items.Add(new PlanItem
                {
                    Tone = slow.Count == 0 ? "good" : "info",
                    Title = $"Most interrupt time: {top.Driver}{(top.Description is null ? "" : $" ({top.Description})")}",
                    Detail = $"{top.TotalMs:0.#} ms over {profile.Seconds:0.#} s ({top.IsrCount:N0} interrupts, {top.DpcCount:N0} DPCs). " +
                             (slow.Count == 0 ? "No driver ran long enough to cause latency problems." : "")
                });
            }
        }
        else
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = "Which drivers use the most interrupt time?",
                Detail = profile?.Error ?? "Windows only tells administrators which driver an interrupt belongs to. Measure for 10 seconds to see each driver's share and its longest run.",
                Action = null
            });
        }
        return section;
    }

    // A graphics card linked below its best: expected (a card wired for fewer lanes, an APU's PCIe 3.0) or a
    // seating or slot problem worth fixing.
    private static PlanItem GraphicsLink(ConnectionNode gpu, bool apu)
    {
        var link = gpu.Link!;
        var (gen, width) = ParseLink(link.Label);
        var (maxGen, maxWidth) = ParseLink(link.Max);
        var wired = WiredLanes(gpu.Name);
        var parts = new List<string>();
        var tone = "info";
        if (width < maxWidth)
        {
            if (wired is { } lanes && width >= lanes)
            {
                parts.Add($"This card is wired for {lanes} lanes (its chip supports {maxWidth}), so x{width} is all it can use.");
            }
            else
            {
                tone = "improve";
                parts.Add($"It trained {width} of its {maxWidth} lanes. It belongs in the top full-length slot, the one wired to the CPU, pressed in until " +
                          "the latch clicks; on some boards an M.2 drive or a second card also takes lanes from it.");
            }
        }
        if (gen < maxGen)
            parts.Add(apu
                ? "Ryzen 5000G and 4000G processors run their PCIe lanes at 3.0, so the card links at 3.0."
                : "The slot, the processor, or a BIOS setting caps the PCIe generation.");
        parts.Add(width >= 8
            ? $"At {link.Label} it has about {link.Gbps / 8:0.#} GB/s, which costs graphics cards little (a few percent in games at most)."
            : $"At {link.Label} it has about {link.Gbps / 8:0.#} GB/s; cards on four lanes lose noticeable speed on PCIe 3.0, most of all when their own memory fills up.");
        return new PlanItem
        {
            Tone = tone,
            Title = $"{gpu.Name} runs at {link.Label}" + (link.Max is null ? "" : $" of {link.Max}"),
            Detail = string.Join(" ", parts),
            Nodes = [gpu.Id]
        };
    }

    private static (double Gen, int Width) ParseLink(string? label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(label ?? "", @"PCIe\s+(\d(?:\.\d)?)\s+x(\d+)");
        return match.Success
            ? (double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value))
            : (0, 0);
    }

    // Cards built with fewer lanes than their chip supports (the chip's full width is what the card reports).
    private static int? WiredLanes(string name)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\bRX\s*6(400|500)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 4;
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"\bRTX\s*(3050|4060|5060)\b|\bRX\s*(6600|6650|7600)\b|\bArc\s*(A380|A580|B570|B580)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 8;
        return null;
    }

    // ---- Power -----------------------------------------------------------------------------------

    private static PlanSection Power(Context context, IReadOnlyList<PowerReading> power)
    {
        var section = new PlanSection { Id = "power", Title = "Power" };
        var usb = context.All.Where(d => d.Usb?.PowerMa is > 0 && d.IdStarts(@"USB\VID_")).ToList();
        var busPowered = usb.Where(d => d.Usb!.SelfPowered != true).OrderByDescending(d => d.Usb!.PowerMa).ToList();
        section.Summary = usb.Count == 0
            ? "No USB power information."
            : $"USB devices ask for up to {busPowered.Sum(d => d.Usb!.PowerMa ?? 0):N0} mA from their ports (about {busPowered.Sum(d => d.Usb!.PowerMa ?? 0) * 5 / 1000.0:0} W at 5 V); " +
              $"{usb.Count - busPowered.Count} have their own power supply.";

        var hungry = busPowered.Where(d => d.Usb!.PowerMa >= 400).ToList();
        if (hungry.Count > 0)
        {
            var over = hungry.Where(d => d.Usb!.PowerMa > (d.Usb.SuperSpeed ? 900 : 500)).ToList();
            section.Items.Add(new PlanItem
            {
                Tone = over.Count > 0 ? "warn" : "info",
                Title = "Most power-hungry USB devices",
                Detail = string.Join(", ", hungry.Select(d => $"{context.NameOf(d)} ({d.Usb!.PowerMa} mA)")) + ". " +
                         (over.Count > 0
                             ? $"{Join(over.Select(context.NameOf))} ask{(over.Count == 1 ? "s" : "")} for more than a {(over[0].Usb!.SuperSpeed ? "USB 3" : "USB 2")} port supplies."
                             : "Each asks for no more than its port supplies (900 mA on USB 3, 500 mA on USB 2).") +
                         (hungry.Any(d => context.Storage.Any(s => s.Device == d && s.Hdd))
                             ? " Portable hard drives draw extra for a moment when they spin up, so they belong on the PC's own ports or a powered hub."
                             : ""),
                Nodes = hungry.Select(ConnectionsService.DeviceNodeId).ToList()
            });
        }

        // Hubs that share one upstream port's power with everything plugged into them.
        foreach (var hub in context.All.Where(d => d.HubBusPowered && d.HubPorts is > 0))
        {
            var load = hub.Children.Where(c => c.Usb?.SelfPowered != true).Sum(c => c.Usb?.PowerMa ?? 0);
            var budget = hub.Usb?.SuperSpeed == true ? 900 : 500;
            section.Items.Add(new PlanItem
            {
                Tone = load > budget ? "warn" : "info",
                Title = load > budget ? $"{context.NameOf(hub)} is asked for more power than it has" : $"{context.NameOf(hub)} has no power supply of its own",
                Detail = $"Devices on it ask for {load} mA; a bus-powered hub gets about {budget} mA from its own port to share.",
                Action = load > budget ? "Give the hub a power adapter, or move a hungry device (drives first) to a port on the PC." : null,
                Nodes = hub.Children.Select(ConnectionsService.DeviceNodeId).Append(ConnectionsService.DeviceNodeId(hub)).ToList()
            });
        }

        var drivesOnHubs = context.Storage.Where(s => s.OnHub && s.Device.Usb?.SelfPowered != true && s.Device.Parent?.HubBusPowered == true).ToList();
        foreach (var drive in drivesOnHubs)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "warn",
                Title = $"{drive.Name} gets its power through an unpowered hub",
                Detail = "Bus-powered drives on unpowered hubs can disconnect or fail to spin up.",
                Action = "Plug it straight into the PC or use a powered hub.",
                Nodes = [drive.Id]
            });
        }

        var asleep = context.All.Where(d => d.PowerState is > 0 && d.IdStarts(@"USB\VID_") && !d.InstanceId.Contains("&MI_", StringComparison.OrdinalIgnoreCase)).ToList();
        if (asleep.Count > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"Windows is saving power on {string.Join(", ", asleep.Select(context.NameOf))}",
                Detail = "USB selective suspend has put idle devices to sleep; they wake as soon as they're used.",
                Nodes = asleep.Select(ConnectionsService.DeviceNodeId).ToList()
            });
        }

        // The biggest consumers in the box, when the sensors report them.
        var cpuWatts = power.FirstOrDefault(p => p.Device == "CPU");
        var gpuWatts = power.FirstOrDefault(p => p.Device == "GPU");
        if (cpuWatts is not null || gpuWatts is not null)
        {
            var gpuName = context.Nodes.Values.FirstOrDefault(n => n.Kind == "gpu")?.Name ?? "The graphics card";
            var parts = new List<string>();
            if (gpuWatts is not null) parts.Add($"{gpuName} draws {gpuWatts.Watts:0} W");
            if (cpuWatts is not null) parts.Add($"the processor package {cpuWatts.Watts:0} W");
            var text = string.Join(" and ", parts);
            var usbWatts = usb.Where(d => d.Usb!.SelfPowered != true).Sum(d => d.Usb!.PowerMa ?? 0) * 5 / 1000.0;
            section.Items.Insert(0, new PlanItem
            {
                Tone = "info",
                Title = "Biggest power users right now",
                Detail = char.ToUpperInvariant(text[0]) + text[1..] + $" right now, and {(parts.Count == 1 ? "it climbs" : "both climb")} under load " +
                         $"(games, video exports). Everything on USB together asks for at most {usbWatts:0} W." +
                         (cpuWatts is null && !SystemTelemetryService.IsElevated
                             ? " The processor's own power reading needs administrator rights on this PC."
                             : ""),
                Nodes = context.Nodes.Values.Where(n => n.Kind == "gpu").Select(n => n.Id).Append("cpu").ToList()
            });
        }

        if (GetSystemPowerStatus(out var status) && status.BatteryFlag != 128 && status.BatteryFlag != 255)
        {
            section.Items.Add(new PlanItem
            {
                Tone = status.AcLineStatus == 1 ? "info" : "improve",
                Title = status.AcLineStatus == 1 ? $"On AC power, battery {status.BatteryLifePercent}%" : $"On battery: {status.BatteryLifePercent}% left",
                Detail = status.AcLineStatus == 1
                    ? "Bus-powered drives and USB devices draw from the charger now."
                    : "Every bus-powered USB device drains the battery; unplug drives you aren't using."
            });
        }
        if (section.Items.Count == 0)
            section.Items.Add(new PlanItem { Tone = "good", Title = "No power problems found" });
        return section;
    }

    // ---- This PC ---------------------------------------------------------------------------------

    private static PlanSection Compatibility(Context context, MemoryAnalysis? memory)
    {
        var firmware = context.Capture.Firmware;
        var form = firmware.FormFactor;
        var pcie = context.All.Count(d => d.Pci is not null);
        var wifi = context.Capture.Network.Wifi.Count;
        var readable = new List<string>
        {
            memory is null ? "Memory slots: not described by the firmware" : $"Memory slots: {memory.Slots.Count} described by the firmware",
            $"USB: {context.Controllers.Count} controller{(context.Controllers.Count == 1 ? "" : "s")} and every port on them",
            pcie > 0 ? $"PCIe: link speed and lanes for {pcie} devices" : "PCIe: no link details (common in virtual machines)",
            wifi > 0 ? "Wi-Fi: the access point and its signal" : "Wi-Fi: none (no Wi-Fi adapter, or the Wireless LAN service isn't installed)",
            SystemTelemetryService.IsElevated
                ? "Administrator: yes, so interrupt time per driver can be measured"
                : "Administrator: no, which only matters for measuring interrupt time per driver"
        };
        return new PlanSection
        {
            Id = "pc",
            Title = "Compatibility",
            Summary = $"{form} ({(firmware.SystemProduct ?? Board() ?? "unknown model")}).",
            Items =
            {
                new PlanItem
                {
                    Tone = "info",
                    Title = $"BoardScout sees this PC as a {form.ToLowerInvariant()}",
                    Detail = form switch
                    {
                        "Laptop" or "Handheld" => "Laptops have fewer USB controllers and share power with the battery, so the power section matters more here. " +
                                                  "Everything on this map works the same way.",
                        "Server" => "On servers, check that the WebView2 Runtime is installed; Wi-Fi sections stay empty when the Wireless LAN service isn't installed.",
                        "Virtual machine" => "Inside a virtual machine most devices are virtual, so link speeds and ports reflect the hypervisor, not real hardware.",
                        _ => "Desktops get the full map: slots, lanes, every USB controller and port, memory slots, and the network path."
                    }
                },
                new PlanItem
                {
                    Tone = "info",
                    Title = "What BoardScout could read here",
                    Detail = string.Join(". ", readable) + "."
                }
            }
        };
    }

    // ---- Helpers ---------------------------------------------------------------------------------

    /// <summary>"A", "A and B", "A, B, and C".</summary>
    private static string Join(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "",
            1 => list[0],
            2 => $"{list[0]} and {list[1]}",
            _ => $"{string.Join(", ", list.Take(list.Count - 1))}, and {list[^1]}"
        };
    }

    private static string Rate(UsbPort port) =>
        port.SuperSpeedPlus ? "10 Gbps" : port.SuperSpeed ? "5 Gbps" : port.Speed == 2 ? "480 Mbps" : port.Speed == 1 ? "12 Mbps" : "1.5 Mbps";

    private static IEnumerable<ConnectionNode> Descendants(ConnectionNode node)
    {
        foreach (var child in node.Children)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static string? Board()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return key?.GetValue("BaseBoardProduct") as string;
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadTrim()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\FileSystem");
            return key?.GetValue("DisableDeleteNotification") is int value ? value == 0 : null;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte AcLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);
}
