using System.Diagnostics;
using System.Management;
using System.Text.Json;
using BoardScout.Models;
using Microsoft.Win32;

namespace BoardScout.Services;

internal static class SystemInfoService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static async Task<string> GatherJsonAsync(ScanManifest? scan = null)
    {
        var osTask = Task.Run(GatherOs);
        var patchTask = Task.Run(GatherPatches);
        var softwareTask = Task.Run(GatherSoftware);
        var dotnetTask = Task.Run(GatherDotNet);
        var taskTask = Task.Run(GatherScheduledTasks);

        await Task.WhenAll(osTask, patchTask, softwareTask, dotnetTask, taskTask);

        var os = osTask.Result;
        var patches = patchTask.Result;
        Analyze(os, patches, softwareTask.Result, dotnetTask.Result, taskTask.Result);

        var build = BuildFromScan(scan);

        return JsonSerializer.Serialize(new
        {
            os,
            build,
            dotnet = dotnetTask.Result,
            patches,
            software = softwareTask.Result,
            tasks = taskTask.Result
        }, JsonOpts);
    }

    private static Dictionary<string, object> GatherOs()
    {
        var result = new Dictionary<string, object>
        {
            ["caption"] = "", ["version"] = "", ["build"] = "",
            ["arch"] = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit",
            ["installDate"] = "", ["lastBoot"] = "", ["uptime"] = "",
            ["supportStatus"] = "", ["verdict"] = "", ["verdictEmoji"] = "computer",
            ["traits"] = new List<string>(), ["patchHealth"] = "",
            ["recommendations"] = new List<string>()
        };
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                result["caption"] = obj["Caption"]?.ToString()?.Trim() ?? "";
                result["version"] = obj["Version"]?.ToString() ?? "";
                result["build"] = obj["BuildNumber"]?.ToString() ?? "";
                result["arch"] = obj["OSArchitecture"]?.ToString() ?? "";
                if (obj["InstallDate"] is string iStr)
                    result["installDate"] = WmiDate(iStr);
                if (obj["LastBootUpTime"] is string bStr)
                {
                    var dt = ManagementDateTimeConverter.ToDateTime(bStr);
                    result["lastBoot"] = dt.ToString("yyyy-MM-dd HH:mm");
                    var up = DateTime.Now - dt;
                    result["uptime"] = up.Days > 0 ? $"{up.Days}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";
                }
            }
        }
        catch { }
        return result;
    }

    private static List<Dictionary<string, string>> GatherPatches()
    {
        var list = new List<Dictionary<string, string>>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering");
            foreach (ManagementObject obj in s.Get())
            {
                var id = obj["HotFixID"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(id) || id == "File 1") continue;
                var by = obj["InstalledBy"]?.ToString() ?? "";
                var idx = by.LastIndexOf('\\');
                if (idx >= 0) by = by[(idx + 1)..];
                list.Add(new()
                {
                    ["id"] = id,
                    ["description"] = obj["Description"]?.ToString() ?? "",
                    ["installedOn"] = obj["InstalledOn"]?.ToString() ?? "",
                    ["installedBy"] = by
                });
            }
        }
        catch { }
        return list.OrderByDescending(p => p["installedOn"]).ToList();
    }

    private static List<Dictionary<string, string>> GatherSoftware()
    {
        var map = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        ReadSoftwareKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ReadSoftwareKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ReadSoftwareKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        return map.Values.OrderBy(s => s["name"], StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadSoftwareKey(RegistryKey root, string path, Dictionary<string, Dictionary<string, string>> map)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var sk = key.OpenSubKey(sub);
                    if (sk is null) continue;
                    var name = sk.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (sk.GetValue("SystemComponent") is int sc && sc == 1) continue;
                    if (map.ContainsKey(name)) continue;
                    var sizeKb = sk.GetValue("EstimatedSize");
                    map[name] = new()
                    {
                        ["name"] = name,
                        ["version"] = sk.GetValue("DisplayVersion")?.ToString() ?? "",
                        ["publisher"] = sk.GetValue("Publisher")?.ToString() ?? "",
                        ["installDate"] = FmtDate(sk.GetValue("InstallDate")?.ToString() ?? ""),
                        ["size"] = sizeKb is int kb ? FmtKb(kb) : ""
                    };
                }
                catch { }
            }
        }
        catch { }
    }

    private static List<Dictionary<string, string>> GatherDotNet()
    {
        var list = new List<Dictionary<string, string>>
        {
            new()
            {
                ["name"] = "CLR (this process)",
                ["version"] = Environment.Version.ToString(),
                ["path"] = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory()
            }
        };
        try
        {
            var psi = new ProcessStartInfo("dotnet", "--list-runtimes")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                var output = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(5000);
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var bi = line.IndexOf('[');
                    if (bi < 0) continue;
                    var nv = line[..bi].Trim();
                    var p = line[(bi + 1)..].TrimEnd(']', ' ');
                    var si = nv.LastIndexOf(' ');
                    if (si < 0) continue;
                    list.Add(new() { ["name"] = nv[..si], ["version"] = nv[(si + 1)..], ["path"] = p });
                }
            }
        }
        catch { }
        try
        {
            using var ndp = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            if (ndp is not null)
            {
                var rel = ndp.GetValue("Release") as int?;
                var ver = ndp.GetValue("Version")?.ToString() ?? "";
                if (rel.HasValue)
                {
                    var friendly = rel.Value switch
                    {
                        >= 533320 => "4.8.1", >= 528040 => "4.8", >= 461808 => "4.7.2",
                        >= 461308 => "4.7.1", >= 460798 => "4.7", >= 394802 => "4.6.2", _ => ver
                    };
                    list.Add(new() { ["name"] = ".NET Framework", ["version"] = friendly, ["path"] = "GAC" });
                }
            }
        }
        catch { }
        return list;
    }

    private static List<Dictionary<string, string>> GatherScheduledTasks()
    {
        var list = new List<Dictionary<string, string>>();
        try
        {
            var psi = new ProcessStartInfo("schtasks", "/query /fo CSV /nh")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi);
            if (proc is null) return list;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = CsvSplit(line);
                if (parts.Length < 3) continue;
                var fullName = parts[0].Trim('"');
                if (string.IsNullOrWhiteSpace(fullName) || fullName == "TaskName") continue;
                var category = fullName.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ? "Windows"
                    : fullName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ? "Microsoft" : "User";
                var shortName = fullName.Contains('\\') ? fullName[(fullName.LastIndexOf('\\') + 1)..] : fullName;
                list.Add(new()
                {
                    ["name"] = shortName,
                    ["status"] = parts[2].Trim('"'),
                    ["category"] = category,
                    ["nextRun"] = parts[1].Trim('"') is "N/A" ? "" : parts[1].Trim('"'),
                    ["author"] = category
                });
            }
        }
        catch { }
        return list;
    }

    private static Dictionary<string, object> BuildFromScan(ScanManifest? scan)
    {
        var build = new Dictionary<string, object>
        {
            ["cpu"] = "", ["cpuDetail"] = "", ["memory"] = "", ["gpu"] = "",
            ["motherboard"] = "", ["bios"] = "", ["tpm"] = "", ["secureBoot"] = "",
            ["disks"] = new List<Dictionary<string, object>>()
        };

        if (scan is not null)
        {
            var cpu = scan.Cpu;
            build["cpu"] = cpu.Name.Replace(" with Radeon Graphics", "").Replace("AMD ", "").Replace("Intel(R) ", "");
            build["cpuDetail"] = $"{cpu.Cores} cores / {cpu.Threads} threads";
            build["memory"] = $"{scan.TotalMemoryGb:0.#} GB ({scan.Memory.Populated}/{scan.Memory.TotalSlots} slots)";
            var gpu = scan.Components.FirstOrDefault(c => c.Category == "gpu");
            build["gpu"] = gpu?.Model ?? "Integrated / not detected";
            build["motherboard"] = $"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product}".Trim();
            build["bios"] = $"{scan.SystemInfo.Bios.Version} ({scan.SystemInfo.Bios.ReleaseDate})";

            var disks = new List<Dictionary<string, object>>();
            foreach (var vol in scan.Volumes)
            {
                disks.Add(new()
                {
                    ["model"] = vol.DiskModel ?? vol.Label ?? $"Volume {vol.Letter}",
                    ["size"] = FmtBytes(vol.SizeBytes),
                    ["bus"] = vol.BusType ?? "Unknown",
                    ["free"] = FmtBytes(vol.FreeBytes),
                    ["usedPercent"] = Math.Round(vol.UsedPercent, 1)
                });
            }
            build["disks"] = disks;
        }
        else
        {
            FillBuildFromWmi(build);
        }

        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            build["secureBoot"] = k?.GetValue("UEFISecureBootEnabled") is int v && v == 1 ? "Enabled" : "Disabled";
        }
        catch { build["secureBoot"] = "N/A"; }

        try
        {
            using var s = new ManagementObjectSearcher(@"root\cimv2\Security\MicrosoftTpm", "SELECT SpecVersion FROM Win32_Tpm");
            foreach (ManagementObject o in s.Get())
                build["tpm"] = $"TPM {o["SpecVersion"]?.ToString()?.Split(',')[0]}";
        }
        catch { build["tpm"] = "Not detected"; }

        return build;
    }

    private static void FillBuildFromWmi(Dictionary<string, object> build)
    {
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject o in s.Get())
            {
                build["cpu"] = o["Name"]?.ToString()?.Trim() ?? "";
                build["cpuDetail"] = $"{o["NumberOfCores"]}C / {o["NumberOfLogicalProcessors"]}T";
            }
        }
        catch { }
        try
        {
            long total = 0;
            using var s = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject o in s.Get())
                if (o["Capacity"] is ulong c) total += (long)c;
            build["memory"] = $"{total / 1_073_741_824.0:0.#} GB";
        }
        catch { }
        try
        {
            var gpus = new List<string>();
            using var s = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject o in s.Get())
            {
                var n = o["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(n)) gpus.Add(n);
            }
            build["gpu"] = string.Join(" + ", gpus);
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject o in s.Get())
                build["motherboard"] = $"{o["Manufacturer"]} {o["Product"]}".Trim();
        }
        catch { }
        try
        {
            using var s = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject o in s.Get())
            {
                var v = o["SMBIOSBIOSVersion"]?.ToString() ?? "";
                var d = o["ReleaseDate"] is string ds ? WmiDate(ds) : "";
                build["bios"] = string.IsNullOrEmpty(d) ? v : $"{v} ({d})";
            }
        }
        catch { }
    }

    private static void Analyze(
        Dictionary<string, object> os,
        List<Dictionary<string, string>> patches,
        List<Dictionary<string, string>> software,
        List<Dictionary<string, string>> dotnet,
        List<Dictionary<string, string>> tasks)
    {
        var traits = new List<string>();
        var recs = new List<string>();
        var caption = os["caption"]?.ToString() ?? "";
        var buildStr = os["build"]?.ToString() ?? "";
        int.TryParse(buildStr, out var build);

        var isWin11 = caption.Contains("Windows 11", StringComparison.OrdinalIgnoreCase);
        var isCurrent = build >= 26100;

        if (isWin11)
            os["supportStatus"] = isCurrent ? "Supported — current release"
                : build >= 22631 ? "Supported — 23H2" : "Nearing end of servicing";
        else if (caption.Contains("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            os["supportStatus"] = "End of support October 2025 — ESU available";
            recs.Add("Windows 10 mainstream support has ended. Plan for Windows 11 or purchase ESU.");
        }
        else
            os["supportStatus"] = "Check vendor documentation";

        var newest = patches
            .Select(p => { DateTime.TryParse(p["installedOn"], out var d); return d; })
            .Where(d => d > DateTime.MinValue)
            .DefaultIfEmpty().Max();
        var patchAge = newest > DateTime.MinValue ? (int)(DateTime.Now - newest).TotalDays : -1;

        os["patchHealth"] = patchAge switch
        {
            < 0 => "No patch dates available",
            <= 30 => $"Current — last patch {patchAge} days ago",
            <= 60 => $"Slightly behind — last patch {patchAge} days ago",
            <= 90 => $"Behind — {patchAge} days since last patch",
            _ => $"Significantly behind — {patchAge} days without patches"
        };

        var patchedRecently = patchAge >= 0 && patchAge <= 30;

        bool Has(string term) => software.Any(s => s["name"].Contains(term, StringComparison.OrdinalIgnoreCase));
        var hasDev = Has("Visual Studio") || Has("VS Code") || Has("JetBrains") || Has("Git") || Has("Node.js") || Has("Python");
        var hasCreative = Has("Adobe") || Has("DaVinci") || Has("Blender") || Has("OBS");
        var hasGaming = Has("Steam") || Has("Epic Games") || Has("Battle.net");

        if (hasDev && isWin11 && isCurrent && patchedRecently)
        { os["verdict"] = "The Power Developer"; os["verdictEmoji"] = "rocket"; }
        else if (hasDev && patchedRecently)
        { os["verdict"] = "The Working Dev"; os["verdictEmoji"] = "keyboard"; }
        else if (!isWin11 && patchedRecently)
        { os["verdict"] = "The Reliable Holdout"; os["verdictEmoji"] = "shield"; }
        else if (!isWin11 && patchAge > 90)
        { os["verdict"] = "The If-It-Ain't-Broke"; os["verdictEmoji"] = "wrench"; }
        else if (isWin11 && isCurrent && patchedRecently)
        { os["verdict"] = "The Early Adopter"; os["verdictEmoji"] = "sparkles"; }
        else if (isWin11 && !isCurrent)
        { os["verdict"] = "The Cautious Upgrader"; os["verdictEmoji"] = "hourglass"; }
        else if (hasGaming && hasCreative)
        { os["verdict"] = "The Creative Gamer"; os["verdictEmoji"] = "art"; }
        else if (hasGaming)
        { os["verdict"] = "The Battle Station"; os["verdictEmoji"] = "joystick"; }
        else
        { os["verdict"] = "The Everyday Driver"; os["verdictEmoji"] = "computer"; }

        if (hasDev) traits.Add("Developer workstation");
        if (hasCreative) traits.Add("Creative tools installed");
        if (hasGaming) traits.Add("Gaming-ready");
        var sdks = dotnet.Count(d => d["name"].Contains("SDK", StringComparison.OrdinalIgnoreCase));
        if (sdks > 0) traits.Add($"{sdks} .NET SDK(s)");
        else if (dotnet.Count > 3) traits.Add($"{dotnet.Count} .NET runtimes");
        if (patchedRecently) traits.Add("Patch discipline: strong");
        else if (patchAge > 60) { traits.Add("Patch discipline: needs attention"); recs.Add("Install pending Windows updates."); }
        if (isWin11 && isCurrent) traits.Add("Running latest OS build");
        else if (!isWin11) traits.Add("Windows 10 holdout");
        var userTasks = tasks.Count(t => t["category"] == "User");
        if (userTasks > 20) traits.Add("Busy task scheduler");
        if (software.Count > 100) traits.Add($"{software.Count} programs installed");
        else if (software.Count < 30) traits.Add("Clean install profile");

        os["traits"] = traits;
        os["recommendations"] = recs;
    }

    private static string WmiDate(string s)
    {
        if (string.IsNullOrWhiteSpace(s) || s.Length < 14) return s;
        try { return ManagementDateTimeConverter.ToDateTime(s).ToString("yyyy-MM-dd HH:mm"); }
        catch { return s; }
    }

    private static string FmtDate(string s) => s.Length == 8 ? $"{s[..4]}-{s[4..6]}-{s[6..8]}" : s;

    private static string FmtKb(int kb) =>
        kb >= 1_048_576 ? $"{kb / 1_048_576.0:0.#} GB" :
        kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

    private static string FmtBytes(long bytes)
    {
        var v = (double)Math.Max(0, bytes);
        string[] u = ["B", "KB", "MB", "GB", "TB"];
        var i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.#} {u[i]}";
    }

    private static string[] CsvSplit(string line)
    {
        var parts = new List<string>();
        var q = false;
        var cur = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { q = !q; continue; }
            if (ch == ',' && !q) { parts.Add(cur.ToString()); cur.Clear(); continue; }
            cur.Append(ch);
        }
        parts.Add(cur.ToString());
        return parts.ToArray();
    }
}
