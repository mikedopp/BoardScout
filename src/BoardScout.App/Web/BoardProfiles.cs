using System.Globalization;
using System.Text.RegularExpressions;

namespace BoardScout.Views;

/// <summary>
/// Boards BoardScout knows slot by slot. For one of them the Overview names each part the way the board prints it,
/// adds the slots, ports, and headers nothing is plugged into, and says what the manual says about lane sharing.
/// Any other board is drawn only from what the system reports. Shared by both editions.
/// </summary>
internal static partial class BoardProfiles
{
    public sealed record Result(IReadOnlyList<BoardPart> Parts, string? Layout, IReadOnlyList<string> Notes);

    /// <param name="board">The board's maker and model, as firmware reports it.</param>
    /// <param name="fanHelp">What to say on a fan header without a reading (it differs per edition).</param>
    /// <param name="memoryGb">Installed memory, for what the memory slots say about it.</param>
    public static Result Apply(string board, IReadOnlyList<BoardPart> parts, string fanHelp, double memoryGb = 0)
    {
        var noted = parts.Select(p => p.Note is null && PartNotes.For(p, memoryGb) is { } note ? p with { Note = note } : p).ToList();
        if (board.Contains("B550M Steel Legend", StringComparison.OrdinalIgnoreCase)) return SteelLegend(noted, fanHelp);
        return new Result(noted, null, []);
    }

    private static string Join(string first, string? second) => second is null ? first : first + " " + second;

    /// <summary>"cpu" or "chipset": from the part itself, or from the "… · CPU" / "… · chipset" ending of its detail.</summary>
    public static string? SideOf(BoardPart part) =>
        part.Side ?? (part.Detail?.EndsWith("· CPU", StringComparison.Ordinal) == true ? "cpu"
            : part.Detail?.EndsWith("· chipset", StringComparison.Ordinal) == true ? "chipset" : null);

    // ---- ASRock B550M Steel Legend (micro-ATX, AM4) ---------------------------------------------------
    // Slots and sharing from ASRock's manual: M2_1 (Ultra M.2) is x4 from the CPU, M2_2 (Hyper M.2) is PCIe 3.0 x2
    // from the chipset and shares lanes with SATA3_5 and SATA3_6, M2_3 is Key E for Wi-Fi. PCIE1 is the CPU's x16;
    // PCIE2 (x1) and PCIE3 (x4 in a full-length slot) come from the chipset.

    private const string Board = "ASRock B550M Steel Legend";

