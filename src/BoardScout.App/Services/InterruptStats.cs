using System.Runtime.InteropServices;

namespace BoardScout.Services;

/// <summary>How much of one logical processor's time went to interrupts and DPCs, and how many it took.</summary>
internal sealed record CoreInterruptLoad(int Core, double InterruptPercent, double DpcPercent, double InterruptsPerSecond, double DpcsPerSecond);

/// <summary>
/// Interrupt and DPC load per logical processor over a short window, from the counters Windows keeps for
/// every processor (NtQuerySystemInformation). Available without admin rights, but only per processor:
/// which driver used the time needs an administrator trace (<see cref="InterruptProfiler"/>).
/// </summary>
internal static class InterruptStats
{
    // SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION: IdleTime, KernelTime, UserTime, DpcTime, InterruptTime (8 bytes each),
    // InterruptCount (4), padding (4).
    private const int PerformanceClass = 8;
    private const int PerformanceSize = 48;

    // SYSTEM_INTERRUPT_INFORMATION: ContextSwitches, DpcCount, DpcRate, TimeIncrement, DpcBypassCount, ApcBypassCount.
    private const int InterruptClass = 23;
    private const int InterruptSize = 24;

    public static async Task<List<CoreInterruptLoad>> SampleAsync(TimeSpan window, CancellationToken token)
    {
        var cores = Environment.ProcessorCount;
        var before = Read(PerformanceClass, PerformanceSize, cores);
        var dpcBefore = Read(InterruptClass, InterruptSize, cores);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await Task.Delay(window, token);
        var after = Read(PerformanceClass, PerformanceSize, cores);
        var dpcAfter = Read(InterruptClass, InterruptSize, cores);
        var seconds = watch.Elapsed.TotalSeconds;
        var result = new List<CoreInterruptLoad>();
        if (before is null || after is null) return result;

        for (var core = 0; core < cores; core++)
        {
            long Delta(int offset) => BitConverter.ToInt64(after, core * PerformanceSize + offset) - BitConverter.ToInt64(before, core * PerformanceSize + offset);
            var total = Delta(8) + Delta(16); // kernel time includes idle; kernel + user = all time on this core
            if (total <= 0) continue;
            var interrupts = unchecked(BitConverter.ToUInt32(after, core * PerformanceSize + 40) - BitConverter.ToUInt32(before, core * PerformanceSize + 40));
            var dpcs = dpcBefore is null || dpcAfter is null ? 0
                : unchecked(BitConverter.ToUInt32(dpcAfter, core * InterruptSize + 4) - BitConverter.ToUInt32(dpcBefore, core * InterruptSize + 4));
            result.Add(new CoreInterruptLoad(core,
                Math.Round(Delta(32) * 100d / total, 2),
                Math.Round(Delta(24) * 100d / total, 2),
                Math.Round(interrupts / seconds),
                Math.Round(dpcs / seconds)));
        }
        return result;
    }

    private static byte[]? Read(int infoClass, int entrySize, int cores)
    {
        var buffer = new byte[entrySize * cores];
        return NtQuerySystemInformation(infoClass, buffer, buffer.Length, out _) == 0 ? buffer : null;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, byte[] buffer, int length, out int returned);
}
