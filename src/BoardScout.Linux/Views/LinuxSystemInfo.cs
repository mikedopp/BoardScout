using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;

namespace BoardScout.Views;

/// <summary>Updates waiting in the package manager, and when the last ones went in.</summary>
internal sealed record UpdateState(string Manager, int? Pending, int? Security, DateTime? LastUpdate, bool AutomaticUpdates, string? Note);

/// <summary>
/// The System tab for Linux: what the distribution says about you, its support status, a security check,
/// the build, installed packages, package history, and timers. Reads files and runs read-only queries only.
/// </summary>
internal static partial class LinuxSystemInfo
{
    public static async Task<string> GatherJsonAsync(HardwareCapture capture) =>
        JsonSerializer.Serialize(await GatherAsync(capture), Json.Options);

    public static async Task<SystemInfoSnapshot> GatherAsync(HardwareCapture capture)
    {
        var watch = Stopwatch.StartNew();
        var distro = Distro.Read();
        var software = Task.Run(() => ReadSoftware(distro));
        var history = Task.Run(() => ReadPackageHistory(distro));
        var updates = Task.Run(() => ReadUpdates(distro));
        var tasks = Task.Run(ReadTasks);
        var services = Task.Run(RunningServices);
        var dotnet = Task.Run(ReadDotNet);
        var firewall = Task.Run(FirewallState);
        await Task.WhenAll(software, history, updates, tasks, services, dotnet, firewall);

        var os = BuildOs(capture, distro, updates.Result, software.Result, services.Result, history.Result);
        var snapshot = new SystemInfoSnapshot
        {
            Os = os,
            Readiness = SecurityCheck(capture, updates.Result, firewall.Result),
            Build = BuildSummary(capture),
            DotNet = dotnet.Result,
            Patches = history.Result,
            Software = software.Result,
            Tasks = tasks.Result,
            Ui = new SystemUiText
            {
                BuildLabel = "Kernel",
                PatchesTitle = "Package history",
                PatchColumns = ["Package", "Change", "Date", "By"],
                SoftwareTitle = "Installed packages",
                SoftwareColumns = ["Name", "Version", "Maintainer or source", "Installed", "Size"],
                TasksTitle = "Timers and scheduled jobs",
                TaskColumns = ["Job", "Status", "Where", "Next run"],
                TaskFilters = ["All", "System", "User", "Cron"],
                DotNetTitle = ".NET on this computer",
                ReadinessFoot = "BoardScout's quick check of what Linux reports. It changes nothing.",
                Foot = "nothing here needs root"
            }
        };
        snapshot.GatheredMs = watch.ElapsedMilliseconds;
        return snapshot;
    }

    // ---- The hero card ------------------------------------------------------------------------------

    private static OsAnalysisInfo BuildOs(HardwareCapture capture, Distro distro, UpdateState updates, List<SoftwareEntry> software,
        HashSet<string> services, List<PatchEntry> history)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var support = Lifecycle.Of(distro, today);
        var uptime = ReadUptime();
        var installed = InstallDate();
        // Ubuntu Server boots to graphical.target too, so look for an installed desktop session instead.
        var desktop = Sys.Children("/usr/share/xsessions").Concat(Sys.Children("/usr/share/wayland-sessions")).Any(n => n.EndsWith(".desktop", StringComparison.Ordinal));
        var server = !desktop || capture.FormFactor == "Server";
        var os = new OsAnalysisInfo
        {
            Caption = distro.PrettyName,
            Edition = server ? "Server (no desktop)" : distro.Variant ?? "Desktop",
            DisplayVersion = distro.Version ?? distro.VersionId ?? "",
            Version = distro.VersionId ?? "",
            Build = capture.Kernel,
            Architecture = Architecture(),
            InstallDate = installed?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "",
            LastBoot = uptime is { } up ? (DateTime.Now - up).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
            Uptime = uptime is { } span ? Format.Duration(span) : "",
            SupportStatus = support.Status,
            SupportTone = support.Tone,
            SupportEnds = support.Ends,
            SupportDaysLeft = support.DaysLeft,
            LastPatch = updates.LastUpdate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""
        };

        // Patch health.
        if (updates.Pending is { } pending)
        {
            (os.PatchHealth, os.PatchTone) = pending switch
            {
                0 when updates.LastUpdate is { } last && (DateTime.Now - last).TotalDays > 60 => ($"Nothing waiting, but nothing installed for {(int)(DateTime.Now - last).TotalDays} days", "warning"),
                0 => ("Up to date: no updates waiting", "good"),
                _ when updates.Security is > 0 => ($"{pending} update{(pending == 1 ? "" : "s")} waiting, {updates.Security} for security", "warning"),
                _ => ($"{pending} update{(pending == 1 ? "" : "s")} waiting", "info")
            };
        }
        else
        {
            os.PatchHealth = updates.Note ?? $"Updates come from {updates.Manager}";
            os.PatchTone = "info";
        }

