namespace BoardScout.Models;

public sealed record SystemTelemetry(
    double CpuUsagePercent,
    ulong MemoryTotalBytes,
    ulong MemoryAvailableBytes,
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
