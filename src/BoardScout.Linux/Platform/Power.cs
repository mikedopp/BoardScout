using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using BoardScout.Models;
using BoardScout.Services;

namespace BoardScout.Platform;

/// <summary>One energy counter in microjoules: a powercap zone (Key is its folder) or an amd_energy input. Range is where it wraps to zero (0: it doesn't).</summary>
internal readonly record struct EnergyCounter(string Key, string Device, long Microjoules, long Range);

/// <summary>
/// The processor's energy counters at one moment (At is a Stopwatch timestamp). Present: the kernel publishes some.
/// Locked: some exist that only root may read (Linux closed them to other users in 2020, after the PLATYPUS power side channel).
/// </summary>
internal sealed record EnergySample(long At, IReadOnlyList<EnergyCounter> Counters, bool Present, bool Locked);

/// <summary>What the power breakdown is built from: the readings, each drive's busy % by disk number, and why the processor has no reading when it has none.</summary>
internal sealed record PowerSample(IReadOnlyList<PowerReading> Readings, IReadOnlyDictionary<int, double>? Busy, string? NoCpuReading);

/// <summary>
/// Power readings: the processor's energy counters (/sys/class/powercap, or the amd_energy hwmon driver), the graphics
/// card's driver, and a Corsair power supply on the kernel's corsair-psu driver. The NVMe power table is the one place
/// BoardScout talks to a device here: an Identify command (read-only) through the drive's controller, which only root may open.
/// </summary>
internal static partial class Power
{
    public static EnergySample ReadEnergy(string powercap = "/sys/class/powercap", string hwmon = "/sys/class/hwmon")
    {
        var at = Stopwatch.GetTimestamp();
        var raw = new List<RawCounter>();
        foreach (var zone in Sys.Children(powercap))
        {
            var file = Path.Combine(powercap, zone, "energy_uj");
            if (!RaplZone().IsMatch(zone) || !File.Exists(file)) continue;
            raw.Add(new RawCounter(zone, Sys.Read(Path.Combine(powercap, zone, "name")), Sys.Long(file),
                Sys.Long(Path.Combine(powercap, zone, "max_energy_range_uj")) ?? 0, Hwmon: false));
        }
        // amd_energy only matters when powercap has no readable package.
        if (!raw.Any(r => Device(r) == "CPU" && r.Microjoules is not null))
            foreach (var chip in Sys.Children(hwmon))
            {
                var path = Path.Combine(hwmon, chip);
                if (Sys.Read(Path.Combine(path, "name")) != "amd_energy") continue;
                foreach (var file in Sys.Children(path).Where(f => f.StartsWith("energy", StringComparison.Ordinal) && f.EndsWith("_input", StringComparison.Ordinal)))
                    raw.Add(new RawCounter($"{chip}/{file}", Sys.Read(Path.Combine(path, file[..^"_input".Length] + "_label")), Sys.Long(Path.Combine(path, file)), 0, Hwmon: true));
            }
        return FromCounters(at, raw);
    }

    /// <summary>
    /// A powercap zone (Key "intel-rapl:0:0", Name its name file) or an amd_energy input (Key "hwmon3/energy1_input", Name
    /// its label), as read. Microjoules is null when the file exists but can't be read.
    /// </summary>
    internal readonly record struct RawCounter(string Key, string? Name, long? Microjoules, long Range, bool Hwmon);

    /// <summary>
    /// What a counter measures: "CPU" (a package or socket), "CPU cores", "CPU graphics" (Intel's uncore domain), "Memory"
    /// (the DRAM domain), or null. intel-rapl:0 is a package and intel-rapl:0:0 one of its domains, on AMD processors too;
    /// intel-rapl-mmio repeats the package through another door, and psys (a laptop's whole platform) isn't the processor.
    /// amd_energy (Zen) labels its inputs per socket ("Esocket0") and per core ("Ecore003").
    /// </summary>
    internal static string? Device(RawCounter counter)
    {
        if (counter.Hwmon)
            return counter.Name?.StartsWith("Esocket", StringComparison.Ordinal) == true ? "CPU"
                : counter.Name?.StartsWith("Ecore", StringComparison.Ordinal) == true ? "CPU cores"
                : null;
        if (!RaplZone().IsMatch(counter.Key)) return null;
        return counter.Name switch
        {
            { } name when name.StartsWith("package", StringComparison.Ordinal) => "CPU",
            "core" => "CPU cores",
            "uncore" => "CPU graphics",
            "dram" => "Memory",
            _ => null
        };
    }

    internal static EnergySample FromCounters(long at, IEnumerable<RawCounter> raw)
    {
        var used = raw.Select(r => (Raw: r, Device: Device(r))).Where(x => x.Device is not null).ToList();
        if (used.Any(x => !x.Raw.Hwmon && x.Device == "CPU" && x.Raw.Microjoules is not null)) used.RemoveAll(x => x.Raw.Hwmon);
        var counters = used.Where(x => x.Raw.Microjoules is not null)
            .Select(x => new EnergyCounter(x.Raw.Key, x.Device!, x.Raw.Microjoules!.Value, x.Raw.Range)).ToList();
        return new EnergySample(at, counters, used.Count > 0, used.Any(x => x.Raw.Microjoules is null));
    }

    /// <summary>Watts per device between two samples: the energy each counter added over the time between them (two sockets add up).</summary>
    public static List<PowerReading> Watts(EnergySample before, EnergySample after) =>
        Watts(before, after, Stopwatch.GetElapsedTime(before.At, after.At).TotalSeconds);