        // Personality.
        var names = software.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool Has(params string[] packages) => packages.Any(names.Contains);
        bool Running(params string[] units) => units.Any(services.Contains);
        var homelab = new List<string>();
        if (Running("plexmediaserver")) homelab.Add("Plex Media Server");
        if (Running("jellyfin")) homelab.Add("Jellyfin");
        if (Running("pihole-FTL")) homelab.Add("Pi-hole");
        if (Running("docker", "containerd", "podman")) homelab.Add("Containers");
        if (Running("libvirtd", "virtqemud")) homelab.Add("Virtual machines (libvirt)");
        if (Running("tailscaled")) homelab.Add("Tailscale");
        if (Running("smbd", "nmbd")) homelab.Add("Samba file shares");
        if (Running("nginx", "apache2", "httpd", "caddy")) homelab.Add("Web server");
        if (Running("home-assistant", "homeassistant")) homelab.Add("Home Assistant");
        if (Running("syncthing", "syncthing@")) homelab.Add("Syncthing");
        var developer = new[] { "build-essential", "gcc", "clang", "git", "code", "dotnet-sdk-10.0", "dotnet-sdk-8.0", "nodejs", "python3-pip", "golang-go", "rustc", "cargo", "docker-ce" }
            .Count(p => names.Contains(p)) + (ReadDotNet().Any(d => d.Kind == "sdk") ? 1 : 0);
        var gaming = Has("steam", "steam-installer", "steam-launcher", "lutris", "heroic", "gamemode");
        var dgpu = capture.Pci.Any(d => d.BaseClass == 0x03 && d.Bus != 0 && d.Vendor is 0x10DE or 0x1002);
        var interim = !support.Rolling && distro.Version?.Contains("LTS", StringComparison.Ordinal) == false && distro.Id == "ubuntu";
        var secure = capture.Security.SecureBoot == true && updates.AutomaticUpdates;
        (os.Verdict, os.VerdictEmoji) = true switch
        {
            _ when capture.FormFactor == "Single-board computer" => ("The Tinkerer", "art"),
            _ when support.Tone == "critical" => ("The Reliable Holdout", "hourglass"),
            _ when homelab.Count >= 2 => ("The Homelab Hero", "wrench"),
            _ when gaming && dgpu => ("The Battle Station", "joystick"),
            _ when developer >= 3 => ("The Power Developer", "keyboard"),
            _ when support.Rolling || interim => ("The Bleeding Edge", "rocket"),
            _ when secure => ("The Security Minded", "shield"),
            _ when software.Count is > 0 and < 600 => ("The Minimalist", "sparkles"),
            _ => ("The Daily Driver", "computer")
        };
        os.Traits.AddRange(homelab.Take(5));
        if (distro.Version?.Contains("LTS", StringComparison.Ordinal) == true) os.Traits.Add("Long-term support release");
        if (support.Rolling) os.Traits.Add("Rolling release");
        if (server) os.Traits.Add("Runs without a desktop");
        if (gaming) os.Traits.Add("Gamer (Steam or Lutris)");
        if (developer >= 3) os.Traits.Add("Developer tools installed");
        if (software.Count > 0) os.Traits.Add($"{software.Count(s => !s.Publisher.StartsWith("snap", StringComparison.Ordinal) && !s.Publisher.StartsWith("Flatpak", StringComparison.Ordinal)):N0} packages");
        var snaps = software.Count(s => s.Publisher.StartsWith("snap", StringComparison.Ordinal));
        if (snaps > 0) os.Traits.Add($"{snaps} snap{(snaps == 1 ? "" : "s")}");
        var flatpaks = software.Count(s => s.Publisher.StartsWith("Flatpak", StringComparison.Ordinal));
        if (flatpaks > 0) os.Traits.Add($"{flatpaks} Flatpak app{(flatpaks == 1 ? "" : "s")}");
        if (uptime is { TotalDays: >= 1 } long_) os.Traits.Add($"Up {(int)long_.TotalDays} day{((int)long_.TotalDays == 1 ? "" : "s")}");

