namespace BoardScout.Models;

public sealed record ThermalReading(string Zone, double TemperatureCelsius);

public sealed record SystemTelemetry(
    double CpuUsagePercent,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
    IReadOnlyList<ThermalReading> Thermals,
    double DiskReadBytesPerSec,
    double DiskWriteBytesPerSec,
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

    public double? CpuTemperatureCelsius => Thermals.Count > 0 ? Thermals[0].TemperatureCelsius : null;
}
