using System.Text;
using System.Text.RegularExpressions;
using BoardScout.Models;

namespace BoardScout.Services;

internal static class UpgradePlannerService
{
    public sealed record ComponentAge(string Name, string Category, string Released, int AgeYears, string Generation);
    public sealed record UpgradeOption(string Slot, string Current, string Recommended, string Why, string Price, string Tier);

    public static List<ComponentAge> GetComponentAges(ScanManifest scan)
    {
        var ages = new List<ComponentAge>();
        var now = DateTime.Now;

        var cpu = scan.Cpu;
        if (!string.IsNullOrEmpty(cpu.Name))
        {
            var (released, gen) = ClassifyCpu(cpu.Name);
            if (released.HasValue)
                ages.Add(new ComponentAge(cpu.Name, "CPU", released.Value.ToString("MMMM yyyy"),
                    (int)((now - released.Value).TotalDays / 365.25), gen));
        }

        var board = scan.SystemInfo.Baseboard;
        if (!string.IsNullOrEmpty(board.Product))
        {
            var (released, gen) = ClassifyBoard(board.Product);
            if (released.HasValue)
                ages.Add(new ComponentAge($"{board.Manufacturer} {board.Product}", "Motherboard",
                    released.Value.ToString("MMMM yyyy"),
                    (int)((now - released.Value).TotalDays / 365.25), gen));
        }

        foreach (var comp in scan.Components.Where(c =>
            c.Category.Contains("Display", StringComparison.OrdinalIgnoreCase) ||
            c.Model.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            c.Model.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase)))
        {
            var (released, gen) = ClassifyGpu(comp.Model);
            if (released.HasValue)
                ages.Add(new ComponentAge(comp.Model, "GPU", released.Value.ToString("MMMM yyyy"),
                    (int)((now - released.Value).TotalDays / 365.25), gen));
        }

        var ramSlot = scan.Memory.Slots.FirstOrDefault(s => s.CapacityGb > 0);
        if (ramSlot is not null)
        {
            var gen = ramSlot.SpeedMhz >= 4800 ? "DDR5" : "DDR4";
            ages.Add(new ComponentAge(
                $"{scan.Memory.Populated}x {ramSlot.CapacityGb:0}GB {gen}-{ramSlot.RatedMhz}", "RAM",
                gen, 0, gen));
        }

