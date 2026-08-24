using System.Text.Json.Serialization;

namespace BoardScout.Models;

public sealed class SystemInfoSnapshot
{
    [JsonPropertyName("os")] public OsAnalysisInfo Os { get; set; } = new();
    [JsonPropertyName("build")] public BuildSummary Build { get; set; } = new();
    [JsonPropertyName("dotnet")] public List<DotNetEntry> DotNet { get; set; } = [];
    [JsonPropertyName("patches")] public List<PatchEntry> Patches { get; set; } = [];
    [JsonPropertyName("software")] public List<SoftwareEntry> Software { get; set; } = [];
    [JsonPropertyName("tasks")] public List<TaskEntry> Tasks { get; set; } = [];
}

public sealed class OsAnalysisInfo
{
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("arch")] public string Architecture { get; set; } = "";
    [JsonPropertyName("installDate")] public string InstallDate { get; set; } = "";
    [JsonPropertyName("lastBoot")] public string LastBoot { get; set; } = "";
    [JsonPropertyName("uptime")] public string Uptime { get; set; } = "";
    [JsonPropertyName("supportStatus")] public string SupportStatus { get; set; } = "";
    [JsonPropertyName("verdict")] public string Verdict { get; set; } = "";
    [JsonPropertyName("verdictEmoji")] public string VerdictEmoji { get; set; } = "";
    [JsonPropertyName("traits")] public List<string> Traits { get; set; } = [];
    [JsonPropertyName("patchHealth")] public string PatchHealth { get; set; } = "";
    [JsonPropertyName("recommendations")] public List<string> Recommendations { get; set; } = [];
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
    [JsonPropertyName("path")] public string Path { get; set; } = "";
}

public sealed class PatchEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("installedOn")] public string InstalledOn { get; set; } = "";
    [JsonPropertyName("installedBy")] public string InstalledBy { get; set; } = "";
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
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("author")] public string Author { get; set; } = "";
    [JsonPropertyName("nextRun")] public string NextRun { get; set; } = "";
    [JsonPropertyName("lastResult")] public string LastResult { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
}
