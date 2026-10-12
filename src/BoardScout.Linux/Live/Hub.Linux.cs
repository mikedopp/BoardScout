using System.Text.Json;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Remote;
using BoardScout.Services;
using BoardScout.Views;

namespace BoardScout.Live;

/// <summary>
/// The Linux half of the dashboard's engine: reads the hardware from /sys and /proc, builds every view, answers
/// the views' requests, and samples the kernel's counters for the live tiles.
/// </summary>
internal sealed partial class Hub
{
    private const string Platform = "linux";

    private readonly Telemetry _telemetry = new();
    private readonly SemaphoreSlim _reading = new(1, 1);
    private HardwareCapture? _capture;
    private LinuxConnections.BuildResult? _map;
    private DiscoveryResult? _discovery;
    private string _discoveryGateways = "";
    private IrqProfile? _profile;
    private FirmwareReport? _firmware;
    private CancellationTokenSource? _planCts;
    private bool _rediscoverQueued, _sweepQueued, _reloadQueued;

    private bool HasReading => _capture is not null;

    static partial void AddPlatformSettings(Dictionary<string, object?> values)
    {
        values["openBrowser"] = AppSettings.Current.OpenBrowser;
        values["port"] = AppSettings.Current.Port;
    }

    static partial void ApplyPlatformSettings(AppSettings settings, JsonElement message)
    {
        if (message.TryGetProperty("openBrowser", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False) settings.OpenBrowser = open.GetBoolean();
    }

    private void SendPlatformReady(Client client)
    {
        if (_firmware is not null) Send(client, "shell", FirmwareJson(_firmware));
    }

    // ---- Reading the hardware -------------------------------------------------------------------------

    /// <summary>
    /// Reads the hardware and network (fast), shows the map, then looks up router, network, and device names
    /// in the background (a few seconds) and shows the map again with them. Names are reused for two minutes.
    /// </summary>
    public async Task LoadAsync(bool rediscover, bool sweep = false)
    {
        if (!await _reading.WaitAsync(0))
        {
            _reloadQueued = true;
            _rediscoverQueued |= rediscover || sweep;
            _sweepQueued |= sweep;
            return;
        }
        try
        {
            do
            {
                _reloadQueued = false;
                rediscover |= _rediscoverQueued;
                sweep |= _sweepQueued;
                _rediscoverQueued = _sweepQueued = false;
                Broadcast("connections", "{\"type\":\"busy\"}");
                var capture = await Task.Run(HardwareCapture.Read);
                _capture = capture;
                _signature = DeviceSignature();
                Log($"READ: {capture.Pci.Count} PCI functions, {capture.UsbDevices.Count(d => !d.IsRootHub)} USB devices, {capture.Disks.Count} drives, " +
                    $"{capture.Links.Count(l => l.Kind != "loopback")} network interfaces in {capture.CaptureMs} ms.");
                var gateways = string.Join(",", capture.Network.Routers.Keys.Order());
                var fresh = _discovery is not null && !rediscover && !sweep && _discoveryGateways == gateways &&
                            DateTimeOffset.Now - _discovery.At < TimeSpan.FromMinutes(2);
                PublishFromCapture(fresh ? _discovery : null);
                if (fresh) continue;

                Broadcast("connections", sweep ? "{\"type\":\"discovery\",\"sweep\":true}" : "{\"type\":\"discovery\"}");
                if (sweep) Log("CONNECTIONS: pinging every address on the local network to find devices, at your request.");
                _discovery = await NetworkDiscovery.RunAsync(capture.Network, CancellationToken.None, sweep);
                _discoveryGateways = gateways;
                Log($"NETWORK: {_discovery.Devices.Count} devices named on the local network.");
                if (ReferenceEquals(_capture, capture)) PublishFromCapture(_discovery);
            }
            while (_reloadQueued);
        }
        catch (Exception ex)
        {
            Log("READ FAILED: " + ex.Message);
            Broadcast("shell", $"{{\"type\":\"error\",\"message\":{JsonSerializer.Serialize(ex.Message)}}}");
        }
        finally
        {
            _reading.Release();
        }
    }

    private void PublishFromReading() => PublishFromCapture();

    // Builds every view from what was last read, with the current privacy setting (no new read).
    private void PublishFromCapture(DiscoveryResult? discovery = null)
    {
        var capture = _capture;
        if (capture is null) return;
        discovery ??= _discovery;
        var privacy = AppSettings.Current.PrivacyMode;
        var map = LinuxConnections.Build(capture, privacy, discovery);
        _map = map;
        Publish("connections", $"{{\"type\":\"connections\",\"data\":{LinuxConnections.ToJson(map.Snapshot)}}}");
        var plan = LinuxPlan.Build(capture, map, new CoreSample([]), null, privacy);
        var overview = LinuxOverview.Build(capture, map, plan, privacy, _firmware);
        Publish("shell", $"{{\"type\":\"overview\",\"data\":{JsonSerializer.Serialize(overview, Json.Options)}}}");
        Publish("topology", $"{{\"type\":\"scan\",\"scan\":{LinuxOverview.ScanJson(capture, privacy, indented: false)}}}");
        if (!_payloads.ContainsKey("system")) _ = LoadSystemAsync();
    }

    private async Task LoadSystemAsync()
    {
        var capture = _capture;
        if (capture is null) return;
        try
        {
            var json = await LinuxSystemInfo.GatherJsonAsync(capture);
            Publish("system", $"{{\"type\":\"system\",\"data\":{json}}}");
            using var document = JsonDocument.Parse(json);
            Log($"SYSTEM: read packages, history, and timers in {document.RootElement.GetProperty("gatheredMs").GetInt64()} ms.");
        }
        catch (Exception ex)
        {
            Log("SYSTEM: " + ex.Message);
        }
    }

    // ---- Messages from the views ----------------------------------------------------------------------

    private async Task HandlePlatformAsync(Client? client, string view, string? type, JsonElement message)
    {
        switch (view, type)
        {
            case ("shell", "rescan"):
                Log("SCAN: reading every device again, at your request.");
                _payloads.TryRemove("system", out _);
                await LoadAsync(rediscover: true);
                break;
            case ("shell", "firmware"):
                Log("FIRMWARE: asking fwupd which devices it can update (nothing is downloaded or installed).");
                Broadcast("shell", "{\"type\":\"firmwarebusy\"}");
                _firmware = await Task.Run(FirmwareUpdates.Check);
                Log($"FIRMWARE: {_firmware.Devices.Count} devices known to fwupd, {_firmware.Updates.Count} updates waiting.");
                Broadcast("shell", FirmwareJson(_firmware));
                if (_firmware.Updates.Count > 0) PublishFromCapture();
                break;
            case ("connections", "refresh"):
                await LoadAsync(rediscover: true);
                break;
            case ("connections", "lanscan"):
                await LoadAsync(rediscover: true, sweep: true);
                break;
            case ("connections", "plan"):
                await BuildPlanAsync(measure: false);
                break;
            case ("connections", "irqprofile"):
                await BuildPlanAsync(measure: true);
                break;
            case ("connections", "eject"):
                if (message.TryGetProperty("id", out var id) && id.GetString() is { } nodeId) await EjectAsync(nodeId);
                break;
        }
    }

    private object Meta() => new
    {
        platform = Platform,
        version = AppInfo.Version,
        commit = AppInfo.Commit,
        runtime = Environment.Version.ToString(),
        dataFolder = DataFolder.Root,
        url = ListenUrl,
        user = Environment.UserName,
        root = _capture?.Root ?? false,
        kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? "",
        distro = Distro.Read().PrettyName,
        agent = Agent?.Label,
        ssh = Agent is null ? SshTool.Describe() : null,
        tools = new[] { "iw", "nmcli", "bluetoothctl", "udisksctl", "fwupdmgr", "systemctl", "apt-get", "dnf", "zypper", "pacman", "apk", "dpkg-query", "rpm", "snap", "flatpak", "dotnet", "udevadm" }
            .Select(n => new { name = n, path = Tools.Find(n) }).Where(t => t.path is not null || t.name is "iw" or "nmcli" or "bluetoothctl" or "udisksctl" or "fwupdmgr" or "systemctl").ToList()
    };

    private static string FirmwareJson(FirmwareReport report) =>
        $"{{\"type\":\"firmware\",\"data\":{JsonSerializer.Serialize(report, Json.Options)}}}";

    // The optimization plan, built from the last map plus a one-second interrupt sample; "measure" also counts
    // interrupts per device for ten seconds.
    private async Task BuildPlanAsync(bool measure)
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        try
        {
            Broadcast("connections", measure ? "{\"type\":\"planbusy\",\"measuring\":true}" : "{\"type\":\"planbusy\"}");
            if (_capture is null) _ = LoadAsync(rediscover: false);
            for (var i = 0; i < 150 && (_capture is null || _map is null); i++) await Task.Delay(100, cts.Token);
            if (_capture is not { } capture || _map is not { } map) throw new InvalidOperationException("The device map isn't ready yet.");
            var cores = LinuxPlan.SampleCoresAsync(TimeSpan.FromSeconds(1), cts.Token);
            if (measure)
            {
                Log("PLAN: counting interrupts per device for 10 seconds (/proc/interrupts), at your request.");
                _profile = await LinuxPlan.ProfileAsync(capture, TimeSpan.FromSeconds(10), AppSettings.Current.PrivacyMode, cts.Token);
                Log(_profile.Error is { } error ? "PLAN: " + error : $"PLAN: the busiest was {_profile.Rows.FirstOrDefault()?.Driver ?? "none"}.");
            }
            var sample = await cores;
            var plan = LinuxPlan.Build(capture, map, sample, _profile, AppSettings.Current.PrivacyMode);
            if (!cts.IsCancellationRequested) Broadcast("connections", JsonSerializer.Serialize(plan, Json.Options));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log("PLAN: " + ex.Message);
            Broadcast("connections", $"{{\"type\":\"planerror\",\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
        }
    }

    // Safely removes a USB drive from its card. The page sends the card id (a hash); the drive is found again in
    // the last read, and DriveEject checks it is still at the same port before touching it.
    private async Task EjectAsync(string id)
    {
        var capture = _capture;
        var device = capture?.UsbDevices.FirstOrDefault(d => LinuxConnections.UsbNodeId(d) == id);
        var disk = device is null ? null : capture!.Disks.FirstOrDefault(d => d.UsbPath == device.SysPath);
        var (ok, message) = disk is null ? (false, "That drive is no longer connected.") : await Task.Run(() => DriveEject.Run(disk));
        Log($"EJECT: {disk?.DisplayName ?? "drive"}: {message} (at your request)");
        Broadcast("connections", $"{{\"type\":\"eject\",\"id\":{JsonSerializer.Serialize(id)},\"ok\":{(ok ? "true" : "false")},\"message\":{JsonSerializer.Serialize(message)}}}");
        if (ok) await LoadAsync(rediscover: false);
    }

    // ---- Live --------------------------------------------------------------------------------------------

    private (string? Header, string? Flow) SampleLive(bool detailed)
    {
        var (header, flow) = _telemetry.Sample(_capture);
        return (Telemetry.HeaderJson(header), Telemetry.FlowJson(flow));
    }

    private static string DeviceSignature() => string.Join("|", new[]
    {
        string.Join(",", Sys.Children("/sys/bus/usb/devices")),
        string.Join(",", Sys.Children("/sys/block")),
        string.Join(",", Sys.Children("/sys/bus/pci/devices")),
        string.Join(",", Sys.Children("/sys/class/net").Select(n => n + ":" + Sys.Read($"/sys/class/net/{n}/operstate"))),
        string.Join(",", Sys.Children("/sys/class/drm").Where(n => n.Contains('-')).Select(n => n + ":" + Sys.Read($"/sys/class/drm/{n}/status")))
    });

    // ---- Exports -----------------------------------------------------------------------------------------

    public string? ExportScan() => _capture is null ? null : LinuxOverview.ScanJson(_capture, AppSettings.Current.PrivacyMode);

    public string? ExportConnections() =>
        _capture is null ? null : LinuxConnections.ToJson(LinuxConnections.Build(_capture, AppSettings.Current.PrivacyMode, _discovery).Snapshot);

    /// <summary>What an issue report needs, always masked (host name, user, home folder, MACs, emails).</summary>
    public string Diagnostics()
    {
        Privacy.Learn(null);
        var capture = _capture;
        var lines = new List<string>
        {
            $"BoardScout for Linux {AppInfo.Version}{(AppInfo.Commit is { } commit ? $" ({commit})" : "")}",
            $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier}",
            $"Distribution: {Distro.Read().PrettyName}",
            $"Kernel: {Sys.Read("/proc/sys/kernel/osrelease")}",
            $"Running as root: {(capture?.Root == true ? "yes" : "no")}",
            $"Started by: {(Agent is null ? "this computer" : "another BoardScout over SSH")}",
            $"Data folder: {DataFolder.Root}",
            $"Settings: glass {AppSettings.Current.GlassEffects} ({AppSettings.Current.GlassOpacity:0.##}, {AppSettings.Current.GlassBlur}px), motion {AppSettings.Current.Motion}, privacy {AppSettings.Current.PrivacyMode}, refresh {AppSettings.Current.TelemetryIntervalMs} ms",
            $"Tools: {string.Join(", ", new[] { "iw", "nmcli", "bluetoothctl", "udisksctl", "fwupdmgr", "udevadm", "ssh" }.Select(t => $"{t} {(Tools.Has(t) ? "yes" : "no")}"))}"
        };
        if (capture is not null)
        {
            lines.Add($"Machine: {capture.Identity.System} · board {capture.Identity.Board} · {capture.FormFactor}");
            lines.Add($"CPU: {capture.Cpu.Name} ({capture.Cpu.Cores}C/{capture.Cpu.Threads}T)");
            lines.Add($"PCI: {capture.Pci.Count} functions, {capture.Pci.Count(d => d.Driver is null && !d.IsBridge)} without a driver");
            lines.Add($"USB: {capture.Usb.Count} buses, {capture.UsbDevices.Count(d => !d.IsRootHub)} devices, socket pairing {(capture.Usb.SelectMany(b => b.RootHub.Ports).Any(p => p.Peer is not null) ? "yes" : "no")}");
            lines.Add($"Drives: {string.Join(", ", capture.Disks.Select(d => $"{d.Name} {d.Transport}"))}");
            lines.Add($"Memory slots from: {capture.Memory.Source}");
            lines.Add($"Read in {capture.CaptureMs} ms at {capture.At:O}");
        }
        lines.Add("");
        lines.Add("Recent log:");
        lines.AddRange(LogLines().TakeLast(60));
        return Privacy.Scrub(string.Join('\n', lines));
    }
}