    private static Result SteelLegend(IReadOnlyList<BoardPart> parts, string fanHelp)
    {
        var list = parts.ToList();
        BoardPart? Take(Func<BoardPart, bool> match) => list.FirstOrDefault(match);
        void Swap(BoardPart old, BoardPart next) => list[list.IndexOf(old)] = next;

        // The CPU socket, the chipset, and the memory slots keep their parts; they only gain the board's labels.
        if (Take(p => p.Kind == "cpu") is { } cpu) Swap(cpu, cpu with { Silk = "AM4", Side = "cpu" });
        if (Take(p => p.Kind == "chipset") is { } chipset) Swap(chipset, chipset with { Silk = "B550", Side = "chipset" });
        foreach (var dimm in list.Where(p => p.Kind == "dimm").ToList())
            Swap(dimm, dimm with { Silk = DimmSlot().IsMatch(dimm.Detail ?? "") ? "DDR4_" + dimm.Detail!.ToUpperInvariant() : dimm.Silk, Side = "cpu" });

        // M.2: the drive on CPU lanes is in M2_1, the first one on chipset lanes in M2_2.
        var m21 = Take(p => p.Kind == "nvme" && SideOf(p) == "cpu");
        var m22 = Take(p => p.Kind == "nvme" && SideOf(p) == "chipset");
        if (m21 is not null)
            Swap(m21, m21 with { Zone = "m2", Silk = "M2_1", Side = "cpu", Note = "Ultra M.2, wired straight to the CPU: the fastest place for an SSD on this board." });
        else
            list.Add(Open("m2-1", "m2", "slot", "M.2 Key M · x4 · CPU", "M2_1", "cpu", 4,
                "Ultra M.2, wired straight to the CPU: the fastest place for an SSD on this board.",
                [new("Socket", "M.2 Key M"), new("Lanes", "x4 from the CPU")]));
        if (m22 is not null)
            Swap(m22, m22 with
            {
                Zone = "m2", Silk = "M2_2", Side = "chipset",
                Note = "Hyper M.2: two PCIe 3.0 lanes from the chipset, about half what a fast NVMe drive can use. It shares lanes with SATA3_5 and SATA3_6, so those two ports are off."
            });
        else
            list.Add(Open("m2-2", "m2", "slot", "M.2 Key M · PCIe 3.0 x2 · chipset", "M2_2", "chipset", 2,
                "Hyper M.2: two PCIe 3.0 lanes from the chipset. A drive here turns SATA3_5 and SATA3_6 off.",
                [new("Socket", "M.2 Key M"), new("Lanes", "PCIe 3.0 x2 from the chipset"), new("Shares", "SATA3_5 and SATA3_6")]));

        // M2_3 is the Wi-Fi socket; it sits with the other M.2 sockets on the board.
        const string keyE = "M2_3 is a Key E socket for a Wi-Fi and Bluetooth card (type 2230). It can't take an SSD.";
        if (Take(p => p.Kind == "wifi") is { } wifi)
        {
            // The card's own name ("Intel Wi-Fi 6E AX210") says more than the adapter's ("Wi-Fi").
            var card = wifi.Facts.FirstOrDefault(f => f.Label == "Adapter")?.Value is { Length: > 0 } adapter
                ? Trademarks().Replace(adapter, "").Replace("  ", " ").Trim() : wifi.Name;
            Swap(wifi, wifi with { Zone = "m2", Name = card, Silk = "M2_3", Detail = "M.2 Key E · " + (wifi.Detail ?? "Wi-Fi"), Note = keyE });
        }
        else
            list.Add(Open("m2-3", "m2", "wifi", "M.2 Key E · Wi-Fi and Bluetooth", "M2_3", "chipset", null, keyE,
                [new("Socket", "M.2 Key E, type 2230"), new("Takes", "Wi-Fi and Bluetooth cards only")]));

        // PCIe slots: the CPU's graphics slot, then the chipset's x1 and x4.
        if (Take(p => p.Zone == "slots" && SideOf(p) == "cpu") is { } pcie1)
            Swap(pcie1, pcie1 with { Silk = "PCIE1", Side = "cpu", Note = Join("In PCIE1, the full-length slot wired straight to the CPU.", pcie1.Note) });
        else
            list.Add(Open("pcie1", "slots", "slot", "PCIe x16 · CPU", "PCIE1", "cpu", 16,
                "The full-length slot wired straight to the CPU, for a graphics card.", [new("Lanes", "x16 from the CPU")]));
        var chipsetCards = list.Where(p => p.Zone == "slots" && SideOf(p) == "chipset" && p.Silk is null).ToList();
        if (chipsetCards.FirstOrDefault(p => p.Width == 1) is { } x1)
            Swap(x1, x1 with { Silk = "PCIE2", Side = "chipset" });
        else
            list.Add(Open("pcie2", "slots", "slot", "PCIe 3.0 x1 · chipset", "PCIE2", "chipset", 1,
                "One PCIe 3.0 lane: right for a sound, USB, capture, or network card, too narrow for fast storage.",
                [new("Lanes", "PCIe 3.0 x1 from the chipset")]));
        const string pcie3Note = "The lower full-length slot, with four PCIe 3.0 lanes from the chipset: the best spot for one more NVMe drive " +
                                 "on an adapter card. Cards that hold several drives need bifurcation, which this board doesn't offer.";
        if (chipsetCards.FirstOrDefault(p => p.Width != 1) is { } x4)
            Swap(x4, x4 with { Silk = "PCIE3", Side = "chipset", Note = x4.Note ?? pcie3Note });
        else
            list.Add(Open("pcie3", "slots", "slot", "PCIe 3.0 x4 in an x16 slot · chipset", "PCIE3", "chipset", 4, pcie3Note,
                [new("Lanes", "PCIe 3.0 x4 from the chipset"), new("Slot", "Full length (x16)")]));

        // SATA3_1 to SATA3_6: drives by the port they report, else in order; a drive in M2_2 turns 5 and 6 off.
        var drives = list.Where(p => p.Zone == "sata" && p.Kind is "disk" or "optical").ToList();
        var used = new HashSet<int>();
        var numbered = new List<(BoardPart Part, int Port)>();
        foreach (var drive in drives)
            if (ReportedPort(drive) is { } port && port is >= 1 and <= 6 && used.Add(port)) numbered.Add((drive, port));
        foreach (var drive in drives.Where(d => numbered.All(n => n.Part != d)))
            if (Enumerable.Range(1, 6).FirstOrDefault(n => !used.Contains(n) && !(m22 is not null && n >= 5)) is var free and > 0 && used.Add(free))
                numbered.Add((drive, free));
        foreach (var (drive, port) in numbered)
            Swap(drive, drive with { Silk = $"SATA3_{port}", Side = "chipset" });
        for (var port = 1; port <= 6; port++)
        {
            if (used.Contains(port)) continue;
            list.Add(m22 is not null && port >= 5
                ? new BoardPart($"sata-port-{port}", "sata", "port", "Off", "Shared with M2_2", "off",
                    [new("Port", $"SATA3_{port}"), new("Why it's off", "M2_2 holds a drive, and they share lanes")],
                    Empty: true, Silk: $"SATA3_{port}", Side: "chipset",
                    Note: "Off while M2_2 holds a drive: the two share lanes. Empty M2_2 to use this port.")
                : Open($"sata-port-{port}", "sata", "port", "SATA 6 Gb/s", $"SATA3_{port}", "chipset", null,
                    "A free SATA port for an SSD, a hard drive, or an optical drive.", [new("Port", $"SATA3_{port}"), new("Speed", "6 Gb/s")]));
        }

        // Headers along the board's edge. Fan readings, when there are any, already have parts of their own.
        if (!list.Any(p => p.Zone == "fans"))
            foreach (var header in new[] { "CPU_FAN", "CPU/WP", "CHA_1", "CHA_2", "CHA_3", "CHA_4" })
                list.Add(new BoardPart("fan-" + header.Replace('/', '-').ToLowerInvariant(), "fans", "fan", header, "No reading", "info",
                    [new("Header", header.Contains("WP", StringComparison.Ordinal) ? "4-pin fan or water pump" : "4-pin fan")],
                    Silk: header, Note: fanHelp));
        foreach (var (header, what) in new[]
                 {
                     ("USB3", "Front-panel USB 3 ports"), ("USB2", "Front-panel USB 2 ports"), ("F_AUDIO", "Front-panel headphone and microphone jacks"),
                     ("PANEL", "The case's power and reset buttons and lights"), ("RGB", "12 V RGB light strips"), ("ARGB", "5 V addressable RGB light strips")
                 })
            list.Add(new BoardPart("hdr-" + header.ToLowerInvariant().Replace("_", ""), "headers", "header", header, what, "info", [new("Header", header), new("For", what)], Silk: header));

        list.Insert(0, new BoardPart("rear-io", "io", "rear-io", "Rear I/O", "DP · HDMI · USB-C · USB-A · 2.5G LAN · audio", "info",
            [new("Video", "DisplayPort and HDMI, from the CPU's graphics"), new("USB", "USB-C and USB-A"), new("Network", "2.5 Gb Ethernet"), new("Audio", "Jacks on the back panel")],
            Note: "The ports on the back of the PC. The video outputs only work with a CPU that has graphics built in."));

        var notes = new List<string>
        {
            "M2_3 is for a Wi-Fi and Bluetooth card only; it can't take an SSD.",
            m22 is not null
                ? "M2_2 holds a drive, so SATA3_5 and SATA3_6 are off: they share lanes."
                : "A drive in M2_2 turns SATA3_5 and SATA3_6 off: they share lanes.",
            "PCIE3 is the best spot for one more NVMe drive, on an adapter card."
        };
        return new Result(Order(list), Board, notes);
    }

