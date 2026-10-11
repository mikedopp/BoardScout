using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;

namespace BoardScout.Views;

/// <summary>A 10-second count of interrupts per device (/proc/interrupts), the Linux stand-in for a driver trace.</summary>
internal sealed record IrqProfile(double Seconds, IReadOnlyList<DriverLoadRow> Rows, string? Error);

/// <summary>Interrupts and softirqs per processor over a short sample.</summary>
internal sealed record CoreSample(IReadOnlyList<CoreLoadRow> Cores);

/// <summary>
/// The optimization plan for Linux: where to plug things in, faster drives, memory placement, interrupts,
/// power, and Linux settings worth a look. Built from the Connections map plus a one-second interrupt sample.
/// </summary>
internal static partial class LinuxPlan
{
    // ---- Sampling ---------------------------------------------------------------------------------

    public static async Task<CoreSample> SampleCoresAsync(TimeSpan span, CancellationToken token)
    {
        var irqBefore = Interrupts.Read();
        var softBefore = Interrupts.ReadSoftirqs();
        var timesBefore = Cpu.ReadTimes();
        var watch = Stopwatch.StartNew();
        await Task.Delay(span, token);
        var seconds = watch.Elapsed.TotalSeconds;
        var irqAfter = Interrupts.Read();
        var softAfter = Interrupts.ReadSoftirqs();
        var timesAfter = Cpu.ReadTimes();
        var cpus = irqAfter.FirstOrDefault()?.Counts.Length ?? Environment.ProcessorCount;
        var rows = new List<CoreLoadRow>();
        for (var core = 0; core < cpus; core++)
        {
            long irq = 0;
            foreach (var line in irqAfter.Where(l => l.Numbered || l.Id is "LOC" or "RES" or "CAL" or "TLB" or "NMI" or "PMI" or "IWI"))
            {
                var before = irqBefore.FirstOrDefault(b => b.Id == line.Id);
                if (core < line.Counts.Length) irq += line.Counts[core] - (before is not null && core < before.Counts.Length ? before.Counts[core] : 0);
            }
            var soft = core < softAfter.Length && core < softBefore.Length ? softAfter[core] - softBefore[core] : 0;
            var key = $"cpu{core}";
            double irqPct = 0, softPct = 0;
            if (timesBefore.TryGetValue(key, out var a) && timesAfter.TryGetValue(key, out var b) && b.Total > a.Total)
            {
                irqPct = (b.Irq - a.Irq) * 100d / (b.Total - a.Total);
                softPct = (b.SoftIrq - a.SoftIrq) * 100d / (b.Total - a.Total);
            }
            rows.Add(new CoreLoadRow
            {
                Core = core,
                InterruptsPerSecond = Math.Round(Math.Max(0, irq) / seconds),
                DpcsPerSecond = Math.Round(Math.Max(0, soft) / seconds),
                InterruptPercent = Math.Round(irqPct, 2),
                DpcPercent = Math.Round(softPct, 2)
            });
        }
        return new CoreSample(rows);
    }

    /// <summary>Counts interrupts per device for a while. Linux shows every user the counts; timing each handler would need a root-only kernel trace.</summary>
    public static async Task<IrqProfile> ProfileAsync(HardwareCapture capture, TimeSpan span, bool privacy, CancellationToken token)
    {
        var before = Interrupts.Read();
        var watch = Stopwatch.StartNew();
        await Task.Delay(span, token);
        var seconds = watch.Elapsed.TotalSeconds;
        var after = Interrupts.Read();
        if (after.Count == 0) return new IrqProfile(seconds, [], "This system doesn't publish /proc/interrupts.");
        var cpus = after[0].Counts.Length;
        var rows = new List<DriverLoadRow>();
        foreach (var group in after.Where(l => l.Numbered).GroupBy(l => l.PciAddress ?? string.Join(",", l.Handlers)))
        {
            long count = 0;
            var busiest = new long[cpus];
            foreach (var line in group)
            {
                var old = before.FirstOrDefault(b => b.Id == line.Id);
                for (var c = 0; c < cpus && c < line.Counts.Length; c++)
                {
                    var delta = line.Counts[c] - (old is not null && c < old.Counts.Length ? old.Counts[c] : 0);
                    count += Math.Max(0, delta);
                    busiest[c] += Math.Max(0, delta);
                }
            }
            if (count == 0) continue;
            var first = group.First();
            var device = first.PciAddress is null ? null : capture.Pci.FirstOrDefault(d => d.Address == first.PciAddress);
            var driver = device?.Driver ?? first.Handlers.FirstOrDefault() ?? $"IRQ {first.Id}";
            var cores = busiest.Select((v, i) => (v, i)).Where(x => x.v > 0).Select(x => x.i).ToList();
            rows.Add(new DriverLoadRow
            {
                Driver = driver,
                Description = device is null ? $"IRQ {string.Join(", ", group.Select(l => l.Id))} · {first.Chip}" : $"{device.VendorShort} {device.Name}",
                Devices = [
                    $"{group.Count()} interrupt line{(group.Count() == 1 ? "" : "s")}",
                    cores.Count == 1 ? $"handled by processor {cores[0]}" : $"spread over processors {string.Join(", ", cores.Take(8))}{(cores.Count > 8 ? "…" : "")}"
                ],
                IsrCount = count
            });
        }
        // Per-processor architecture counters: the timer tick, rescheduling and function-call IPIs.
        foreach (var line in after.Where(l => !l.Numbered && l.Id is "LOC" or "RES" or "CAL" or "TLB"))
        {
            var old = before.FirstOrDefault(b => b.Id == line.Id);
            var count = Math.Max(0, line.Total - (old?.Total ?? 0));
            if (count == 0) continue;
            rows.Add(new DriverLoadRow
            {
                Driver = line.Id switch { "LOC" => "Local timer", "RES" => "Rescheduling", "CAL" => "Function calls", _ => "TLB shootdowns" },
                Description = $"{line.Description} (the kernel itself, on every processor)",
                Devices = ["kernel"],
                IsrCount = count
            });
        }
        return new IrqProfile(seconds, rows.OrderByDescending(r => r.IsrCount).Take(14).ToList(), null);
    }

