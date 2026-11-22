using System.Text.Json;
using System.Text.Json.Serialization;

namespace BoardScout.Models;

// The scan and driver-check models (ScanManifest, DriverReport and the rest) come with the driver engine in
// Services/DriverCore/ScanModels.cs.

public enum SuggestionSeverity { Info, Improvement, Warning, Critical }

public sealed record EfficiencySuggestion(
    SuggestionSeverity Severity,
    string Title,
    string Detail,
    string Action,
    string Category);

// Source-generated metadata: no reflection warm-up when the cached scan loads at startup.
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(ScanManifest))]
[JsonSerializable(typeof(DriverReport))]
[JsonSerializable(typeof(CpuInfo))]
[JsonSerializable(typeof(SystemInfoSnapshot))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ConnectionsSnapshot))]
[JsonSerializable(typeof(ConnectionFlow))]
[JsonSerializable(typeof(WanLookup))]
[JsonSerializable(typeof(BoardScout.Services.SpeedTestUpdate))]
[JsonSerializable(typeof(OptimizationPlan))]
internal sealed partial class BoardScoutJson : JsonSerializerContext;