    private static BoardPart Open(string id, string zone, string kind, string detail, string silk, string side, int? width, string note, List<Fact> facts) =>
        new(id, zone, kind, "Open", detail, "open", facts, Width: width, Empty: true, Silk: silk, Side: side, Note: note);

    // The port a SATA drive reports, as "(port 2)" in its link or a "Port" fact.
    private static int? ReportedPort(BoardPart drive)
    {
        foreach (var text in new[] { drive.Detail }.Concat(drive.Facts.Where(f => f.Label.Contains("port", StringComparison.OrdinalIgnoreCase)).Select(f => f.Value)))
            if (text is not null && PortNumber().Match(text) is { Success: true } match)
                return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        return null;
    }

    // Slots top to bottom, as they sit on the board; SATA ports by number after the controller.
    private static List<BoardPart> Order(List<BoardPart> parts)
    {
        static int Rank(BoardPart p) => p.Silk switch
        {
            "M2_1" => 0, "PCIE1" => 1, "PCIE2" => 2, "M2_3" => 3, "M2_2" => 4, "PCIE3" => 5,
            { } silk when silk.StartsWith("SATA3_", StringComparison.Ordinal) => 10 + silk[^1] - '0',
            _ => p.Kind == "sata" ? 9 : 20
        };
        return parts.Select((p, i) => (p, i)).OrderBy(x => x.p.Zone is "m2" or "slots" or "sata" ? Rank(x.p) : 0).ThenBy(x => x.i).Select(x => x.p).ToList();
    }