    // ---- The plan ---------------------------------------------------------------------------------

    public static OptimizationPlan Build(HardwareCapture capture, LinuxConnections.BuildResult map, CoreSample cores, IrqProfile? profile, bool privacy)
    {
        var context = new Context(capture, map, privacy);
        var memory = MemoryAdvice.Analyze(capture.Memory.Firmware, capture.Identity.BoardName);
        var plan = new OptimizationPlan
        {
            FormFactor = capture.FormFactor,
            Elevated = true,
            Cores = cores.Cores.ToList(),
            UsbControllers = context.Controllers.Select(c => c.Summary).ToList()
        };
        plan.Sections.Add(UsbPorts(context));
        plan.Sections.Add(UsbDrives(context));
        plan.Sections.Add(InsideThePc(context));
        plan.Sections.Add(Memory(memory, plan, capture));
        plan.Sections.Add(InterruptSection(context, cores, profile, plan));
        plan.Sections.Add(Power(context));
        plan.Sections.Add(LinuxSettings(context));
        plan.Sections.Add(Compatibility(context, memory));
        return plan;
    }

    private sealed class Context
    {
        public Context(HardwareCapture capture, LinuxConnections.BuildResult map, bool privacy)
        {
            Capture = capture;
            Privacy = privacy;
            Root = map.Snapshot.Root;
            foreach (var node in LinuxConnections.Walk(Root))
            {
                Nodes[node.Id] = node;
                foreach (var child in node.Children) Parents[child.Id] = node;
            }
            foreach (var controller in map.Controllers)
            {
                var where = "add-in";
                for (var p = Parents.GetValueOrDefault(controller.Summary.Node); p is not null; p = Parents.GetValueOrDefault(p.Id))
                {
                    if (p.Id == "chipset") { where = "chipset"; break; }
                    if (p.Kind is "cpu" or "integrated" || p.Id == "platform") { where = "CPU"; break; }
                }
                controller.Summary.Where = where;
                Controllers.Add(controller);
            }
        }

        public HardwareCapture Capture { get; }
        public bool Privacy { get; }
        public ConnectionNode Root { get; }
        public Dictionary<string, ConnectionNode> Nodes { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, ConnectionNode> Parents { get; } = new(StringComparer.Ordinal);
        public List<LinuxConnections.ControllerPorts> Controllers { get; } = [];

        public IEnumerable<ConnectionNode> UsbDrives => Nodes.Values.Where(n => n.Kind == "storage");

        public UsbDevice? UsbOf(ConnectionNode node) => Capture.UsbDevices.FirstOrDefault(d => LinuxConnections.UsbNodeId(d) == node.Id);
        public BlockDisk? DiskOf(ConnectionNode node) => node.Disk is { } index ? Capture.Disks.FirstOrDefault(d => d.Index == index) : null;
    }

    private static PlanSection UsbPorts(Context context)
    {
        var section = new PlanSection { Id = "usb-ports", Title = "USB ports" };
        var controllers = context.Controllers;
        var free3 = controllers.Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        section.Summary = controllers.Count == 0
            ? "No USB controllers found."
            : $"{controllers.Count} USB controller{(controllers.Count == 1 ? "" : "s")} with {controllers.Sum(c => c.Summary.Usb3Ports)} USB 3 ports ({free3} free) and " +
              $"{controllers.Sum(c => c.Summary.Usb2Ports)} USB 2-only ports ({controllers.Sum(c => c.Summary.Usb2Ports - c.Summary.Usb2Used)} free). " +
              "Ports the firmware marks as not wired are left out; USB 2-only ports include internal headers and built-in devices like Bluetooth.";

        var cpuFree = controllers.Where(c => c.Summary.Where == "CPU").Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        var chipsetFree = controllers.Where(c => c.Summary.Where == "chipset").Sum(c => c.Summary.Usb3Ports - c.Summary.Usb3Used);
        if (free3 > 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"{free3} free USB 3 port{(free3 == 1 ? "" : "s")}: {cpuFree} on the CPU's controllers, {chipsetFree} on the chipset's",
                Detail = "Ports on the CPU's own USB controllers reach the processor directly; the chipset's ports share the chipset's one link " +
                         "with its drives and network, which only matters when several of them are busy at once. Linux can't say where a port " +
                         "is on the case, so match them by plugging a device in and watching which controller it appears under on this map.",
                Nodes = controllers.Where(c => c.Summary.Usb3Ports > c.Summary.Usb3Used).Select(c => c.Summary.Node).ToList()
            });
        }

