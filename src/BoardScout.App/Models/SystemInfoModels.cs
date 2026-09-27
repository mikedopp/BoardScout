using System.Text.Json.Serialization;

namespace BoardScout.Models;

// Payload for Assets\system.html. Property names are the JavaScript field names.

public sealed class SystemInfoSnapshot
{
    [JsonPropertyName("os")] public OsAnalysisInfo Os { get; set; } = new();
    [JsonPropertyName("readiness")] public UpgradeReadiness? Readiness { get; set; }
    [JsonPropertyName("build")] public BuildSummary Build { get; set; } = new();
    [JsonPropertyName("dotnet")] public List<DotNetEntry> DotNet { get; set; } = [];
    [JsonPropertyName("patches")] public List<PatchEntry> Patches { get; set; } = [];
    [JsonPropertyName("software")] public List<SoftwareEntry> Software { get; set; } = [];
    [JsonPropertyName("tasks")] public List<TaskEntry> Tasks { get; set; } = [];
    [JsonPropertyName("gatheredMs")] public long GatheredMs { get; set; }
}

public sealed class OsAnalysisInfo
{
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("edition")] public string Edition { get; set; } = "";
    [JsonPropertyName("displayVersion")] public string DisplayVersion { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("arch")] public string Architecture { get; set; } = "";
    [JsonPropertyName("installDate")] public string InstallDate { get; set; } = "";
    [JsonPropertyName("lastBoot")] public string LastBoot { get; set; } = "";
    [JsonPropertyName("uptime")] public string Uptime { get; set; } = "";
    [JsonPropertyName("supportStatus")] public string SupportStatus { get; set; } = "";
    [JsonPropertyName("supportTone")] public string SupportTone { get; set; } = "info";
    [JsonPropertyName("supportEnds")] public string SupportEnds { get; set; } = "";
    [JsonPropertyName("supportDaysLeft")] public int? SupportDaysLeft { get; set; }
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("verdictEmoji")] public string VerdictEmoji { get; set; } = "computer";
    [JsonPropertyName("traits")] public List<string> Traits { get; set; } = [];
    [JsonPropertyName("patchHealth")] public string PatchHealth { get; set; } = "";
    [JsonPropertyName("patchTone")] public string PatchTone { get; set; } = "info";
    [JsonPropertyName("lastPatch")] public string LastPatch { get; set; } = "";
    [JsonPropertyName("recommendations")] public List<string> Recommendations { get; set; } = [];

    [JsonIgnore] public int BuildNumber { get; set; }
    [JsonIgnore] public int Ubr { get; set; }
    [JsonIgnore] public string EditionId { get; set; } = "";
}

public sealed class UpgradeReadiness
{
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("tone")] public string Tone { get; set; } = "info";
    [JsonPropertyName("checks")] public List<ReadinessCheck> Checks { get; set; } = [];
}

public sealed class ReadinessCheck
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "unknown"; // pass | warn | fail | unknown
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
}

public sealed class BuildSummary
{
    [JsonPropertyName("cpu")] public string Cpu { get; set; } = "";
    [JsonPropertyName("cpuDetail")] public string CpuDetail { get; set; } = "";
    [JsonPropertyName("memory")] public string Memory { get; set; } = "";
    [JsonPropertyName("gpu")] public string Gpu { get; set; } = "";
    [JsonPropertyName("motherboard")] public string Motherboard { get; set; } = "";
    [JsonPropertyName("bios")] public string Bios { get; set; } = "";
    [JsonPropertyName("tpm")] public string Tpm { get; set; } = "";
    [JsonPropertyName("secureBoot")] public string SecureBoot { get; set; } = "";
    [JsonPropertyName("disks")] public List<DiskEntry> Disks { get; set; } = [];

    [JsonIgnore] public string CpuName { get; set; } = "";
    [JsonIgnore] public double MemoryGb { get; set; }
    [JsonIgnore] public int? TpmVersion { get; set; }          // 1 = TPM 1.2, 2 = TPM 2.0, null = none found
    [JsonIgnore] public bool? SecureBootEnabled { get; set; }   // null = legacy BIOS boot (no UEFI Secure Boot state)
}

public sealed class DiskEntry
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("size")] public string Size { get; set; } = "";
    [JsonPropertyName("bus")] public string Bus { get; set; } = "";
    [JsonPropertyName("free")] public string Free { get; set; } = "";
    [JsonPropertyName("usedPercent")] public double UsedPercent { get; set; }
}

public sealed class DotNetEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "runtime"; // runtime | sdk | framework | self
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public sealed class PatchEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("installedOn")] public string InstalledOn { get; set; } = ""; // yyyy-MM-dd so text sorts by date
    [JsonPropertyName("installedBy")] public string InstalledBy { get; set; } = "";

    [JsonIgnore] public DateTime? InstalledDate { get; set; }
}

public sealed class SoftwareEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("publisher")] public string Publisher { get; set; } = "";
    [JsonPropertyName("installDate")] public string InstallDate { get; set; } = "";
    [JsonPropertyName("size")] public string Size { get; set; } = "";
}

public sealed class TaskEntry
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("folder")] public string Folder { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("nextRun")] public string NextRun { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}
