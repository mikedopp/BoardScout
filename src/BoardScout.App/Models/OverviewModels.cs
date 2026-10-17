namespace BoardScout.Views;

// What the browser dashboard's shell shows outside the shared web views (Overview, Drivers, Storage, Efficiency,
// the header tiles), built by the Linux edition from /sys and by the Windows edition from its scan and map.
// Serialized with camelCase names (Json.Options).

internal sealed record Fact(string Label, string Value);

/// <summary>
/// One part on the Overview board: what it is, where it sits, and what to say when it's hovered. Silk is the label
/// printed beside it on the board ("M2_1", "PCIE3", "SATA3_2"); Side says whose lanes feed it ("cpu" or "chipset");
/// Note is one plain sentence about what it is good for, or what to know about it. Tone is good, warn, open, off
/// (a port the board turns off), or info.
/// </summary>
internal sealed record BoardPart(string Id, string Zone, string Kind, string Name, string? Detail, string Tone, IReadOnlyList<Fact> Facts,
    string? Live = null, int? Width = null, bool Empty = false, string? Node = null,
    string? Silk = null, string? Side = null, string? Note = null);

/// <summary>How old a part's design is, from the inventory (Windows): "CPU", "Ryzen 7 5700G", 5 years, "Zen 3 APU", "April 2021".</summary>
internal sealed record PartAge(string Part, string Name, int Years, string Generation, string Released);

/// <summary>A row on the Drivers tab. Url is a vendor page to open (Windows driver checks); never a download.</summary>
internal sealed record DriverRow(string Device, string Category, string Driver, string? Version, string Source, string? Firmware, string Status, string Tone,
    string? Node, string? Latest = null, string? Url = null);

internal sealed record VolumeRow(string MountPoint, string? Label, string FileSystem, long SizeBytes, long FreeBytes, double UsedPercent, string Disks, bool Lvm, bool ReadOnly);

internal sealed record DiskRow(string Name, string Model, string Bus, long SizeBytes, string Kind, string? Firmware, double? TemperatureC, string? Node);

internal sealed record Suggestion(string Severity, string Title, string Detail, string? Action, string Category);

/// <summary>
/// Everything the Overview view and the shell's tabs show. Layout names the board whose real slots and headers the
/// parts follow when BoardScout knows that board (BoardProfiles), with its BoardNotes; otherwise the board is drawn
/// from what the system reports, and both are empty.
/// </summary>
internal sealed record OverviewPayload(
    string Title,
    string Subtitle,
    string FormFactor,
    string Hostname,
    IReadOnlyList<BoardPart> Parts,
    IReadOnlyList<Fact> Details,
    IReadOnlyList<DriverRow> Drivers,
    IReadOnlyList<VolumeRow> Volumes,
    IReadOnlyList<DiskRow> Disks,
    IReadOnlyList<Suggestion> Suggestions,
    double MemoryGb,
    int Components,
    double DataUsedTb,
    int? Updates,
    string GatheredAt,
    long GatheredMs,
    string? Layout = null,
    IReadOnlyList<string>? BoardNotes = null,
    IReadOnlyList<PartAge>? Ages = null);

/// <summary>
/// The header tiles' numbers: processor, memory, temperatures, fans, and network, right now. CpuWatts is the processor
/// package's power; Power is the whole breakdown, for the Overview's summary.
/// </summary>
internal sealed record HeaderTelemetry(
    double Cpu,
    double MemoryUsedGb,
    double MemoryTotalGb,
    double? CpuTemp,
    double? GpuTemp,
    double? ChipsetTemp,
    IReadOnlyList<HeaderFan> Fans,
    double NetIn,
    double NetOut,
    double? GpuWatts,
    string SampledAt,
    double? CpuWatts = null,
    BoardScout.Models.PowerBreakdown? Power = null);

internal sealed record HeaderFan(string Name, int Rpm);
