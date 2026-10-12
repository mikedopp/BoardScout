using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using BoardScout.Remote;
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

/// <summary>How this dashboard was started by another BoardScout over SSH.</summary>
internal sealed record AgentInfo(string Label);

/// <summary>
/// The browser dashboard's engine, shared by both editions: the open dashboards and their message streams, the
/// log, settings, the live loops, and the Machines view. What it reads and builds comes from the platform half,
/// Hub.Linux.cs or Hub.Windows.cs, which keeps the latest of each view for dashboards that open later.
/// </summary>
internal sealed partial class Hub
{
    private static readonly string[] SettingsViews = ["shell", "connections", "system", "topology", "machines"];

    private readonly ConcurrentDictionary<string, Client> _clients = new();
    private readonly ConcurrentDictionary<string, string> _payloads = new();
    private readonly LinkedList<string> _log = new();
    private readonly object _gate = new();
    private string? _lastHeader;
    private CancellationTokenSource? _speedCts, _wanCts;
    private string _signature = "";
    private MachinesController? _machines;
    private long _lastActivity = Environment.TickCount64;

    public static Hub Current { get; } = new();

    /// <summary>"linux" or "windows": which edition is serving, so the shell can word things for it.</summary>
    public static string PlatformName => Platform;

    public string ListenUrl { get; set; } = "";
    public string DataRoot { get; set; } = "";
    public Action? Quit { get; set; }

    /// <summary>Set when another BoardScout started this dashboard over SSH; it then serves only that one.</summary>
    public AgentInfo? Agent { get; set; }

    public bool AnyClients => !_clients.IsEmpty;
    public TimeSpan Idle => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastActivity));
    public event Action? ClientsChanged;

    public void Touch() => Interlocked.Exchange(ref _lastActivity, Environment.TickCount64);

    // ---- Clients ------------------------------------------------------------------------------------

    public Client Connect()
    {
        Touch();
        var client = new Client();
        _clients[client.Id] = client;
        client.Outbox.Writer.TryWrite(Envelope("shell", $"{{\"type\":\"hello\",\"client\":\"{client.Id}\"}}"));
        ClientsChanged?.Invoke();
        if (!HasReading) _ = LoadAsync(rediscover: false);
        return client;
    }

    public void Disconnect(Client client)
    {
        Touch();
        _clients.TryRemove(client.Id, out _);
        client.Outbox.Writer.TryComplete();
        ClientsChanged?.Invoke();
    }

    public Client? Find(string? id) => id is not null && _clients.TryGetValue(id, out var client) ? client : null;

    /// <summary>Ends every open event stream, so stopping doesn't wait for browsers to hang up.</summary>
    public void CloseAll()
    {
        foreach (var client in _clients.Values) client.Outbox.Writer.TryComplete();
        _machines?.Dispose();
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
        var values = new Dictionary<string, object?>
        {
            ["type"] = "settings",
            ["platform"] = Platform,
            ["glass"] = s.GlassEffects,
            ["glassOpacity"] = s.GlassOpacity,
            ["glassBlur"] = s.GlassBlur,
            ["motion"] = s.Motion,
            ["privacy"] = s.PrivacyMode,
            ["crawlers"] = s.Crawlers,
            ["refreshMs"] = s.TelemetryIntervalMs,
            ["agent"] = Current.Agent is not null,
            ["remoteName"] = Current.Agent?.Label
        };
        AddPlatformSettings(values);
        return JsonSerializer.Serialize(values, Json.Options);
    }

    static partial void AddPlatformSettings(Dictionary<string, object?> values);

    static partial void ApplyPlatformSettings(AppSettings settings, JsonElement message);

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
            ApplyPlatformSettings(s, message);
        });
        if (message.TryGetProperty("resetGlass", out var reset) && reset.ValueKind == JsonValueKind.True) AppSettings.ResetGlass();
        var json = SettingsJson();
        foreach (var view in SettingsViews) Broadcast(view, json);
        if (privacyBefore != AppSettings.Current.PrivacyMode)
        {
            Log(AppSettings.Current.PrivacyMode ? "PRIVACY: on. Names, addresses, and serial-like values are masked." : "PRIVACY: off.");
            Broadcast("shell", $"{{\"type\":\"log\",\"lines\":{JsonSerializer.Serialize(LogLines())}}}");
            PublishFromReading();
            _machines?.SendState();
        }
    }

    // ---- Messages from the views ----------------------------------------------------------------------

    public async Task HandleAsync(Client? client, string view, JsonElement message)
    {
        Touch();
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
                SendPlatformReady(client);
            }
            if (view == "machines")
            {
                if (Agent is null) Machines.SendState();
                return;
            }
            if (_payloads.TryGetValue(view, out var payload)) Send(client, view, payload);
            else if (view == "system" && HasReading) _ = LoadSystemAsync();
            return;
        }

        switch (view, type)
        {
            case ("shell", "tab"):
                if (client is not null && message.TryGetProperty("name", out var name)) client.ActiveTab = name.GetString() ?? "overview";
                return;
            case ("shell", "visibility"):
                if (client is not null && message.TryGetProperty("hidden", out var hidden)) client.Hidden = hidden.ValueKind == JsonValueKind.True;
                return;
            case ("shell", "settings"):
                ApplySettings(message);
                return;
            case ("shell", "quit"):
                Log(Agent is null ? "Stopping, at your request." : "Disconnecting, at your request.");
                Quit?.Invoke();
                return;
            case ("connections", "speedtest"):
                await RunSpeedTestAsync();
                return;
            case ("connections", "speedcancel"):
                _speedCts?.Cancel();
                return;
            case ("connections", "wan"):
                await LookupWanAsync();
                return;
            case ("machines", _):
                // A dashboard reached over SSH never starts SSH sessions of its own.
                if (Agent is null) await Machines.HandleAsync(message);
                return;
        }
        await HandlePlatformAsync(client, view, type, message);
    }

    /// <summary>The Machines view's controller: remote computers reached over SSH.</summary>
    private MachinesController Machines => _machines ??= new MachinesController(DataRoot, json => Broadcast("machines", json), Log);

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
                var detailed = _clients.Values.Any(c => !c.Hidden && c.ActiveTab is "connections" or "overview");
                var (header, flowJson) = await Task.Run(() => SampleLive(detailed), stopping);
                if (header is null) continue;
                _lastHeader = header;
                Broadcast("shell", header, c => !c.Hidden);
                if (flowJson is null) continue;
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
    /// Watches for devices coming and going (USB, drives, network, PCI) while a dashboard is open, and reads the
    /// map again shortly after something changes.
    /// </summary>
    public async Task RunDeviceWatchAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1500, stopping);
                if (!HasReading || _clients.IsEmpty) continue;
                var signature = await Task.Run(DeviceSignature, stopping);
                if (signature == _signature) continue;
                await Task.Delay(400, stopping);
                _signature = await Task.Run(DeviceSignature, stopping);
                Log("CHANGE: a device was added or removed; reading the map again.");
                await LoadAsync(rediscover: false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
