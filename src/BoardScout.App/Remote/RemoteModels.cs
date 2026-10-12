using System.Text.Json.Serialization;

namespace BoardScout.Remote;

/// <summary>
/// A computer BoardScout can reach over SSH: a Host from ~/.ssh/config, or one added on the Machines page.
/// Nothing secret is kept: SSH finds the key (from the config, the agent, or the key file named here).
/// </summary>
internal sealed record MachineEntry
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";

    /// <summary>What the Machines page calls it.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";

    /// <summary>What ssh connects to: a Host alias from ~/.ssh/config, or user@host.</summary>
    [JsonPropertyName("target")] public string Target { get; init; } = "";

    [JsonPropertyName("port")] public int? Port { get; init; }

    /// <summary>A private key file to use instead of SSH's own choice (its path, never its contents).</summary>
    [JsonPropertyName("identity")] public string? Identity { get; init; }

    /// <summary>Where BoardScout keeps its copy on that computer, instead of the default folder.</summary>
    [JsonPropertyName("remoteFolder")] public string? RemoteFolder { get; init; }

    /// <summary>"saved" (added here) or "ssh-config" (a Host in ~/.ssh/config).</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "saved";
}

/// <summary>What a machine said about itself before BoardScout ran anything there.</summary>
internal sealed record RemoteHost
{
    /// <summary>"linux", "windows", or another uname -s value (unsupported).</summary>
    public string Os { get; init; } = "";

    /// <summary>The build that runs there (linux-x64, linux-musl-arm64, win-x64…), or null when there is none.</summary>
    public string? Rid { get; init; }

    /// <summary>The processor as the machine reports it (x86_64, aarch64, AMD64…).</summary>
    public string Machine { get; init; } = "";

    /// <summary>Why no build fits, in plain words, when Rid is null.</summary>
    public string? Unsupported { get; init; }

    /// <summary>Set when Windows on Arm runs the x64 build under emulation.</summary>
    public string? Note { get; init; }
}

/// <summary>One machine's health at a glance, for the table that checks every machine at once.</summary>
internal sealed record MachineSummary
{
    [JsonPropertyName("platform")] public string Platform { get; init; } = "";
    [JsonPropertyName("hostname")] public string Hostname { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("osBuild")] public string OsBuild { get; init; } = "";
    [JsonPropertyName("support")] public string Support { get; init; } = "";
    [JsonPropertyName("supportTone")] public string SupportTone { get; init; } = "info";
    [JsonPropertyName("verdict")] public string Verdict { get; init; } = "";
    [JsonPropertyName("cpu")] public string Cpu { get; init; } = "";
    [JsonPropertyName("memory")] public string Memory { get; init; } = "";
    [JsonPropertyName("uptime")] public string Uptime { get; init; } = "";
    [JsonPropertyName("patches")] public string Patches { get; init; } = "";
    [JsonPropertyName("patchTone")] public string PatchTone { get; init; } = "info";

    /// <summary>The security or upgrade checks that didn't pass: label and state (warn, fail).</summary>
    [JsonPropertyName("checks")] public List<SummaryCheck> Checks { get; init; } = [];

    /// <summary>Plan items marked improve or warn, worst first, as titles.</summary>
    [JsonPropertyName("fixes")] public List<string> Fixes { get; init; } = [];

    /// <summary>Links running below what both ends support.</summary>
    [JsonPropertyName("slowLinks")] public int SlowLinks { get; init; }

    /// <summary>Devices reporting a problem (no driver, an error code).</summary>
    [JsonPropertyName("problems")] public int Problems { get; init; }

    [JsonPropertyName("fullest")] public string? Fullest { get; init; }
    [JsonPropertyName("fullestPercent")] public double? FullestPercent { get; init; }
    [JsonPropertyName("cpuTemp")] public double? CpuTemp { get; init; }
    [JsonPropertyName("elevated")] public bool Elevated { get; init; }
    [JsonPropertyName("version")] public string Version { get; init; } = "";
    [JsonPropertyName("gatheredMs")] public long GatheredMs { get; init; }
    [JsonPropertyName("checkedAt")] public string CheckedAt { get; init; } = "";
}

internal sealed record SummaryCheck([property: JsonPropertyName("label")] string Label, [property: JsonPropertyName("state")] string State, [property: JsonPropertyName("detail")] string Detail);