    internal static List<PowerReading> Watts(EnergySample before, EnergySample after, double seconds)
    {
        var result = new List<PowerReading>();
        if (seconds < 0.05) return result;
        var old = before.Counters.ToDictionary(c => c.Key);
        foreach (var group in after.Counters.Where(c => old.ContainsKey(c.Key)).GroupBy(c => c.Device))
        {
            var joules = group.Sum(c => Delta(old[c.Key].Microjoules, c.Microjoules, c.Range)) / 1e6;
            result.Add(new PowerReading(group.Key, Math.Round(joules / seconds, 1)));
        }
        return result;
    }

    /// <summary>Microjoules between two readings of a counter that wraps to zero at range (0: a drop means it was reset, so nothing is counted).</summary>
    internal static long Delta(long before, long after, long range) =>
        after >= before ? after - before : range > before ? range - before + after : 0;

    /// <summary>
    /// Everything the breakdown reads: the processor's energy counters between two samples (no earlier sample: none yet),
    /// a graphics card from its driver (amdgpu, nouveau, xe), and a power supply on corsair-psu.
    /// </summary>
    public static List<PowerReading> Readings(EnergySample? before, EnergySample after, IReadOnlyList<HwmonChip> chips)
    {
        var readings = before is null ? new List<PowerReading>() : Watts(before, after);
        // An APU's amdgpu reports the whole chip's power ("slowPPT", "fastPPT"): that's the processor package, not a card.
        var gpu = chips.Where(c => c.Name is "amdgpu" or "nouveau" or "xe").SelectMany(c => c.PowerWatts)
            .Where(p => p.Label is not ("slowPPT" or "fastPPT")).Select(p => (double?)p.Value).FirstOrDefault(v => v > 0);
        if (gpu is { } card) readings.Add(new PowerReading("GPU", Math.Round(card, 1)));
        readings.AddRange(Supply(chips));
        return readings;
    }

    /// <summary>A Corsair HXi or RMi power supply on the kernel's corsair-psu driver: its total output ("power total") and each rail ("power +12v").</summary>
    internal static IEnumerable<PowerReading> Supply(IReadOnlyList<HwmonChip> chips)
    {
        foreach (var reading in chips.Where(c => c.Name == "corsairpsu").SelectMany(c => c.PowerWatts))
        {
            var label = reading.Label.ToLowerInvariant();
            var device = label.Contains("total") ? "PSU output"
                : label.Contains("12v") ? "PSU +12V"
                : label.Contains("3.3v") || label.Contains("3v3") ? "PSU +3.3V"
                : label.Contains("5v") ? "PSU +5V"
                : null;
            if (device is not null) yield return new PowerReading(device, Math.Round(reading.Value, 1), "the Corsair power supply");
        }
    }

    /// <summary>Why the processor has no reading, or null when it has one.</summary>
    public static string? NoCpuReading(EnergySample sample, IReadOnlyList<PowerReading> readings, bool root)
    {
        if (readings.Any(r => r.Device == "CPU")) return null;
        if (sample.Counters.Any(c => c.Device == "CPU")) return "Measuring: the processor's energy counters need two readings a moment apart.";
        if (sample.Locked)
            return root
                ? "The processor's energy counters can't be read here, even as root (a kernel lockdown or security policy may block them)."
                : "Linux lets only root read the processor's energy counters (/sys/class/powercap). Start BoardScout with sudo to see it.";
        return "The kernel publishes no energy counters for this processor (/sys/class/powercap is empty; the intel_rapl_msr module provides them on Intel and AMD).";
    }

    // ---- NVMe power states ------------------------------------------------------------------------------

    /// <summary>struct nvme_passthru_cmd from linux/nvme_ioctl.h: 72 bytes.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NvmeAdminCommand
    {
        public byte Opcode;
        public byte Flags;
        public ushort Reserved;
        public uint NamespaceId;
        public uint Cdw2, Cdw3;
        public ulong Metadata;
        public ulong Address;
        public uint MetadataLength;
        public uint DataLength;
        public uint Cdw10, Cdw11, Cdw12, Cdw13, Cdw14, Cdw15;
        public uint TimeoutMs;
        public uint Result;
    }

    /// <summary>NVME_IOCTL_ADMIN_CMD: _IOWR('N', 0x41, struct nvme_admin_cmd).</summary>
    internal const uint NvmeIoctlAdminCommand = (3u << 30) | (72u << 16) | ('N' << 8) | 0x41;

    /// <summary>
    /// An NVMe drive's power states from Identify Controller, asked through its controller (/dev/nvme0). The controller is
    /// root's alone on most systems, so other users get null and the drive falls back to typical figures.
    /// </summary>
    public static unsafe DrivePowerTable? NvmePowerTable(string controller)
    {
        if (!OperatingSystem.IsLinux() || !NvmeController().IsMatch(controller)) return null;
        var fd = -1;
        try
        {
            fd = open("/dev/" + controller, 0); // O_RDONLY: Identify only reads
            if (fd < 0) return null;
            var data = new byte[4096];
            fixed (byte* buffer = data)
            {
                var command = new NvmeAdminCommand
                {
                    Opcode = 0x06,                       // Identify
                    Address = (ulong)(nint)buffer,
                    DataLength = (uint)data.Length,
                    Cdw10 = 1,                           // CNS 1: the controller
                    TimeoutMs = 5000
                };
                if (ioctl(fd, NvmeIoctlAdminCommand, ref command) != 0) return null;
            }
            return DrivePowerTable.FromIdentify(data);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        finally
        {
            if (fd >= 0) close(fd);
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "ioctl")]
    private static partial int ioctl(int fd, nuint request, ref NvmeAdminCommand command);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int close(int fd);

    [GeneratedRegex(@"^intel-rapl:\d+(:\d+)?$")]
    private static partial Regex RaplZone();

    [GeneratedRegex(@"^nvme\d+$")]
    private static partial Regex NvmeController();
}