        return ages;
    }

    public static List<UpgradeOption> GetUpgradeOptions(ScanManifest scan)
    {
        var options = new List<UpgradeOption>();
        var socket = DetectSocket(scan);
        var chipset = DetectChipset(scan);

        if (socket == "AM4")
            BuildAm4Upgrades(scan, chipset, options);

        return options;
    }

    public static string GenerateReport(ScanManifest scan)
    {
        var ages = GetComponentAges(scan);
        var upgrades = GetUpgradeOptions(scan);
        var board = scan.SystemInfo.Baseboard;
        var cpu = scan.Cpu;
        var sb = new StringBuilder();

        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html lang='en'><head><meta charset='utf-8'>");
        sb.AppendLine($"<title>BoardScout Upgrade Planner — {board.Manufacturer} {board.Product}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine(ReportCss());
        sb.AppendLine("</style></head><body>");

        sb.AppendLine("<div class='container'>");
        sb.AppendLine($"<h1>Upgrade Planner</h1>");
        sb.AppendLine($"<p class='board-name'>{board.Manufacturer} {board.Product}</p>");
        sb.AppendLine($"<p class='subtitle'>{cpu.Name} · {scan.Memory.Populated}/{scan.Memory.TotalSlots} DIMM · {scan.FormFactor}</p>");

        sb.AppendLine("<h2>Component Ages</h2>");
        sb.AppendLine("<div class='age-grid'>");
        foreach (var age in ages)
        {
            var color = age.AgeYears switch { >= 5 => "#EF4444", >= 3 => "#F59E0B", _ => "#10B981" };
            sb.AppendLine($"<div class='age-card'>");
            sb.AppendLine($"<div class='age-badge' style='background:{color}'>{age.AgeYears}yr</div>");
            sb.AppendLine($"<div class='age-cat'>{age.Category}</div>");
            sb.AppendLine($"<div class='age-name'>{Esc(age.Name)}</div>");
            sb.AppendLine($"<div class='age-detail'>{age.Generation} · Released {age.Released}</div>");
            sb.AppendLine("</div>");
        }
        sb.AppendLine("</div>");

        if (upgrades.Count > 0)
        {
            sb.AppendLine("<h2>Pimped Out — Best Compatible Upgrades</h2>");
            sb.AppendLine("<p class='disclaimer'>Prices reflect August 2026 US market estimates and may vary. " +
                          "DDR4 and legacy AM4 parts have inflated pricing due to global DRAM shortages and AM4 discontinuation.</p>");
            sb.AppendLine("<table class='upgrade-table'>");
            sb.AppendLine("<tr><th>Slot</th><th>Current</th><th>Upgrade To</th><th>Why</th><th>Est. Price</th></tr>");

            decimal totalCost = 0;
            foreach (var u in upgrades)
            {
                var rowClass = u.Tier switch { "essential" => "tier-essential", "performance" => "tier-perf", _ => "tier-luxury" };
                sb.AppendLine($"<tr class='{rowClass}'>");
                sb.AppendLine($"<td>{Esc(u.Slot)}</td>");
                sb.AppendLine($"<td>{Esc(u.Current)}</td>");
                sb.AppendLine($"<td><strong>{Esc(u.Recommended)}</strong></td>");
                sb.AppendLine($"<td>{Esc(u.Why)}</td>");
                sb.AppendLine($"<td class='price'>{u.Price}</td>");
                sb.AppendLine("</tr>");
                if (decimal.TryParse(u.Price.Replace("$", "").Replace(",", "").Replace("~", ""), out var p))
                    totalCost += p;
            }
            sb.AppendLine($"<tr class='total'><td colspan='4'>Estimated Total</td><td class='price'>~${totalCost:N0}</td></tr>");
            sb.AppendLine("</table>");

            sb.AppendLine("<div class='summary-box'>");
            sb.AppendLine("<h3>The Dream Build Summary</h3>");
            sb.AppendLine("<p>This is your board pushed to its absolute limits — every slot filled, " +
                          "every lane saturated, running the best silicon AM4 ever got.</p>");
            sb.AppendLine("</div>");
        }

        sb.AppendLine("<div class='footer'>");
        sb.AppendLine($"<p>Generated by BoardScout v{UI.VersionButton.AppVersion} · {DateTime.Now:yyyy-MM-dd HH:mm}</p>");
        sb.AppendLine("<p>All product names are trademarks of their respective owners. " +
                      "Prices are estimates and not offers to sell. No affiliation with any hardware vendor.</p>");
        sb.AppendLine("</div>");

        sb.AppendLine("</div></body></html>");
        return sb.ToString();
    }

    private static void BuildAm4Upgrades(ScanManifest scan, string chipset, List<UpgradeOption> opts)
    {
        var cpu = scan.Cpu;
        var cpuName = cpu.Name;

        if (!cpuName.Contains("5800X3D", StringComparison.OrdinalIgnoreCase) &&
            !cpuName.Contains("5950X", StringComparison.OrdinalIgnoreCase))
        {
            opts.Add(new UpgradeOption("CPU", cpuName,
                "AMD Ryzen 7 5800X3D",
                "Best AM4 gaming CPU ever made — 96MB 3D V-Cache crushes frame times. " +
                "The Anniversary Edition relaunched June 2026 at $349 MSRP.",
                "~$349", "essential"));
            opts.Add(new UpgradeOption("CPU (alt)", cpuName,
                "AMD Ryzen 9 5950X",
                "16 cores / 32 threads — the AM4 productivity king for rendering, " +
                "compiling, and heavy multitasking. Used ~$280-$295.",
                "~$295", "performance"));
        }

        var memGb = scan.TotalMemoryGb;
        var populated = scan.Memory.Populated;
        var totalSlots = scan.Memory.TotalSlots;
        var currentSpeed = scan.Memory.Slots.FirstOrDefault()?.RatedMhz ?? 0;
        var currentSpeedStr = currentSpeed > 0 ? $"{currentSpeed} MHz" : "unknown speed";

        if (memGb < 60)
        {
            var targetGb = totalSlots >= 4 ? 128 : 64;
            opts.Add(new UpgradeOption("RAM",
                $"{memGb:F0} GB DDR4 ({populated}/{totalSlots} slots, {currentSpeedStr})",
                $"{targetGb} GB DDR4-3600 CL16 ({(totalSlots >= 4 ? "4x32GB" : "2x32GB")})",
                $"Fill all {totalSlots} slots to max. DDR4-3600 is the sweet spot for Zen 3. " +
                "Note: DDR4 prices spiked in 2026 due to DRAM shortages — 64GB kits around $500.",
                totalSlots >= 4 ? "~$950" : "~$509", "performance"));
        }

        var gpu = scan.Components.FirstOrDefault(c =>
            c.Model.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            c.Model.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase));
        if (gpu is not null && !gpu.Model.Contains("4090") && !gpu.Model.Contains("4080"))
        {
            opts.Add(new UpgradeOption("GPU", gpu.Model,
                "NVIDIA GeForce RTX 4070 Ti Super",
                "16GB GDDR6X, PCIe 4.0 x16 — massive upgrade from RTX 3050. " +
                "The board's top x16 slot runs full Gen4 bandwidth from the CPU.",
                "~$784", "essential"));
            opts.Add(new UpgradeOption("GPU (dream)", gpu.Model,
                "NVIDIA GeForce RTX 4090",
                "The absolute king — 24GB GDDR6X, Ada Lovelace. " +
                "PCIe 4.0 x16 won't bottleneck it for gaming. Power-hungry (450W TDP).",
                "~$1,800", "luxury"));
        }

        var hasGen4Nvme = scan.Components.Any(c =>
            c.Model.Contains("P3 Plus", StringComparison.OrdinalIgnoreCase) ||
            c.Model.Contains("990 Pro", StringComparison.OrdinalIgnoreCase) ||
            c.Model.Contains("SN850", StringComparison.OrdinalIgnoreCase));
        if (!hasGen4Nvme)
        {
            opts.Add(new UpgradeOption("NVMe (M2_1)", "Current M.2 drive",
                "Samsung 990 Pro 2TB (Gen4)",
                "7,450 MB/s reads in the CPU-direct M.2 slot. " +
                "The fastest Gen4 drive available. Prime Day 2026 price hit $369.",
                "~$385", "performance"));
        }

        opts.Add(new UpgradeOption("CPU Cooler", "Stock / current cooler",
            "Noctua NH-D15",
            "Dual-tower air king — handles 5800X3D or 5950X with near-silent operation. " +
            "AM4 mounting kit included. The B550M micro-ATX case must fit 165mm height.",
            "~$110", "essential"));

        if (chipset.Contains("B550", StringComparison.OrdinalIgnoreCase))
        {
            opts.Add(new UpgradeOption("WiFi", "Intel AX210 (Wi-Fi 6E)",
                "Already best-in-class",
                "The AX210 in M2_3 Key-E is the top WiFi card for this board. " +
                "Wi-Fi 7 cards exist but the B550 M.2 Key-E slot caps at Gen3 speeds — no benefit.",
                "—", "performance"));
        }
    }

    private static string DetectSocket(ScanManifest scan)
    {
        var cpu = scan.Cpu.Name;
        if (Regex.IsMatch(cpu, @"Ryzen.*(5\d00|3\d00|2\d00|1\d00|PRO)", RegexOptions.IgnoreCase))
            return "AM4";
        if (Regex.IsMatch(cpu, @"Ryzen.*(9\d00|7\d00X3D|7\d00)", RegexOptions.IgnoreCase) &&
            cpu.Contains("7000", StringComparison.OrdinalIgnoreCase))
            return "AM5";
        if (cpu.Contains("Core", StringComparison.OrdinalIgnoreCase))
            return "LGA";
        return "unknown";
    }

    private static string DetectChipset(ScanManifest scan)
    {
        var product = scan.SystemInfo.Baseboard.Product;
        if (product.Contains("B550", StringComparison.OrdinalIgnoreCase)) return "B550";
        if (product.Contains("X570", StringComparison.OrdinalIgnoreCase)) return "X570";
        if (product.Contains("B450", StringComparison.OrdinalIgnoreCase)) return "B450";
        if (product.Contains("X470", StringComparison.OrdinalIgnoreCase)) return "X470";
        if (product.Contains("A520", StringComparison.OrdinalIgnoreCase)) return "A520";
        return "unknown";
    }

    private static (DateTime? Released, string Gen) ClassifyCpu(string name)
    {
        if (name.Contains("5800X3D")) return (new DateTime(2022, 4, 1), "Zen 3 V-Cache");
        if (name.Contains("5950X")) return (new DateTime(2020, 11, 5), "Zen 3");
        if (name.Contains("5900X")) return (new DateTime(2020, 11, 5), "Zen 3");
        if (name.Contains("5800X") && !name.Contains("3D")) return (new DateTime(2020, 11, 5), "Zen 3");
        if (name.Contains("5700X")) return (new DateTime(2022, 4, 4), "Zen 3");
        if (name.Contains("5700G")) return (new DateTime(2021, 4, 13), "Zen 3 APU");
        if (name.Contains("5600X")) return (new DateTime(2020, 11, 5), "Zen 3");
        if (name.Contains("5600G")) return (new DateTime(2021, 4, 13), "Zen 3 APU");
        if (name.Contains("5600") && !name.Contains("X") && !name.Contains("G"))
            return (new DateTime(2022, 4, 4), "Zen 3");
        if (name.Contains("3950X")) return (new DateTime(2019, 11, 25), "Zen 2");
        if (name.Contains("3900X")) return (new DateTime(2019, 7, 7), "Zen 2");
        if (name.Contains("3800X")) return (new DateTime(2019, 7, 7), "Zen 2");
        if (name.Contains("3700X")) return (new DateTime(2019, 7, 7), "Zen 2");
        if (name.Contains("3600")) return (new DateTime(2019, 7, 7), "Zen 2");
        if (Regex.IsMatch(name, @"i[3579]-1[234]\d{2,3}")) return (new DateTime(2021, 3, 1), "Alder Lake / Raptor Lake");
        return (null, "");
    }

    private static (DateTime? Released, string Gen) ClassifyGpu(string model)
    {
        if (model.Contains("4090")) return (new DateTime(2022, 10, 12), "Ada Lovelace");
        if (model.Contains("4080 SUPER")) return (new DateTime(2024, 1, 31), "Ada Lovelace Refresh");
        if (model.Contains("4080")) return (new DateTime(2022, 11, 16), "Ada Lovelace");
        if (model.Contains("4070 Ti SUPER")) return (new DateTime(2024, 1, 24), "Ada Lovelace Refresh");
        if (model.Contains("4070 Ti")) return (new DateTime(2023, 1, 5), "Ada Lovelace");
        if (model.Contains("4070 SUPER")) return (new DateTime(2024, 1, 17), "Ada Lovelace Refresh");
        if (model.Contains("4070")) return (new DateTime(2023, 4, 13), "Ada Lovelace");
        if (model.Contains("4060 Ti")) return (new DateTime(2023, 5, 24), "Ada Lovelace");
        if (model.Contains("4060")) return (new DateTime(2023, 6, 29), "Ada Lovelace");
        if (model.Contains("3090")) return (new DateTime(2020, 9, 24), "Ampere");
        if (model.Contains("3080")) return (new DateTime(2020, 9, 17), "Ampere");
        if (model.Contains("3070")) return (new DateTime(2020, 10, 29), "Ampere");
        if (model.Contains("3060 Ti")) return (new DateTime(2020, 12, 2), "Ampere");
        if (model.Contains("3060")) return (new DateTime(2021, 2, 25), "Ampere");
        if (model.Contains("3050")) return (new DateTime(2022, 1, 27), "Ampere");
        if (model.Contains("RX 7900")) return (new DateTime(2022, 12, 13), "RDNA 3");
        if (model.Contains("RX 7800")) return (new DateTime(2023, 9, 6), "RDNA 3");
        if (model.Contains("RX 7700")) return (new DateTime(2023, 9, 6), "RDNA 3");
        if (model.Contains("RX 7600")) return (new DateTime(2023, 5, 25), "RDNA 3");
        if (model.Contains("RX 6900")) return (new DateTime(2020, 12, 8), "RDNA 2");
        if (model.Contains("RX 6800")) return (new DateTime(2020, 11, 18), "RDNA 2");
        if (model.Contains("RX 6700")) return (new DateTime(2021, 3, 18), "RDNA 2");
        if (model.Contains("RX 6600")) return (new DateTime(2021, 10, 13), "RDNA 2");
        return (null, "");
    }

    private static (DateTime? Released, string Gen) ClassifyBoard(string product)
    {
        if (product.Contains("B550", StringComparison.OrdinalIgnoreCase))
            return (new DateTime(2020, 6, 16), "AMD B550");
        if (product.Contains("X570", StringComparison.OrdinalIgnoreCase))
            return (new DateTime(2019, 7, 7), "AMD X570");
        if (product.Contains("B450", StringComparison.OrdinalIgnoreCase))
            return (new DateTime(2018, 7, 31), "AMD B450");
        if (product.Contains("A520", StringComparison.OrdinalIgnoreCase))
            return (new DateTime(2020, 8, 18), "AMD A520");
        return (null, "");
    }

    private static (DateTime? Released, string Gen) ClassifyRam(MemorySlot slot)
    {
        if (slot.SpeedMhz >= 4800) return (new DateTime(2021, 11, 1), "DDR5");
        if (slot.SpeedMhz >= 2133) return (new DateTime(2014, 1, 1), "DDR4");
        return (null, "");
    }

    private static string Esc(string s) => System.Net.WebUtility.HtmlEncode(s);

    private static string ReportCss() => """
        * { margin: 0; padding: 0; box-sizing: border-box; }
        body { font-family: 'Segoe UI', system-ui, sans-serif; background: #0A0F14; color: #E8EDF4; padding: 32px; }
        .container { max-width: 960px; margin: 0 auto; }
        h1 { font-size: 28px; color: #3D9EFF; margin-bottom: 4px; }
        h2 { font-size: 18px; color: #8A9BB2; margin: 32px 0 16px; text-transform: uppercase; letter-spacing: 1px; }
        h3 { font-size: 16px; color: #3D9EFF; margin-bottom: 8px; }
        .board-name { font-size: 15px; color: #E8EDF4; font-weight: 600; }
        .subtitle { font-size: 13px; color: #8A9BB2; margin-top: 4px; }
        .disclaimer { font-size: 12px; color: #8A9BB2; margin-bottom: 16px; font-style: italic; }
        .age-grid { display: grid; grid-template-columns: repeat(auto-fill, minmax(280px, 1fr)); gap: 12px; }
        .age-card { background: #0C1620; border: 1px solid #162030; border-radius: 8px; padding: 16px; display: flex; flex-direction: column; gap: 4px; }
        .age-badge { display: inline-block; width: fit-content; padding: 2px 10px; border-radius: 12px; font-size: 13px; font-weight: 700; color: #fff; }
        .age-cat { font-size: 11px; color: #8A9BB2; text-transform: uppercase; letter-spacing: 0.5px; margin-top: 4px; }
        .age-name { font-size: 14px; font-weight: 600; }
        .age-detail { font-size: 12px; color: #8A9BB2; }
        .upgrade-table { width: 100%; border-collapse: collapse; font-size: 13px; }
        .upgrade-table th { background: #111C2C; color: #8A9BB2; text-align: left; padding: 10px 12px; font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; }
        .upgrade-table td { padding: 10px 12px; border-bottom: 1px solid #162030; vertical-align: top; }
        .upgrade-table .price { color: #10B981; font-weight: 600; white-space: nowrap; }
        .tier-essential td:first-child { border-left: 3px solid #3D9EFF; }
        .tier-perf td:first-child { border-left: 3px solid #F59E0B; }
        .tier-luxury td:first-child { border-left: 3px solid #A78BFA; }
        .total td { font-weight: 700; font-size: 15px; border-top: 2px solid #3D9EFF; background: #111C2C; }
        .total .price { color: #3D9EFF; font-size: 15px; }
        .summary-box { background: linear-gradient(135deg, #0C1620 0%, #162040 100%); border: 1px solid #3D9EFF; border-radius: 8px; padding: 20px; margin: 24px 0; }
        .summary-box p { font-size: 14px; color: #8A9BB2; line-height: 1.6; }
        .footer { margin-top: 48px; padding-top: 16px; border-top: 1px solid #162030; font-size: 11px; color: #5A6B82; }
        @media print { body { background: #fff; color: #111; } .age-card { border-color: #ddd; } .upgrade-table td { border-color: #ddd; } }
        """;
}
