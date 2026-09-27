using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Models;
using Microsoft.Win32;

namespace BoardScout.Services;

/// <summary>
/// Gathers the System tab: OS, lifecycle, patches, software, .NET, scheduled tasks, and a quick
/// Windows 11 readiness check. Everything runs on the thread pool; nothing here needs elevation.
/// </summary>
internal static class SystemInfoService
{
    // From Microsoft's lifecycle pages (Windows 11 Home and Pro / Enterprise and Education),
    // checked 2026-09-26. Dates are the last day of servicing.
    private sealed record Windows11Release(int Build, string Version, DateTime HomeProEnd, DateTime EnterpriseEnd);

    private static readonly Windows11Release[] Windows11Releases =
    [
        new(22000, "21H2", new(2023, 10, 10), new(2024, 10, 8)),
        new(22621, "22H2", new(2024, 10, 8), new(2025, 10, 14)),
        new(22631, "23H2", new(2025, 11, 11), new(2026, 11, 10)),
        new(26100, "24H2", new(2026, 10, 13), new(2027, 10, 12)),
        new(26200, "25H2", new(2027, 10, 12), new(2028, 10, 10)),
        new(28000, "26H1", new(2028, 3, 14), new(2029, 3, 13)) // new silicon only, not a feature update
    ];

    private const int LatestGeneralWindows11Build = 26200;
    private static readonly DateTime Windows10SupportEnded = new(2025, 10, 14);
    private static readonly DateTime Windows10ConsumerEsuEnds = new(2027, 10, 12); // microsoft.com/windows/extended-security-updates

    public static Task<string> GatherJsonAsync(ScanManifest? scan) => Task.Run(async () =>
    {
        var clock = Stopwatch.StartNew();
        var osTask = Task.Run(GatherOs);
        var patchTask = Task.Run(GatherPatches);
        var softwareTask = Task.Run(GatherSoftware);
        var dotnetTask = Task.Run(GatherDotNet);
        var taskTask = Task.Run(GatherScheduledTasks);
        var build = BuildSummaryFor(scan);

        await Task.WhenAll(osTask, patchTask, softwareTask, dotnetTask, taskTask).ConfigureAwait(false);

        var snapshot = new SystemInfoSnapshot
        {
            Os = osTask.Result,
            Build = build,
            DotNet = dotnetTask.Result,
            Patches = patchTask.Result,
            Software = softwareTask.Result,
            Tasks = taskTask.Result
        };
        Analyze(snapshot);
        snapshot.GatheredMs = clock.ElapsedMilliseconds;
        return JsonSerializer.Serialize(snapshot, BoardScoutJson.Default.SystemInfoSnapshot);
    });

    /// <summary>"Windows 10 Pro 22H2 · build 19045.7725" — Environment.OSVersion omits the edition and patch level.</summary>
    public static string WindowsDescription()
    {
        try
        {
            using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var product = current?.GetValue("ProductName") as string ?? "Windows";
            int.TryParse(current?.GetValue("CurrentBuild") as string, out var build);
            if (build >= 22000) product = product.Replace("Windows 10", "Windows 11"); // ProductName was never updated for 11
            var display = current?.GetValue("DisplayVersion") as string;
            var ubr = current?.GetValue("UBR") is int revision ? $".{revision}" : "";
            return $"{product}{(string.IsNullOrEmpty(display) ? "" : " " + display)} · build {build}{ubr}";
        }
        catch
        {
            return $"Windows {Environment.OSVersion.Version}";
        }
    }