    [GeneratedRegex(@"^[A-H][1-4]$", RegexOptions.IgnoreCase)]
    private static partial Regex DimmSlot();

    [GeneratedRegex(@"port\s*(\d)", RegexOptions.IgnoreCase)]
    private static partial Regex PortNumber();

    [GeneratedRegex(@"\((R|TM)\)", RegexOptions.IgnoreCase)]
    private static partial Regex Trademarks();
}

/// <summary>
/// One plain sentence about what a part is suited to, from what is known about it: core count, graphics tier,
/// installed memory. Estimates, worded as such; the Overview's inspector shows them under the part's details.
/// </summary>
internal static partial class PartNotes
{
    public static string? For(BoardPart part, double memoryGb) => part.Kind switch
    {
        "cpu" => CoreCount().Match(part.Detail ?? "") is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) switch
        {
            >= 16 => "Workstation-class core count: heavy rendering, compiling, simulation, and other parallel work.",
            >= 8 => "Plenty of cores for gaming, content creation, streaming, and heavy multitasking.",
            >= 6 => "Well balanced for modern games, office work, and everyday creative work.",
            >= 4 => "Fine for everyday work, media, and moderate multitasking.",
            _ => "Best suited to light everyday work."
        } : null,
        "gpu" when part.Zone == "slots" => Graphics(part.Name),
        "gpu" => "Graphics built into the processor: fine for the desktop, video, several displays, and light 3D.",
        "dimm" when !part.Empty && memoryGb > 0 => memoryGb switch
        {
            >= 60 => "A lot of memory: large creative projects, virtual machines, datasets, and heavy multitasking.",
            >= 30 => "Comfortable for modern games, content creation, development, and heavy multitasking.",
            >= 15 => "Solid mainstream memory for games, office work, and moderate creative work.",
            _ => "Enough for light work; memory-hungry apps would run better with more."
        },
        "nvme" => "Fast, low-latency storage: right for the system, apps, games, and projects. How fast depends on the PCIe generation and lanes it gets.",
        "chipset" => "Runs the USB and SATA ports, the extra slots, and onboard devices. All of them share its one link to the CPU.",
        _ => null
    };

    // GeForce by the last two digits of the model (3050 → 50), Radeon by its hundreds (6600 → 60).
    private static string Graphics(string name)
    {
        if (GpuModel().Match(name) is not { Success: true } match) return "A graphics card: games, video, and GPU-accelerated apps. How fast depends on the model, power, and cooling.";
        var number = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var tier = match.Groups[1].Value.Equals("RX", StringComparison.OrdinalIgnoreCase) ? number % 1000 / 100 * 10 : number % 100;
        return tier switch
        {
            >= 80 => "High-end graphics: demanding high-resolution games, GPU rendering, and heavy compute.",
            >= 70 => "Performance graphics: high-refresh games, content creation, and GPU-accelerated work.",
            >= 60 => "Mainstream graphics: modern games, with useful acceleration in creative apps.",
            _ => "Entry-level graphics: esports and lighter 1080p games, video acceleration, and some GPU compute."
        };
    }

    [GeneratedRegex(@"(\d+)\s+cores?")]
    private static partial Regex CoreCount();

    [GeneratedRegex(@"\b(RTX|GTX|RX|Arc\s+[AB])\s*(\d{3,4})", RegexOptions.IgnoreCase)]
    private static partial Regex GpuModel();
}
