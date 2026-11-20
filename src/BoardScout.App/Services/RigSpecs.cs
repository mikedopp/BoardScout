using BoardScout.Models;

namespace BoardScout.Services;

/// <summary>One plate of the header's spec strip: what it is, the figure worth showing, and a line of detail.</summary>
internal sealed record RigSpec(string Label, string Value, string Detail);

/// <summary>
/// The headline parts of this PC, from its scan, in the order someone would read them out: processor, graphics,
/// memory, storage, Windows. Every figure is one the scan recorded; a part the scan has nothing for is left out
/// rather than guessed.
/// </summary>
internal static class RigSpecs
{
    public static List<RigSpec> For(ScanManifest scan)
    {
        var specs = new List<RigSpec>();

        var cpu = scan.Cpu;
        if (cpu.Name.Length > 0)
            specs.Add(new("CPU", Views.WindowsOverview.ShortCpuName(cpu.Name),
                cpu.Cores > 0 ? $"{cpu.Cores} cores · {cpu.Threads} threads" : ""));

        var cards = scan.Components.Where(c => c.Category.Equals("gpu", StringComparison.OrdinalIgnoreCase) && c.Model.Length > 0)
            .Select(c => c.Model).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (cards.Count > 0)
        {
            // A card in a slot is the one to name; the processor's own graphics come second.
            var card = cards.FirstOrDefault(IsAddInCard) ?? cards[0];
            specs.Add(new("Graphics", ShortGraphicsName(card), cards.Count > 1 ? $"+ {cards.Count - 1} more" : ""));
        }

        if (scan.Memory.TotalBytes > 0)
        {
            var sticks = scan.Memory.Slots.Where(s => s.CapacityGb > 0).ToList();
            var detail = "";
            if (sticks.Count > 0)
            {
                detail = sticks.Select(s => s.CapacityGb).Distinct().Count() == 1
                    ? $"{sticks.Count} × {sticks[0].CapacityGb:0.#} GB"
                    : $"{sticks.Count} sticks";
                var speed = sticks.Max(s => Math.Max(s.SpeedMhz, 0));
                if (speed > 0) detail += $" · {speed} MHz";
            }
            specs.Add(new("Memory", $"{Math.Round(scan.TotalMemoryGb):0} GB", detail));
        }

        var volumes = scan.Volumes.Where(v => v.SizeBytes > 0).ToList();
        if (volumes.Count > 0)
            specs.Add(new("Storage", Size(volumes.Sum(v => v.SizeBytes)), volumes.Count == 1 ? "1 volume" : $"{volumes.Count} volumes"));

        var os = scan.Scan.Os.Caption.Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (os.Length > 0)
            specs.Add(new("System", os, scan.FormFactor is { Length: > 0 } form && form != "unknown" ? form.ToUpperInvariant() : ""));

        return specs;
    }

    private static bool IsAddInCard(string model) =>
        model.Contains("RTX", StringComparison.OrdinalIgnoreCase) || model.Contains("GTX", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || model.Contains("Arc", StringComparison.OrdinalIgnoreCase) ||
        model.Contains("Quadro", StringComparison.OrdinalIgnoreCase) || model.Contains("Radeon Pro", StringComparison.OrdinalIgnoreCase);

    /// <summary>"NVIDIA GeForce RTX 3050" reads as "GeForce RTX 3050": the plate is already labelled Graphics.</summary>
    internal static string ShortGraphicsName(string model)
    {
        var name = model.Replace("(R)", "", StringComparison.OrdinalIgnoreCase).Replace("(TM)", "", StringComparison.OrdinalIgnoreCase);
        foreach (var maker in new[] { "NVIDIA ", "AMD ", "Intel " })
            if (name.StartsWith(maker, StringComparison.OrdinalIgnoreCase)) name = name[maker.Length..];
        return string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Binary units, like the rest of the app and Explorer: 1 TB is 1024 GB.</summary>
    internal static string Size(long bytes)
    {
        const double gb = 1_073_741_824d;
        var gigabytes = bytes / gb;
        return gigabytes >= 1024 ? $"{gigabytes / 1024:0.#} TB" : $"{gigabytes:0} GB";
    }
}