    private static OsAnalysisInfo GatherOs()
    {
        var os = new OsAnalysisInfo { Architecture = Environment.Is64BitOperatingSystem ? "64-bit" : "32-bit" };
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Caption, Version, BuildNumber, OSArchitecture, InstallDate, LastBootUpTime FROM Win32_OperatingSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                os.Caption = obj["Caption"]?.ToString()?.Trim() ?? "";
                os.Version = obj["Version"]?.ToString() ?? "";
                if (int.TryParse(obj["BuildNumber"]?.ToString(), out var buildNumber)) os.BuildNumber = buildNumber;
                os.Architecture = obj["OSArchitecture"]?.ToString() ?? os.Architecture;
                if (obj["InstallDate"] is string installed)
                    os.InstallDate = WmiDate(installed);
                if (obj["LastBootUpTime"] is string booted)
                {
                    var bootTime = ManagementDateTimeConverter.ToDateTime(booted);
                    os.LastBoot = bootTime.ToString("yyyy-MM-dd HH:mm");
                    var up = DateTime.Now - bootTime;
                    os.Uptime = up.Days > 0 ? $"{up.Days}d {up.Hours}h {up.Minutes}m" : $"{up.Hours}h {up.Minutes}m";
                }
            }
        }
        catch { }

        try
        {
            // Registry has what WMI lacks: the marketing version (22H2) and the update build revision.
            using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (current is not null)
            {
                os.DisplayVersion = current.GetValue("DisplayVersion") as string ?? current.GetValue("ReleaseId") as string ?? "";
                os.Ubr = current.GetValue("UBR") is int ubr ? ubr : 0;
                os.EditionId = current.GetValue("EditionID") as string ?? "";
                if (os.BuildNumber == 0 && int.TryParse(current.GetValue("CurrentBuild") as string, out var build))
                    os.BuildNumber = build;
                if (string.IsNullOrEmpty(os.Caption))
                    os.Caption = current.GetValue("ProductName") as string ?? "Windows";
            }
        }
        catch { }

        os.Build = os.Ubr > 0 ? $"{os.BuildNumber}.{os.Ubr}" : os.BuildNumber.ToString(CultureInfo.InvariantCulture);
        os.Edition = os.EditionId;
        return os;
    }

    private static List<PatchEntry> GatherPatches()
    {
        var list = new List<PatchEntry>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT HotFixID, Description, InstalledOn, InstalledBy FROM Win32_QuickFixEngineering");
            foreach (ManagementObject obj in searcher.Get())
            {
                var id = obj["HotFixID"]?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(id) || id == "File 1") continue;
                var by = obj["InstalledBy"]?.ToString() ?? "";
                var slash = by.LastIndexOf('\\');
                if (slash >= 0) by = by[(slash + 1)..];
                var installed = ParsePatchDate(obj["InstalledOn"]?.ToString());
                list.Add(new PatchEntry
                {
                    Id = id,
                    Description = obj["Description"]?.ToString() ?? "",
                    InstalledDate = installed,
                    InstalledOn = installed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
                    InstalledBy = by
                });
            }
        }
        catch { }
        return list
            .OrderByDescending(p => p.InstalledDate ?? DateTime.MinValue)
            .ThenByDescending(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // WMI reports InstalledOn as text, usually M/d/yyyy whatever the locale, and on some systems
    // as a hexadecimal FILETIME. Sorting that text put 9/9/2026 ahead of 12/1/2025.
    private static DateTime? ParsePatchDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (DateTime.TryParseExact(raw, ["M/d/yyyy", "MM/dd/yyyy", "yyyyMMdd", "yyyy-MM-dd"],
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            return date;
        if (raw.Length >= 15 && long.TryParse(raw, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var fileTime))
        {
            try { return DateTime.FromFileTimeUtc(fileTime).ToLocalTime().Date; }
            catch (ArgumentOutOfRangeException) { }
        }
        return DateTime.TryParse(raw, CultureInfo.CurrentCulture, DateTimeStyles.None, out date) ? date : null;
    }

    private static List<SoftwareEntry> GatherSoftware()
    {
        var map = new Dictionary<string, SoftwareEntry>(StringComparer.OrdinalIgnoreCase);
        ReadSoftwareKey(Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ReadSoftwareKey(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall", map);
        ReadSoftwareKey(Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", map);
        return map.Values.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void ReadSoftwareKey(RegistryKey root, string path, Dictionary<string, SoftwareEntry> map)
    {
        try
        {
            using var key = root.OpenSubKey(path);
            if (key is null) return;
            foreach (var sub in key.GetSubKeyNames())
            {
                try
                {
                    using var entry = key.OpenSubKey(sub);
                    if (entry is null) continue;
                    var name = entry.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    if (entry.GetValue("SystemComponent") is int systemComponent && systemComponent == 1) continue;
                    if (map.ContainsKey(name)) continue;
                    map[name] = new SoftwareEntry
                    {
                        Name = name,
                        Version = entry.GetValue("DisplayVersion")?.ToString() ?? "",
                        Publisher = entry.GetValue("Publisher")?.ToString() ?? "",
                        InstallDate = FormatInstallDate(entry.GetValue("InstallDate")?.ToString() ?? ""),
                        Size = entry.GetValue("EstimatedSize") is int kb ? FormatKb(kb) : ""
                    };
                }
                catch { }
            }
        }
        catch { }
    }

    // Reads the dotnet install folders directly; launching `dotnet --list-runtimes` cost ~140 ms
    // and never listed SDKs, so the ".NET SDK" trait could not appear.
    private static List<DotNetEntry> GatherDotNet()
    {
        var list = new List<DotNetEntry>();
        foreach (var (root, suffix) in DotNetRoots())
        {
            try
            {
                var shared = Path.Combine(root, "shared");
                if (Directory.Exists(shared))
                {
                    foreach (var framework in Directory.EnumerateDirectories(shared))
                        foreach (var version in Directory.EnumerateDirectories(framework))
                            list.Add(new DotNetEntry
                            {
                                Name = Path.GetFileName(framework) + suffix,
                                Version = Path.GetFileName(version),
                                Kind = "runtime",
                                Path = framework
                            });
                }

                var sdk = Path.Combine(root, "sdk");
                if (Directory.Exists(sdk))
                {
                    foreach (var version in Directory.EnumerateDirectories(sdk))
                    {
                        var name = Path.GetFileName(version);
                        if (name.Length > 0 && char.IsDigit(name[0]))
                            list.Add(new DotNetEntry { Name = ".NET SDK" + suffix, Version = name, Kind = "sdk", Path = sdk });
                    }
                }
            }
            catch { }
        }

        list = list
            .OrderBy(d => d.Kind == "sdk" ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(d => ParseVersion(d.Version))
            .ToList();

        try
        {
            using var ndp = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full");
            if (ndp?.GetValue("Release") is int release)
            {
                var friendly = release switch
                {
                    >= 533320 => "4.8.1", >= 528040 => "4.8", >= 461808 => "4.7.2",
                    >= 461308 => "4.7.1", >= 460798 => "4.7", >= 394802 => "4.6.2",
                    _ => ndp.GetValue("Version")?.ToString() ?? "4.x"
                };
                list.Add(new DotNetEntry { Name = ".NET Framework", Version = friendly, Kind = "framework", Path = "GAC" });
            }
        }
        catch { }

        list.Add(new DotNetEntry
        {
            Name = "BoardScout (self-contained)",
            Version = Environment.Version.ToString(),
            Kind = "self",
            Path = AppContext.BaseDirectory
        });
        return list;
    }

    private static IEnumerable<(string Root, string Suffix)> DotNetRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        (string? Root, string Suffix)[] candidates =
        [
            (Environment.GetEnvironmentVariable("DOTNET_ROOT"), ""),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"), ""),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "dotnet"), " (x86)")
        ];
        foreach (var (root, suffix) in candidates)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;
            if (seen.Add(Path.GetFullPath(root).TrimEnd('\\'))) yield return (root, suffix);
        }
    }

    private static List<TaskEntry> GatherScheduledTasks()
    {
        var list = new List<TaskEntry>();
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", "/query /fo CSV /nh")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(psi);
            if (process is null) return list;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = CsvSplit(line);
                if (parts.Length < 3) continue;
                var fullName = parts[0].Trim();
                if (!fullName.StartsWith('\\')) continue;
                var slash = fullName.LastIndexOf('\\');
                var folder = slash > 0 ? fullName[..slash] : "\\";
                var nextRun = parts[1].Trim();
                list.Add(new TaskEntry
                {
                    Name = fullName[(slash + 1)..],
                    Folder = folder,
                    Status = parts[2].Trim(),
                    NextRun = nextRun is "N/A" ? "" : nextRun,
                    Category = fullName.StartsWith(@"\Microsoft\Windows\", StringComparison.OrdinalIgnoreCase) ? "Windows"
                        : fullName.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) ? "Microsoft" : "User"
                });
            }
        }
        catch { }
        return list;
    }

    private static BuildSummary BuildSummaryFor(ScanManifest? scan)
    {
        var build = new BuildSummary();
        if (scan is not null)
        {
            var cpu = scan.Cpu;
            build.CpuName = cpu.Name;
            build.Cpu = cpu.Name.Replace(" with Radeon Graphics", "").Replace("AMD ", "").Replace("Intel(R) ", "");
            build.CpuDetail = $"{cpu.Cores} cores / {cpu.Threads} threads";
            build.MemoryGb = scan.TotalMemoryGb;
            build.Memory = $"{scan.TotalMemoryGb:0.#} GB ({scan.Memory.Populated}/{scan.Memory.TotalSlots} slots)";
            var gpu = scan.Components.FirstOrDefault(c => c.Category == "gpu");
            build.Gpu = gpu?.Model ?? "Integrated / not detected";
            build.Motherboard = $"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product}".Trim();
            build.Bios = $"{scan.SystemInfo.Bios.Version} ({scan.SystemInfo.Bios.ReleaseDate})";
            build.Disks = scan.Volumes.Select(volume => new DiskEntry
            {
                Model = volume.DiskModel ?? volume.Label ?? $"Volume {volume.Letter}",
                Size = FormatBytes(volume.SizeBytes),
                Bus = volume.BusType ?? "Unknown",
                Free = FormatBytes(volume.FreeBytes),
                UsedPercent = Math.Round(volume.UsedPercent, 1)
            }).ToList();
        }
        else
        {
            FillBuildFromWmi(build);
        }

        // TPM Base Services answers in ~10 ms without elevation. The WMI Win32_Tpm class needs
        // admin: unelevated it spent ~5 s timing out and then reported "Not detected".
        build.TpmVersion = ReadTpmVersion();
        build.Tpm = build.TpmVersion switch
        {
            2 => "TPM 2.0",
            1 => "TPM 1.2",
            _ => "Not detected"
        };

        var uefi = IsUefiBoot();
        build.SecureBootEnabled = uefi == false ? null : ReadSecureBoot();
        build.SecureBoot = uefi == false
            ? "Legacy BIOS boot (no Secure Boot)"
            : build.SecureBootEnabled switch
            {
                true => "On",
                false => "Off (UEFI, can be turned on)",
                null => "Unknown"
            };
        return build;
    }

    private static void FillBuildFromWmi(BuildSummary build)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name, NumberOfCores, NumberOfLogicalProcessors FROM Win32_Processor");
            foreach (ManagementObject obj in searcher.Get())
            {
                build.CpuName = obj["Name"]?.ToString()?.Trim() ?? "";
                build.Cpu = build.CpuName;
                build.CpuDetail = $"{obj["NumberOfCores"]}C / {obj["NumberOfLogicalProcessors"]}T";
            }
        }
        catch { }
        try
        {
            long total = 0;
            using var searcher = new ManagementObjectSearcher("SELECT Capacity FROM Win32_PhysicalMemory");
            foreach (ManagementObject obj in searcher.Get())
                if (obj["Capacity"] is ulong capacity) total += (long)capacity;
            build.MemoryGb = total / 1_073_741_824d;
            build.Memory = $"{build.MemoryGb:0.#} GB";
        }
        catch { }
        try
        {
            var gpus = new List<string>();
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController");
            foreach (ManagementObject obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString()?.Trim();
                if (!string.IsNullOrEmpty(name)) gpus.Add(name);
            }
            build.Gpu = string.Join(" + ", gpus);
        }
        catch { }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Manufacturer, Product FROM Win32_BaseBoard");
            foreach (ManagementObject obj in searcher.Get())
                build.Motherboard = $"{obj["Manufacturer"]} {obj["Product"]}".Trim();
        }
        catch { }
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion, ReleaseDate FROM Win32_BIOS");
            foreach (ManagementObject obj in searcher.Get())
            {
                var version = obj["SMBIOSBIOSVersion"]?.ToString() ?? "";
                var date = obj["ReleaseDate"] is string released ? WmiDate(released) : "";
                build.Bios = string.IsNullOrEmpty(date) ? version : $"{version} ({date})";
            }
        }
        catch { }
    }

    private static void Analyze(SystemInfoSnapshot snapshot)
    {
        var os = snapshot.Os;
        var today = DateTime.Today;
        var traits = new List<string>();
        var recs = new List<string>();

        var newestPatch = snapshot.Patches.Select(p => p.InstalledDate).Where(d => d.HasValue).Max();
        var patchAge = newestPatch.HasValue ? Math.Max(0, (int)(today - newestPatch.Value.Date).TotalDays) : -1;
        var patchedRecently = patchAge is >= 0 and <= 45;
        os.LastPatch = newestPatch?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        (os.PatchHealth, os.PatchTone) = patchAge switch
        {
            < 0 => ("No update history available", "info"),
            <= 35 => ($"Current — last update {os.LastPatch} ({Days(patchAge)} ago)", "good"),
            <= 65 => ($"A month behind — last update {Days(patchAge)} ago", "warning"),
            <= 95 => ($"Behind — {Days(patchAge)} since the last update", "warning"),
            _ => ($"Significantly behind — {Days(patchAge)} without updates", "critical")
        };

        var isServer = os.Caption.Contains("Server", StringComparison.OrdinalIgnoreCase);
        var isWindows11 = !isServer && os.BuildNumber >= 22000;
        var isWindows10 = !isServer && os.BuildNumber is >= 10240 and < 22000;
        var isLtsc = Regex.IsMatch(os.EditionId, "EnterpriseSN?$", RegexOptions.IgnoreCase);
        // Pro Education follows the Home and Pro schedule; Enterprise and Education get a longer one.
        var enterpriseFamily = os.EditionId.Contains("Enterprise", StringComparison.OrdinalIgnoreCase) ||
                               (os.EditionId.Contains("Education", StringComparison.OrdinalIgnoreCase) &&
                                !os.EditionId.Contains("ProfessionalEducation", StringComparison.OrdinalIgnoreCase));
        var windows11Supported = false;

        if (isServer)
        {
            SetSupport(os, "Windows Server — check Microsoft Lifecycle for this release", "info", null);
        }
        else if (isLtsc)
        {
            SetSupport(os, "LTSC edition — follows the Fixed Lifecycle Policy; check Microsoft Lifecycle", "info", null);
        }
        else if (isWindows11)
        {
            var release = Windows11Releases.FirstOrDefault(r => r.Build == os.BuildNumber);
            if (release is null)
            {
                windows11Supported = true;
                SetSupport(os, $"Build {os.BuildNumber} is newer than BoardScout's lifecycle table (Insider or a new release)", "info", null);
            }
            else
            {
                var ends = enterpriseFamily ? release.EnterpriseEnd : release.HomeProEnd;
                var left = (int)(ends - today).TotalDays;
                var nextUpdate = release.Build < LatestGeneralWindows11Build
                    ? "Windows 11 25H2"
                    : "the next Windows 11 feature update";
                windows11Supported = left >= 0;
                if (left < 0)
                {
                    SetSupport(os, $"Windows 11 {release.Version} stopped getting updates {ends:MMM d, yyyy}", "critical", ends);
                    recs.Add($"Install {nextUpdate} from Windows Update — {release.Version} no longer receives security fixes.");
                }
                else if (left <= 60)
                {
                    SetSupport(os, $"Windows 11 {release.Version} is supported until {ends:MMM d, yyyy} — {Days(left)} left", "warning", ends);
                    recs.Add($"Install {nextUpdate} from Windows Update before {release.Version} servicing ends on {ends:MMMM d, yyyy}.");
                }
                else
                {
                    SetSupport(os, $"Windows 11 {release.Version} is supported until {ends:MMM d, yyyy}", "good", ends);
                    if (release.Build < LatestGeneralWindows11Build)
                        recs.Add("Windows 11 25H2 is available from Windows Update.");
                }
            }
        }
        else if (isWindows10)
        {
            if (os.BuildNumber < 19045)
            {
                SetSupport(os, $"Windows 10 {os.DisplayVersion} is out of support and cannot use Extended Security Updates", "critical", null);
                recs.Add("Update to Windows 10 22H2 (required for Extended Security Updates) or move to Windows 11.");
            }
            else if (patchedRecently)
            {
                SetSupport(os, $"Extended Security Updates active — security fixes through {Windows10ConsumerEsuEnds:MMM d, yyyy}",
                    (Windows10ConsumerEsuEnds - today).TotalDays <= 90 ? "warning" : "info", Windows10ConsumerEsuEnds);
            }
            else
            {
                SetSupport(os, $"Support ended {Windows10SupportEnded:MMM d, yyyy} and no recent security updates were found", "critical", Windows10ConsumerEsuEnds);
                recs.Add("Enroll in Windows 10 Extended Security Updates (free with PC settings sync, 1,000 Microsoft Rewards points, " +
                         $"or a one-time $30 purchase) — it delivers security fixes through {Windows10ConsumerEsuEnds:MMMM d, yyyy}.");
            }
            if (enterpriseFamily)
                recs.Add("Organizations can buy commercial ESU for up to three years after end of support.");
            snapshot.Readiness = CheckWindows11Readiness(snapshot.Build);
            if (snapshot.Readiness.Tone == "good")
                recs.Add($"This hardware looks ready for Windows 11 — upgrading keeps you supported past {Windows10ConsumerEsuEnds:MMMM d, yyyy}.");
            else if (snapshot.Readiness.Tone == "warning")
                recs.Add("This hardware is probably Windows 11-ready — run PC Health Check to confirm before ESU ends.");
        }
        else
        {
            SetSupport(os, "Check Microsoft Lifecycle for this Windows version", "info", null);
        }

        if (patchAge > 45 && !isServer)
            recs.Add("Open Windows Update and install pending updates.");

        var software = snapshot.Software;
        bool Has(string pattern) => software.Any(s => Regex.IsMatch(s.Name, pattern, RegexOptions.IgnoreCase));
        // Anchored patterns: a bare "Git" substring used to match "Digital…", and Acrobat Reader
        // alone made every PC "creative".
        var hasDev = Has(@"\bVisual Studio\b|\bJetBrains\b|^Git\b|^Node\.js\b|^Python \d");
        var hasCreative = Has(@"^Adobe (Photoshop|Premiere|Illustrator|After Effects|Lightroom|Audition|InDesign)|DaVinci Resolve|\bBlender\b|^OBS Studio\b");
        var hasGaming = Has(@"^Steam$|Epic Games|Battle\.net");

        if (hasDev && windows11Supported && patchedRecently)
        { os.Verdict = "The Power Developer"; os.VerdictEmoji = "rocket"; }
        else if (hasDev && patchedRecently)
        { os.Verdict = "The Working Dev"; os.VerdictEmoji = "keyboard"; }
        else if (!isWindows11 && patchedRecently)
        { os.Verdict = "The Reliable Holdout"; os.VerdictEmoji = "shield"; }
        else if (!isWindows11 && patchAge > 90)
        { os.Verdict = "The If-It-Ain't-Broke"; os.VerdictEmoji = "wrench"; }
        else if (windows11Supported && patchedRecently)
        { os.Verdict = "The Early Adopter"; os.VerdictEmoji = "sparkles"; }
        else if (isWindows11)
        { os.Verdict = "The Cautious Upgrader"; os.VerdictEmoji = "hourglass"; }
        else if (hasGaming && hasCreative)
        { os.Verdict = "The Creative Gamer"; os.VerdictEmoji = "art"; }
        else if (hasGaming)
        { os.Verdict = "The Battle Station"; os.VerdictEmoji = "joystick"; }
        else
        { os.Verdict = "The Everyday Driver"; os.VerdictEmoji = "computer"; }

        if (hasDev) traits.Add("Developer workstation");
        if (hasCreative) traits.Add("Creative tools installed");
        if (hasGaming) traits.Add("Gaming-ready");
        var sdks = snapshot.DotNet.Count(d => d.Kind == "sdk");
        var runtimes = snapshot.DotNet.Count(d => d.Kind == "runtime");
        if (sdks > 0) traits.Add($"{sdks} .NET SDK{(sdks == 1 ? "" : "s")}");
        else if (runtimes > 3) traits.Add($"{runtimes} .NET runtimes");
        if (patchedRecently) traits.Add("Patch discipline: strong");
        else if (patchAge > 60) traits.Add("Patch discipline: needs attention");
        if (isWindows11 && windows11Supported && os.BuildNumber >= LatestGeneralWindows11Build) traits.Add("Running the latest Windows 11");
        else if (isWindows10) traits.Add("Windows 10 holdout");
        var userTasks = snapshot.Tasks.Count(t => t.Category == "User");
        if (userTasks > 20) traits.Add("Busy task scheduler");
        if (software.Count > 100) traits.Add($"{software.Count} programs installed");
        else if (software.Count < 30) traits.Add("Clean install profile");

        os.Traits = traits;
        os.Recommendations = recs;
    }

    private static void SetSupport(OsAnalysisInfo os, string status, string tone, DateTime? ends)
    {
        os.SupportStatus = status;
        os.SupportTone = tone;
        os.SupportEnds = ends?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        os.SupportDaysLeft = ends is null ? null : (int)(ends.Value - DateTime.Today).TotalDays;
    }

    private static UpgradeReadiness CheckWindows11Readiness(BuildSummary build)
    {
        var checks = new List<ReadinessCheck>
        {
            build.TpmVersion switch
            {
                2 => Check("TPM 2.0", "pass", "Present"),
                1 => Check("TPM 2.0", "fail", "Only TPM 1.2 found"),
                _ => Check("TPM 2.0", "fail", "Not found — AMD fTPM or Intel PTT may be off in firmware setup")
            },
            build.SecureBootEnabled switch
            {
                true => Check("UEFI Secure Boot", "pass", "On"),
                false => Check("UEFI Secure Boot", "warn", "Supported but off — turning it on in firmware setup is recommended"),
                null when build.SecureBoot.StartsWith("Legacy", StringComparison.Ordinal) =>
                    Check("UEFI Secure Boot", "fail", "Legacy BIOS boot — convert the disk with MBR2GPT and switch firmware to UEFI"),
                _ => Check("UEFI Secure Boot", "unknown", "Could not read the Secure Boot state")
            },
            CheckCpu(build.CpuName),
            build.MemoryGb switch
            {
                >= 4 => Check("Memory", "pass", $"{build.MemoryGb:0.#} GB (4 GB minimum)"),
                > 0 => Check("Memory", "fail", $"{build.MemoryGb:0.#} GB — 4 GB minimum"),
                _ => Check("Memory", "unknown", "Not measured")
            },
            CheckSystemDrive()
        };

        var failed = checks.Where(c => c.State == "fail").Select(c => c.Label).ToList();
        var unknown = checks.Any(c => c.State == "unknown");
        var (summary, tone) = failed.Count > 0
            ? ($"Not ready yet: {string.Join(", ", failed)}", "critical")
            : unknown
                ? ("Probably ready — confirm the unknown items with PC Health Check", "warning")
                : ("Looks ready for Windows 11", "good");
        return new UpgradeReadiness
        {
            Title = "Windows 11 readiness",
            Summary = summary,
            Tone = tone,
            Checks = checks
        };
    }

    private static ReadinessCheck CheckCpu(string cpu)
    {
        if (string.IsNullOrWhiteSpace(cpu)) return Check("Processor", "unknown", "Not detected");
        var name = Regex.Replace(cpu, @"\s+", " ").Trim();

        if (Regex.IsMatch(name, @"Core(\(TM\))? Ultra", RegexOptions.IgnoreCase))
            return Check("Processor", "pass", "Intel Core Ultra");
        var intel = Regex.Match(name, @"\bi[3579]-(\d{4,5})", RegexOptions.IgnoreCase);
        if (intel.Success)
        {
            var digits = intel.Groups[1].Value;
            var generation = int.Parse(digits.Length == 5 ? digits[..2] : digits[..1], CultureInfo.InvariantCulture);
            return generation >= 8
                ? Check("Processor", "pass", $"Intel Core {generation}th gen")
                : Check("Processor", "fail", $"Intel Core {generation}th gen — Windows 11 lists 8th gen and newer");
        }

        if (Regex.IsMatch(name, @"Ryzen AI", RegexOptions.IgnoreCase))
            return Check("Processor", "pass", "AMD Ryzen AI");
        var ryzen = Regex.Match(name, @"Ryzen (?:Threadripper )?(?:\d )?(?:PRO )?(\d)(\d{3})([A-Z]*)", RegexOptions.IgnoreCase);
        if (ryzen.Success)
        {
            var series = ryzen.Groups[1].Value[0] - '0';
            var suffix = ryzen.Groups[3].Value.ToUpperInvariant();
            var model = ryzen.Value;
            // Ryzen 2000 APUs (G/U/H) are first-generation Zen despite the number, so they stay "unknown".
            if (series >= 3 || (series == 2 && !suffix.Contains('G') && !suffix.Contains('U') && !suffix.Contains('H')))
                return Check("Processor", "pass", $"{model} — Ryzen {series}000 series is a supported generation");
            if (series == 1)
                return Check("Processor", "fail", $"{model} — first-generation Ryzen is not on Microsoft's list");
            return Check("Processor", "unknown", $"{model} — check Microsoft's supported-CPU list");
        }
        return Check("Processor", "unknown", "Not in BoardScout's quick list — PC Health Check will say");
    }

    private static ReadinessCheck CheckSystemDrive()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
            if (string.IsNullOrEmpty(root)) return Check("System drive", "unknown", "Not measured");
            var gb = new DriveInfo(root).TotalSize / 1_073_741_824d;
            return gb >= 64
                ? Check("System drive", "pass", $"{gb:0} GB (64 GB minimum)")
                : Check("System drive", "fail", $"{gb:0} GB — 64 GB minimum");
        }
        catch
        {
            return Check("System drive", "unknown", "Not measured");
        }
    }

    private static ReadinessCheck Check(string label, string state, string detail) =>
        new() { Label = label, State = state, Detail = detail };

    private static int? ReadTpmVersion()
    {
        try
        {
            if (Tbsi_GetDeviceInfo((uint)Marshal.SizeOf<TpmDeviceInfo>(), out var info) != 0) return null;
            return info.TpmVersion is 1 or 2 ? (int)info.TpmVersion : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool? IsUefiBoot()
    {
        try
        {
            return GetFirmwareType(out var type) ? type == FirmwareTypeUefi : null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    private static bool? ReadSecureBoot()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            return key?.GetValue("UEFISecureBootEnabled") is int value ? value == 1 : null;
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string text)
    {
        var dash = text.IndexOf('-');
        return Version.TryParse(dash > 0 ? text[..dash] : text, out var version) ? version : new Version(0, 0);
    }

    private static string Days(int days) => days == 1 ? "1 day" : $"{days} days";

    private static string WmiDate(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 14) return value;
        try { return ManagementDateTimeConverter.ToDateTime(value).ToString("yyyy-MM-dd HH:mm"); }
        catch { return value; }
    }

    private static string FormatInstallDate(string value) =>
        value.Length == 8 ? $"{value[..4]}-{value[4..6]}-{value[6..8]}" : value;

    private static string FormatKb(int kb) =>
        kb >= 1_048_576 ? $"{kb / 1_048_576.0:0.#} GB" :
        kb >= 1024 ? $"{kb / 1024.0:0.#} MB" : $"{kb} KB";

    private static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static string[] CsvSplit(string line)
    {
        var parts = new List<string>();
        var quoted = false;
        var current = new System.Text.StringBuilder();
        foreach (var ch in line)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (ch == ',' && !quoted) { parts.Add(current.ToString()); current.Clear(); continue; }
            current.Append(ch);
        }
        parts.Add(current.ToString());
        return parts.ToArray();
    }

    private const int FirmwareTypeUefi = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TpmDeviceInfo
    {
        public uint StructVersion;
        public uint TpmVersion;
        public uint TpmInterfaceType;
        public uint TpmImpRevision;
    }

    [DllImport("tbs.dll")]
    private static extern uint Tbsi_GetDeviceInfo(uint size, out TpmDeviceInfo info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFirmwareType(out int firmwareType);
}
