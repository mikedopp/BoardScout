using System.Text;
using System.Text.Json;
using BoardScout.Models;

namespace BoardScout.Services;

internal static class SpecSheetGenerator
{
    public static string Generate(ScanManifest scan, DriverReport? report)
    {
        var cpu = scan.Cpu;
        var board = scan.SystemInfo.Baseboard;
        var bios = scan.SystemInfo.Bios;
        var mem = scan.Memory;
        var first = mem.Slots.FirstOrDefault();
        var gpu = scan.Components.FirstOrDefault(c => c.Category == "gpu");
        var nvme = scan.Components.Where(c => c.Category == "storage" && c.LookupHints.BusType?.Equals("NVMe", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var sata = scan.Components.Where(c => c.Category == "storage" && c.LookupHints.BusType?.Equals("SATA", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var usb = scan.Components.Where(c => c.Category == "storage" && c.LookupHints.BusType?.Equals("USB", StringComparison.OrdinalIgnoreCase) == true).ToList();
        var wifi = scan.Components.FirstOrDefault(c => c.Category == "network" && c.Model.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase));
        var lan = scan.Components.FirstOrDefault(c => c.Category == "network" && c != wifi);
        var chipset = scan.Components.FirstOrDefault(c => c.Category == "chipset");
        var audio = scan.Components.Where(c => c.Category == "audio").ToList();
        var updates = report?.Results.Count(r => r.Status == "update-available") ?? 0;
        var totalStorageTb = scan.Volumes.Sum(v => v.SizeBytes) / 1_099_511_627_776d;
        var usedStorageTb = scan.Volumes.Sum(v => v.SizeBytes - v.FreeBytes) / 1_099_511_627_776d;
        var hostname = scan.Scan.Hostname;
        var scanDate = scan.Scan.TimestampUtc?.ToLocalTime().ToString("g") ?? "Unknown";
        var jsonData = JsonSerializer.Serialize(scan, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        });

        var sb = new StringBuilder();
        sb.AppendLine("<!doctype html>");
        sb.AppendLine("<html lang=\"en\">");
        sb.AppendLine("<head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
        sb.AppendLine($"<title>{Esc(hostname)} — BoardScout Spec Sheet</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(Css());
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<div class=\"page\">");

        // Header
        sb.AppendLine("<header class=\"hero\">");
        sb.AppendLine($"<div class=\"hero-badge\">BoardScout v0.6.0</div>");
        sb.AppendLine($"<h1>{Esc(board.Manufacturer)} {Esc(board.Product)}</h1>");
        sb.AppendLine($"<p class=\"hero-sub\">{Esc(hostname)} · {Esc(scan.FormFactor.ToUpperInvariant())} · {Esc(scan.Scan.Os.Caption)}</p>");
        sb.AppendLine($"<p class=\"hero-date\">Scanned {Esc(scanDate)}</p>");
        sb.AppendLine("</header>");

        // Stats bar
        sb.AppendLine("<div class=\"stats\">");
        Stat(sb, cpu.Cores.ToString(), "Cores");
        Stat(sb, cpu.Threads.ToString(), "Threads");
        Stat(sb, $"{scan.TotalMemoryGb:0.#}", "GB RAM");
        Stat(sb, $"{totalStorageTb:0.1}", "TB Storage");
        Stat(sb, scan.Components.Count.ToString(), "Components");
        if (report is not null)
            Stat(sb, updates.ToString(), updates == 0 ? "Up to date" : "Updates");
        sb.AppendLine("</div>");

        // Specs grid
        sb.AppendLine("<div class=\"specs\">");

        // CPU
        SpecCard(sb, "Processor", "cpu", [
            (cpu.Name, null),
            ($"{cpu.Cores} cores / {cpu.Threads} threads", "muted"),
        ]);

        // GPU
        if (gpu is not null)
        {
            SpecCard(sb, "Graphics", "gpu", [
                (gpu.Model, null),
                ($"Driver {gpu.Current.DriverVersion ?? "—"}", "muted"),
                ($"PCIe x16 · CPU-direct", "muted"),
            ]);
        }

        // Memory
        var memLines = new List<(string, string?)>
        {
            ($"{scan.TotalMemoryGb:0.#} GB DDR4 · {mem.Populated} of {mem.TotalSlots} slots", null)
        };
        if (first is not null)
        {
            memLines.Add(($"{first.SpeedMhz} MT/s active / {first.RatedMhz} MT/s rated", "muted"));
            if (!string.IsNullOrWhiteSpace(first.PartNumber))
                memLines.Add((first.PartNumber.Trim(), "muted"));
        }
        SpecCard(sb, "Memory", "mem", memLines);

        // Motherboard
        SpecCard(sb, "Motherboard", "board", [
            ($"{board.Manufacturer} {board.Product}", null),
            ($"BIOS {bios.Version} · {bios.ReleaseDate}", "muted"),
            ($"Chipset: {chipset?.Model ?? "—"}", "muted"),
        ]);

        // NVMe Storage
        if (nvme.Count > 0)
        {
            var nvmeLines = nvme.Select(d => ((string, string?))(d.Model, null)).ToList();
            nvmeLines.Add(("NVMe PCIe", "muted"));
            SpecCard(sb, "NVMe Drives", "nvme", nvmeLines);
        }

        // SATA Storage
        if (sata.Count > 0)
        {
            var sataLines = sata.Select(d => ((string, string?))(d.Model, null)).ToList();
            sataLines.Add(("SATA III", "muted"));
            SpecCard(sb, "SATA Drives", "sata", sataLines);
        }

        // USB Storage
        if (usb.Count > 0)
        {
            var usbLines = usb.Select(d =>
            {
                var size = d.LookupHints.SizeBytes.HasValue
                    ? $" · {d.LookupHints.SizeBytes.Value / 1_073_741_824d:0.#} GB"
                    : "";
                return ((string, string?))($"{d.Model}{size}", null);
            }).ToList();
            SpecCard(sb, "External Drives", "usb-storage", usbLines);
        }

        // Network
        var netLines = new List<(string, string?)>();
        if (wifi is not null) netLines.Add((wifi.Model, null));
        if (lan is not null) netLines.Add((lan.Model, null));
        if (netLines.Count > 0)
            SpecCard(sb, "Networking", "net", netLines);

        // Audio
        if (audio.Count > 0)
        {
            var audioLines = audio.Select(a => ((string, string?))(a.Model, null)).ToList();
            SpecCard(sb, "Audio", "audio", audioLines);
        }

        sb.AppendLine("</div>"); // specs

        // Volumes table
        sb.AppendLine("<div class=\"section\"><h2>Storage volumes</h2>");
        sb.AppendLine("<div class=\"table-wrap\"><table><thead><tr>");
        sb.AppendLine("<th>Volume</th><th>Disk</th><th>Bus</th><th>Capacity</th><th>Free</th><th>Used</th>");
        sb.AppendLine("</tr></thead><tbody>");
        foreach (var vol in scan.Volumes.OrderByDescending(v => v.UsedPercent))
        {
            var usedClass = vol.UsedPercent >= 95 ? "crit" : vol.UsedPercent >= 85 ? "warn" : "good";
            sb.AppendLine($"<tr><td>{Esc(vol.Letter)}</td><td>{Esc(vol.DiskModel ?? vol.Label)}</td>");
            sb.AppendLine($"<td>{Esc(vol.BusType ?? "—")}</td><td>{FormatBytes(vol.SizeBytes)}</td>");
            sb.AppendLine($"<td>{FormatBytes(vol.FreeBytes)}</td><td class=\"{usedClass}\">{vol.UsedPercent:0}%</td></tr>");
        }
        sb.AppendLine("</tbody></table></div></div>");

        // USB devices
        var usbDevices = scan.UsbDevices.Where(d => d.DeviceClass != "USB").ToList();
        if (usbDevices.Count > 0)
        {
            sb.AppendLine("<div class=\"section\"><h2>USB devices</h2><div class=\"usb-grid\">");
            foreach (var d in usbDevices)
                sb.AppendLine($"<div class=\"usb-chip\">{Esc(d.Name)}</div>");
            sb.AppendLine("</div></div>");
        }

        // JSON embed
        sb.AppendLine("<details class=\"json-section\"><summary>Raw scan JSON</summary>");
        sb.AppendLine($"<pre><code>{Esc(jsonData)}</code></pre>");
        sb.AppendLine("</details>");

        // Footer
        sb.AppendLine("<footer>");
        sb.AppendLine($"Generated by <strong>BoardScout v0.6.0</strong> · {Esc(scanDate)}");
        sb.AppendLine("</footer>");

        sb.AppendLine("</div>"); // page
        sb.AppendLine("</body></html>");
        return sb.ToString();
    }

    private static void Stat(StringBuilder sb, string value, string label) =>
        sb.AppendLine($"<div class=\"stat\"><span class=\"stat-val\">{Esc(value)}</span><span class=\"stat-lbl\">{Esc(label)}</span></div>");

    private static void SpecCard(StringBuilder sb, string title, string icon, List<(string Text, string? Class)> lines)
    {
        sb.AppendLine($"<div class=\"card\"><div class=\"card-head\">{Esc(title)}</div>");
        foreach (var (text, cls) in lines)
            sb.AppendLine($"<div class=\"{cls ?? "card-val"}\">{Esc(text)}</div>");
        sb.AppendLine("</div>");
    }

    private static string Esc(string s) =>
        System.Net.WebUtility.HtmlEncode(s ?? "");

    private static string FormatBytes(long bytes)
    {
        double value = bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static string Css() => """
    *{box-sizing:border-box;margin:0;padding:0}
    body{background:#060b10;color:#e8edf4;font-family:'Segoe UI',system-ui,sans-serif;line-height:1.6}
    .page{max-width:1100px;margin:0 auto;padding:32px 24px 60px}
    .hero{text-align:center;padding:48px 0 32px;border-bottom:1px solid #162030;margin-bottom:32px}
    .hero h1{font-size:28px;font-weight:700;letter-spacing:-.5px;margin:12px 0 8px}
    .hero-badge{display:inline-block;background:#162030;color:#3d9eff;font-size:11px;font-weight:600;
      padding:4px 14px;border-radius:20px;letter-spacing:1px;text-transform:uppercase;border:1px solid #1a3a5c}
    .hero-sub{color:#8a9bb2;font-size:14px}
    .hero-date{color:#4a6080;font-size:12px;margin-top:4px}
    .stats{display:flex;gap:4px;justify-content:center;flex-wrap:wrap;margin-bottom:36px}
    .stat{background:#0c1620;border:1px solid #162030;border-radius:12px;padding:16px 24px;text-align:center;min-width:120px}
    .stat-val{display:block;font-size:26px;font-weight:700;color:#3d9eff}
    .stat-lbl{font-size:11px;color:#8a9bb2;text-transform:uppercase;letter-spacing:1px}
    .specs{display:grid;grid-template-columns:repeat(auto-fill,minmax(300px,1fr));gap:16px;margin-bottom:36px}
    .card{background:#0c1620;border:1px solid #162030;border-radius:12px;padding:20px;transition:border-color .2s}
    .card:hover{border-color:#3d9eff40}
    .card-head{font-size:11px;font-weight:600;color:#3d9eff;text-transform:uppercase;letter-spacing:1.5px;margin-bottom:10px}
    .card-val{font-size:14px;font-weight:500;margin:3px 0}
    .muted{font-size:12px;color:#8a9bb2;margin:2px 0}
    .section{margin-bottom:36px}
    .section h2{font-size:14px;font-weight:600;color:#8a9bb2;text-transform:uppercase;letter-spacing:2px;margin-bottom:14px}
    .table-wrap{overflow-x:auto;border-radius:12px;border:1px solid #162030}
    table{width:100%;border-collapse:collapse;font-size:13px}
    th{text-align:left;padding:10px 14px;background:#0c1620;color:#8a9bb2;font-weight:600;font-size:11px;
      text-transform:uppercase;letter-spacing:1px;border-bottom:1px solid #162030}
    td{padding:10px 14px;border-bottom:1px solid #162030}
    tr:last-child td{border-bottom:none}
    tr:hover td{background:#0c162080}
    .good{color:#10b981}.warn{color:#f59e0b}.crit{color:#ef4444}
    .usb-grid{display:flex;flex-wrap:wrap;gap:8px}
    .usb-chip{background:#0c1620;border:1px solid #162030;border-radius:8px;padding:6px 14px;font-size:12px;color:#8a9bb2}
    .json-section{margin-top:36px;border:1px solid #162030;border-radius:12px;overflow:hidden}
    .json-section summary{padding:14px 20px;background:#0c1620;cursor:pointer;font-size:13px;color:#8a9bb2;font-weight:600}
    .json-section summary:hover{color:#e8edf4}
    .json-section pre{padding:20px;overflow-x:auto;font-size:11px;font-family:'Cascadia Mono','Consolas',monospace;
      color:#8a9bb2;background:#060b10;max-height:500px;overflow-y:auto}
    footer{text-align:center;padding:36px 0 12px;color:#4a6080;font-size:12px;border-top:1px solid #162030;margin-top:20px}
    footer strong{color:#3d9eff}
    @media(max-width:600px){.stats{flex-direction:column}.specs{grid-template-columns:1fr}.hero h1{font-size:22px}}
    @media print{body{background:#fff;color:#111}.card,.stat,.usb-chip{background:#f5f5f5;border-color:#ddd}
      .card-head,.stat-val,.hero-badge{color:#1a6fd4}th{background:#f0f0f0}.muted,.stat-lbl,.hero-sub{color:#666}}
    """;
}
