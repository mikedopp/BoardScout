using System.Text.RegularExpressions;

namespace BoardScout.Services;

internal sealed record DimmPlacement(DimmSlot Slot, string Label, int PositionInChannel, bool Recommended);

internal sealed record PlanFinding(string Tone, string Title, string Detail, string? Action = null);

internal sealed record MemoryAnalysis(
    IReadOnlyList<DimmPlacement> Slots,
    int Populated,
    int ChannelsTotal,
    int ChannelsUsed,
    double TotalGb,
    string Type,
    int ConfiguredMts,
    int RatedMts,
    IReadOnlyList<PlanFinding> Findings)
{
    public string? ChannelText => ChannelsUsed switch
    {
        0 => null,
        1 => "single channel",
        2 => "dual channel",
        4 => "quad channel",
        _ => $"{ChannelsUsed} channels"
    };
}

/// <summary>
/// Checks how memory is installed, from the firmware's own slot list: channels in use, whether two sticks
/// sit in the slots boards recommend, speed against the modules' rating, mixed kits, and free slots.
/// </summary>
internal static partial class MemoryAdvice
{
    public static MemoryAnalysis? Analyze(FirmwareFacts firmware, string? boardProduct)
    {
        var slots = firmware.MemorySlots;
        if (slots.Count == 0 || slots.All(s => !s.Populated)) return null;

        // Slot order inside each channel, as the firmware lists them (DIMM 0 before DIMM 1, A1 before A2).
        var placements = new List<DimmPlacement>();
        foreach (var channel in slots.GroupBy(s => s.Channel ?? "?"))
        {
            var ordered = channel.OrderBy(s => s.Position ?? 0).ToList();
            for (var i = 0; i < ordered.Count; i++)
                placements.Add(new DimmPlacement(ordered[i], Label(ordered[i], i), i, false));
        }
        placements = placements.OrderBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();

        var populated = placements.Where(p => p.Slot.Populated).ToList();
        var channels = placements.Select(p => p.Slot.Channel).Where(c => c is not null).Distinct().Count();
        var used = populated.Select(p => p.Slot.Channel).Where(c => c is not null).Distinct().Count();
        var perChannel = channels > 0 ? placements.Count / channels : 0;
        var totalGb = populated.Sum(p => p.Slot.SizeMb) / 1024d;
        var type = populated[0].Slot.MemoryType;
        var configured = populated.Select(p => p.Slot.ConfiguredMts).Where(v => v > 0).DefaultIfEmpty(0).Min();
        var firmwareRated = populated.Select(p => p.Slot.SpeedMts).Where(v => v > 0).DefaultIfEmpty(0).Min();
        var partRated = populated.Select(p => RatedFromPart(p.Slot.PartNumber)).Where(v => v > 0).DefaultIfEmpty(0).Min();
        var rated = Math.Max(firmwareRated, partRated);
        var findings = new List<PlanFinding>();
        var verifiedBoard = boardProduct?.Contains("B550M Steel Legend", StringComparison.OrdinalIgnoreCase) == true;

        // With one stick per channel on a desktop board with two slots per channel, the second slot of each
        // channel (A2/B2) is the one boards recommend: it sits at the end of the trace, so no empty slot
        // hangs off the line. Servers and workstations often want the first slot instead, so they get no
        // slot advice.
        var twoPerChannelLayout = firmware.FormFactor == "Desktop" && channels == 2 && perChannel == 2 &&
                                  populated.Count == channels && used == channels;
        if (twoPerChannelLayout)
        {
            placements = placements.Select(p => p with { Recommended = p.PositionInChannel == 1 }).ToList();
            populated = placements.Where(p => p.Slot.Populated).ToList();
        }

        if (populated.Count == 1 && channels >= 2)
        {
            findings.Add(new("improve", "One stick means single channel",
                $"A single {populated[0].Slot.SizeMb / 1024d:0.#} GB stick uses one memory channel, half the bandwidth this CPU can use.",
                "Add a matching stick in the other channel's second slot (usually B2) for dual channel."));
        }
        else if (populated.Count >= 2 && used == 1 && channels >= 2)
        {
            var labels = string.Join(" and ", populated.Select(p => p.Label));
            findings.Add(new("warn", "Both sticks are on the same channel",
                $"{labels} are both on channel {populated[0].Slot.Channel}, so memory runs single channel at half its bandwidth.",
                "Move one stick to the other channel: for two sticks, use A2 and B2."));
        }
        else if (used >= 2)
        {
            findings.Add(new("good", $"{(used == 2 ? "Dual" : used == 4 ? "Quad" : $"{used}-way")} channel is working",
                $"Sticks are on {used} channels ({string.Join(", ", populated.Select(p => p.Label))}), so the CPU gets the full memory bandwidth."));
        }

        if (twoPerChannelLayout)
        {
            var wrong = populated.Where(p => !p.Recommended).ToList();
            if (wrong.Count > 0)
            {
                var now = string.Join(" and ", populated.Select(p => p.Label));
                var best = string.Join(" and ", placements.Where(p => p.Recommended).Select(p => p.Label));
                var who = verifiedBoard ? "The ASRock B550M Steel Legend manual" : "Most boards' manuals";
                findings.Add(new("improve", $"Sticks are in {now}; {best} are the recommended slots",
                    $"Windows lists them in the first slot of each channel ({string.Join(", ", wrong.Select(p => p.Slot.DeviceLocator + " / " + p.Slot.BankLocator))}). " +
                    $"{who} recommends {best} for two sticks: the slots at the end of each channel's traces, which keep signals cleanest at high speed. " +
                    (configured > 0 && rated > 0 && configured >= rated
                        ? $"It already runs at the rated {configured} MT/s, so this is about stability headroom rather than speed."
                        : "Moving them can help the memory reach its rated speed."),
                    $"Power off, unplug, and move the sticks to {best}. Check the slot labels printed on the board."));
            }
            else
            {
                findings.Add(new("good", $"Sticks are in the recommended slots ({string.Join(" and ", populated.Select(p => p.Label))})",
                    "The second slot of each channel is where boards want two sticks."));
            }
        }
        else if (populated.Count == 3)
        {
            findings.Add(new("info", "Three sticks leave the channels unbalanced",
                "One channel has more memory than the other, so part of the memory runs single channel (flex mode).",
                "Two or four matching sticks keep everything dual channel."));
        }

        if (configured > 0 && rated > configured)
        {
            findings.Add(new("improve", $"Running at {configured} MT/s; the modules are rated {rated}",
                "The memory is running at its safe default speed instead of the speed printed on the kit.",
                "Turn on XMP (Intel) or DOCP/EXPO (AMD) in the BIOS memory settings."));
        }
        else if (configured > 0)
        {
            findings.Add(new("good", $"Running at the rated {configured} MT/s",
                partRated > 0 ? $"That matches the rating in the part number ({populated[0].Slot.PartNumber})." : "The firmware reports it at full speed."));
        }

        var parts = populated.Select(p => p.Slot.PartNumber ?? "?").Distinct().Count();
        var sizes = populated.Select(p => p.Slot.SizeMb).Distinct().Count();
        if (populated.Count >= 2 && (parts > 1 || sizes > 1))
        {
            findings.Add(new("info", "The sticks don't match",
                sizes > 1 ? "Different sizes put part of the memory in single channel (flex mode)." : "Different kits can force a lower common speed and looser timings.",
                "A matched kit avoids both."));
        }

        var free = placements.Count(p => !p.Slot.Populated);
        if (free > 0)
        {
            findings.Add(new("info", $"{free} free slot{(free == 1 ? "" : "s")}",
                $"Room for {free} more stick{(free == 1 ? "" : "s")}" +
                (populated.Count > 0 && populated[0].Slot.PartNumber is { } part ? $". Matching the installed {populated[0].Slot.SizeMb / 1024d:0.#} GB {part} keeps the channels balanced" : "") +
                (free + populated.Count == 4 && populated.Count == 2 ? "; four sticks sometimes need a slightly lower speed." : ".")));
        }

        return new MemoryAnalysis(placements, populated.Count, channels, used, totalGb, type, configured, rated, findings);
    }

