using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using BoardScout.Models;
using BoardScout.Platform;
using BoardScout.Services;
using BoardScout.Views;

namespace BoardScout.Live;

/// <summary>One open dashboard (a browser tab): its message stream and which tab it is showing.</summary>
internal sealed class Client
{
    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];

    public Channel<string> Outbox { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(1024)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public string ActiveTab { get; set; } = "overview";
    public bool Hidden { get; set; }
}

/// <summary>
/// The Linux edition's engine: reads the hardware, builds every view, keeps the latest of each for dashboards
/// that open later, answers the views' requests, and streams live rates while someone is watching.
/// </summary>
internal sealed class Hub
{
    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ConcurrentDictionary<string, string> _payloads = new();
    private readonly Telemetry _telemetry = new();
    private readonly LinkedList<string> _log = new();
    private readonly object _gate = new();
    private readonly SemaphoreSlim _reading = new(1, 1);
    private HardwareCapture? _capture;
    private LinuxConnections.BuildResult? _map;
    private DiscoveryResult? _discovery;
    private string _discoveryGateways = "";
    private IrqProfile? _profile;
    private FirmwareReport? _firmware;
    private string? _lastHeader;
    private CancellationTokenSource? _speedCts, _planCts, _wanCts;
    private bool _rediscoverQueued, _sweepQueued, _reloadQueued;
    private string _signature = "";

    public static Hub Current { get; } = new();

    public string ListenUrl { get; set; } = "";
    public Action? Quit { get; set; }
    public bool AnyClients => !_clients.IsEmpty;
    public event Action? ClientsChanged;

    // ---- Clients ------------------------------------------------------------------------------------

    public Client Connect()
    {
        var client = new Client();
        _clients[client.Id] = client;
        client.Outbox.Writer.TryWrite(Envelope("shell", $"{{\"type\":\"hello\",\"client\":\"{client.Id}\"}}"));
        ClientsChanged?.Invoke();
        if (_capture is null) _ = LoadAsync(rediscover: false);
        return client;
    }

    public void Disconnect(Client client)
    {
        _clients.TryRemove(client.Id, out _);
        client.Outbox.Writer.TryComplete();
        ClientsChanged?.Invoke();
    }

    public Client? Find(string? id) => id is not null && _clients.TryGetValue(id, out var client) ? client : null;

    /// <summary>Ends every open event stream, so stopping doesn't wait for browsers to hang up.</summary>
    public void CloseAll()
    {
        foreach (var client in _clients.Values) client.Outbox.Writer.TryComplete();
    }

    private static string Envelope(string view, string json) => $"{{\"view\":\"{view}\",\"msg\":{json}}}";

    private void Broadcast(string view, string json, Func<Client, bool>? to = null)
    {
        var line = Envelope(view, json);
        foreach (var client in _clients.Values)
            if (to is null || to(client)) client.Outbox.Writer.TryWrite(line);
    }

    private static void Send(Client client, string view, string json) => client.Outbox.Writer.TryWrite(Envelope(view, json));

    // Messages that are the current state of a view, replayed to any view that opens later.
    private void Publish(string view, string json)
    {
        _payloads[view] = json;
        Broadcast(view, json);
    }

    // ---- Log ---------------------------------------------------------------------------------------

