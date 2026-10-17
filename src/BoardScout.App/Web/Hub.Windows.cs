using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using BoardScout.Models;
using BoardScout.Remote;
using BoardScout.Services;
using BoardScout.UI;
using BoardScout.Views;

namespace BoardScout.Live;

/// <summary>
/// The Windows half of the browser dashboard's engine, for a PC reached over SSH (or served with --serve): the
/// same Connections map, plan, DriverScout scan, System view, and sensors the Windows app shows in its window.
/// </summary>
internal sealed partial class Hub
{
    private const string Platform = "windows";
    private const uint PresentDevices = 0x00000100; // CM_GETIDLIST_FILTER_PRESENT

    private readonly SemaphoreSlim _reading = new(1, 1);
    private readonly Dictionary<string, string> _netKeys = [];
    private DriverScoutService? _scout;
    private SystemTelemetryService? _telemetry;
    private ConnectionsService.Capture? _capture;
    private ConnectionsSnapshot? _map;
    private DiscoveryResult? _discovery;
    private string _discoveryGateways = "";
    private ScanManifest? _scan;
    private string? _scanPath;
    private DriverReport? _report;
    private InterruptProfile? _profile;
    private SystemTelemetry? _lastTelemetry;
    private OptimizationPlan? _plan;
    private CancellationTokenSource? _planCts;
    private bool _rediscoverQueued, _sweepQueued, _reloadQueued, _scanning, _checkingDrivers, _systemAgain;
    private int _systemLoading;

    private bool HasReading => _capture is not null;

    private DriverScoutService Scout
    {
        get
        {
            if (_scout is not null) return _scout;
            var scout = new DriverScoutService();
            scout.OutputReceived += (_, line) => Log("DRIVERSCOUT: " + line);
            return _scout = scout;
        }
    }

    private SystemTelemetryService Telemetry => _telemetry ??= new SystemTelemetryService();

    private void SendPlatformReady(Client client)
    {
        Send(client, "shell", DriversStateJson());
    }

