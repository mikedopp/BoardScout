using BoardScout.Models;

namespace BoardScout.Services;

public static class SuggestionEngine
{
    public static List<EfficiencySuggestion> Analyze(ScanManifest scan, DriverReport? report)
    {
        var suggestions = new List<EfficiencySuggestion>();
        AddMemorySuggestions(scan, suggestions);
        AddStorageSuggestions(scan, suggestions);
        AddDeviceSuggestions(scan, suggestions);
        AddFirmwareSuggestions(scan, suggestions);
        AddDriverSuggestions(scan, report, suggestions);

        if (suggestions.Count == 0)
        {
            suggestions.Add(new(
                SuggestionSeverity.Info,
                "No immediate efficiency issues detected",
                "BoardScout did not find a low-space volume, device error, memory-speed mismatch, or known driver update.",
                "Run Check Drivers periodically for vendor and OEM updates.",
                "System"));
        }

        return suggestions
            .OrderByDescending(s => s.Severity)
            .ThenBy(s => s.Category)
            .ToList();
    }

    private static void AddMemorySuggestions(ScanManifest scan, List<EfficiencySuggestion> output)
    {
        var first = scan.Memory.Slots.FirstOrDefault();
        if (first is not null && first.RatedMhz > 0 && first.SpeedMhz > 0 && first.SpeedMhz < first.RatedMhz * .9)
        {
            var improvement = Math.Round((first.RatedMhz / (double)first.SpeedMhz - 1) * 100);
            output.Add(new(
                SuggestionSeverity.Improvement,
                "Enable XMP/DOCP/EXPO for memory",
                $"Installed memory is rated for {first.RatedMhz} MT/s but is running at {first.SpeedMhz} MT/s.",
                $"Enable the memory profile in firmware setup. The theoretical memory bandwidth increase is about {improvement:0}%.",
                "Memory"));
        }

        if (scan.TotalMemoryGb < 16)
        {
            output.Add(new(
                SuggestionSeverity.Improvement,
                "Consider at least 16 GB of memory",
                $"This PC reports {scan.TotalMemoryGb:0.#} GB. Modern browsers and creative tools can pressure systems below 16 GB.",
                "Check matched DIMM support in the motherboard manual before upgrading.",
                "Memory"));
        }
    }

    private static void AddStorageSuggestions(ScanManifest scan, List<EfficiencySuggestion> output)
    {
        foreach (var volume in scan.Volumes.Where(v => v.SizeBytes > 0))
        {
            var freeGb = volume.FreeBytes / 1_073_741_824d;
            if (volume.UsedPercent >= 95)
            {
                output.Add(new(
                    SuggestionSeverity.Critical,
                    $"{volume.Letter} is {volume.UsedPercent:0}% full",
                    $"Only {freeGb:0.#} GB remains on {volume.DiskModel ?? volume.Label ?? "this volume"}. Very low free space can reduce update reliability and SSD performance.",
                    "Move large files, remove safe temporary data, or expand the volume.",
                    "Storage"));
            }
            else if (volume.UsedPercent >= 85)
            {
                output.Add(new(
                    SuggestionSeverity.Warning,
                    $"{volume.Letter} is nearing capacity",
                    $"{freeGb:0.#} GB remains ({volume.UsedPercent:0}% used).",
                    "Target at least 15% free space for frequently written SSDs and system volumes.",
                    "Storage"));
            }
        }
    }

    private static void AddDeviceSuggestions(ScanManifest scan, List<EfficiencySuggestion> output)
    {
        foreach (var device in scan.ProblemDevices.Where(d => d.ErrorCode != 0))
        {
            output.Add(new(
                SuggestionSeverity.Warning,
                $"Resolve device error {device.ErrorCode}: {device.Name}",
                string.IsNullOrWhiteSpace(device.Description) ? "Windows reports that this device is not working normally." : device.Description,
                "Open Device Manager, inspect the device status, and reinstall or update its driver if appropriate.",
                "Hardware"));
        }
    }

    private static void AddFirmwareSuggestions(ScanManifest scan, List<EfficiencySuggestion> output)
    {
        if (DateTime.TryParse(scan.SystemInfo.Bios.ReleaseDate, out var released) &&
            released < DateTime.Today.AddYears(-2))
        {
            output.Add(new(
                SuggestionSeverity.Info,
                $"Review BIOS updates for {scan.SystemInfo.Baseboard.Product}",
                $"Installed BIOS {scan.SystemInfo.Bios.Version} is dated {released:yyyy-MM-dd}.",
                "Use Check Drivers for an OEM advisory. Never flash firmware unless the update applies to the exact board and revision.",
                "Firmware"));
        }
    }

    private static void AddDriverSuggestions(
        ScanManifest scan,
        DriverReport? report,
        List<EfficiencySuggestion> output)
    {
        if (report is not null)
        {
            foreach (var result in report.Results.Where(r => r.Status == "update-available"))
            {
                output.Add(new(
                    SuggestionSeverity.Improvement,
                    $"Driver update available: {result.Model}",
                    $"Installed {result.Best.InstalledVersion ?? "unknown"}; latest {result.Best.LatestVersion ?? result.Best.LatestDate ?? "available"} from {result.Best.Source}.",
                    "Open Drivers and review the vendor link. BoardScout never installs updates automatically.",
                    "Drivers"));
            }
            return;
        }

        var stale = scan.Components.Count(component =>
        {
            var current = component.Current;
            if (current.DriverSource?.Equals("disk.inf", StringComparison.OrdinalIgnoreCase) == true) return false;
            if (current.DriverDate?.StartsWith("2006-06-", StringComparison.Ordinal) == true) return false;
            return DateTime.TryParse(current.DriverDate, out var date) && date < DateTime.Today.AddYears(-3);
        });

        if (stale > 0)
        {
            output.Add(new(
                SuggestionSeverity.Info,
                $"{stale} driver{(stale == 1 ? "" : "s")} may be old",
                "Age alone does not prove that a driver is obsolete, especially for stable inbox devices.",
                "Run Check Drivers to compare supported components against vendor, OEM, and Microsoft catalog sources.",
                "Drivers"));
        }
    }
}
