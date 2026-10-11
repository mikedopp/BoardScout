using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardScout.Platform;

/// <summary>
/// One line of /proc/interrupts: an IRQ number (or an architecture counter like LOC), its count on each
/// processor, the interrupt controller and type, and the handlers (drivers) attached to it.
/// </summary>
internal sealed record IrqLine(string Id, long[] Counts, string? Chip, string? Type, IReadOnlyList<string> Handlers, string? Description, string? PciAddress)
{
    public long Total => Counts.Sum();
    public bool Numbered => int.TryParse(Id, out _);
    public bool MessageSignaled => Chip?.Contains("MSI", StringComparison.Ordinal) == true;
}

/// <summary>Interrupt and softirq counters (/proc/interrupts, /proc/softirqs); every user may read them.</summary>
internal static partial class Interrupts
{
    public static List<IrqLine> Read() => Parse(Sys.Lines("/proc/interrupts").ToList());

    public static List<IrqLine> Parse(IReadOnlyList<string> lines)
    {
        var result = new List<IrqLine>();
        if (lines.Count == 0) return result;
        var cpus = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var id = line[..colon].Trim();
            var rest = line[(colon + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var counts = new long[cpus];
            var i = 0;
            for (; i < cpus && i < rest.Length; i++)
            {
                if (!long.TryParse(rest[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out counts[i])) break;
            }
            var tail = rest.Skip(i).ToList();
            if (int.TryParse(id, out _))
            {
                // "IR-PCI-MSIX-0000:02:00.0    1-edge      nvme0q1" or "IR-IO-APIC    2-edge      timer"
                var chip = tail.ElementAtOrDefault(0);
                var type = tail.ElementAtOrDefault(1);
                var handlers = string.Join(' ', tail.Skip(2)).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var address = chip is null ? null : PciAddressIn().Match(chip) is { Success: true } m ? m.Value : null;
                result.Add(new IrqLine(id, counts, chip, type, handlers, null, address));
            }
            else
            {
                // Architecture counters: "LOC: … Local timer interrupts"
                result.Add(new IrqLine(id, counts, null, null, [], string.Join(' ', tail), null));
            }
        }
        return result;
    }

    /// <summary>Softirq counts per processor, summed over every kind (the follow-up work drivers queue).</summary>
    public static long[] ReadSoftirqs()
    {
        var lines = Sys.Lines("/proc/softirqs").ToList();
        if (lines.Count == 0) return [];
        var cpus = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var totals = new long[cpus];
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (var i = 1; i <= cpus && i < f.Length; i++)
                if (long.TryParse(f[i], out var v)) totals[i - 1] += v;
        }
        return totals;
    }

    [GeneratedRegex(@"[0-9a-f]{4}:[0-9a-f]{2}:[0-9a-f]{2}\.[0-7]")]
    private static partial Regex PciAddressIn();
}
