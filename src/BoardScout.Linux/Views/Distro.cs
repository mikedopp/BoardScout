using System.Globalization;
using BoardScout.Platform;

namespace BoardScout.Views;

/// <summary>The distribution, from /etc/os-release.</summary>
internal sealed record Distro(
    string Id,
    IReadOnlyList<string> Like,
    string Name,
    string PrettyName,
    string? Version,
    string? VersionId,
    string? Codename,
    string? UbuntuCodename,
    string? Variant)
{
    public bool IsLike(string id) => Id == id || Like.Contains(id);

    public static Distro Read() => Parse(Sys.Lines("/etc/os-release").Any() ? Sys.Lines("/etc/os-release") : Sys.Lines("/usr/lib/os-release"));

    public static Distro Parse(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            var equals = line.IndexOf('=');
            if (equals <= 0 || line.StartsWith('#')) continue;
            values[line[..equals].Trim()] = line[(equals + 1)..].Trim().Trim('"', '\'');
        }
        string? V(string key) => values.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        var name = V("NAME") ?? "Linux";
        return new Distro(
            (V("ID") ?? "linux").ToLowerInvariant(),
            (V("ID_LIKE") ?? "").ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries),
            name,
            V("PRETTY_NAME") ?? name,
            V("VERSION"),
            V("VERSION_ID"),
            V("VERSION_CODENAME"),
            V("UBUNTU_CODENAME"),
            V("VARIANT"));
    }
}

/// <summary>Where a release stands in its support lifecycle.</summary>
internal sealed record SupportStatus(string Status, string Tone, string Ends, int? DaysLeft, bool Rolling);

/// <summary>
/// Support dates for common distributions, from their published lifecycles as of 2026-10-11 (Ubuntu's release
/// cycle page, Debian's security and LTS pages, Red Hat's lifecycle, Linux Mint's release list). Dates are
/// month-accurate; the distributions' own pages have the final word.
/// </summary>
internal static class Lifecycle
{
    public const string AsOf = "2026-10-11";

    // Ubuntu: standard security maintenance, then Expanded Security Maintenance with Ubuntu Pro.
    private static readonly Dictionary<string, (string Version, DateOnly Standard, DateOnly? Esm, bool Lts)> Ubuntu = new()
    {
        ["bionic"] = ("18.04 LTS", new(2023, 5, 31), new(2028, 4, 30), true),
        ["focal"] = ("20.04 LTS", new(2025, 5, 31), new(2030, 4, 30), true),
        ["jammy"] = ("22.04 LTS", new(2027, 5, 31), new(2032, 4, 30), true),
        ["noble"] = ("24.04 LTS", new(2029, 5, 31), new(2034, 4, 30), true),
        ["resolute"] = ("26.04 LTS", new(2031, 5, 31), new(2036, 4, 30), true),
        ["oracular"] = ("24.10", new(2025, 7, 10), null, false),
        ["plucky"] = ("25.04", new(2026, 1, 15), null, false),
        ["questing"] = ("25.10", new(2026, 7, 9), null, false)
    };

    // Debian: regular security support, then LTS (main architectures only).
    private static readonly Dictionary<string, (string Version, DateOnly Security, DateOnly Lts)> Debian = new()
    {
        ["buster"] = ("10", new(2022, 9, 10), new(2024, 6, 30)),
        ["bullseye"] = ("11", new(2024, 8, 14), new(2026, 8, 31)),
        ["bookworm"] = ("12", new(2026, 6, 10), new(2028, 6, 30)),
        ["trixie"] = ("13", new(2028, 8, 9), new(2030, 6, 30))
    };

    private static readonly Dictionary<string, string> DebianByVersion = new() { ["10"] = "buster", ["11"] = "bullseye", ["12"] = "bookworm", ["13"] = "trixie" };

    // Red Hat Enterprise Linux and its rebuilds (Rocky, Alma): end of maintenance support.
    private static readonly Dictionary<int, DateOnly> Rhel = new()
    {
        [7] = new(2024, 6, 30),
        [8] = new(2029, 5, 31),
        [9] = new(2032, 5, 31),
        [10] = new(2035, 5, 31)
    };

