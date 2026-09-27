namespace BoardScout.Models;

public sealed record ThermalReading(string Zone, double TemperatureCelsius);

public sealed record FanReading(string Name, int Rpm, bool Active);

public sealed record SystemTelemetry(
    double CpuUsagePercent,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
    IReadOnlyList<ThermalReading> Thermals,
    IReadOnlyList<FanReading> Fans,
    double NetworkSentBytesPerSec,
    double NetworkReceivedBytesPerSec,
    DateTimeOffset SampledAt)
{
    public ulong MemoryUsedBytes => MemoryTotalBytes > MemoryAvailableBytes
        ? MemoryTotalBytes - MemoryAvailableBytes
        : 0;

    public double MemoryUsedGb => MemoryUsedBytes / 1_073_741_824d;

    public double MemoryUsagePercent => MemoryTotalBytes == 0
        ? 0
        : MemoryUsedBytes * 100d / MemoryTotalBytes;
}

/// <summary>What the hardware sensor library could reach, and why not when it could not.</summary>
public sealed record SensorStatus(
    bool Started,
    bool Elevated,
    bool PawnIoInstalled,
    string? PawnIoVersion,
    int TemperatureZones,
    int Fans,
    string? Error)
{
    public static SensorStatus Pending { get; } = new(false, false, false, null, 0, 0, null);

    /// <summary>CPU, VRM, and motherboard fan sensors need the PawnIO driver and an elevated process.</summary>
    public bool MotherboardSensorsAvailable => Elevated && PawnIoInstalled;
}
