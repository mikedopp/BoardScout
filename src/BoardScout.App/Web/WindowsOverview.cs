using System.Globalization;
using System.Text.RegularExpressions;
using BoardScout.Models;
using BoardScout.Remote;
using BoardScout.Services;

namespace BoardScout.Views;

/// <summary>
/// The browser dashboard's Overview, Drivers, Storage, and Efficiency for a Windows PC, built from the same
/// Connections map, DriverScout scan, and driver report the Windows app shows in its own window.
/// </summary>
internal static partial class WindowsOverview
{
    public static OverviewPayload Build(ConnectionsService.Capture capture, ConnectionsSnapshot map, ScanManifest? scan, DriverReport? report,
        OptimizationPlan? plan, SystemTelemetry? telemetry, bool privacy)
    {
        var nodes = Walk(map.Root).ToList();
        var title = Title(capture, scan);
        var cpuName = scan?.Cpu.Name is { Length: > 0 } scanned ? scanned : map.Root.Name;
        var cores = scan?.Cpu.Cores ?? 0;
        var threads = scan?.Cpu.Threads is > 0 and var t ? t : Environment.ProcessorCount;
        var subtitle = string.Join("  •  ", new[]
        {
            capture.Firmware.FormFactor.ToUpperInvariant(),
            ShortCpuName(cpuName),
            cores > 0 ? $"{cores}C/{threads}T" : $"{threads} threads",
            SystemInfoService.WindowsDescription()
        }.Where(s => s.Length > 0));
        var volumes = Volumes(privacy);
        var slots = capture.Firmware.MemorySlots;
        var memoryGb = telemetry is not null ? telemetry.MemoryTotalBytes / 1_073_741_824d : scan?.TotalMemoryGb ?? slots.Sum(s => s.SizeMb) / 1024d;

        var details = new List<Fact>();
        if (capture.Firmware.SystemMaker is { } maker && capture.Firmware.SystemProduct is { } product) details.Add(new("Computer", $"{Company(maker)} {product}"));
        if (scan is not null) details.Add(new("BIOS", $"{scan.SystemInfo.Bios.Version} · {scan.SystemInfo.Bios.ReleaseDate}".Trim(' ', '·')));
        details.Add(new("Memory", $"{memoryGb:0.#} GB" + (capture.Firmware.SlotCount > 0 ? $" · {slots.Count(s => s.Populated)} of {capture.Firmware.SlotCount} slots" : "")));
        if (scan is not null && $"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product}".Trim() is { Length: > 0 } board && board != title)
            details.Add(new("Motherboard", board));
        if (slots.FirstOrDefault(s => s.Populated) is { ConfiguredMts: > 0 } stick)
            details.Add(new("Memory speed", $"{stick.ConfiguredMts} / {Math.Max(stick.SpeedMts, stick.ConfiguredMts)} MT/s"));
        details.Add(new("Storage", $"{capture.DiskFacts.Count} drives · {volumes.Count} volumes"));
        details.Add(new("USB", $"{nodes.Count(n => n.Link?.Bus == "usb" && n.Kind is not ("usb-hub" or "usb-controller"))} attached devices"));
        var problems = nodes.Count(n => n.Problem);
        details.Add(new("Device health", problems == 0 ? "No reported errors" : $"{problems} device{(problems == 1 ? "" : "s")} need attention"));
        details.Add(new("Windows", SystemInfoService.WindowsDescription()));
        details.Add(new("Last read", capture.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

        return new OverviewPayload(
            privacy ? Privacy.Scrub(title) : title,
            subtitle,
            capture.Firmware.FormFactor,
            privacy ? Privacy.HostPlaceholder : Environment.MachineName,
            Parts(capture, map, nodes, telemetry, privacy),
            details,
            Drivers(scan, report, privacy),
            volumes,
            Disks(capture, nodes, privacy),
            Suggestions(scan, report, plan),
            Math.Round(memoryGb, 1),
            nodes.Count(n => n.Kind is not ("router" or "internet" or "dns" or "lan-group" or "virtual-group" or "integrated" or "memory" or "dimm-empty") && !n.Id.StartsWith("lan-", StringComparison.Ordinal)),
            Math.Round(volumes.Sum(v => v.SizeBytes - v.FreeBytes) / 1e12, 2),
            report?.Results.Count(r => r.Status == "update-available"),
            capture.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            capture.CaptureMs);
    }

    /// <summary>
    /// The board's name ("ASRock B550M Steel Legend"), or the computer's when the board only carries an OEM part
    /// number (Dell's "0JP3NX" says less than "Dell OptiPlex 3050").
    /// </summary>
    public static string Title(ConnectionsService.Capture capture, ScanManifest? scan)
    {
        var boardMaker = scan?.SystemInfo.Baseboard.Manufacturer.Trim() ?? "";
        var boardName = scan?.SystemInfo.Baseboard.Product.Trim() ?? "";
        var maker = capture.Firmware.SystemMaker?.Trim() ?? "";
        var product = capture.Firmware.SystemProduct?.Trim() ?? "";
        var partNumber = PartNumber().IsMatch(boardName) && boardName.Any(char.IsDigit);
        var oem = OemMaker().IsMatch(maker);
        if ((partNumber || oem || boardName.Length == 0) && product.Length > 0 && !Filler(product)) return $"{Company(maker)} {product}".Trim();
        if (boardName.Length > 0) return $"{Company(boardMaker)} {boardName}".Trim();
        return Environment.MachineName;
    }

    private static bool Filler(string text) => Regex.IsMatch(text, "System Product Name|To be filled|Default string|^System$", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"^[0-9A-Z]{5,8}$")]
    private static partial Regex PartNumber();

    [GeneratedRegex(@"^(Dell|HP|Hewlett|LENOVO|Lenovo|Acer|Apple|Microsoft|Fujitsu|Toshiba|Dynabook|Panasonic|Framework|Intel\(R\) Client|Google|Microsoft)", RegexOptions.IgnoreCase)]
    private static partial Regex OemMaker();

    /// <summary>"ASUSTeK COMPUTER INC." → "ASUS", "Micro-Star International Co., Ltd." → "MSI", "Dell Inc." → "Dell".</summary>
    public static string Company(string maker)
    {
        var text = maker.Trim();
        if (text.StartsWith("ASUSTeK", StringComparison.OrdinalIgnoreCase)) return "ASUS";
        if (text.StartsWith("Micro-Star", StringComparison.OrdinalIgnoreCase)) return "MSI";
        if (text.StartsWith("Gigabyte", StringComparison.OrdinalIgnoreCase)) return "Gigabyte";
        if (text.StartsWith("Hewlett", StringComparison.OrdinalIgnoreCase)) return "HP";
        if (text.Equals("LENOVO", StringComparison.Ordinal)) return "Lenovo";
        return Regex.Replace(text, @"[,.]?\s+(Inc\.?|Corporation|Corp\.?|Co\.,?\s*Ltd\.?|Ltd\.?|LLC|GmbH|Technology|Computer|International)\b.*$", "", RegexOptions.IgnoreCase).Trim();
    }

    /// <summary>"AMD Ryzen 7 5700G with Radeon Graphics" → "AMD Ryzen 7 5700G".</summary>
    public static string ShortCpuName(string name) =>
        Regex.Replace(Regex.Replace(name, @"\((R|TM|tm)\)|\bCPU\b|\bProcessor\b|with Radeon.*$|@.*$|\s\d+-Core$", ""), @"\s+", " ").Trim();

    // ---- The board ---------------------------------------------------------------------------------

    private static List<BoardPart> Parts(ConnectionsService.Capture capture, ConnectionsSnapshot map, List<ConnectionNode> nodes, SystemTelemetry? telemetry, bool privacy)
    {
        var parts = new List<BoardPart>();
        string Scrub(string text) => privacy ? Privacy.Scrub(text) : text;
        static List<Fact> Facts(ConnectionNode? node, int take = 8) => node is null ? [] : node.Facts.Take(take).Select(f => new Fact(f.Label, f.Value)).ToList();
        static string? Live(ConnectionNode node) => node.Disk is { } disk ? $"disk:{disk}" : node.Net is { } net ? $"net:{net}" : null;
        static int? Width(ConnectionNode node) => node.Link?.Label is { } label && Regex.Match(label, @"x(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        string Tone(ConnectionNode node) => node.Warning is not null || node.Problem || node.Link?.Degraded == true ? "warn" : "good";

        var cpu = map.Root;
        parts.Add(new BoardPart("cpu", "cpu", "cpu", ShortCpuName(cpu.Name), cpu.Detail ?? $"{Environment.ProcessorCount} threads", "good", Facts(cpu, 7), Live: "cpu", Node: "cpu"));

        // Memory slots: the map's (named by channel and position, A1 · B2, as the board prints them), else the
        // firmware's own list.
        var slots = capture.Firmware.MemorySlots;
        var dimms = nodes.Where(n => n.Kind is "dimm" or "dimm-empty").ToList();
        if (dimms.Count > 0)
        {
            foreach (var dimm in dimms)
            {
                var halves = dimm.Name.Split(" · ", 2); // "A1 · 8 GB", "A2 · empty"
                var populated = dimm.Kind == "dimm";
                parts.Add(new BoardPart(dimm.Id, "memory", "dimm", populated && halves.Length == 2 ? halves[1] : "Open", halves[0],
                    populated ? "good" : "open", Facts(dimm), Live: "mem", Empty: !populated, Node: "memory"));
            }
        }
        else if (slots.Count > 0)
        {
            foreach (var slot in slots)
            {
                var facts = new List<Fact> { new("Slot", $"{slot.DeviceLocator}{(slot.BankLocator.Length > 0 ? " · " + slot.BankLocator : "")}") };
                if (slot.Populated)
                {
                    facts.Add(new("Size", $"{slot.SizeMb / 1024d:0.#} GB {slot.MemoryType}"));
                    if (slot.ConfiguredMts > 0) facts.Add(new("Speed", $"{slot.ConfiguredMts} MT/s (rated {Math.Max(slot.SpeedMts, slot.ConfiguredMts)})"));
                    if (slot.PartNumber is { Length: > 0 } part) facts.Add(new("Part number", part));
                }
                parts.Add(new BoardPart("dimm-" + ConnectionsService.ShortHash(slot.DeviceLocator + slot.BankLocator), "memory", "dimm",
                    slot.Populated ? $"{slot.SizeMb / 1024d:0.#} GB" : "Open", slot.DeviceLocator, slot.Populated ? "good" : "open", facts, Live: "mem",
                    Empty: !slot.Populated, Node: "memory"));
            }
        }
        else if (nodes.FirstOrDefault(n => n.Kind == "memory") is { } memory)
        {
            parts.Add(new BoardPart("memory", "memory", "memory", memory.Name, memory.Detail, "info", Facts(memory), Live: "mem", Node: memory.Id));
        }

        void Attach(ConnectionNode node, string side)
        {
            switch (node.Kind)
            {
                case "gpu":
                    parts.Add(new BoardPart("n-" + node.Id, side == "integrated" ? "cpu" : "slots", "gpu", Scrub(node.Name),
                        side == "integrated" ? node.Detail ?? "Integrated graphics" : $"{node.Link?.Label ?? "PCIe"} · {(side == "cpu" ? "CPU" : "chipset")}",
                        Tone(node), Facts(node), Width: Width(node), Node: node.Id));
                    break;
                case "nvme":
                    parts.Add(new BoardPart("n-" + node.Id, "m2", "nvme", Scrub(node.Name), $"{node.Link?.Label ?? "NVMe"} · {(side == "cpu" ? "CPU" : "chipset")}",
                        Tone(node), Facts(node), Live: Live(node), Width: Width(node), Node: node.Id));
                    break;
                case "sata":
                    parts.Add(new BoardPart("n-" + node.Id, "sata", "sata", node.Name, node.Detail ?? node.Link?.Label, Tone(node), Facts(node), Node: node.Id));
                    foreach (var drive in node.Children)
                        parts.Add(new BoardPart("d-" + drive.Id, "sata", drive.Kind is "optical" ? "optical" : "disk", Scrub(drive.Name), drive.Link?.Label ?? drive.Detail,
                            Tone(drive), Facts(drive), Live: Live(drive), Node: drive.Id));
                    break;
                case "usb-controller" or "ethernet" or "wifi" or "audio" or "bluetooth":
                    parts.Add(new BoardPart("n-" + node.Id, "io", node.Kind, Scrub(node.Name), node.Link?.Bus == "pcie" ? $"{node.Link.Label} · {(side == "cpu" ? "CPU" : side)}" : node.Detail ?? node.Link?.Label,
                        Tone(node), Facts(node), Live: Live(node), Node: node.Id));
                    break;
                case "integrated" or "memory" or "virtual-group" or "chipset" or "router" or "internet" or "dns" or "lan-group":
                    break;
                default:
                    if (node.Link?.Bus == "pcie")
                        parts.Add(new BoardPart("n-" + node.Id, "slots", "device", Scrub(node.Name), $"{node.Link.Label} · {(side == "cpu" ? "CPU" : "chipset")}",
                            Tone(node), Facts(node), Live: Live(node), Width: Width(node), Node: node.Id));
                    break;
            }
        }

        foreach (var node in cpu.Children)
        {
            if (node.Kind == "integrated")
                foreach (var inside in node.Children) Attach(inside, "integrated");
            else if (node.Kind == "chipset")
            {
                parts.Add(new BoardPart("chipset", "chipset", "chipset", node.Name, node.Link?.Label ?? node.Detail, Tone(node), Facts(node),
                    Live: node.Sensor is not null ? "chipset" : null, Node: node.Id));
                foreach (var behind in node.Children) Attach(behind, "chipset");
            }
            else Attach(node, "cpu");
        }

        // Display outputs: every monitor, by the connector it uses.
        var monitors = nodes.Where(n => n.Kind == "monitor").ToList();
        if (monitors.Count > 0)
        {
            var kinds = monitors.Select(m => (m.Link?.Label ?? "Display").Split(' ', '·')[0].Trim()).GroupBy(k => k)
                .Select(g => $"{g.Key}{(g.Count() > 1 ? $" ×{g.Count()}" : "")}");
            parts.Add(new BoardPart("outputs", "io", "monitor", "Display outputs", string.Join(" · ", kinds), "good",
                monitors.Select(m => new Fact(Scrub(m.Name), m.Link?.Label ?? "Connected")).ToList()));
        }

        foreach (var fan in telemetry?.Fans ?? [])
            parts.Add(new BoardPart("fan-" + ConnectionsService.ShortHash(fan.Name), "fans", "fan", fan.Name, fan.Rpm > 0 ? $"{fan.Rpm} RPM" : "stopped",
                fan.Rpm > 0 ? "good" : "open", [new("Fan", fan.Name), new("Speed", $"{fan.Rpm} RPM")], Live: "fan:" + fan.Name));
        return parts;
    }

    // ---- Drivers, storage, efficiency --------------------------------------------------------------------

    private static List<DriverRow> Drivers(ScanManifest? scan, DriverReport? report, bool privacy)
    {
        var rows = new List<DriverRow>();
        if (scan is null) return rows;
        var results = report?.Results.Where(r => r.ComponentKey.Length > 0).GroupBy(r => r.ComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase) ?? [];
        if (scan.SystemInfo.Bios.Version.Length > 0)
            rows.Add(new DriverRow($"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product}".Trim(), "Firmware", "BIOS/UEFI",
                scan.SystemInfo.Bios.Version, scan.SystemInfo.Bios.Vendor, scan.SystemInfo.Bios.ReleaseDate, "Installed", "info", null));
        foreach (var component in scan.Components)
        {
            results.TryGetValue(component.ComponentKey, out var result);
            var (status, tone) = (result?.Status ?? "") switch
            {
                "update-available" => ("Update available", "warn"),
                "current" => ("Current", "good"),
                "manual-check" => ("Check by hand", "info"),
                "error" => ("Check failed", "warn"),
                _ => (report is null ? "Not checked" : "No source", "info")
            };
            var model = privacy ? Privacy.Scrub(component.Model) : component.Model;
            rows.Add(new DriverRow(model, component.Category, component.Current.DriverVersion ?? "—",
                component.Current.DriverDate, result?.Best.Source ?? component.Current.DriverSource ?? "Windows", component.Current.Firmware,
                status, tone, null, result?.Best.LatestVersion ?? result?.Best.LatestDate, OfficialUrl(result?.DownloadUrl ?? result?.Best.DownloadUrl)));
        }
        return rows;
    }

    private static string? OfficialUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    /// <summary>Every ready fixed and removable volume, read now (not from the last scan).</summary>
    public static List<VolumeRow> Volumes(bool privacy)
    {
        var rows = new List<VolumeRow>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                var size = drive.TotalSize;
                var free = drive.AvailableFreeSpace;
                rows.Add(new VolumeRow(drive.Name.TrimEnd('\\'), privacy || drive.VolumeLabel.Length == 0 ? null : drive.VolumeLabel, drive.DriveFormat, size, free,
                    size > 0 ? Math.Round((size - free) * 100d / size, 1) : 0, drive.DriveType == DriveType.Removable ? "removable" : "", false, false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return rows;
    }

    private static List<DiskRow> Disks(ConnectionsService.Capture capture, List<ConnectionNode> nodes, bool privacy)
    {
        var rows = new List<DiskRow>();
        foreach (var (number, facts) in capture.DiskFacts.OrderBy(d => d.Key))
        {
            var node = nodes.FirstOrDefault(n => n.Disk == number);
            var name = node?.Name ?? $"{facts.Vendor} {facts.Product}".Trim();
            var bus = facts.Bus switch { 17 => "nvme", 11 => "sata", 7 => "usb", 12 => "sd", 8 => "raid", 10 => "sas", 14 => "virtual", _ => node?.Link?.Bus ?? "other" };
            var spinning = facts.Spinning ?? DriveModels.SpinningFromModelNumber(facts.Vendor, facts.Product);
            rows.Add(new DiskRow($"Disk {number}", privacy ? Privacy.Scrub(name) : name, bus, facts.SizeBytes ?? 0,
                spinning == true ? "HDD" : bus == "nvme" ? "NVMe SSD" : spinning == false ? "SSD" : "Disk", facts.Firmware, facts.TemperatureC, node?.Id));
        }
        return rows;
    }

    private static List<Suggestion> Suggestions(ScanManifest? scan, DriverReport? report, OptimizationPlan? plan)
    {
        var list = new List<Suggestion>();
        foreach (var section in plan?.Sections ?? [])
            foreach (var item in section.Items.Where(i => i.Tone is "warn" or "improve"))
                list.Add(new Suggestion(item.Tone == "warn" ? "warning" : "improvement", item.Title, item.Detail, item.Action, section.Title));
        if (scan is not null)
            foreach (var s in SuggestionEngine.Analyze(scan, report).Where(s => s.Severity != SuggestionSeverity.Info || list.Count == 0))
                list.Add(new Suggestion(s.Severity switch { SuggestionSeverity.Critical or SuggestionSeverity.Warning => "warning", SuggestionSeverity.Improvement => "improvement", _ => "info" },
                    s.Title, s.Detail, s.Action, s.Category));
        if (list.Count == 0) list.Add(new Suggestion("info", "Nothing to fix", "Every check in the optimization plan passed.", null, "Overview"));
        return list.DistinctBy(s => s.Title).OrderBy(s => s.Severity switch { "warning" => 0, "improvement" => 1, _ => 2 }).ToList();
    }

    // ---- The live tiles ----------------------------------------------------------------------------------

    public static HeaderTelemetry Header(SystemTelemetry telemetry)
    {
        double? Zone(string zone) => telemetry.Thermals.FirstOrDefault(t => t.Zone == zone) is { } reading ? Math.Round(reading.TemperatureCelsius, 1) : null;
        return new HeaderTelemetry(
            Math.Round(telemetry.CpuUsagePercent, 1),
            Math.Round(telemetry.MemoryUsedGb, 2),
            Math.Round(telemetry.MemoryTotalBytes / 1_073_741_824d, 2),
            Zone("CPU"), Zone("GPU"), Zone("Chipset"),
            telemetry.Fans.Select(f => new HeaderFan(f.Name, f.Rpm)).ToList(),
            Math.Round(telemetry.NetworkReceivedBytesPerSec),
            Math.Round(telemetry.NetworkSentBytesPerSec),
            telemetry.Power.FirstOrDefault(p => p.Device == "GPU") is { } gpu ? Math.Round(gpu.Watts, 1) : null,
            telemetry.SampledAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Live rates for the map, as the Windows app sends them. Network: [bytes in/s, out/s, packets in/s, out/s,
    /// dropped+errors/s, bytes in, bytes out, packets in, packets out, dropped, errors since Windows started].
    /// Disk: [bytes read/s, written/s, reads/s, writes/s, requests queued now, % busy].
    /// </summary>
    public static ConnectionFlow Flow(SystemTelemetry telemetry, Dictionary<string, string> netKeys)
    {
        var flow = new ConnectionFlow { CpuPercent = Math.Round(telemetry.CpuUsagePercent, 1) };
        foreach (var (id, rate) in telemetry.InterfaceRates ?? new Dictionary<string, LinkRate>())
        {
            if (!netKeys.TryGetValue(id, out var key)) netKeys[id] = key = ConnectionsService.NetKey(id);
            var totals = telemetry.InterfaceCounters?.GetValueOrDefault(id) ?? default;
            flow.Network[key] =
            [
                Math.Round(rate.In), Math.Round(rate.Out), Math.Round(rate.OpsIn), Math.Round(rate.OpsOut),
                Math.Round(totals.DroppedPerSec, 1),
                totals.BytesIn, totals.BytesOut, totals.PacketsIn, totals.PacketsOut, totals.Dropped, totals.Errors
            ];
        }
        foreach (var (number, rate) in telemetry.DiskRates ?? new Dictionary<int, LinkRate>())
        {
            var queue = telemetry.DiskQueues?.GetValueOrDefault(number) ?? 0;
            var busy = telemetry.DiskBusy?.GetValueOrDefault(number) ?? 0;
            flow.Disks[number.ToString(CultureInfo.InvariantCulture)] =
                [Math.Round(rate.In), Math.Round(rate.Out), Math.Round(rate.OpsIn), Math.Round(rate.OpsOut), queue, busy];
        }
        foreach (var (zone, key) in new[] { ("CPU", "cpu"), ("GPU", "gpu"), ("Chipset", "chipset") })
            if (telemetry.Thermals.FirstOrDefault(t => t.Zone == zone) is { } reading)
                flow.Temperatures[key] = reading.TemperatureCelsius;
        return flow;
    }

    // ---- The machine summary (for the table of every machine) ------------------------------------------

    /// <summary>Reads this PC once and prints its one-row summary: what a BoardScout elsewhere asks for over SSH.</summary>
    public static async Task<MachineSummary> SummaryAsync(bool privacy)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var scout = new DriverScoutService();
        var scanPath = scout.GetLatestScanPath() ?? await scout.ScanAsync(CancellationToken.None);
        var scan = await scout.LoadScanAsync(scanPath, CancellationToken.None);
        Privacy.Learn(await File.ReadAllTextAsync(scanPath));
        using var telemetry = new SystemTelemetryService();
        telemetry.Sample();
        var capture = await Task.Run(ConnectionsService.Read);
        var map = ConnectionsService.Build(capture, privacy, null);
        var cores = await InterruptStats.SampleAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
        var sample = telemetry.Sample();
        var plan = PlanService.Build(capture, map, cores, null, privacy, sample.Power);
        var system = System.Text.Json.JsonSerializer.Deserialize(await SystemInfoService.GatherJsonAsync(scan), BoardScoutJson.Default.SystemInfoSnapshot) ?? new SystemInfoSnapshot();
        return MachineSummaries.Build("windows", Title(capture, scan), system, map, plan,
            Volumes(privacy).Select(v => (v.MountPoint, v.SizeBytes, v.FreeBytes)),
            sample.Thermals.FirstOrDefault(t => t.Zone == "CPU")?.TemperatureCelsius, SystemTelemetryService.IsElevated, clock.ElapsedMilliseconds, privacy);
    }

    public static IEnumerable<ConnectionNode> Walk(ConnectionNode node)
    {
        yield return node;
        foreach (var child in node.Children)
            foreach (var descendant in Walk(child))
                yield return descendant;
    }
}