        foreach (var node in context.Nodes.Values.Where(n => n.Link is { Bus: "usb", Degraded: true } && n.Kind is not ("storage" or "usb-hub")))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"Move {node.Name} to a USB 3 port",
                Detail = $"It's a USB 3 device connected at USB 2 speed (480 Mbps){HubText(context, node)}.",
                Action = cpuFree + chipsetFree > 0 ? "Plug it straight into a free USB 3 port (often blue, or marked SS)." : "Use a USB 3 port or hub.",
                Nodes = [node.Id]
            });
        }

        foreach (var hub in context.Nodes.Values.Where(n => n.Kind == "usb-hub"))
        {
            var drives = hub.Children.Where(c => c.Kind == "storage").ToList();
            if (drives.Count < 2) continue;
            var hdds = drives.All(d => context.DiskOf(d)?.DriveType().Spinning == true);
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"{drives.Count} drives share {hub.Name}",
                Detail = $"{Format.Join(drives.Select(d => d.Name))} reach the computer through one hub link. " +
                         (hdds
                             ? "Hard drives rarely fill a 5 Gbps link, so this is fine for everyday use; copying between them at once shares the link."
                             : "Copying between them, or to both at once, splits the link's speed between them."),
                Nodes = drives.Select(d => d.Id).Append(hub.Id).ToList()
            });
        }

        if (section.Items.Count == 0)
            section.Items.Add(new PlanItem { Tone = "good", Title = "Every USB device is on a port that matches its speed" });
        return section;
    }

    private static string HubText(Context context, ConnectionNode node) =>
        context.Parents.GetValueOrDefault(node.Id) is { Kind: "usb-hub" } hub ? $", through {hub.Name}" : "";

    private static PlanSection UsbDrives(Context context)
    {
        var section = new PlanSection { Id = "usb-drives", Title = "USB drives" };
        var drives = context.UsbDrives.ToList();
        var ssd = drives.Count(d => context.DiskOf(d)?.DriveType().Spinning == false);
        var hdd = drives.Count(d => context.DiskOf(d)?.DriveType().Spinning == true);
        section.Summary = drives.Count == 0
            ? "No USB drives are connected."
            : $"{drives.Count} USB drive{(drives.Count == 1 ? "" : "s")}: {ssd} SSD, {hdd} hard drive{(hdd == 1 ? "" : "s")}" +
              (drives.Count - ssd - hdd > 0 ? $", {drives.Count - ssd - hdd} that don't say." : ".");
        var atBest = new List<ConnectionNode>();
        foreach (var drive in drives)
        {
            var disk = context.DiskOf(drive);
            var usb = context.UsbOf(drive);
            if (disk is null || usb is null) continue;
            var spinning = disk.DriveType().Spinning;
            if (drive.Link?.Degraded == true)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "warn",
                    Title = $"{drive.Name} is running at USB 2 speed",
                    Detail = $"Its USB bridge is a USB 3 part, but it's connected at 480 Mbps, about 40 MB/s: {(spinning == false ? "a tenth" : "a quarter")} of what it can do." +
                             (usb.Port?.Peer is not null ? " The port it's in is a USB 3 socket, so the cable or the drive's own socket is the likely fault." : " The port it's in is USB 2-only."),
                    Action = usb.Port?.Peer is not null
                        ? "Try a known-good USB 3 cable and push the plug fully in; on Micro-B plugs the USB 3 contacts are on the wide half."
                        : "Plug it into a USB 3 port, with a USB 3 cable, and not through a USB 2 hub.",
                    Nodes = [drive.Id]
                });
            }
            else if (usb.SpeedMbps <= 480)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} is a USB 2 drive",
                    Detail = "USB 2 limits it to about 40 MB/s whichever port it uses.",
                    Nodes = [drive.Id]
                });
            }
            else if (spinning == false && usb.ReportsUsb3 && usb.VersionNumber >= 3.1 && usb.SpeedMbps == 5000 &&
                     usb.Port?.Hub?.SpeedMbps >= 10000)
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} runs at 5 Gbps on a 10 Gbps controller",
                    Detail = "It reports USB 3.1 or later, which may mean it supports 10 Gbps; many 5 Gbps enclosures report that too. A 10 Gbps cable is worth a try.",
                    Nodes = [drive.Id]
                });
            }
            else atBest.Add(drive);

            if (spinning == false && disk.UsbDriver == "usb-storage")
            {
                section.Items.Add(new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} uses the older USB storage protocol",
                    Detail = "Linux talks Bulk-Only Transport to its enclosure instead of UAS, which costs SSDs speed on small files. A UAS enclosure fixes it " +
                             "(some enclosures that support UAS are kept on the old protocol by a kernel quirk because their UAS is broken).",
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
                Detail = string.Join("; ", atBest.GroupBy(d => d.Link?.Short ?? "USB").Select(g => $"{Format.Join(g.Select(d => d.Name))} at {g.Key}")) + ". " +
                         (atBest.Any(d => context.DiskOf(d)?.DriveType().Spinning == true)
                             ? "Each is linked at the fastest speed it supports. A hard drive can't move more than about 2 Gbps (250 MB/s), so any USB 3 port suits it."
                             : "Each is linked at the fastest speed it supports."),
                Nodes = atBest.Select(d => d.Id).ToList()
            });
        }
        if (drives.Count > 0 && section.Items.All(i => i.Tone == "good"))
            section.Items.Insert(0, new PlanItem { Tone = "good", Title = "Every USB drive runs at the speed its port and cable allow" });
        return section;
    }

    private static PlanSection InsideThePc(Context context)
    {
        var section = new PlanSection { Id = "inside", Title = "Inside the PC" };
        var nvme = context.Nodes.Values.Where(n => n.Kind == "nvme").ToList();
        var sata = context.Nodes.Values.Where(n => n.Kind == "disk").ToList();
        var gpus = context.Nodes.Values.Where(n => n.Kind == "gpu").ToList();
        section.Summary = $"{nvme.Count} NVMe and {sata.Count} SATA drive{(nvme.Count + sata.Count == 1 ? "" : "s")}" +
                          (gpus.Count == 0 ? "." : $", and {(gpus.Count == 1 ? "one graphics device" : $"{gpus.Count} graphics devices")}.");

        foreach (var gpu in gpus.Where(g => g.Link?.Degraded == true))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"{gpu.Name} runs at {gpu.Link!.Label} of {gpu.Link.Max}",
                Detail = $"At {gpu.Link.Label} it has about {gpu.Link.Gbps / 8:0.#} GB/s. Graphics cards drop to a slower link when idle to save power, " +
                         "so check again while a game or video is running before moving it.",
                Nodes = [gpu.Id]
            });
        }

        foreach (var drive in nvme.Where(n => n.Link?.Degraded == true))
        {
            var link = drive.Link!;
            var lanes = link.Label.Split('x').Last();
            var maxLanes = link.Max?.Split('x').Last();
            section.Items.Add(lanes != maxLanes
                ? new PlanItem
                {
                    Tone = "improve",
                    Title = $"{drive.Name} gets {lanes} of its {maxLanes} PCIe lanes",
                    Detail = $"Linked at {link.Label}; the drive supports {link.Max}. The M.2 socket or its lane sharing gives it fewer lanes. " +
                             "A free x4 slot with an M.2-to-PCIe adapter card, or an M.2 socket wired for x4, would give it all four.",
                    Nodes = [drive.Id]
                }
                : new PlanItem
                {
                    Tone = "info",
                    Title = $"{drive.Name} runs at {link.Label} instead of {link.Max}",
                    Detail = "The slot, the processor, or a BIOS setting limits the PCIe generation.",
                    Nodes = [drive.Id]
                });
        }

        double Potential(ConnectionNode n) => n.Link?.MaxGbps ?? n.Link?.Gbps ?? 0;
        bool BehindChipset(ConnectionNode n)
        {
            for (var p = context.Parents.GetValueOrDefault(n.Id); p is not null; p = context.Parents.GetValueOrDefault(p.Id))
                if (p.Id == "chipset") return true;
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
                    Action = "Swap the two drives' M.2 sockets; Linux finds filesystems by UUID, so they mount the same afterwards.",
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
        else if (chipsetDrives.Count > 0 && context.Capture.Pci.Any(d => d.IsBridge && d.Bus == 0 && d.Slot < 0x10 && d.Children.Count == 0 && d.Link?.MaxWidth >= 4))
        {
            var port = context.Capture.Pci.First(d => d.IsBridge && d.Bus == 0 && d.Slot < 0x10 && d.Children.Count == 0 && d.Link?.MaxWidth >= 4);
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"A CPU slot is free ({port.SlotName ?? $"PCIe x{port.Link!.MaxWidth}"}, up to PCIe {Format.Generation(port.Link!.MaxGen)})",
                Detail = $"Every drive here sits behind the chipset. The CPU's own slot is empty; an NVMe drive on an M.2-to-PCIe card there would have " +
                         "the processor's lanes to itself. Worth it for heavy, sustained work; everyday use won't notice.",
                Nodes = chipsetDrives.Select(n => n.Id).ToList()
            });
        }

        if (context.Nodes.GetValueOrDefault("chipset") is { } chipset)
        {
            var behind = LinuxConnections.Walk(chipset).Where(n => n.Kind is "nvme" or "disk" or "ethernet" or "wifi").ToList();
            var usbDrives = LinuxConnections.Walk(chipset).Count(n => n.Kind == "storage");
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

        // TRIM: a weekly fstrim timer, or the discard mount option, keeps SSDs fast.
        var ssds = context.Capture.Disks.Where(d => d.DriveType().Spinning == false && d.DiscardMaxBytes > 0).ToList();
        if (ssds.Count > 0)
        {
            var timer = Tools.Run("systemctl", "is-enabled", "fstrim.timer")?.Trim();
            var discard = Sys.Lines("/proc/self/mounts").Any(l => l.Contains("discard", StringComparison.Ordinal));
            section.Items.Add(timer == "enabled" || discard
                ? new PlanItem { Tone = "good", Title = "TRIM is on", Detail = timer == "enabled" ? "The weekly fstrim timer tells your SSDs which blocks are free, which keeps them fast." : "Filesystems are mounted with discard, so SSDs hear about freed blocks right away." }
                : new PlanItem { Tone = "improve", Title = "TRIM isn't scheduled", Detail = "SSDs slow down over time when they aren't told which blocks are free.", Action = "Run sudo systemctl enable --now fstrim.timer for a weekly TRIM." });
        }
        foreach (var slow in sata.Where(n => n.Link?.Gbps is < 6 && context.DiskOf(n)?.DriveType().Spinning == false))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"{slow.Name} is linked at {slow.Link!.Short}",
                Detail = "SATA SSDs need a 6 Gbps (SATA III) link for full speed; at 3 Gbps they top out near 280 MB/s.",
                Action = "Use a SATA III port (the board manual lists them) and a good SATA cable.",
                Nodes = [slow.Id]
            });
        }
        if (sata.Count > 0 && sata.All(n => n.Link?.Gbps is null or >= 6 || context.DiskOf(n)?.DriveType().Spinning == true))
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"SATA drive{(sata.Count == 1 ? "" : "s")} at the speed {(sata.Count == 1 ? "it needs" : "they need")}",
                Detail = "SATA III tops out near 550 MB/s; hard drives don't fill even a 3 Gbps link, so there's nothing to move.",
                Nodes = sata.Select(n => n.Id).ToList()
            });
        return section;
    }

    private static PlanSection Memory(MemoryAnalysis? memory, OptimizationPlan plan, HardwareCapture capture)
    {
        var section = new PlanSection { Id = "memory", Title = "Memory" };
        if (memory is null)
        {
            section.Summary = capture.Memory.Source == "not available"
                ? "The memory slots aren't readable here: this system's udev doesn't copy them out of the firmware, and the raw table needs root (sudo boardscout --plan-json)."
                : "The firmware didn't describe the memory slots.";
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
        if (capture.FormFactor == "Laptop" && memory.Slots.Count <= 2)
            section.Items.Add(new PlanItem { Tone = "info", Title = "Laptop memory", Detail = "Laptops often solder some memory to the board, which the firmware may list as a slot." });
        return section;
    }

    private static PlanSection InterruptSection(Context context, CoreSample sample, IrqProfile? profile, OptimizationPlan plan)
    {
        var section = new PlanSection { Id = "interrupts", Title = "Conflicts and interrupts" };
        foreach (var node in context.Nodes.Values.Where(n => n.Problem && n.Warning is not null && n.Kind != "usb"))
        {
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"{node.Name} isn't working",
                Detail = node.Warning!,
                Nodes = [node.Id]
            });
        }

        // Legacy interrupt lines with more than one handler attached.
        var shared = context.Capture.Irqs.Where(l => l.Numbered && !l.MessageSignaled && l.Handlers.Count > 1).ToList();
        foreach (var line in shared)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"IRQ {line.Id} is shared by {Format.Join(line.Handlers)}",
                Detail = "Sharing a legacy interrupt line works, but each interrupt makes every driver on the line check whether it's theirs, which adds latency."
            });
        }
        var msi = context.Capture.Pci.Count(d => d.MsiVectors > 0);
        var legacy = context.Capture.Pci.Where(d => d.MsiVectors == 0 && d.Irq is not null && d.Driver is not null).ToList();
        if (shared.Count == 0)
        {
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = "No conflicts: nothing shares an interrupt line",
                Detail = $"{msi} PCI device{(msi == 1 ? " uses" : "s use")} message-signaled interrupts, which can't conflict" +
                         (legacy.Count > 0 ? $"; {legacy.Count} still use{(legacy.Count == 1 ? "s" : "")} a legacy line of {(legacy.Count == 1 ? "its" : "their")} own ({string.Join(", ", legacy.Select(d => d.Name))})." : "."),
                Nodes = legacy.Select(LinuxConnections.PciNodeId).Where(context.Nodes.ContainsKey).ToList()
            });
        }

        var cores = sample.Cores;
        if (cores.Count > 0)
        {
            var total = cores.Sum(c => c.InterruptsPerSecond);
            var busiest = cores.MaxBy(c => c.InterruptsPerSecond)!;
            var time = cores.Average(c => c.InterruptPercent + c.DpcPercent);
            // Kernels built without IRQ time accounting report no interrupt time at all, only counts.
            var accounted = Cpu.ReadTimes().TryGetValue("cpu", out var since) && since.Irq + since.SoftIrq > 0;
            section.Summary = accounted
                ? $"{total:N0} interrupts per second across {cores.Count} logical processors; interrupts and softirqs take {time:0.##}% of processor time."
                : $"{total:N0} interrupts per second across {cores.Count} logical processors (this kernel counts interrupts but doesn't time them).";
            var balancer = Tools.Run("systemctl", "is-active", "irqbalance")?.Trim();
            section.Items.Add(new PlanItem
            {
                Tone = time > 5 ? "improve" : "good",
                Title = time > 5 ? "Interrupts are using a noticeable share of the processor" : "Interrupt load is light",
                Detail = $"Processor {busiest.Core} handles the most: {busiest.InterruptsPerSecond:N0} of the {total:N0} per second. " +
                         (balancer == "active" ? "irqbalance is running and spreads device interrupts across processors." :
                          cores.Count > 1 ? "irqbalance isn't running, so each device's interrupts stay where the kernel first put them; that's fine unless one processor is busy with them." : "")
            });
        }

        if (profile is not null)
        {
            plan.ProfileSeconds = Math.Round(profile.Seconds, 1);
            plan.ProfileError = profile.Error;
        }
        if (profile is { Error: null } && profile.Rows.Count > 0)
        {
            plan.Drivers = profile.Rows.Select(r => new DriverLoadRow
            {
                Driver = r.Driver,
                Description = context.Privacy && r.Description is not null ? Privacy.Scrub(r.Description) : r.Description,
                Devices = r.Devices,
                IsrCount = r.IsrCount
            }).ToList();
            var top = profile.Rows.First(r => r.Devices.FirstOrDefault() != "kernel" || profile.Rows.All(x => x.Devices.FirstOrDefault() == "kernel"));
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"Most interrupts: {top.Driver}{(top.Description is null ? "" : $" ({top.Description})")}",
                Detail = $"{top.IsrCount / profile.Seconds:N0} a second over {profile.Seconds:0.#} s. Counts show which device keeps the processors busiest; " +
                         "timing each handler would need a root-only kernel trace, which BoardScout doesn't run."
            });
        }
        else
        {
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = "Which devices interrupt the most?",
                Detail = profile?.Error ?? "Count interrupts per device for 10 seconds. Linux shows every user these counts, so no root is needed."
            });
        }
        return section;
    }

    private static PlanSection Power(Context context)
    {
        var section = new PlanSection { Id = "power", Title = "Power" };
        var usb = context.Capture.UsbDevices.Where(d => !d.IsRootHub && d.MaxPowerMa > 0).ToList();
        var busPowered = usb.Where(d => !d.SelfPowered).OrderByDescending(d => d.MaxPowerMa).ToList();
        var name = (UsbDevice d) => context.Nodes.GetValueOrDefault(LinuxConnections.UsbNodeId(d))?.Name ?? d.ProductName ?? $"{d.Vendor:X4}:{d.Product:X4}";
        section.Summary = usb.Count == 0
            ? "No USB power information."
            : $"USB devices ask for up to {busPowered.Sum(d => d.MaxPowerMa):N0} mA from their ports (about {busPowered.Sum(d => d.MaxPowerMa) * 5 / 1000.0:0} W at 5 V); " +
              $"{usb.Count - busPowered.Count} {(usb.Count - busPowered.Count == 1 ? "has its" : "have their")} own power supply. These are the devices' own declared needs: Linux reads them without asking the devices anything.";

        var hungry = busPowered.Where(d => d.MaxPowerMa >= 400 && !d.IsHub).ToList();
        if (hungry.Count > 0)
        {
            var over = hungry.Where(d => d.MaxPowerMa > (d.SpeedMbps >= 5000 ? 900 : 500)).ToList();
            section.Items.Add(new PlanItem
            {
                Tone = over.Count > 0 ? "warn" : "info",
                Title = "Most power-hungry USB devices",
                Detail = string.Join(", ", hungry.Select(d => $"{name(d)} ({d.MaxPowerMa} mA)")) + ". " +
                         (over.Count > 0
                             ? $"{Format.Join(over.Select(name))} ask{(over.Count == 1 ? "s" : "")} for more than the port supplies."
                             : "Each asks for no more than its port supplies (900 mA on USB 3, 500 mA on USB 2).") +
                         (hungry.Any(d => context.Capture.Disks.Any(k => k.UsbPath == d.SysPath && k.DriveType().Spinning != false))
                             ? " Portable hard drives draw extra for a moment when they spin up, so they belong on the computer's own ports or a powered hub."
                             : ""),
                Nodes = hungry.Select(LinuxConnections.UsbNodeId).ToList()
            });
        }

        foreach (var hub in context.Capture.UsbDevices.Where(d => d.IsHub && !d.IsRootHub && !d.SelfPowered))
        {
            var load = hub.Children.Where(c => !c.SelfPowered).Sum(c => c.MaxPowerMa);
            var budget = hub.SpeedMbps >= 5000 ? 900 : 500;
            section.Items.Add(new PlanItem
            {
                Tone = load > budget ? "warn" : "info",
                Title = load > budget ? $"{name(hub)} is asked for more power than it has" : $"{name(hub)} has no power supply of its own",
                Detail = $"Devices on it ask for {load} mA; a bus-powered hub gets about {budget} mA from its own port to share.",
                Action = load > budget ? "Give the hub a power adapter, or move a hungry device (drives first) to a port on the computer." : null,
                Nodes = hub.Children.Select(LinuxConnections.UsbNodeId).Append(LinuxConnections.UsbNodeId(hub)).ToList()
            });
        }

        var asleep = context.Capture.UsbDevices.Where(d => !d.IsRootHub && d.RuntimeStatus == "suspended").ToList();
        if (asleep.Count > 0)
            section.Items.Add(new PlanItem
            {
                Tone = "good",
                Title = $"Linux is saving power on {Format.Join(asleep.Select(name))}",
                Detail = "USB autosuspend has put idle devices to sleep; they wake as soon as they're used.",
                Nodes = asleep.Select(LinuxConnections.UsbNodeId).ToList()
            });

        if (Sensors.GpuWatts(context.Capture.Sensors) is { } watts)
        {
            var gpuName = context.Nodes.Values.FirstOrDefault(n => n.Kind == "gpu")?.Name ?? "The graphics card";
            section.Items.Insert(0, new PlanItem
            {
                Tone = "info",
                Title = "Biggest power user right now",
                Detail = $"{gpuName} draws {watts:0} W right now and climbs under load (games, video exports). The processor's own reading needs root on most kernels.",
                Nodes = context.Nodes.Values.Where(n => n.Kind == "gpu").Select(n => n.Id).ToList()
            });
        }

        foreach (var supply in Sys.Children("/sys/class/power_supply"))
        {
            if (Sys.Read($"/sys/class/power_supply/{supply}/type") != "Battery") continue;
            var capacity = Sys.Int($"/sys/class/power_supply/{supply}/capacity");
            var status = Sys.Read($"/sys/class/power_supply/{supply}/status");
            var full = Sys.Long($"/sys/class/power_supply/{supply}/energy_full") ?? Sys.Long($"/sys/class/power_supply/{supply}/charge_full");
            var design = Sys.Long($"/sys/class/power_supply/{supply}/energy_full_design") ?? Sys.Long($"/sys/class/power_supply/{supply}/charge_full_design");
            var health = full is > 0 && design is > 0 ? $" It holds {full * 100 / design}% of its original charge." : "";
            var charging = status is "Charging" or "Full" or "Not charging";
            section.Items.Add(new PlanItem
            {
                Tone = charging ? "info" : "improve",
                Title = charging ? $"On AC power, battery {capacity}%" : $"On battery: {capacity}% left",
                Detail = (charging ? "Bus-powered drives and USB devices draw from the charger now." : "Every bus-powered USB device drains the battery; unplug drives you aren't using.") + health
            });
        }

        var governor = context.Capture.Cpu.Governor;
        if (governor is not null)
        {
            var desktop = context.Capture.FormFactor is "Desktop" or "Server" or "Mini PC";
            var pstate = context.Capture.Cpu.ScalingDriver is "intel_pstate" or "amd-pstate-epp";
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"Processor power policy: {governor}" + (context.Capture.Cpu.EnergyPreference is { } epp ? $" ({epp.Replace('_', ' ')})" : ""),
                Detail = pstate && governor == "powersave"
                    ? $"With {context.Capture.Cpu.ScalingDriver}, \"powersave\" still boosts to full speed under load; the energy preference decides how eagerly."
                    : governor == "performance"
                        ? "The processor stays at high clocks: quickest response, more power and heat at idle."
                        : desktop ? "The kernel picks clock speeds from load." : "The kernel picks clock speeds from load, which saves battery."
            });
        }
        if (section.Items.Count == 0)
            section.Items.Add(new PlanItem { Tone = "good", Title = "No power problems found" });
        return section;
    }

    // Linux-specific checks: restarts owed after updates, swap, failed services, full filesystems, the kernel's health.
    private static PlanSection LinuxSettings(Context context)
    {
        var section = new PlanSection { Id = "linux", Title = "Linux" };
        var capture = context.Capture;
        section.Summary = $"Kernel {capture.Kernel} on {Distro.Read().PrettyName}.";

        var reboot = RebootReason(capture.Kernel);
        if (reboot is not null)
            section.Items.Add(new PlanItem { Tone = "improve", Title = "Restart to finish updates", Detail = reboot, Action = "Restart when it suits you; the new kernel and libraries load then." });

        var (total, available, swapTotal, swapFree) = Cpu.ReadMemory();
        if (swapTotal == 0)
            section.Items.Add(new PlanItem { Tone = "info", Title = "No swap", Detail = "Without swap, running out of memory ends programs abruptly. A small swap file (or zram) gives the kernel room to cope." });
        else if (swapTotal - swapFree > swapTotal / 2)
            section.Items.Add(new PlanItem { Tone = "improve", Title = $"Swap is {100 - swapFree * 100 / swapTotal}% used", Detail = $"{Format.Bytes((long)(swapTotal - swapFree))} of memory has been pushed to swap, so this computer is short of RAM for its workload." });
        else
            section.Items.Add(new PlanItem { Tone = "good", Title = "Memory isn't under pressure", Detail = $"{Format.Bytes((long)available)} of {Format.Bytes((long)total)} available; swap {(swapTotal - swapFree == 0 ? "unused" : $"{Format.Bytes((long)(swapTotal - swapFree))} used")}." });

        var failed = (Tools.Run("systemctl", "--failed", "--plain", "--no-legend", "--no-pager") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split(' ')[0]).Where(u => u.Length > 0 && u != "●").ToList();
        if (failed.Count > 0)
            section.Items.Add(new PlanItem
            {
                Tone = "improve",
                Title = $"{failed.Count} service{(failed.Count == 1 ? "" : "s")} failed",
                Detail = string.Join(", ", failed.Take(8)) + (failed.Count > 8 ? ", …" : "") + ".",
                Action = $"See why with systemctl status {failed[0]}."
            });

        foreach (var volume in capture.Volumes.Where(v => v.SizeBytes > 0 && !v.ReadOnly && v.FreeBytes * 100 / v.SizeBytes < 10))
            section.Items.Add(new PlanItem
            {
                Tone = volume.FreeBytes * 100 / volume.SizeBytes < 5 ? "warn" : "improve",
                Title = $"{(context.Privacy ? "A filesystem" : volume.MountPoint)} is {100 - volume.FreeBytes * 100 / volume.SizeBytes}% full",
                Detail = $"{Format.Bytes(volume.FreeBytes)} free of {Format.Bytes(volume.SizeBytes)}. Filesystems slow down and updates fail when they fill up." +
                         (volume.Lvm ? " It's an LVM volume, so it can grow into free space in its volume group (sudo vgs shows it)." : "")
            });

        if (KernelInfo.Tainted() is { } taint)
            section.Items.Add(new PlanItem
            {
                Tone = taint.Contains("oops", StringComparison.Ordinal) || taint.Contains("warning", StringComparison.Ordinal) ? "improve" : "info",
                Title = "The kernel marks itself tainted",
                Detail = $"Because {taint}. Common and harmless with NVIDIA's driver or firmware workarounds; after an oops or warning, check journalctl -k."
            });

        if (capture.Cpu.Vulnerabilities.Count > 0)
            section.Items.Add(new PlanItem
            {
                Tone = "info",
                Title = $"{capture.Cpu.Vulnerabilities.Count} processor flaw{(capture.Cpu.Vulnerabilities.Count == 1 ? "" : "s")} not mitigated",
                Detail = string.Join("; ", capture.Cpu.Vulnerabilities.Take(4)) + ". A BIOS or microcode update usually adds the fix."
            });

        if (section.Items.All(i => i.Tone == "good"))
            section.Items.Insert(0, new PlanItem { Tone = "good", Title = "Nothing to fix in Linux itself" });
        return section;
    }

    /// <summary>Why a restart is owed: Debian's reboot-required file, or a newer kernel installed than the one running.</summary>
    public static string? RebootReason(string runningKernel)
    {
        if (Sys.Exists("/run/reboot-required") || Sys.Exists("/var/run/reboot-required"))
        {
            var packages = Sys.Lines("/run/reboot-required.pkgs").Concat(Sys.Lines("/var/run/reboot-required.pkgs")).Distinct().ToList();
            return packages.Count > 0 ? $"Updated packages need it: {string.Join(", ", packages.Take(6))}." : "Installed updates asked for a restart.";
        }
        var installed = Sys.Children("/boot").Where(f => f.StartsWith("vmlinuz-", StringComparison.Ordinal)).Select(f => f[8..]).ToList();
        var newest = installed.OrderBy(v => v, KernelVersionComparer.Instance).LastOrDefault();
        if (newest is not null && KernelVersionComparer.Instance.Compare(newest, runningKernel) > 0)
            return $"Kernel {newest} is installed but {runningKernel} is still running.";
        return null;
    }

    private static PlanSection Compatibility(Context context, MemoryAnalysis? memory)
    {
        var capture = context.Capture;
        var pcie = capture.Pci.Count(d => d.Link is not null);
        var paired = capture.Usb.SelectMany(b => b.RootHub.Ports).Any(p => p.Peer is not null);
        var readable = new List<string>
        {
            memory is null ? $"Memory slots: {capture.Memory.Source}" : $"Memory slots: {memory.Slots.Count}, from {capture.Memory.Source}",
            $"USB: {context.Controllers.Count} controller{(context.Controllers.Count == 1 ? "" : "s")}" + (paired ? ", with the firmware's USB 2/USB 3 socket pairing" : ", without socket pairing (each half counted)"),
            pcie > 0 ? $"PCIe: link speed and lanes for {pcie} devices" : "PCIe: no link details (common in virtual machines and on Arm boards)",
            capture.Links.Any(l => l.Wireless)
                ? Tools.Has("iw") ? "Wi-Fi: from iw, including the router's beacon details" : Tools.Has("nmcli") ? "Wi-Fi: from NetworkManager (no router model from beacons without iw)" : "Wi-Fi: signal level only (install iw)"
                : "Wi-Fi: no Wi-Fi adapter",
            capture.Root ? "Running as root: also reads the raw firmware tables" : "Running as a regular user: nothing here needs root"
        };
        return new PlanSection
        {
            Id = "pc",
            Title = "Compatibility",
            Summary = $"{capture.FormFactor} ({capture.Identity.System ?? capture.DeviceTreeModel ?? "unknown model"}).",
            Items =
            {
                new PlanItem
                {
                    Tone = "info",
                    Title = $"BoardScout sees this computer as a {capture.FormFactor.ToLowerInvariant()}",
                    Detail = capture.FormFactor switch
                    {
                        "Laptop" or "Handheld" => "Laptops have fewer USB controllers and share power with the battery, so the power section matters more here.",
                        "Server" => "Servers get the full map; headless ones show no monitors, and Wi-Fi sections stay empty without a Wi-Fi card.",
                        "Virtual machine" => "Inside a virtual machine most devices are virtual, so link speeds and ports reflect the hypervisor, not real hardware.",
                        "Single-board computer" => "Boards like the Raspberry Pi hang their USB, network, and storage off the system-on-chip rather than a PCIe bus.",
                        _ => "Desktops get the full map: slots, lanes, every USB controller and port, memory slots, and the network path."
                    }
                },
                new PlanItem { Tone = "info", Title = "What BoardScout could read here", Detail = string.Join(". ", readable) + "." }
            }
        };
    }

    /// <summary>Orders kernel versions numerically: "7.0.0-38-generic" after "7.0.0-9-generic".</summary>
    internal sealed partial class KernelVersionComparer : IComparer<string>
    {
        public static readonly KernelVersionComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            var a = Numbers().Matches(x ?? "").Select(m => long.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            var b = Numbers().Matches(y ?? "").Select(m => long.Parse(m.Value, CultureInfo.InvariantCulture)).ToList();
            for (var i = 0; i < Math.Min(a.Count, b.Count); i++)
                if (a[i] != b[i]) return a[i].CompareTo(b[i]);
            return a.Count.CompareTo(b.Count);
        }

        [GeneratedRegex(@"\d+")]
        private static partial Regex Numbers();
    }
}