        // Recommendations.
        if (updates.Pending is > 0)
            os.Recommendations.Add(updates.Manager == "apt" ? $"Install the {updates.Pending} waiting update{(updates.Pending == 1 ? "" : "s")}: sudo apt upgrade"
                : $"Install the {updates.Pending} waiting update{(updates.Pending == 1 ? "" : "s")} with {updates.Manager}");
        if (LinuxPlan.RebootReason(capture.Kernel) is { } reboot) os.Recommendations.Add($"Restart to finish updates. {reboot}");
        if (!updates.AutomaticUpdates && updates.Manager == "apt")
            os.Recommendations.Add("Turn on automatic security updates: sudo dpkg-reconfigure -plow unattended-upgrades");
        if (support.Tone is "critical")
            os.Recommendations.Add("Upgrade to a supported release: this one no longer gets security fixes.");
        else if (support.Tone == "warning" && support.DaysLeft is { } days)
            os.Recommendations.Add($"Plan the upgrade to the next release: support runs out in {days} days.");
        if (history.Count == 0 && updates.Manager == "apt")
            os.Recommendations.Add("No package history found in /var/log/apt.");
        return os;
    }

    private static string Architecture() => RuntimeInformation.OSArchitecture switch
    {
        System.Runtime.InteropServices.Architecture.X64 => "x86_64",
        System.Runtime.InteropServices.Architecture.Arm64 => "aarch64",
        System.Runtime.InteropServices.Architecture.Arm => "armv7l",
        var other => other.ToString().ToLowerInvariant()
    };

    private static TimeSpan? ReadUptime() =>
        double.TryParse(Sys.Read("/proc/uptime")?.Split(' ')[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            ? TimeSpan.FromSeconds(seconds)
            : null;

    // The installer's log folder (Debian, Ubuntu), else when the machine id was made (first boot).
    private static DateTime? InstallDate()
    {
        foreach (var path in new[] { "/var/log/installer", "/var/log/anaconda" })
            if (Directory.Exists(path)) return Directory.GetCreationTime(path) is { Year: > 2000 } created ? created : Directory.GetLastWriteTime(path);
        return File.Exists("/etc/machine-id") ? File.GetLastWriteTime("/etc/machine-id") : null;
    }

    // ---- Security check -------------------------------------------------------------------------------

    private static UpgradeReadiness SecurityCheck(HardwareCapture capture, UpdateState updates, (bool? Active, string Name) firewall)
    {
        var checks = new List<ReadinessCheck>
        {
            capture.Security.SecureBoot switch
            {
                true => new ReadinessCheck { Label = "Secure Boot", State = "pass", Detail = "On: the firmware only starts signed boot loaders and kernels" },
                false => new ReadinessCheck { Label = "Secure Boot", State = "warn", Detail = "Off in the firmware settings" },
                null when !capture.Security.Uefi => new ReadinessCheck { Label = "Secure Boot", State = "warn", Detail = "Not available: the computer boots in legacy BIOS mode" },
                _ => new ReadinessCheck { Label = "Secure Boot", State = "unknown", Detail = "The firmware doesn't say" }
            },
            capture.Security.TpmVersion switch
            {
                2 => new ReadinessCheck { Label = "TPM", State = "pass", Detail = "TPM 2.0 present" + (capture.Security.TpmMaker is { } maker ? $" ({maker})" : "") },
                1 => new ReadinessCheck { Label = "TPM", State = "warn", Detail = "TPM 1.2: older, and not used for disk encryption unlocking" },
                _ => new ReadinessCheck { Label = "TPM", State = "unknown", Detail = "None found (it may be switched off in the firmware)" }
            },
            firewall.Active switch
            {
                true => new ReadinessCheck { Label = "Firewall", State = "pass", Detail = $"{firewall.Name} is active" },
                false => new ReadinessCheck { Label = "Firewall", State = "warn", Detail = $"{firewall.Name} is installed but not active" },
                _ => new ReadinessCheck { Label = "Firewall", State = "unknown", Detail = "No ufw or firewalld found; nftables rules may still apply (listing them needs root)" }
            },
            updates.Manager switch
            {
                "apt" => new ReadinessCheck { Label = "Automatic security updates", State = updates.AutomaticUpdates ? "pass" : "warn", Detail = updates.AutomaticUpdates ? "unattended-upgrades installs them" : "Off: updates wait until someone installs them" },
                _ => new ReadinessCheck { Label = "Automatic updates", State = updates.AutomaticUpdates ? "pass" : "unknown", Detail = updates.AutomaticUpdates ? "A timer installs them" : $"Not detected for {updates.Manager}" }
            },
            capture.Cpu.Vulnerabilities.Count == 0
                ? new ReadinessCheck { Label = "Processor flaws", State = "pass", Detail = "Every known flaw is mitigated or not present" }
                : new ReadinessCheck { Label = "Processor flaws", State = "warn", Detail = $"{capture.Cpu.Vulnerabilities.Count} not mitigated: {capture.Cpu.Vulnerabilities[0]}" },
            LinuxPlan.RebootReason(capture.Kernel) is { } reboot
                ? new ReadinessCheck { Label = "Running the newest kernel", State = "warn", Detail = reboot }
                : new ReadinessCheck { Label = "Running the newest kernel", State = "pass", Detail = $"{capture.Kernel} is the newest installed" },
            SshPasswordLogins() switch
            {
                null => new ReadinessCheck { Label = "SSH server", State = "pass", Detail = "Not installed" },
                true => new ReadinessCheck { Label = "SSH password logins", State = "warn", Detail = "Allowed: keys alone are safer (PasswordAuthentication no)" },
                false => new ReadinessCheck { Label = "SSH password logins", State = "pass", Detail = "Off: only keys can log in" }
            }
        };
        var fails = checks.Count(c => c.State == "fail");
        var warns = checks.Count(c => c.State == "warn");
        return new UpgradeReadiness
        {
            Title = "Security check",
            Tone = fails > 0 ? "critical" : warns > 0 ? "warning" : "good",
            Summary = fails + warns == 0 ? "Everything BoardScout checks looks right" : $"{warns + fails} thing{(warns + fails == 1 ? "" : "s")} worth a look",
            Checks = checks
        };
    }

    // sshd_config and its drop-ins are world-readable; null when no SSH server is installed.
    private static bool? SshPasswordLogins()
    {
        if (!File.Exists("/etc/ssh/sshd_config")) return null;
        var files = new List<string>();
        if (Directory.Exists("/etc/ssh/sshd_config.d")) files.AddRange(Directory.GetFiles("/etc/ssh/sshd_config.d", "*.conf").Order(StringComparer.Ordinal));
        files.Add("/etc/ssh/sshd_config");
        // sshd uses the first value it reads, and the drop-ins are included first.
        foreach (var line in files.SelectMany(Sys.Lines).Select(l => l.Trim()))
        {
            if (line.StartsWith('#') || !line.StartsWith("PasswordAuthentication", StringComparison.OrdinalIgnoreCase)) continue;
            return line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1)?.Equals("yes", StringComparison.OrdinalIgnoreCase) == true;
        }
        return true;
    }

    private static (bool? Active, string Name) FirewallState()
    {
        if (Tools.Has("ufw"))
        {
            // ufw's own state file is world-readable; "ufw status" needs root.
            var enabled = Sys.Lines("/etc/ufw/ufw.conf").FirstOrDefault(l => l.StartsWith("ENABLED=", StringComparison.Ordinal))?.Split('=')[1].Trim().Equals("yes", StringComparison.OrdinalIgnoreCase);
            return (enabled == true && Tools.Run("systemctl", "is-active", "ufw")?.Trim() == "active", "ufw");
        }
        if (Tools.Has("firewall-cmd") || Sys.Exists("/usr/lib/systemd/system/firewalld.service"))
            return (Tools.Run("systemctl", "is-active", "firewalld")?.Trim() == "active", "firewalld");
        return (null, "firewall");
    }

    private static HashSet<string> RunningServices()
    {
        var text = Tools.Run("systemctl", "list-units", "--type=service", "--state=running", "--no-legend", "--plain", "--no-pager") ?? "";
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split(' ')[0])
            .Select(u => u.EndsWith(".service", StringComparison.Ordinal) ? u[..^8] : u)
            .Select(u => u.Contains('@') ? u[..(u.IndexOf('@') + 1)] : u)
            .ToHashSet(StringComparer.Ordinal);
    }

    // ---- Build summary --------------------------------------------------------------------------------

    private static BuildSummary BuildSummary(HardwareCapture capture)
    {
        var memory = capture.Memory.Firmware;
        var populated = memory.MemorySlots.Where(s => s.Populated).ToList();
        var (total, _, _, _) = Cpu.ReadMemory();
        var gpus = capture.Pci.Where(d => d.BaseClass == 0x03).Select(d => $"{d.VendorShort} {d.Name}").ToList();
        var summary = new BuildSummary
        {
            Cpu = Format.ShortCpuName(capture.Cpu.Name),
            CpuDetail = $"{capture.Cpu.Cores} cores, {capture.Cpu.Threads} threads" + (capture.Cpu.MaxMhz is { } mhz ? $" · up to {mhz / 1000:0.0#} GHz" : ""),
            Memory = $"{total / 1_073_741_824d:0.#} GB" + (populated.Count > 0
                ? $" {populated[0].MemoryType} · {populated.Count} of {memory.SlotCount} slots" + (populated[0].ConfiguredMts > 0 ? $" · {populated[0].ConfiguredMts} MT/s" : "")
                : ""),
            Gpu = gpus.Count == 0 ? (capture.Displays.Count > 0 ? "Built-in graphics" : "") : string.Join(" + ", gpus),
            Motherboard = string.Join(" · ", new[] { capture.Identity.System, capture.Identity.Board ?? capture.DeviceTreeModel }.Where(s => s is not null).Distinct()),
            Bios = capture.Identity.BiosVersion is { } bios ? $"{bios}{(capture.Identity.BiosDate is { } date ? $" ({date})" : "")}" : "",
            Tpm = capture.Security.TpmVersion switch { 2 => "TPM 2.0", 1 => "TPM 1.2", _ => "Not detected" },
            SecureBoot = capture.Security.SecureBoot switch { true => "On", false => "Off", null when !capture.Security.Uefi => "Legacy BIOS boot", _ => "Unknown" }
        };
        foreach (var disk in capture.Disks.Where(d => !d.Optical))
        {
            var volumes = capture.Volumes.Where(v => v.Disks.Contains(disk.Name) && v.Disks.Count == 1).ToList();
            var size = volumes.Sum(v => v.SizeBytes);
            var free = volumes.Sum(v => v.FreeBytes);
            summary.Disks.Add(new DiskEntry
            {
                Model = disk.DisplayName,
                Size = Format.Bytes(disk.SizeBytes),
                Bus = disk.Transport switch { "nvme" => "NVMe", "sata" => "SATA", "usb" => "USB", "mmc" => "SD/eMMC", "virtio" => "Virtual", _ => "SCSI" },
                Free = size > 0 ? $"{Format.Bytes(free)}" : "nothing mounted",
                UsedPercent = size > 0 ? Math.Round((size - free) * 100d / size, 1) : 0
            });
        }
        return summary;
    }

    // ---- .NET -----------------------------------------------------------------------------------------

    private static List<DotNetEntry> ReadDotNet()
    {
        var list = new List<DotNetEntry>
        {
            new() { Name = "BoardScout's own runtime", Version = Environment.Version.ToString(), Kind = "self", Path = AppContext.BaseDirectory }
        };
        if (Tools.Run("dotnet", "--list-runtimes") is { } runtimes)
            foreach (var match in DotNetLine().Matches(runtimes).Cast<Match>())
                list.Add(new DotNetEntry { Name = match.Groups[1].Value, Version = match.Groups[2].Value, Kind = "runtime", Path = match.Groups[3].Value });
        if (Tools.Run("dotnet", "--list-sdks") is { } sdks)
            foreach (var match in SdkLine().Matches(sdks).Cast<Match>())
                list.Add(new DotNetEntry { Name = ".NET SDK", Version = match.Groups[1].Value, Kind = "sdk", Path = match.Groups[2].Value });
        return list;
    }

    // ---- Packages ---------------------------------------------------------------------------------------

    private static List<SoftwareEntry> ReadSoftware(Distro distro)
    {
        var list = new List<SoftwareEntry>();
        if (File.Exists("/var/lib/dpkg/status")) list.AddRange(ReadDpkg());
        else if (Tools.Has("rpm")) list.AddRange(ReadRpm());
        else if (Directory.Exists("/var/lib/pacman/local")) list.AddRange(ReadPacman());
        else if (File.Exists("/lib/apk/db/installed")) list.AddRange(ParseApkInstalled(Sys.Lines("/lib/apk/db/installed")));
        if (Tools.Run("snap", ["list", "--color=never", "--unicode=never"], 6000) is { } snaps)
        {
            foreach (var line in snaps.Split('\n').Skip(1))
            {
                var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (f.Length >= 5) list.Add(new SoftwareEntry { Name = f[0], Version = f[1], Publisher = $"snap · {f[4].TrimEnd('*', '✓')}" });
            }
        }
        if (Tools.Run("flatpak", ["list", "--app", "--columns=application,version,origin"], 6000) is { } flatpaks)
        {
            foreach (var line in flatpaks.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var f = line.Split('\t');
                if (f.Length >= 1 && f[0].Length > 0) list.Add(new SoftwareEntry { Name = f[0], Version = f.ElementAtOrDefault(1) ?? "", Publisher = $"Flatpak · {f.ElementAtOrDefault(2)}" });
            }
        }
        return list;
    }

    // /var/lib/dpkg/status: one stanza per package; only installed ones count.
    private static IEnumerable<SoftwareEntry> ReadDpkg()
    {
        string? name = null, version = null, maintainer = null, status = null, architecture = null;
        long sizeKb = 0;
        var result = new List<SoftwareEntry>();
        void Flush()
        {
            if (name is not null && status?.EndsWith(" installed", StringComparison.Ordinal) == true)
            {
                var listFile = File.Exists($"/var/lib/dpkg/info/{name}:{architecture}.list") ? $"/var/lib/dpkg/info/{name}:{architecture}.list" : $"/var/lib/dpkg/info/{name}.list";
                result.Add(new SoftwareEntry
                {
                    Name = name,
                    Version = version ?? "",
                    Publisher = Maintainer().Replace(maintainer ?? "", "").Trim(),
                    InstallDate = File.Exists(listFile) ? File.GetLastWriteTime(listFile).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                    Size = sizeKb > 0 ? Size(sizeKb * 1024) : ""
                });
            }
            name = version = maintainer = status = architecture = null;
            sizeKb = 0;
        }
        foreach (var line in Sys.Lines("/var/lib/dpkg/status"))
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.StartsWith("Package: ", StringComparison.Ordinal)) name = line[9..];
            else if (line.StartsWith("Version: ", StringComparison.Ordinal)) version = line[9..];
            else if (line.StartsWith("Maintainer: ", StringComparison.Ordinal)) maintainer = line[12..];
            else if (line.StartsWith("Status: ", StringComparison.Ordinal)) status = line[8..];
            else if (line.StartsWith("Architecture: ", StringComparison.Ordinal)) architecture = line[14..];
            else if (line.StartsWith("Installed-Size: ", StringComparison.Ordinal)) long.TryParse(line[16..], out sizeKb);
        }
        Flush();
        return result;
    }

    private static IEnumerable<SoftwareEntry> ReadRpm()
    {
        var text = Tools.Run("rpm", ["-qa", "--qf", "%{NAME}\t%{VERSION}-%{RELEASE}\t%{VENDOR}\t%{INSTALLTIME}\t%{SIZE}\n"], 15000) ?? "";
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var f = line.Split('\t');
            if (f.Length < 5 || f[0].StartsWith("gpg-pubkey", StringComparison.Ordinal)) continue;
            yield return new SoftwareEntry
            {
                Name = f[0],
                Version = f[1],
                Publisher = f[2] == "(none)" ? "" : f[2],
                InstallDate = long.TryParse(f[3], out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                Size = long.TryParse(f[4], out var bytes) ? Size(bytes) : ""
            };
        }
    }

    private static IEnumerable<SoftwareEntry> ReadPacman()
    {
        foreach (var directory in Sys.Children("/var/lib/pacman/local"))
        {
            var lines = Sys.Lines($"/var/lib/pacman/local/{directory}/desc").ToList();
            string? Field(string key) { var i = lines.IndexOf($"%{key}%"); return i >= 0 && i + 1 < lines.Count ? lines[i + 1] : null; }
            if (Field("NAME") is not { } name) continue;
            yield return new SoftwareEntry
            {
                Name = name,
                Version = Field("VERSION") ?? "",
                Publisher = Maintainer().Replace(Field("PACKAGER") ?? "", "").Trim(),
                InstallDate = long.TryParse(Field("INSTALLDATE"), out var epoch) ? DateTimeOffset.FromUnixTimeSeconds(epoch).LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "",
                Size = long.TryParse(Field("SIZE"), out var bytes) ? Size(bytes) : ""
            };
        }
    }

    /// <summary>
    /// Alpine's /lib/apk/db/installed: one block per package, "P:name", "V:version", "m:maintainer", "I:size". It
    /// records when a package was built, not installed, so the install date stays empty.
    /// </summary>
    internal static List<SoftwareEntry> ParseApkInstalled(IEnumerable<string> lines)
    {
        var result = new List<SoftwareEntry>();
        string? name = null, version = null, maintainer = null, size = null;
        void Flush()
        {
            if (name is not null)
                result.Add(new SoftwareEntry
                {
                    Name = name,
                    Version = version ?? "",
                    Publisher = Maintainer().Replace(maintainer ?? "", "").Trim(),
                    Size = long.TryParse(size, out var bytes) ? Size(bytes) : ""
                });
            name = version = maintainer = size = null;
        }
        foreach (var line in lines)
        {
            if (line.Length == 0) { Flush(); continue; }
            if (line.Length < 2 || line[1] != ':') continue;
            var value = line[2..];
            switch (line[0])
            {
                case 'P': name = value; break;
                case 'V': version = value; break;
                case 'm': maintainer = value; break;
                case 'I': size = value; break;
            }
        }
        Flush();
        return result;
    }

    private static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0} KB",
        _ => $"{bytes} B"
    };

    // ---- Package history ----------------------------------------------------------------------------------

    private static List<PatchEntry> ReadPackageHistory(Distro distro)
    {
        var entries = new List<PatchEntry>();
        if (Directory.Exists("/var/log/apt")) entries.AddRange(ReadAptHistory());
        else if (File.Exists("/var/log/dnf.rpm.log")) entries.AddRange(ReadDnfHistory());
        else if (File.Exists("/var/log/pacman.log")) entries.AddRange(ReadPacmanHistory());
        return entries.OrderByDescending(e => e.InstalledDate).Take(400).ToList();
    }

    private static IEnumerable<PatchEntry> ReadAptHistory()
    {
        var files = Directory.GetFiles("/var/log/apt", "history.log*").OrderBy(f => f.Length).ThenBy(f => f, StringComparer.Ordinal).Take(6);
        var result = new List<PatchEntry>();
        foreach (var file in files)
        {
            IEnumerable<string> lines;
            try
            {
                lines = file.EndsWith(".gz", StringComparison.Ordinal)
                    ? new StreamReader(new GZipStream(File.OpenRead(file), CompressionMode.Decompress)).ReadToEnd().Split('\n')
                    : File.ReadAllLines(file);
            }
            catch
            {
                continue;
            }
            DateTime? date = null;
            string by = "";
            foreach (var line in lines)
            {
                if (line.StartsWith("Start-Date: ", StringComparison.Ordinal))
                {
                    date = DateTime.TryParse(line[12..].Replace("  ", " "), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
                    by = "apt";
                }
                else if (line.StartsWith("Commandline: ", StringComparison.Ordinal) && line.Contains("unattended-upgrade", StringComparison.Ordinal)) by = "automatic updates";
                else if (line.StartsWith("Requested-By: ", StringComparison.Ordinal)) by = line[14..].Split(' ')[0];
                else if (date is not null && AptAction().Match(line) is { Success: true } action)
                {
                    var verb = action.Groups[1].Value;
                    foreach (Match package in AptPackage().Matches(action.Groups[2].Value))
                    {
                        var versions = package.Groups[2].Value.Split(", ");
                        var description = verb switch
                        {
                            "Upgrade" => versions.Length >= 2 ? $"Upgraded {versions[0]} → {versions[1]}" : $"Upgraded to {versions[0]}",
                            "Install" => $"Installed {versions[0]}" + (versions.Contains("automatic") ? " (dependency)" : ""),
                            "Remove" or "Purge" => $"Removed {versions[0]}",
                            "Downgrade" => versions.Length >= 2 ? $"Downgraded {versions[0]} → {versions[1]}" : "Downgraded",
                            "Reinstall" => $"Reinstalled {versions[0]}",
                            _ => verb
                        };
                        result.Add(new PatchEntry
                        {
                            Id = package.Groups[1].Value.Split(':')[0],
                            Description = description,
                            InstalledOn = date.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                            InstalledBy = by,
                            InstalledDate = date
                        });
                    }
                }
            }
        }
        return result;
    }

    private static IEnumerable<PatchEntry> ReadDnfHistory()
    {
        foreach (var line in Sys.Lines("/var/log/dnf.rpm.log"))
        {
            var match = Regex.Match(line, @"^(\S+)\s+SUBDEBUG\s+(Upgrade|Upgraded|Install|Installed|Erase|Downgrade):\s+(\S+)");
            if (!match.Success || !DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            if (match.Groups[2].Value == "Upgraded") continue;
            yield return new PatchEntry { Id = match.Groups[3].Value, Description = match.Groups[2].Value, InstalledOn = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), InstalledBy = "dnf", InstalledDate = date };
        }
    }

    private static IEnumerable<PatchEntry> ReadPacmanHistory()
    {
        foreach (var line in Sys.Lines("/var/log/pacman.log"))
        {
            var match = Regex.Match(line, @"^\[([^\]]+)\] \[ALPM\] (upgraded|installed|removed|downgraded) (\S+) \(([^)]*)\)");
            if (!match.Success || !DateTime.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) continue;
            yield return new PatchEntry { Id = match.Groups[3].Value, Description = $"{char.ToUpperInvariant(match.Groups[2].Value[0])}{match.Groups[2].Value[1..]} {match.Groups[4].Value}", InstalledOn = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), InstalledBy = "pacman", InstalledDate = date };
        }
    }

    // ---- Updates waiting ------------------------------------------------------------------------------------

    private static UpdateState ReadUpdates(Distro distro)
    {
        if (Tools.Has("apt-get"))
        {
            // A simulated upgrade reads the package lists already downloaded; it changes nothing and needs no root.
            var simulated = Tools.Run("apt-get", ["-s", "-o", "Debug::NoLocking=1", "upgrade"], 20000);
            var installs = simulated?.Split('\n').Where(l => l.StartsWith("Inst ", StringComparison.Ordinal)).ToList();
            var automatic = Sys.Lines("/etc/apt/apt.conf.d/20auto-upgrades").Concat(Sys.Lines("/etc/apt/apt.conf.d/10periodic"))
                .Any(l => l.Contains("Unattended-Upgrade", StringComparison.Ordinal) && l.Contains("\"1\"", StringComparison.Ordinal));
            var last = AptLastUpdate();
            return new UpdateState("apt", installs?.Count, installs?.Count(l => l.Contains("-security", StringComparison.Ordinal)), last, automatic,
                simulated is null ? "Couldn't ask apt which updates are waiting" : null);
        }
        if (Tools.Has("dnf"))
        {
            var automatic = Tools.Run("systemctl", "is-enabled", "dnf-automatic.timer")?.Trim() == "enabled" || Tools.Run("systemctl", "is-enabled", "dnf5-automatic.timer")?.Trim() == "enabled";
            // -C: only the package lists already downloaded (exit 100 means updates are waiting). Nothing is fetched.
            var (code, output, _) = Tools.RunFull("dnf", ["-C", "-q", "check-update"], 30000);
            return code is 0 or 100
                ? new UpdateState("dnf", ParseDnfUpdates(output).Count, null, null, automatic, null)
                : new UpdateState("dnf", null, null, null, automatic, "dnf has no package lists cached for this account; dnf check-update would download them");
        }
        if (Tools.Has("zypper"))
        {
            var (code, output, _) = Tools.RunFull("zypper", ["--non-interactive", "--no-refresh", "-q", "list-updates"], 30000);
            var automatic = Tools.Run("systemctl", "is-enabled", "transactional-update.timer")?.Trim() == "enabled";
            return code is 0 or >= 100 and < 110
                ? new UpdateState("zypper", ParseZypperUpdates(output).Count, null, null, automatic, null)
                : new UpdateState("zypper", null, null, null, automatic, "Couldn't ask zypper which updates are waiting");
        }
        if (Tools.Has("pacman"))
        {
            // Against the package lists from the last sync (pacman -Sy); exit 1 with nothing printed means none.
            var (code, output, _) = Tools.RunFull("pacman", ["-Qu"], 20000);
            return code is 0 or 1
                ? new UpdateState("pacman", ParsePacmanUpdates(output).Count, null, PacmanLastUpgrade(), false, null)
                : new UpdateState("pacman", null, null, null, false, "Rolling release: run pacman -Syu to update");
        }
        if (Tools.Has("apk"))
        {
            var (code, output, _) = Tools.RunFull("apk", ["version", "-l", "<"], 20000);
            return code == 0
                ? new UpdateState("apk", ParseApkUpdates(output).Count, null, null, false, null)
                : new UpdateState("apk", null, null, null, false, "Couldn't ask apk which updates are waiting");
        }
        return new UpdateState(distro.Name, null, null, null, false, null);
    }

    /// <summary>dnf check-update: "name.arch  version  repository" lines, up to any "Obsoleting" section.</summary>
    internal static List<string> ParseDnfUpdates(string output)
    {
        var names = new List<string>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("Obsoleting", StringComparison.Ordinal)) break;
            if (line.StartsWith(' ')) continue;
            var f = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 3 && f[0].Contains('.') && char.IsDigit(f[1][0])) names.Add(f[0]);
        }
        return names;
    }

    /// <summary>zypper list-updates: a table whose update rows start with "v |".</summary>
    internal static List<string> ParseZypperUpdates(string output) =>
        output.Split('\n').Select(l => l.Split('|').Select(c => c.Trim()).ToArray())
            .Where(c => c.Length >= 5 && c[0] == "v").Select(c => c[2]).ToList();

    /// <summary>pacman -Qu: "name 1.0-1 -> 1.1-1".</summary>
    internal static List<string> ParsePacmanUpdates(string output) =>
        output.Split('\n').Where(l => l.Contains(" -> ", StringComparison.Ordinal)).Select(l => l.Split(' ')[0]).ToList();

    /// <summary>apk version -l '&lt;': "name-1.2-r0   &lt; 1.2-r1" under an "Installed: Available:" header.</summary>
    internal static List<string> ParseApkUpdates(string output) =>
        output.Split('\n').Where(l => l.Contains(" < ", StringComparison.Ordinal) && !l.StartsWith("Installed", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]).ToList();

    private static DateTime? PacmanLastUpgrade()
    {
        DateTime? latest = null;
        foreach (var line in Sys.Lines("/var/log/pacman.log"))
        {
            if (!line.Contains("starting full system upgrade", StringComparison.Ordinal)) continue;
            var close = line.IndexOf(']');
            if (line.StartsWith('[') && close > 1 && DateTime.TryParse(line[1..close], CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) latest = d;
        }
        return latest;
    }

    private static DateTime? AptLastUpdate()
    {
        DateTime? latest = null;
        foreach (var line in Sys.Lines("/var/log/apt/history.log"))
            if (line.StartsWith("Start-Date: ", StringComparison.Ordinal) &&
                DateTime.TryParse(line[12..].Replace("  ", " "), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
                latest = d;
        return latest;
    }

    // ---- Timers and cron ------------------------------------------------------------------------------------

    private static List<TaskEntry> ReadTasks()
    {
        var list = new List<TaskEntry>();
        list.AddRange(Timers(user: false));
        list.AddRange(Timers(user: true));
        foreach (var file in new[] { "/etc/crontab" }.Concat(Directory.Exists("/etc/cron.d") ? Directory.GetFiles("/etc/cron.d") : []))
        {
            foreach (var line in Sys.Lines(file).Select(l => l.Trim()))
            {
                if (line.Length == 0 || line.StartsWith('#') || line.Contains('=') && !line.Contains(' ')) continue;
                var f = line.Split((char[])[' ', '\t'], 7, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 7 || Regex.IsMatch(f[0], @"^[A-Z_]+=")) continue;
                list.Add(new TaskEntry { Name = Shorten(f[6]), Folder = file, Status = "Scheduled", NextRun = string.Join(' ', f.Take(5)) + $" (as {f[5]})", Category = "Cron" });
            }
        }
        foreach (var (folder, when) in new[] { ("/etc/cron.hourly", "Hourly"), ("/etc/cron.daily", "Daily"), ("/etc/cron.weekly", "Weekly"), ("/etc/cron.monthly", "Monthly") })
            foreach (var script in Sys.Children(folder).Where(n => !n.StartsWith('.') && n != "0anacron"))
                list.Add(new TaskEntry { Name = script, Folder = folder, Status = "Scheduled", NextRun = when, Category = "Cron" });
        return list;
    }

    private static IEnumerable<TaskEntry> Timers(bool user)
    {
        var args = new List<string>();
        if (user) args.Add("--user");
        args.AddRange(["list-timers", "--all", "--output=json", "--no-pager"]);
        var text = Tools.Run("systemctl", args, 5000);
        if (string.IsNullOrWhiteSpace(text)) yield break;
        JsonDocument document;
        try { document = JsonDocument.Parse(text); }
        catch (JsonException) { yield break; }
        using (document)
        {
            foreach (var timer in document.RootElement.EnumerateArray())
            {
                var unit = timer.TryGetProperty("unit", out var u) ? u.GetString() ?? "" : "";
                long? next = timer.TryGetProperty("next", out var n) && n.ValueKind == JsonValueKind.Number ? n.GetInt64() : null;
                var activates = timer.TryGetProperty("activates", out var a) ? a.GetString() : null;
                yield return new TaskEntry
                {
                    Name = unit.EndsWith(".timer", StringComparison.Ordinal) ? unit[..^6] : unit,
                    Folder = activates is null ? "systemd timer" : $"runs {activates}",
                    Status = next is > 0 ? "Waiting" : "Inactive",
                    NextRun = next is > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(next.Value / 1000).LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "",
                    Category = user ? "User" : "System"
                };
            }
        }
    }

    private static string Shorten(string command) => command.Length > 90 ? command[..87] + "…" : command;

    [GeneratedRegex(@"\s*<[^>]*>")]
    private static partial Regex Maintainer();

    [GeneratedRegex(@"^(Upgrade|Install|Remove|Purge|Downgrade|Reinstall): (.*)$")]
    private static partial Regex AptAction();

    [GeneratedRegex(@"([^\s,()]+) \(([^)]*)\)")]
    private static partial Regex AptPackage();

    [GeneratedRegex(@"^(\S+) (\S+) \[(.+)\]$", RegexOptions.Multiline)]
    private static partial Regex DotNetLine();

    [GeneratedRegex(@"^(\S+) \[(.+)\]$", RegexOptions.Multiline)]
    private static partial Regex SdkLine();
}