    private string DriversStateJson() =>
        JsonSerializer.Serialize(new { type = "drivercheck", busy = _checkingDrivers, scanning = _scanning, checkedAt = _report?.GeneratedUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") }, Json.Options);

    // ---- Reading the hardware -------------------------------------------------------------------------

    /// <summary>
    /// Reads the device tree, links, and network (a few seconds), shows the map, then looks up router, network,
    /// and device names. The DriverScout inventory (for Topology, Drivers, and the System view's build) comes
    /// from the last scan on this PC, or a new one when there is none.
    /// </summary>
    public async Task LoadAsync(bool rediscover, bool sweep = false)
    {
        if (_scan is null && !_scanning) _ = EnsureScanAsync(fresh: false);
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
                var capture = await Task.Run(ConnectionsService.Read);
                _capture = capture;
                _signature = DeviceSignature();
                Log($"READ: {capture.Tree?.Descendants().Count() ?? 0} devices, {capture.DiskFacts.Count} drives, {capture.Network.Adapters.Count} network adapters in {capture.CaptureMs} ms.");
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

    // The scan behind Topology, Drivers, and the System view's build: the last one, or a new one.
    private async Task EnsureScanAsync(bool fresh)
    {
        if (_scanning) return;
        _scanning = true;
        Broadcast("shell", DriversStateJson());
        try
        {
            var path = fresh ? null : Scout.GetLatestScanPath();
            if (path is null)
            {
                Log("SCAN: running DriverScout's hardware inventory (read-only PowerShell and WMI; about half a minute).");
                path = await Scout.ScanAsync(CancellationToken.None);
            }
            var scan = await Scout.LoadScanAsync(path, CancellationToken.None);
            DriverReport? report = null;
            if (Scout.GetLatestReportPath() is { } reportPath)
            {
                var candidate = await Scout.LoadReportAsync(reportPath, CancellationToken.None);
                if (candidate.BasedOnScan.Equals(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) report = candidate;
            }
            Privacy.Learn(await File.ReadAllTextAsync(path));
            _scan = scan;
            _scanPath = path;
            _report = report;
            Log($"SCAN: {scan.Components.Count} components from the inventory of {scan.Scan.TimestampUtc?.ToLocalTime():yyyy-MM-dd HH:mm}.");
            PublishTopology();
            PublishFromCapture();
            _ = LoadSystemAsync();
        }
        catch (Exception ex)
        {
            Log("SCAN: " + ex.Message);
        }
        finally
        {
            _scanning = false;
            Broadcast("shell", DriversStateJson());
        }
    }

    private void PublishTopology()
    {
        if (_scanPath is not { } path) return;
        try
        {
            // Hardware only: Topology never needs the PC name, owner details, or serial numbers.
            var scan = Privacy.SanitizeScanJson(File.ReadAllText(path), full: true);
            Publish("topology", $"{{\"type\":\"scan\",\"scan\":{scan}}}");
        }
        catch (Exception ex)
        {
            Log("TOPOLOGY: " + ex.Message);
        }
    }

    private void PublishFromReading()
    {
        PublishFromCapture();
        PublishTopology();
    }

    // Builds every view from what was last read, with the current privacy setting (no new read).
    private void PublishFromCapture(DiscoveryResult? discovery = null)
    {
        var capture = _capture;
        if (capture is null) return;
        discovery ??= _discovery;
        var privacy = AppSettings.Current.PrivacyMode;
        var map = ConnectionsService.Build(capture, privacy, discovery);
        _map = map;
        Publish("connections", $"{{\"type\":\"connections\",\"data\":{ConnectionsService.ToJson(map)}}}");
        var plan = _plan = PlanService.Build(capture, map, [], _profile, privacy, _lastTelemetry);
        var overview = WindowsOverview.Build(capture, map, _scan, _report, plan, _lastTelemetry, privacy);
        PublishOverview($"{{\"type\":\"overview\",\"data\":{JsonSerializer.Serialize(overview, Json.Options)}}}");
        if (!_payloads.ContainsKey("system")) _ = LoadSystemAsync();
    }

    private async Task LoadSystemAsync()
    {
        // One read at a time; a scan finishing during a read asks for another, with the scan's build details.
        if (Interlocked.Exchange(ref _systemLoading, 1) == 1)
        {
            _systemAgain = true;
            return;
        }
        try
        {
            do
            {
                _systemAgain = false;
                await ReadSystemAsync();
            }
            while (_systemAgain);
        }
        finally
        {
            _systemLoading = 0;
        }
    }

    private async Task ReadSystemAsync()
    {
        try
        {
            var json = await SystemInfoService.GatherJsonAsync(_scan);
            Publish("system", $"{{\"type\":\"system\",\"data\":{json}}}");
            using var document = JsonDocument.Parse(json);
            Log($"SYSTEM: read updates, apps, and scheduled tasks in {document.RootElement.GetProperty("gatheredMs").GetInt64()} ms.");
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
                await Task.WhenAll(LoadAsync(rediscover: true), EnsureScanAsync(fresh: true));
                break;
            case ("shell", "drivers"):
                await CheckDriversAsync();
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
            case ("connections", "elevate"):
                Log(Agent is not null
                    ? "PLAN: BoardScout here runs with the rights of the SSH account. Sign in with an administrator account to time interrupts per driver."
                    : "PLAN: start BoardScout --serve from an administrator terminal to time interrupts per driver.");
                break;
            case ("connections", "eject"):
                if (message.TryGetProperty("id", out var id) && id.GetString() is { } nodeId) await EjectAsync(nodeId);
                break;
        }
    }

    // DriverScout's update check: vendor, OEM, and Windows Update catalog pages for each component. Nothing is
    // downloaded or installed; the Drivers tab links to the official pages.
    private async Task CheckDriversAsync()
    {
        if (_checkingDrivers) return;
        if (_scanPath is null) await EnsureScanAsync(fresh: false);
        if (_scanPath is not { } path) return;
        _checkingDrivers = true;
        Broadcast("shell", DriversStateJson());
        try
        {
            Log("DRIVERS: checking vendor, OEM, and Windows Update catalog sources for newer drivers, at your request (nothing is installed).");
            var reportPath = await Scout.CheckDriversAsync(path, CancellationToken.None);
            _report = await Scout.LoadReportAsync(reportPath, CancellationToken.None);
            Log($"DRIVERS: {_report.Results.Count(r => r.Status == "update-available")} updates available among {_report.Results.Count} checked.");
            PublishFromCapture();
        }
        catch (Exception ex)
        {
            Log("DRIVERS: " + ex.Message);
        }
        finally
        {
            _checkingDrivers = false;
            Broadcast("shell", DriversStateJson());
        }
    }

    private object Meta()
    {
        var sensors = Telemetry.SensorStatus;
        return new
        {
            platform = Platform,
            version = AppInfo.Version,
            commit = AppInfo.Commit,
            runtime = Environment.Version.ToString(),
            dataFolder = Scout.DataRoot,
            url = ListenUrl,
            user = Environment.UserName,
            root = SystemTelemetryService.IsElevated,
            windows = SystemInfoService.WindowsDescription(),
            agent = Agent?.Label,
            ssh = Agent is null ? SshTool.Describe() : null,
            sensors = new { pawnIo = sensors.PawnIoInstalled, pawnIoVersion = sensors.PawnIoVersion, elevated = sensors.Elevated, zones = sensors.TemperatureZones, fans = sensors.Fans }
        };
    }

    // The optimization plan, built from the last map plus a one-second interrupt sample. Timing interrupts per
    // driver runs a ten-second kernel trace, which needs administrator rights.
    private async Task BuildPlanAsync(bool measure)
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        try
        {
            Broadcast("connections", measure ? "{\"type\":\"planbusy\",\"measuring\":true}" : "{\"type\":\"planbusy\"}");
            if (_capture is null) _ = LoadAsync(rediscover: false);
            for (var i = 0; i < 150 && _capture is null; i++) await Task.Delay(100, cts.Token);
            if (_capture is not { } capture) throw new InvalidOperationException("The device map isn't ready yet.");
            var cores = InterruptStats.SampleAsync(TimeSpan.FromSeconds(1), cts.Token);
            if (measure && SystemTelemetryService.IsElevated)
            {
                Log("PLAN: timing interrupts and DPCs per driver for 10 seconds with a Windows kernel trace, at your request.");
                var profile = await InterruptProfiler.MeasureAsync(TimeSpan.FromSeconds(10), capture.Tree, cts.Token);
                if (profile.Error is null || _profile is null) _profile = profile;
                Log(profile.Error is { } error ? "PLAN: " + error : $"PLAN: {profile.Drivers.Count} drivers handled interrupts; the busiest was {profile.Drivers.FirstOrDefault()?.Driver ?? "none"}.");
            }
            var loads = await cores;
            var privacy = AppSettings.Current.PrivacyMode;
            var json = await Task.Run(() => PlanService.ToJson(
                PlanService.Build(capture, ConnectionsService.Build(capture, privacy, _discovery), loads, _profile, privacy, _lastTelemetry)), cts.Token);
            if (!cts.IsCancellationRequested) Broadcast("connections", json);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Log("PLAN: " + ex.Message);
            Broadcast("connections", $"{{\"type\":\"planerror\",\"error\":{JsonSerializer.Serialize(ex.Message)}}}");
        }
    }

    // Safely removes an external drive from its card. The page sends the card id (a hash), never an instance id;
    // the device is found again in the last read. Windows flushes the drive and refuses while files are open.
    private async Task EjectAsync(string id)
    {
        var device = _capture?.Tree?.Descendants().FirstOrDefault(d => d.IdStarts(@"USB\") && ConnectionsService.DeviceNodeId(d) == id);
        var ok = false;
        string message;
        if (device is null)
        {
            message = "That drive is no longer connected.";
        }
        else
        {
            var outcome = await Task.Run(() => DeviceTree.Eject(device.InstanceId));
            ok = outcome.Ok;
            message = outcome.Message;
            Log($"EJECT: {device.Name}: {(outcome.Ok ? "stopped, safe to unplug" : $"not ejected (veto {outcome.Veto}{(outcome.Who is null ? "" : ", " + outcome.Who)})")}, at your request.");
        }
        Broadcast("connections", $"{{\"type\":\"eject\",\"id\":{JsonSerializer.Serialize(id)},\"ok\":{(ok ? "true" : "false")},\"message\":{JsonSerializer.Serialize(message)}}}");
        if (ok) await LoadAsync(rediscover: false);
    }

    // ---- Live --------------------------------------------------------------------------------------------

    private (string? Header, string? Flow) SampleLive(bool detailed)
    {
        Telemetry.DetailedRates = detailed;
        var sample = _lastTelemetry = Telemetry.Sample();
        var header = $"{{\"type\":\"telemetry\",\"data\":{JsonSerializer.Serialize(WindowsOverview.Header(sample, _capture), Json.Options)}}}";
        return (header, detailed ? JsonSerializer.Serialize(WindowsOverview.Flow(sample, _netKeys), Json.Options) : null);
    }

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    private static extern int DeviceIdListSize(out uint length, string? filter, uint flags);

    // Cheap to read every second and a half: the length of the list of present devices, the network adapters'
    // states, and the drive letters. Any device arriving or leaving changes it.
    private static string DeviceSignature()
    {
        var devices = DeviceIdListSize(out var length, null, PresentDevices) == 0 ? length : 0;
        string adapters;
        try
        {
            adapters = string.Join(",", NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Id[..Math.Min(8, n.Id.Length)] + (int)n.OperationalStatus));
        }
        catch (NetworkInformationException)
        {
            adapters = "";
        }
        return $"{devices}|{adapters}|{string.Join(",", DriveInfo.GetDrives().Select(d => d.Name))}";
    }

    // ---- Exports -----------------------------------------------------------------------------------------

    public string? ExportScan() =>
        _scanPath is null ? null : Privacy.SanitizeScanJson(File.ReadAllText(_scanPath), AppSettings.Current.PrivacyMode);

    public string? ExportConnections() =>
        _capture is null ? null : ConnectionsService.ToJson(ConnectionsService.Build(_capture, AppSettings.Current.PrivacyMode, _discovery));

    /// <summary>What an issue report needs, always masked (PC name, user, paths, serial numbers).</summary>
    public string Diagnostics()
    {
        var sensors = Telemetry.SensorStatus;
        var scan = _scan;
        var text = new StringBuilder()
            .AppendLine($"BoardScout {AppInfo.Version}{(AppInfo.Commit is { } commit ? $" ({commit})" : "")} (browser dashboard)")
            .AppendLine($".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture}) on {SystemInfoService.WindowsDescription()}")
            .AppendLine($"Started by: {(Agent is null ? "this PC (--serve)" : "another BoardScout over SSH")}")
            .AppendLine($"Elevated: {(sensors.Elevated ? "yes" : "no")} · PawnIO: {(sensors.PawnIoInstalled ? sensors.PawnIoVersion ?? "installed" : "not installed")}")
            .AppendLine($"Sensors: {sensors.TemperatureZones} temperature zone(s), {sensors.Fans} fan(s){(sensors.Error is null ? "" : $" · error: {sensors.Error}")}")
            .AppendLine($"Board: {(scan is null ? "no scan yet" : $"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product} · BIOS {scan.SystemInfo.Bios.Version}")}")
            .AppendLine($"Map: {(_capture is null ? "not read yet" : $"{_capture.DiskFacts.Count} drives, read in {_capture.CaptureMs} ms")}")
            .AppendLine($"Data folder: {Scout.DataRoot}")
            .AppendLine($"Settings: glass {AppSettings.Current.GlassEffects} ({AppSettings.Current.GlassOpacity:0.##}, {AppSettings.Current.GlassBlur}px), motion {AppSettings.Current.Motion}, privacy {AppSettings.Current.PrivacyMode}, refresh {AppSettings.Current.TelemetryIntervalMs} ms")
            .AppendLine()
            .AppendLine("Recent log:");
        foreach (var line in LogLines().TakeLast(60)) text.AppendLine(line);
        return Privacy.Scrub(text.ToString());
    }
}