    public static SupportStatus Of(Distro distro, DateOnly today)
    {
        if (distro.Id is "arch" or "manjaro" or "endeavouros" or "garuda" or "void" or "gentoo" or "opensuse-tumbleweed" or "cachyos" ||
            distro.VersionId is null && distro.IsLike("arch"))
            return new SupportStatus("Rolling release: current as long as you update", "good", "Never (rolling)", null, true);

        // Ubuntu itself, and the many distributions built on an Ubuntu release (Mint, Pop!_OS, elementary, Zorin).
        var ubuntuCodename = distro.Id == "ubuntu" ? distro.Codename : distro.UbuntuCodename;
        if (ubuntuCodename is not null && Ubuntu.TryGetValue(ubuntuCodename, out var ubuntu))
        {
            var basedOn = distro.Id == "ubuntu" ? "" : $" (built on Ubuntu {ubuntu.Version})";
            if (today <= ubuntu.Standard)
                return Days($"Supported{basedOn}: security updates until {Month(ubuntu.Standard)}", ubuntu.Standard, today, ubuntu.Lts ? "good" : "info");
            if (ubuntu.Esm is { } esm && today <= esm)
                return Days($"Standard support ended {Month(ubuntu.Standard)}{basedOn}; Ubuntu Pro (ESM) covers it until {Month(esm)}", esm, today, "warning");
            return new SupportStatus($"End of life since {Month(ubuntu.Esm ?? ubuntu.Standard)}{basedOn}: no more security updates", "critical",
                Month(ubuntu.Esm ?? ubuntu.Standard), -1, false);
        }

        var codename = distro.Codename ?? (distro.VersionId is { } id && DebianByVersion.TryGetValue(id.Split('.')[0], out var c) ? c : null);
        if ((distro.Id is "debian" or "raspbian" || distro.IsLike("debian") && distro.Id != "ubuntu") && codename is not null && Debian.TryGetValue(codename, out var debian))
        {
            var name = distro.Id is "debian" ? "" : $" (built on Debian {debian.Version})";
            if (today <= debian.Security)
                return Days($"Supported{name}: security updates until {Month(debian.Security)}, then LTS to {Month(debian.Lts)}", debian.Security, today, "good");
            if (today <= debian.Lts)
                return Days($"In Debian LTS{name}: security fixes until {Month(debian.Lts)} (main architectures only)", debian.Lts, today, "warning");
            return new SupportStatus($"End of life since {Month(debian.Lts)}{name}: no more security updates", "critical", Month(debian.Lts), -1, false);
        }

        if (distro.Id is "rhel" or "rocky" or "almalinux" or "ol" or "centos" && int.TryParse(distro.VersionId?.Split('.')[0], out var major) && Rhel.TryGetValue(major, out var rhelEnd))
        {
            if (distro.Id == "centos") return new SupportStatus("CentOS Stream: supported while Red Hat develops the next RHEL minor release", "info", "Check centos.org", null, false);
            return today <= rhelEnd
                ? Days($"Supported: maintenance updates until {Month(rhelEnd)}", rhelEnd, today, major >= 9 ? "good" : "info")
                : new SupportStatus($"End of maintenance since {Month(rhelEnd)}", "critical", Month(rhelEnd), -1, false);
        }

        if (distro.Id == "fedora" && int.TryParse(distro.VersionId, out var fedora))
        {
            // Each release is supported until four weeks after the release two versions later (about 13 months).
            return fedora >= 43
                ? new SupportStatus($"Supported: Fedora {fedora} gets updates until about a month after Fedora {fedora + 2} comes out", "good", $"About a month after Fedora {fedora + 2}", null, false)
                : new SupportStatus($"Fedora {fedora} is past end of life: upgrade to a current release", "critical", "Ended", -1, false);
        }

        return new SupportStatus($"BoardScout has no support dates for {distro.Name}; check its own lifecycle page", "info", "", null, false);
    }

    private static SupportStatus Days(string status, DateOnly end, DateOnly today, string tone)
    {
        var days = end.DayNumber - today.DayNumber;
        if (tone == "good" && days < 180) tone = "warning";
        return new SupportStatus(status, tone, end.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), days, false);
    }

    private static string Month(DateOnly date) => date.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
}