    public void Log(string text)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {text}";
        lock (_gate)
        {
            _log.AddLast(line);
            while (_log.Count > 500) _log.RemoveFirst();
        }
        Broadcast("shell", $"{{\"type\":\"logline\",\"line\":{JsonSerializer.Serialize(Scrubbed(line))}}}");
    }

    public IReadOnlyList<string> LogLines()
    {
        lock (_gate) return _log.Select(Scrubbed).ToList();
    }

    private static string Scrubbed(string line) => AppSettings.Current.PrivacyMode ? Privacy.Scrub(line) : line;

    // ---- Settings ----------------------------------------------------------------------------------

    public static string SettingsJson()
    {
        var s = AppSettings.Current;
        return JsonSerializer.Serialize(new
        {
            type = "settings",
            platform = "linux",
            glass = s.GlassEffects,
            glassOpacity = s.GlassOpacity,
            glassBlur = s.GlassBlur,
            motion = s.Motion,
            privacy = s.PrivacyMode,
            crawlers = s.Crawlers,
            refreshMs = s.TelemetryIntervalMs,
            openBrowser = s.OpenBrowser,
            port = s.Port
        }, Json.Options);
    }

    private void ApplySettings(JsonElement message)
    {
        var privacyBefore = AppSettings.Current.PrivacyMode;
        AppSettings.Update(s =>
        {
            if (message.TryGetProperty("glass", out var glass) && glass.ValueKind is JsonValueKind.True or JsonValueKind.False) s.GlassEffects = glass.GetBoolean();
            if (message.TryGetProperty("glassOpacity", out var opacity) && opacity.TryGetDouble(out var o)) s.GlassOpacity = o;
            if (message.TryGetProperty("glassBlur", out var blur) && blur.TryGetInt32(out var b)) s.GlassBlur = b;
            if (message.TryGetProperty("motion", out var motion) && motion.ValueKind is JsonValueKind.True or JsonValueKind.False) s.Motion = motion.GetBoolean();
            if (message.TryGetProperty("privacy", out var privacy) && privacy.ValueKind is JsonValueKind.True or JsonValueKind.False) s.PrivacyMode = privacy.GetBoolean();
            if (message.TryGetProperty("crawlers", out var crawlers) && crawlers.TryGetInt32(out var c)) s.Crawlers = c;
            if (message.TryGetProperty("refreshMs", out var refresh) && refresh.TryGetInt32(out var r)) s.TelemetryIntervalMs = r;
            if (message.TryGetProperty("openBrowser", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False) s.OpenBrowser = open.GetBoolean();
        });
        if (message.TryGetProperty("resetGlass", out var reset) && reset.ValueKind == JsonValueKind.True) AppSettings.ResetGlass();
        var json = SettingsJson();
        foreach (var view in new[] { "shell", "connections", "system", "topology" }) Broadcast(view, json);
        if (privacyBefore != AppSettings.Current.PrivacyMode)
        {
            Log(AppSettings.Current.PrivacyMode ? "PRIVACY: on. Names, addresses, and serial-like values are masked." : "PRIVACY: off.");
            Broadcast("shell", $"{{\"type\":\"log\",\"lines\":{JsonSerializer.Serialize(LogLines())}}}");
            PublishFromCapture();
        }
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

    public async Task HandleAsync(Client? client, string view, JsonElement message)
    {
        var type = message.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "ready")
        {
            if (client is null) return;
            Send(client, view, SettingsJson());
            if (view == "shell")
            {
                Send(client, "shell", $"{{\"type\":\"meta\",\"data\":{JsonSerializer.Serialize(Meta(), Json.Options)}}}");
                Send(client, "shell", $"{{\"type\":\"log\",\"lines\":{JsonSerializer.Serialize(LogLines())}}}");
                if (_lastHeader is not null) Send(client, "shell", _lastHeader);
                if (_firmware is not null) Send(client, "shell", FirmwareJson(_firmware));
            }
            if (_payloads.TryGetValue(view, out var payload)) Send(client, view, payload);
            else if (view == "system" && _capture is not null) _ = LoadSystemAsync();
            return;
        }

        switch (view, type)
        {
            case ("shell", "tab"):
                if (client is not null && message.TryGetProperty("name", out var name)) client.ActiveTab = name.GetString() ?? "overview";
                break;
            case ("shell", "visibility"):
                if (client is not null && message.TryGetProperty("hidden", out var hidden)) client.Hidden = hidden.ValueKind == JsonValueKind.True;
                break;
            case ("shell", "settings"):
                ApplySettings(message);
                break;
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
            case ("shell", "quit"):
                Log("Stopping, at your request.");
                Quit?.Invoke();
                break;
            case ("connections", "refresh"):
                await LoadAsync(rediscover: true);
                break;
            case ("connections", "lanscan"):
                await LoadAsync(rediscover: true, sweep: true);
                break;
            case ("connections", "speedtest"):
                await RunSpeedTestAsync();
                break;
            case ("connections", "speedcancel"):
                _speedCts?.Cancel();
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
            case ("connections", "wan"):
                await LookupWanAsync();
                break;
        }
    }

    private object Meta() => new
    {
        version = AppInfo.Version,
        commit = AppInfo.Commit,
        runtime = Environment.Version.ToString(),
        dataFolder = DataFolder.Root,
        url = ListenUrl,
        user = Environment.UserName,
        root = _capture?.Root ?? false,
        kernel = Sys.Read("/proc/sys/kernel/osrelease") ?? "",
        distro = Distro.Read().PrettyName,
        tools = new[] { "iw", "nmcli", "bluetoothctl", "udisksctl", "fwupdmgr", "systemctl", "apt-get", "dpkg-query", "rpm", "snap", "flatpak", "dotnet", "udevadm" }
            .Select(n => new { name = n, path = Tools.Find(n) }).ToList()
    };

    private static string FirmwareJson(FirmwareReport report) =>
        $"{{\"type\":\"firmware\",\"data\":{JsonSerializer.Serialize(report, Json.Options)}}}";

    private async Task RunSpeedTestAsync()
    {
        _speedCts?.Cancel();
        var cts = _speedCts = new CancellationTokenSource();
        Log($"SPEED TEST: testing against Cloudflare ({SpeedTest.Server}) at your request.");
        void Send(SpeedTestUpdate update)
        {
            if (AppSettings.Current.PrivacyMode) update.Edge = null;
            Broadcast("connections", JsonSerializer.Serialize(update, Json.Options));
        }
        try
        {
            var result = await Task.Run(() => SpeedTest.RunAsync(Send, cts.Token), cts.Token);
            Send(result);
            Log($"SPEED TEST: {result.DownloadMbps:0.#} Mbps down, {result.UploadMbps:0.#} Mbps up, {result.LatencyMs:0} ms latency; used {result.UsedMegabytes:0} MB.");
        }
        catch (OperationCanceledException)
        {
            Send(new SpeedTestUpdate { Phase = "error", Error = "Stopped." });
        }
        catch (Exception ex)
        {
            Send(new SpeedTestUpdate { Phase = "error", Error = ex.Message });
            Log("SPEED TEST: " + ex.Message);
        }
    }

    private async Task LookupWanAsync()
    {
        _wanCts?.Cancel();
        _wanCts = new CancellationTokenSource();
        Log($"PUBLIC IP: asked Cloudflare ({NetworkProbe.WanLookupUrl}) at your request.");
        var result = await NetworkProbe.LookupPublicAddressAsync(_wanCts.Token);
        if (result.Ok && AppSettings.Current.PrivacyMode)
        {
            result.Ip = "Hidden (privacy mode)";
            result.Location = null;
            result.Edge = null;
        }
        Broadcast("connections", JsonSerializer.Serialize(result, Json.Options));
    }

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

    // ---- Live loops -------------------------------------------------------------------------------------

    /// <summary>Header numbers for every dashboard, and flowing rates for the ones showing Connections or Overview.</summary>
    public async Task RunTelemetryAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(AppSettings.Current.TelemetryIntervalMs, stopping);
                if (_clients.Values.All(c => c.Hidden)) continue;
                var (header, flow) = _telemetry.Sample(_capture);
                _lastHeader = Telemetry.HeaderJson(header);
                Broadcast("shell", _lastHeader, c => !c.Hidden);
                var flowJson = Telemetry.FlowJson(flow);
                Broadcast("connections", flowJson, c => !c.Hidden && c.ActiveTab == "connections");
                Broadcast("shell", $"{{\"type\":\"flow\",\"data\":{flowJson}}}", c => !c.Hidden && c.ActiveTab == "overview");
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log("LIVE: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Watches for devices coming and going (USB, drives, network, PCI hotplug) while a dashboard is open, and
    /// reads the map again about half a second after something changes.
    /// </summary>
    public async Task RunDeviceWatchAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, stopping);
                if (_capture is null || _clients.IsEmpty) continue;
                var signature = DeviceSignature();
                if (signature == _signature) continue;
                await Task.Delay(400, stopping);
                _signature = DeviceSignature();
                Log("CHANGE: a device was added or removed; reading the map again.");
                await LoadAsync(rediscover: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
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
            $"Data folder: {DataFolder.Root}",
            $"Settings: glass {AppSettings.Current.GlassEffects} ({AppSettings.Current.GlassOpacity:0.##}, {AppSettings.Current.GlassBlur}px), motion {AppSettings.Current.Motion}, privacy {AppSettings.Current.PrivacyMode}, refresh {AppSettings.Current.TelemetryIntervalMs} ms",
            $"Tools: {string.Join(", ", new[] { "iw", "nmcli", "bluetoothctl", "udisksctl", "fwupdmgr", "udevadm" }.Select(t => $"{t} {(Tools.Has(t) ? "yes" : "no")}"))}"
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
