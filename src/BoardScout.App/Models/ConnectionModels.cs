using System.Text.Json.Serialization;

namespace BoardScout.Models;

// Payload for Assets\connections.html. Property names are the JavaScript field names.
// Nothing here carries device instance IDs or serial numbers: USB and disk instance IDs embed serials.

public sealed class ConnectionsSnapshot
{
    [JsonPropertyName("root")] public ConnectionNode Root { get; set; } = new();
    [JsonPropertyName("notes")] public List<string> Notes { get; set; } = [];
    [JsonPropertyName("privacy")] public bool Privacy { get; set; }
    [JsonPropertyName("elevated")] public bool Elevated { get; set; }
    [JsonPropertyName("gatheredMs")] public long GatheredMs { get; set; }
    [JsonPropertyName("gatheredAt")] public string GatheredAt { get; set; } = "";
}

public sealed class ConnectionNode
{
    /// <summary>Short stable id (a hash of the device instance, never the instance itself).</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>cpu, chipset, integrated, gpu, monitor, nvme, sata, disk, usb-controller, usb-hub, usb, storage,
    /// input, audio, capture, bluetooth, bt-device, ethernet, wifi, router, internet, dns.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";

    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("link")] public ConnectionLink? Link { get; set; }
    [JsonPropertyName("facts")] public List<ConnectionFact> Facts { get; set; } = [];
    [JsonPropertyName("warning")] public string? Warning { get; set; }
    [JsonPropertyName("note")] public string? Note { get; set; }

    /// <summary>Physical disk number, for live read/write rates.</summary>
    [JsonPropertyName("disk")] public int? Disk { get; set; }

    /// <summary>Key of the network interface, for live receive/send rates.</summary>
    [JsonPropertyName("net")] public string? Net { get; set; }

    /// <summary>Which live temperature belongs on this card: cpu, gpu, or chipset.</summary>
    [JsonPropertyName("sensor")] public string? Sensor { get; set; }

    [JsonPropertyName("tempC")] public double? TemperatureC { get; set; }
    [JsonPropertyName("problem")] public bool Problem { get; set; }
    [JsonPropertyName("children")] public List<ConnectionNode> Children { get; set; } = [];
}

/// <summary>How a device connects to the one above it.</summary>
public sealed class ConnectionLink
{
    /// <summary>pcie, internal, usb, sata, display, bluetooth, ethernet, wifi, wan, lan.</summary>
    [JsonPropertyName("bus")] public string Bus { get; set; } = "";

    /// <summary>Full label, for example "PCIe 3.0 x4" or "USB 3.2 Gen 1 (5 Gbps)".</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "";

    /// <summary>Badge text on the diagram, for example "3.0 x4" or "5 Gbps".</summary>
    [JsonPropertyName("short")] public string Short { get; set; } = "";

    /// <summary>Usable data rate in gigabits per second, when known.</summary>
    [JsonPropertyName("gbps")] public double? Gbps { get; set; }

    [JsonPropertyName("max")] public string? Max { get; set; }
    [JsonPropertyName("maxGbps")] public double? MaxGbps { get; set; }
    [JsonPropertyName("degraded")] public bool Degraded { get; set; }
}

public sealed class ConnectionFact
{
    public ConnectionFact() { }

    public ConnectionFact(string label, string value)
    {
        Label = label;
        Value = value;
    }

    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("value")] public string Value { get; set; } = "";
}

/// <summary>Live rates sent to the Connections view on every telemetry tick.</summary>
public sealed class ConnectionFlow
{
    [JsonPropertyName("type")] public string Type { get; set; } = "flow";

    /// <summary>Interface key → [received, sent] bytes per second.</summary>
    [JsonPropertyName("net")] public Dictionary<string, double[]> Network { get; set; } = [];

    /// <summary>Disk number → [read, written] bytes per second.</summary>
    [JsonPropertyName("disk")] public Dictionary<string, double[]> Disks { get; set; } = [];

    /// <summary>cpu, gpu, chipset → °C, when the sensor library can read them.</summary>
    [JsonPropertyName("temps")] public Dictionary<string, double> Temperatures { get; set; } = [];

    [JsonPropertyName("cpu")] public double CpuPercent { get; set; }
}

/// <summary>Result of the on-demand public IP lookup.</summary>
public sealed class WanLookup
{
    [JsonPropertyName("type")] public string Type { get; set; } = "wan";
    [JsonPropertyName("ok")] public bool Ok { get; set; }
    [JsonPropertyName("ip")] public string? Ip { get; set; }
    [JsonPropertyName("location")] public string? Location { get; set; }
    [JsonPropertyName("edge")] public string? Edge { get; set; }
    [JsonPropertyName("error")] public string? Error { get; set; }
    [JsonPropertyName("checkedAt")] public string? CheckedAt { get; set; }
}
