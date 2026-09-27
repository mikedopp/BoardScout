using System.Text.Json.Serialization;

namespace BoardScout.Models;

// Payload for the Connections view's optimization plan. Property names are the JavaScript field names.

public sealed class OptimizationPlan
{
    [JsonPropertyName("type")] public string Type { get; set; } = "plan";
    [JsonPropertyName("formFactor")] public string FormFactor { get; set; } = "Desktop";
    [JsonPropertyName("sections")] public List<PlanSection> Sections { get; set; } = [];
    [JsonPropertyName("usbControllers")] public List<UsbControllerSummary> UsbControllers { get; set; } = [];
    [JsonPropertyName("memorySlots")] public List<MemorySlotRow> MemorySlots { get; set; } = [];
    [JsonPropertyName("cores")] public List<CoreLoadRow> Cores { get; set; } = [];
    [JsonPropertyName("drivers")] public List<DriverLoadRow>? Drivers { get; set; }
    [JsonPropertyName("profileSeconds")] public double? ProfileSeconds { get; set; }
    [JsonPropertyName("profileError")] public string? ProfileError { get; set; }
    [JsonPropertyName("eventsLost")] public long EventsLost { get; set; }
    [JsonPropertyName("elevated")] public bool Elevated { get; set; }
    [JsonPropertyName("gatheredMs")] public long GatheredMs { get; set; }
}

public sealed class PlanSection
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("summary")] public string Summary { get; set; } = "";
    [JsonPropertyName("items")] public List<PlanItem> Items { get; set; } = [];
}

public sealed class PlanItem
{
    /// <summary>good, info, improve, or warn.</summary>
    [JsonPropertyName("tone")] public string Tone { get; set; } = "info";
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("detail")] public string Detail { get; set; } = "";
    [JsonPropertyName("action")] public string? Action { get; set; }

    /// <summary>Connections map cards this item is about.</summary>
    [JsonPropertyName("nodes")] public List<string> Nodes { get; set; } = [];
}

public sealed class UsbControllerSummary
{
    [JsonPropertyName("node")] public string Node { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>CPU, chipset, or add-in card.</summary>
    [JsonPropertyName("where")] public string Where { get; set; } = "";
    [JsonPropertyName("usb3Ports")] public int Usb3Ports { get; set; }
    [JsonPropertyName("usb3Used")] public int Usb3Used { get; set; }
    [JsonPropertyName("usb2Ports")] public int Usb2Ports { get; set; }
    [JsonPropertyName("usb2Used")] public int Usb2Used { get; set; }
    [JsonPropertyName("devices")] public List<string> Devices { get; set; } = [];
    [JsonPropertyName("powerMa")] public int PowerMa { get; set; }
}

public sealed class MemorySlotRow
{
    [JsonPropertyName("slot")] public string Slot { get; set; } = "";
    [JsonPropertyName("locator")] public string Locator { get; set; } = "";
    [JsonPropertyName("channel")] public string? Channel { get; set; }
    [JsonPropertyName("populated")] public bool Populated { get; set; }
    [JsonPropertyName("sizeGb")] public double SizeGb { get; set; }
    [JsonPropertyName("speed")] public int Speed { get; set; }
    [JsonPropertyName("configured")] public int Configured { get; set; }
    [JsonPropertyName("part")] public string? Part { get; set; }
    [JsonPropertyName("recommended")] public bool Recommended { get; set; }
}

public sealed class CoreLoadRow
{
    [JsonPropertyName("core")] public int Core { get; set; }
    [JsonPropertyName("interruptPct")] public double InterruptPercent { get; set; }
    [JsonPropertyName("dpcPct")] public double DpcPercent { get; set; }
    [JsonPropertyName("interruptsPerSec")] public double InterruptsPerSecond { get; set; }
    [JsonPropertyName("dpcsPerSec")] public double DpcsPerSecond { get; set; }
}

public sealed class DriverLoadRow
{
    [JsonPropertyName("driver")] public string Driver { get; set; } = "";
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("devices")] public List<string> Devices { get; set; } = [];
    [JsonPropertyName("isrCount")] public long IsrCount { get; set; }
    [JsonPropertyName("isrMs")] public double IsrMs { get; set; }
    [JsonPropertyName("isrMaxUs")] public double IsrMaxUs { get; set; }
    [JsonPropertyName("dpcCount")] public long DpcCount { get; set; }
    [JsonPropertyName("dpcMs")] public double DpcMs { get; set; }
    [JsonPropertyName("dpcMaxUs")] public double DpcMaxUs { get; set; }
    [JsonPropertyName("cpuPct")] public double CpuPercent { get; set; }
}
