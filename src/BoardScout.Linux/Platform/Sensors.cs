using System.Globalization;

namespace BoardScout.Platform;

internal sealed record SensorValue(string Label, double Value);

/// <summary>One hwmon chip: the kernel driver's name, the device it belongs to, and its readings.</summary>
internal sealed record HwmonChip(string Name, string? DevicePath, IReadOnlyList<SensorValue> Temps, IReadOnlyList<SensorValue> Fans,
    IReadOnlyList<SensorValue> PowerWatts);

/// <summary>Temperatures, fans, and power from /sys/class/hwmon (every user may read them; no driver to install).</summary>
internal static class Sensors
{
    public static List<HwmonChip> Read()
    {
        var chips = new List<HwmonChip>();
        foreach (var hwmon in Sys.Children("/sys/class/hwmon"))
        {
            var path = $"/sys/class/hwmon/{hwmon}";
            var name = Sys.Read(Path.Combine(path, "name"));
            if (name is null) continue;
            var temps = new List<SensorValue>();
            var fans = new List<SensorValue>();
            var power = new List<SensorValue>();
            foreach (var file in Sys.Children(path))
            {
                if (!file.EndsWith("_input", StringComparison.Ordinal) && !file.EndsWith("_average", StringComparison.Ordinal)) continue;
                var stem = file[..file.LastIndexOf('_')];
                var label = Sys.Read(Path.Combine(path, stem + "_label")) ?? stem;
                if (!double.TryParse(Sys.Read(Path.Combine(path, file)), NumberStyles.Float, CultureInfo.InvariantCulture, out var raw)) continue;
                if (file.StartsWith("temp", StringComparison.Ordinal) && file.EndsWith("_input", StringComparison.Ordinal))
                {
                    var celsius = raw / 1000;
                    if (celsius is > -40 and < 150) temps.Add(new SensorValue(label, celsius));
                }
                else if (file.StartsWith("fan", StringComparison.Ordinal) && file.EndsWith("_input", StringComparison.Ordinal))
                {
                    if (raw is >= 0 and < 30000) fans.Add(new SensorValue(label, raw));
                }
                else if (file.StartsWith("power", StringComparison.Ordinal))
                {
                    // Microwatts; amdgpu reports the whole card's draw here.
                    if (power.All(p => p.Label != label)) power.Add(new SensorValue(label, raw / 1_000_000));
                }
            }
            chips.Add(new HwmonChip(name, Sys.Real(Path.Combine(path, "device")), temps, fans, power));
        }
        return chips;
    }

    /// <summary>The processor's own temperature: package (Intel), Tctl/Tdie (AMD), or the SoC sensor on Arm boards.</summary>
    public static double? Cpu(IReadOnlyList<HwmonChip> chips)
    {
        var intel = chips.Where(c => c.Name == "coretemp").SelectMany(c => c.Temps).Where(t => t.Label.StartsWith("Package", StringComparison.Ordinal)).ToList();
        if (intel.Count > 0) return intel.Max(t => t.Value);
        foreach (var name in new[] { "k10temp", "zenpower" })
        {
            var amd = chips.Where(c => c.Name == name).SelectMany(c => c.Temps).ToList();
            var best = amd.FirstOrDefault(t => t.Label == "Tdie") ?? amd.FirstOrDefault(t => t.Label == "Tctl") ?? amd.FirstOrDefault();
            if (best is not null) return best.Value;
        }
        var soc = chips.FirstOrDefault(c => c.Name is "cpu_thermal" or "soc_thermal" or "cpu-thermal" or "x86_pkg_temp")?.Temps.FirstOrDefault();
        if (soc is not null) return soc.Value;
        return chips.Where(c => c.Name == "coretemp").SelectMany(c => c.Temps).Select(t => (double?)t.Value).Max();
    }

    /// <summary>A graphics card's temperature: amdgpu "edge", nouveau and radeon "temp1".</summary>
    public static double? Gpu(IReadOnlyList<HwmonChip> chips, string? pciAddress = null)
    {
        foreach (var chip in chips.Where(c => c.Name is "amdgpu" or "nouveau" or "radeon" or "xe" or "i915"))
        {
            if (pciAddress is not null && chip.DevicePath?.EndsWith(pciAddress, StringComparison.Ordinal) != true) continue;
            var temp = chip.Temps.FirstOrDefault(t => t.Label == "edge") ?? chip.Temps.FirstOrDefault();
            if (temp is not null) return temp.Value;
        }
        return null;
    }

    /// <summary>The graphics card's power draw in watts, when its driver reports it.</summary>
    public static double? GpuWatts(IReadOnlyList<HwmonChip> chips) =>
        chips.Where(c => c.Name is "amdgpu" or "nouveau" or "xe").SelectMany(c => c.PowerWatts).Select(p => (double?)p.Value).FirstOrDefault(v => v > 0);

    /// <summary>Intel chipsets (PCH) report their own temperature; most others don't.</summary>
    public static double? Chipset(IReadOnlyList<HwmonChip> chips) =>
        chips.FirstOrDefault(c => c.Name.StartsWith("pch_", StringComparison.Ordinal))?.Temps.FirstOrDefault()?.Value;

    /// <summary>Drive temperatures keyed by the drive's device path: NVMe "Composite", and SATA drives with the drivetemp module.</summary>
    public static Dictionary<string, double> Drives(IReadOnlyList<HwmonChip> chips)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var chip in chips.Where(c => c.Name is "nvme" or "drivetemp" && c.DevicePath is not null))
        {
            var temp = chip.Temps.FirstOrDefault(t => t.Label == "Composite") ?? chip.Temps.FirstOrDefault();
            if (temp is not null) result[chip.DevicePath!] = temp.Value;
        }
        return result;
    }

    /// <summary>Fans with a name: "CPU fan", "fan2", "Processor Fan" (dell_smm).</summary>
    public static List<(string Name, int Rpm)> Fans(IReadOnlyList<HwmonChip> chips) =>
        chips.SelectMany(c => c.Fans.Select(f => (Name: f.Label.StartsWith("fan", StringComparison.Ordinal) ? $"{c.Name} {f.Label}" : f.Label, Rpm: (int)f.Value)))
            .ToList();
}
