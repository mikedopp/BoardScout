using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardScout.Models;

public sealed class ScanManifest
{
    [JsonPropertyName("schema_version")] public string SchemaVersion { get; set; } = "";
    [JsonPropertyName("scan")] public ScanMetadata Scan { get; set; } = new();
    [JsonPropertyName("system")] public MachineSystem SystemInfo { get; set; } = new();
    [JsonPropertyName("components")] public List<HardwareComponent> Components { get; set; } = [];
    [JsonPropertyName("drivers")] public List<InstalledDriver> Drivers { get; set; } = [];
    [JsonPropertyName("devices")] public List<PnpDevice> Devices { get; set; } = [];
    [JsonPropertyName("memory")] public MemoryInfo Memory { get; set; } = new();
    [JsonPropertyName("volumes")] public List<VolumeInfo> Volumes { get; set; } = [];
    [JsonPropertyName("dead_devices")] public List<ProblemDevice> ProblemDevices { get; set; } = [];
    [JsonPropertyName("usb_devices")] public List<UsbDevice> UsbDevices { get; set; } = [];
    [JsonPropertyName("pcie_topology")] public List<PcieDevice> PcieTopology { get; set; } = [];
    [JsonPropertyName("expansion_slots")] public List<ExpansionSlot> ExpansionSlots { get; set; } = [];
    [JsonPropertyName("form_factor")] public string FormFactor { get; set; } = "unknown";

    public CpuInfo Cpu => SystemInfo.GetCpu();
    public double TotalMemoryGb => Memory.TotalBytes / 1_073_741_824d;
}

public sealed class ScanMetadata
{
    [JsonPropertyName("tool")] public string Tool { get; set; } = "";
    [JsonPropertyName("tool_version")] public string ToolVersion { get; set; } = "";
    [JsonPropertyName("timestamp_utc")] public DateTimeOffset? TimestampUtc { get; set; }
    [JsonPropertyName("hostname")] public string Hostname { get; set; } = "";
    [JsonPropertyName("machine_id")] public string MachineId { get; set; } = "";
    [JsonPropertyName("os")] public OsInfo Os { get; set; } = new();
}

public sealed class OsInfo
{
    [JsonPropertyName("caption")] public string Caption { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("arch")] public string Architecture { get; set; } = "";
}

public sealed class MachineSystem
{
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("baseboard")] public BaseboardInfo Baseboard { get; set; } = new();
    [JsonPropertyName("bios")] public BiosInfo Bios { get; set; } = new();
    [JsonPropertyName("cpu")] public JsonElement CpuElement { get; set; }

    private CpuInfo? _cpu;

    // The board map asks for the CPU on every paint; parse the element once.
    public CpuInfo GetCpu() => _cpu ??= ParseCpu();

    private CpuInfo ParseCpu()
    {
        try
        {
            if (CpuElement.ValueKind == JsonValueKind.Array)
                return CpuElement.EnumerateArray().FirstOrDefault().Deserialize(DriverJson.Default.CpuInfo) ?? new();
            if (CpuElement.ValueKind == JsonValueKind.Object)
                return CpuElement.Deserialize(DriverJson.Default.CpuInfo) ?? new();
        }
        catch { }
        return new();
    }
}

public sealed class BaseboardInfo
{
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("product")] public string Product { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
}

public sealed class BiosInfo
{
    [JsonPropertyName("vendor")] public string Vendor { get; set; } = "";
    [JsonPropertyName("version")] public string Version { get; set; } = "";
    [JsonPropertyName("release_date")] public string ReleaseDate { get; set; } = "";
}

public sealed class CpuInfo
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("cores")] public int Cores { get; set; }
    [JsonPropertyName("threads")] public int Threads { get; set; }
}

public sealed class MemoryInfo
{
    [JsonPropertyName("total_slots")] public int TotalSlots { get; set; }
    [JsonPropertyName("populated")] public int Populated { get; set; }
    [JsonPropertyName("total_bytes")] public long TotalBytes { get; set; }
    [JsonPropertyName("slots")] public List<MemorySlot> Slots { get; set; } = [];
}

public sealed class MemorySlot
{
    [JsonPropertyName("bank")] public string Bank { get; set; } = "";
    [JsonPropertyName("locator")] public string Locator { get; set; } = "";
    [JsonPropertyName("capacity_gb")] public double CapacityGb { get; set; }
    [JsonPropertyName("speed_mhz")] public int SpeedMhz { get; set; }
    [JsonPropertyName("rated_mhz")] public int RatedMhz { get; set; }
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; set; } = "";
    [JsonPropertyName("part_number")] public string PartNumber { get; set; } = "";
}