    // "A2", "DIMM_A2", or channel letter plus position ("DIMM 1" on channel A → "A2").
    private static string Label(DimmSlot slot, int positionInChannel)
    {
        var direct = DirectLabel().Match(slot.DeviceLocator);
        if (direct.Success) return direct.Groups[1].Value.ToUpperInvariant();
        return slot.Channel is { } channel ? $"{channel}{positionInChannel + 1}" : slot.DeviceLocator;
    }

    // Speed printed in common part numbers: F4-3200C16 (G.Skill), CMK16GX4M2B3200C16 (Corsair),
    // KF432C16 (Kingston Fury, ×100), BL8G32C16 (Crucial Ballistix, ×100).
    private static int RatedFromPart(string? part)
    {
        if (string.IsNullOrWhiteSpace(part)) return 0;
        var match = GSkill().Match(part);
        if (match.Success) return int.Parse(match.Groups[1].Value);
        match = Corsair().Match(part);
        if (match.Success) return int.Parse(match.Groups[1].Value);
        match = HundredsStyle().Match(part);
        if (match.Success) return int.Parse(match.Groups[1].Value) * 100;
        return 0;
    }

    [GeneratedRegex(@"(?:^|_)([A-H][1-4])$", RegexOptions.IgnoreCase)]
    private static partial Regex DirectLabel();

    [GeneratedRegex(@"^F[45]-(\d{4})C", RegexOptions.IgnoreCase)]
    private static partial Regex GSkill();

    [GeneratedRegex(@"X[45]M\d[A-Z](\d{4})C", RegexOptions.IgnoreCase)]
    private static partial Regex Corsair();

    [GeneratedRegex(@"^(?:KF[45]|BL\d+G)(\d{2})C", RegexOptions.IgnoreCase)]
    private static partial Regex HundredsStyle();
}
