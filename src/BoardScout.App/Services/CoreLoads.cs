using System.Runtime.InteropServices;

namespace BoardScout.Services;

/// <summary>
/// How busy each logical processor was between two readings of the counters Windows keeps per processor
/// (idle, kernel, and user time). One cheap call per reading; no admin rights needed.
/// </summary>
internal static class CoreLoads
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: IdleTime, KernelTime (which includes idle), UserTime, DpcTime,
    // InterruptTime (8 bytes each), InterruptCount (4), padding (4).
    private const int PerformanceClass = 8;
    internal const int EntrySize = 48;

    public static byte[]? Read()
    {
        var buffer = new byte[EntrySize * Environment.ProcessorCount];
        return NtQuerySystemInformation(PerformanceClass, buffer, buffer.Length, out _) == 0 ? buffer : null;
    }

    /// <summary>The share of the time between the two readings each processor spent working, 0 to 1.</summary>
    public static double[] Between(byte[] before, byte[] after)
    {
        var loads = new double[Math.Min(before.Length, after.Length) / EntrySize];
        for (var core = 0; core < loads.Length; core++)
        {
            long Delta(int offset) => BitConverter.ToInt64(after, core * EntrySize + offset) - BitConverter.ToInt64(before, core * EntrySize + offset);
            var total = Delta(8) + Delta(16);
            loads[core] = total <= 0 ? 0 : Math.Clamp((total - Delta(0)) / (double)total, 0, 1);
        }
        return loads;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returned);
}