public sealed class HardwareComponent
{
    [JsonPropertyName("component_key")] public string ComponentKey { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("current")] public CurrentVersion Current { get; set; } = new();
    [JsonPropertyName("lookup_hints")] public LookupHints LookupHints { get; set; } = new();
}

public sealed class CurrentVersion
{
    [JsonPropertyName("driver_version")] public string? DriverVersion { get; set; }
    [JsonPropertyName("driver_date")] public string? DriverDate { get; set; }
    [JsonPropertyName("driver_source")] public string? DriverSource { get; set; }
    [JsonPropertyName("firmware")] public string? Firmware { get; set; }
}

public sealed class LookupHints
{
    [JsonPropertyName("bus_type")] public string? BusType { get; set; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; set; }
    [JsonPropertyName("media_type")] public string? MediaType { get; set; }
}

public sealed class VolumeInfo
{
    [JsonPropertyName("letter")] public string Letter { get; set; } = "";
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("file_system")] public string FileSystem { get; set; } = "";
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("free_bytes")] public long FreeBytes { get; set; }
    [JsonPropertyName("drive_type")] public string DriveType { get; set; } = "";
    [JsonPropertyName("disk_model")] public string? DiskModel { get; set; }
    [JsonPropertyName("bus_type")] public string? BusType { get; set; }

    public double UsedPercent => SizeBytes <= 0 ? 0 : (SizeBytes - FreeBytes) * 100d / SizeBytes;
}

public sealed class ProblemDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("class")] public string DeviceClass { get; set; } = "";
    [JsonPropertyName("error_code")] public int ErrorCode { get; set; }
    [JsonPropertyName("error_desc")] public string Description { get; set; } = "";
}

public sealed class UsbDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("class")] public string DeviceClass { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
}

public sealed class PcieDevice
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("class")] public string DeviceClass { get; set; } = "";
    [JsonPropertyName("location")] public string Location { get; set; } = "";
}

public sealed class ExpansionSlot
{
    [JsonPropertyName("designation")] public string Designation { get; set; } = "";
    [JsonPropertyName("description")] public string Description { get; set; } = "";
    [JsonPropertyName("current_usage")] public int CurrentUsage { get; set; }
}

public sealed class DriverReport
{
    [JsonPropertyName("generated_utc")] public DateTimeOffset? GeneratedUtc { get; set; }
    [JsonPropertyName("based_on_scan")] public string BasedOnScan { get; set; } = "";
    [JsonPropertyName("results")] public List<DriverResult> Results { get; set; } = [];
}

public sealed class DriverResult
{
    [JsonPropertyName("component_key")] public string ComponentKey { get; set; } = "";
    [JsonPropertyName("category")] public string Category { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
    [JsonPropertyName("best")] public DriverCandidate Best { get; set; } = new();
}

public sealed class DriverCandidate
{
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("trust")] public string Trust { get; set; } = "";
    [JsonPropertyName("status")] public string Status { get; set; } = "";
    [JsonPropertyName("installed_version")] public string? InstalledVersion { get; set; }
    [JsonPropertyName("latest_version")] public string? LatestVersion { get; set; }
    [JsonPropertyName("latest_date")] public string? LatestDate { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }
    [JsonPropertyName("tool")] public string? Tool { get; set; }
    [JsonPropertyName("download_url")] public string? DownloadUrl { get; set; }
}

/// <summary>One signed driver Windows has installed for a device (Win32_PnPSignedDriver).</summary>
public sealed class InstalledDriver
{
    [JsonPropertyName("device_name")] public string DeviceName { get; set; } = "";
    [JsonPropertyName("device_class")] public string? DeviceClass { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("inf")] public string? Inf { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
}

/// <summary>Every Plug and Play device, with or without a driver (Win32_PnPEntity).</summary>
public sealed class PnpDevice
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("class")] public string? DeviceClass { get; set; }
    [JsonPropertyName("manufacturer")] public string? Manufacturer { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("present")] public bool? Present { get; set; }
    [JsonPropertyName("bus")] public string? Bus { get; set; }
    [JsonPropertyName("ven")] public string? Ven { get; set; }
    [JsonPropertyName("dev")] public string? Dev { get; set; }
    [JsonPropertyName("hardware_id")] public string? HardwareId { get; set; }
}

// Source-generated metadata for the scan and the driver report (no reflection warm-up on load).
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ScanManifest))]
[JsonSerializable(typeof(DriverReport))]
[JsonSerializable(typeof(CpuInfo))]
internal sealed partial class DriverJson : JsonSerializerContext;
