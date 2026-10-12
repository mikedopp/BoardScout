namespace BoardScout.Views;

// What the browser dashboard's shell shows outside the shared web views (Overview, Drivers, Storage, Efficiency,
// the header tiles), built by the Linux edition from /sys and by the Windows edition from its scan and map.
// Serialized with camelCase names (Json.Options).

internal sealed record Fact(string Label, string Value);

/// <summary>One part on the Overview board: what it is, where it sits, and what to say when it's hovered.</summary>
internal sealed record BoardPart(string Id, string Zone, string Kind, string Name, string? Detail, string Tone, IReadOnlyList<Fact> Facts,
    string? Live = null, int? Width = null, bool Empty = false, string? Node = null);

/// <summary>A row on the Drivers tab. Url is a vendor page to open (Windows driver checks); never a download.</summary>
internal sealed record DriverRow(string Device, string Category, string Driver, string? Version, string Source, string? Firmware, string Status, string Tone,
    string? Node, string? Latest = null, string? Url = null);

internal sealed record VolumeRow(string MountPoint, string? Label, string FileSystem, long SizeBytes, long FreeBytes, double UsedPercent, string Disks, bool Lvm, bool ReadOnly);

internal sealed record DiskRow(string Name, string Model, string Bus, long SizeBytes, string Kind, string? Firmware, double? TemperatureC, string? Node);

internal sealed record Suggestion(string Severity, string Title, string Detail, string? Action, string Category);

/// <summary>Everything the shell shows outside the shared web views.</summary>
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
    long GatheredMs);

/// <summary>The header tiles' numbers: processor, memory, temperatures, fans, and network, right now.</summary>
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
    string SampledAt);

internal sealed record HeaderFan(string Name, int Rpm);
