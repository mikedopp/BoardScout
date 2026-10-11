using System.Globalization;

namespace BoardScout.Platform;

internal sealed record CpuFacts(
    string Name,
    string Vendor,
    int Sockets,
    int Cores,
    int Threads,
    string? Microcode,
    double? MaxMhz,
    string? Governor,
    string? ScalingDriver,
    string? EnergyPreference,
    string Architecture,
    IReadOnlyList<string> Vulnerabilities);

/// <summary>Time a processor spent in each state since boot, in clock ticks (/proc/stat).</summary>
internal readonly record struct CpuTimes(long User, long Nice, long System, long Idle, long IoWait, long Irq, long SoftIrq, long Steal)
{
    public long Total => User + Nice + System + Idle + IoWait + Irq + SoftIrq + Steal;
    public long Busy => Total - Idle - IoWait;
}

internal static class Cpu
{
    public static CpuFacts Read()
    {
        string? name = null, vendor = null, microcode = null, hardware = null;
        var cores = new HashSet<(string Package, string Core)>();
        var packages = new HashSet<string>();
        var threads = 0;
        string package = "0";
        foreach (var line in Sys.Lines("/proc/cpuinfo"))
        {
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            switch (key)
            {
                case "processor": threads++; break;
                case "model name": name ??= value; break;
                case "vendor_id": vendor ??= value; break;
                case "microcode": microcode ??= value; break;
                case "physical id": package = value; packages.Add(value); break;
                case "core id": cores.Add((package, value)); break;
                // Arm boards: "Hardware : BCM2835", "Model : Raspberry Pi 4 Model B Rev 1.4".
                case "Hardware": hardware ??= value; break;
                case "Model": name ??= value; break;
            }
        }
        threads = Math.Max(threads, Environment.ProcessorCount);
        var architecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        if (name is null && hardware is not null) name = hardware;
        // Arm processors list no model name; the device tree names the board instead.
        name ??= Sys.Text("/sys/firmware/devicetree/base/model") ?? $"{architecture} processor";

        var maxKhz = Sys.Long("/sys/devices/system/cpu/cpu0/cpufreq/cpuinfo_max_freq");
        var vulnerabilities = Sys.Children("/sys/devices/system/cpu/vulnerabilities")
            .Select(v => (Name: v, State: Sys.Read($"/sys/devices/system/cpu/vulnerabilities/{v}") ?? ""))
            .Where(v => v.State.StartsWith("Vulnerable", StringComparison.Ordinal))
            .Select(v => $"{v.Name.Replace('_', ' ')}: {v.State}")
            .ToList();
        return new CpuFacts(
            name.Trim(),
            vendor ?? (architecture.StartsWith("arm", StringComparison.Ordinal) ? "ARM" : "Unknown"),
            Math.Max(1, packages.Count),
            cores.Count > 0 ? cores.Count : threads,
            threads,
            microcode,
            maxKhz is > 0 ? maxKhz / 1000d : null,
            Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"),
            Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/scaling_driver"),
            Sys.Read("/sys/devices/system/cpu/cpu0/cpufreq/energy_performance_preference"),
            architecture,
            vulnerabilities);
    }

    /// <summary>Every "cpu" line of /proc/stat: the total first ("cpu"), then each processor ("cpu0"…).</summary>
    public static Dictionary<string, CpuTimes> ReadTimes()
    {
        var result = new Dictionary<string, CpuTimes>(StringComparer.Ordinal);
        foreach (var line in Sys.Lines("/proc/stat"))
        {
            if (!line.StartsWith("cpu", StringComparison.Ordinal)) break;
            if (ParseStat(line) is { } entry) result[entry.Name] = entry.Times;
        }
        return result;
    }

    public static (string Name, CpuTimes Times)? ParseStat(string line)
    {
        var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (f.Length < 8) return null;
        long N(int i) => i < f.Length && long.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        return (f[0], new CpuTimes(N(1), N(2), N(3), N(4), N(5), N(6), N(7), N(8)));
    }

    /// <summary>Busy share between two readings, in percent.</summary>
    public static double Usage(CpuTimes before, CpuTimes after)
    {
        var total = after.Total - before.Total;
        return total <= 0 ? 0 : Math.Clamp((after.Busy - before.Busy) * 100d / total, 0, 100);
    }

    public static (ulong Total, ulong Available, ulong SwapTotal, ulong SwapFree) ReadMemory()
    {
        ulong total = 0, available = 0, swapTotal = 0, swapFree = 0;
        foreach (var line in Sys.Lines("/proc/meminfo"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !ulong.TryParse(parts[1], out var kb)) continue;
            switch (parts[0])
            {
                case "MemTotal:": total = kb * 1024; break;
                case "MemAvailable:": available = kb * 1024; break;
                case "SwapTotal:": swapTotal = kb * 1024; break;
                case "SwapFree:": swapFree = kb * 1024; break;
            }
        }
        return (total, available, swapTotal, swapFree);
    }
}
