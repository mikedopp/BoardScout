using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using BoardScout.Models;
using BoardScout.Services;
using Microsoft.Web.WebView2.WinForms;

namespace BoardScout.UI;

public sealed class MainForm : Form
{
    private readonly DriverScoutService _service = new();
    private readonly DriverDownloadService _downloadService;
    private readonly SystemTelemetryService _telemetryService = new();
    private readonly BoardMapControl _boardMap = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _topologyWebView = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _systemWebView = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _topologyCard = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _systemCard = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _connectionsWebView = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _connectionsCard = new() { Dock = DockStyle.Fill };
    private bool _topologyReady;
    private bool _systemReady;
    private bool _connectionsReady;
    private string? _topologyPayload;
    private string? _systemPayload;
    private string? _connectionsPayload;
    private TabPage? _connectionsPage;
    private bool _connectionsStale = true;
    private bool _connectionsLoading;
    private bool _connectionsReloadQueued;
    private bool _rediscoverQueued;
    private bool _sweepQueued;
    private ConnectionsService.Capture? _connectionsCapture;
    private DiscoveryResult? _discovery;
    private string? _discoveryGateways;
    private CancellationTokenSource? _wanLookupCts;
    private CancellationTokenSource? _speedTestCts;
    private CancellationTokenSource? _planCts;
    private InterruptProfile? _interruptProfile;
    private readonly bool _measureOnStart = Environment.GetCommandLineArgs().Contains(MeasureArgument, StringComparer.OrdinalIgnoreCase);
    private const string MeasureArgument = "--measure-interrupts";

    // A device change is read twice: a quick look 0.4 s after the first event, so a plugged-in device shows up
    // at once, and a final look once events have been quiet for 2 s, since a drive's disk and volume arrive
    // a moment after the USB device itself.
    private readonly System.Windows.Forms.Timer _deviceChangeTimer = new() { Interval = 400 };
    private readonly System.Windows.Forms.Timer _deviceSettleTimer = new() { Interval = 2000 };
    private bool _deviceBurst;
    private bool _changedSinceRefresh;
    private readonly Dictionary<string, string> _netKeys = [];
    private readonly List<(TabPage Page, WebView2 View)> _webTabs = [];
    private readonly GlassMetricTile _tempTile = new("TEMP", "—");
    private readonly GlassMetricTile _fanTile = new("FANS", "—");
    private readonly GlassMetricTile _networkTile = new("NET", "—");
    private readonly ToolTip _toolTip = new() { InitialDelay = 300, AutoPopDelay = 12000 };
    private readonly RoundedButton _privacyChip = new();
    private readonly List<string> _logLines = [];
    private bool _privacyShown;
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly FlowLayoutPanel _metrics = new();
    private readonly FlowLayoutPanel _quickFacts = new();
    private readonly DataGridView _drivers = new();
    private readonly DataGridView _storage = new();
    private readonly DataGridView _suggestions = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly RoundedButton _scanButton = new();
    private readonly RoundedButton _updatesButton = new();
    private readonly RoundedButton _loadButton = new();
    private readonly RoundedButton _exportButton = new();
    private readonly RoundedButton _cancelButton = new();
    private readonly RoundedButton _downloadButton = new();
    private readonly RoundedButton _pimpButton = new();
    private readonly LinkLabel _feedbackLink = new();
    private readonly LinkLabel _dataFolderLink = new();
    private readonly RoundedButton _zoomOutButton = new();
    private readonly RoundedButton _zoomResetButton = new();
    private readonly RoundedButton _zoomInButton = new();
    private readonly VersionButton _versionButton = new();
    private readonly ContentTabControl _tabs = new();
    private readonly SidebarNavigationControl _navigation = new();
    private readonly List<(Button Button, bool Primary)> _themedButtons = [];

    private readonly AuroraPanel _header = new();
    private readonly Panel _statusPanel = new();
    private readonly GlassCard _boardCard = new();
    private readonly GlassCard _detailsCard = new();
    private readonly Panel _boardToolbar = new();
    private readonly GlassPanel _inspectPanel = new();
    private readonly Label _factsHeading = new();
    private readonly Label _inspectCategory = new();
    private readonly Label _inspectTitle = new();
    private readonly PillLabel _inspectStatus = new();
    private readonly Label _inspectDetail = new();
    private readonly Label _inspectCapabilityHeading = new();
    private readonly Label _inspectCapability = new();
    private readonly LinkLabel _inspectLink = new();
    private readonly NotifyIcon _trayIcon = new();

    private ScanManifest? _scan;
    private DriverReport? _report;
    private string? _scanPath;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _telemetryCts;
    private int _telemetryIntervalMs;
    private SystemTelemetry? _lastTelemetry;
    private BoardPartDetails? _currentPartDetails;
    private VersionPopout? _versionPopout;

    public MainForm()
    {
        _downloadService = new DriverDownloadService(_service.DataRoot);
        AppSettings.Load(_service.DataRoot);
        AppTheme.SetDarkMode(true);
        Text = "BoardScout";
        Icon = AppTheme.CreateAppIcon();
        MinimumSize = new Size(1440, 780);
        Size = new Size(1540, 960);
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.Background;
        ForeColor = AppTheme.Text;
        Font = new Font("Segoe UI", 9.25f);

        BuildUi();
        BuildTrayIcon();
        HandleCreated += (_, _) => AppTheme.ApplyWindowTheme(this);
        _service.OutputReceived += ServiceOnOutputReceived;
        _boardMap.PartHovered += (_, details) => ShowPartDetails(details);
        _boardMap.ZoomChanged += (_, _) => _zoomResetButton.Text = $"{_boardMap.ZoomPercent}%";
        _versionButton.Click += (_, _) => ToggleVersionPopout();
        AppSettings.Changed += OnSettingsChanged;
        Privacy.Learn(null);
        _privacyShown = Privacy.Enabled;
        Shown += async (_, _) =>
        {
            await LoadCachedDataAsync();
            if (_scan is null) _ = LoadSystemInfoAsync();
            StartTelemetry();
            // The web views start after the cached board is on screen so they never delay it.
            await InitializeWebViewsAsync();
            if (_measureOnStart && _connectionsPage is not null) _tabs.SelectedTab = _connectionsPage;
        };
        FormClosing += (_, _) =>
        {
            AppSettings.Changed -= OnSettingsChanged;
            _versionPopout?.Close();
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _operationCts?.Cancel();
            _telemetryCts?.Cancel();
            _telemetryService.Dispose();
            _wanLookupCts?.Cancel();
            _speedTestCts?.Cancel();
            _planCts?.Cancel();
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _deviceChangeTimer.Dispose();
            _deviceSettleTimer.Dispose();
            _topologyWebView.Dispose();
            _systemWebView.Dispose();
            _connectionsWebView.Dispose();
        };
        _deviceChangeTimer.Tick += (_, _) =>
        {
            _deviceChangeTimer.Stop();
            RefreshAfterDeviceChange();
        };
        _deviceSettleTimer.Tick += (_, _) =>
        {
            _deviceSettleTimer.Stop();
            _deviceBurst = false;
            if (_changedSinceRefresh) RefreshAfterDeviceChange();
        };
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
    }

    // Plugging or unplugging a device, or a network change, redraws the Connections map (once things settle).
    private const int WmDeviceChange = 0x0219;
    private const int DbtDevNodesChanged = 0x0007;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange && m.WParam == DbtDevNodesChanged) QueueConnectionsRefresh();
        base.WndProc(ref m);
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(QueueConnectionsRefresh); } catch (InvalidOperationException) { }
    }

    private void QueueConnectionsRefresh()
    {
        _connectionsStale = true;
        _changedSinceRefresh = true;
        if (!_deviceBurst)
        {
            _deviceBurst = true;
            _deviceChangeTimer.Start();
        }
        _deviceSettleTimer.Stop();
        _deviceSettleTimer.Start();
    }

    private void RefreshAfterDeviceChange()
    {
        _changedSinceRefresh = false;
        _connectionsStale = true;
        if (ConnectionsVisible) _ = LoadConnectionsAsync();
    }

    private void BuildTrayIcon()
    {
        _trayIcon.Icon = Icon;
        _trayIcon.Text = $"BoardScout v{VersionButton.AppVersion}";
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        var menu = new ContextMenuStrip();
        menu.Items.Add($"BoardScout v{VersionButton.AppVersion}");
        menu.Items[0]!.Enabled = false;
        menu.Items[0]!.Font = new Font("Segoe UI Semibold", 9);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Exit", null, (_, _) => { _trayIcon.Visible = false; Close(); });
        _trayIcon.ContextMenuStrip = menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Maximized;
        _trayIcon.Visible = false;
        Activate();
    }

    // Ctrl+Shift+P flips privacy mode from anywhere in the native UI, e.g. right before a screenshot.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Shift | Keys.P))
        {
            AppSettings.Update(s => s.PrivacyMode = !s.PrivacyMode);
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        SyncWebViewVisibility();
        if (WindowState == FormWindowState.Minimized && AppSettings.Current.MinimizeToTray)
        {
            _trayIcon.Visible = true;
            Hide();
        }
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        SyncWebViewVisibility();
    }

    // A WebView2 keeps rendering after its tab is hidden: the tab page disappears before the view hears
    // about it, so the browser still thinks the page is on screen and the Topology and Connections
    // animations kept using CPU and GPU on every other tab. Hiding the view itself while it is still
    // on screen (Deselecting, or before minimizing) tells the browser the page is in the background.
    private void SyncWebViewVisibility()
    {
        var onScreen = Visible && WindowState != FormWindowState.Minimized;
        foreach (var (page, view) in _webTabs)
        {
            var show = onScreen && _tabs.SelectedTab == page;
            if (view.Visible != show) view.Visible = show;
        }
    }

    private void BuildUi()
    {
        BuildHeader();
        Controls.Add(_tabs);
        Controls.Add(_navigation);
        Controls.Add(_progress);
        Controls.Add(BuildStatusBar());
        Controls.Add(_header);

        _tabs.Dock = DockStyle.Fill;

        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildWebTab("Topology", _topologyCard, _topologyWebView));
        _tabs.TabPages.Add(_connectionsPage = BuildWebTab("Connections", _connectionsCard, _connectionsWebView));
        _tabs.TabPages.Add(BuildDriversTab());
        _tabs.TabPages.Add(BuildStorageTab());
        _tabs.TabPages.Add(BuildSuggestionsTab());
        _tabs.TabPages.Add(BuildWebTab("System", _systemCard, _systemWebView));
        _tabs.TabPages.Add(BuildLogTab());
        _navigation.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex != _navigation.SelectedIndex)
                _tabs.SelectedIndex = _navigation.SelectedIndex;
        };
        _tabs.Deselecting += (_, e) =>
        {
            foreach (var (page, view) in _webTabs)
                if (page == e.TabPage) view.Visible = false;
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_navigation.SelectedIndex != _tabs.SelectedIndex)
                _navigation.SelectedIndex = _tabs.SelectedIndex;
            SyncWebViewVisibility();
            // Per-disk and per-adapter rates are only sampled while the Connections map is on screen.
            _telemetryService.DetailedRates = ConnectionsVisible;
            if (ConnectionsVisible && _connectionsStale) _ = LoadConnectionsAsync();
        };

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 3;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 25;
        _progress.Visible = false;
        foreach (var scrolling in new Control[] { _quickFacts, _drivers, _storage, _suggestions, _log })
            AppTheme.UseDarkScrollbars(scrolling);
        ShowPartDetails(null);
        ApplyTheme();
    }

    private void BuildHeader()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = 158;
        _header.BackColor = AppTheme.Surface;
        _header.Padding = new Padding(24, 16, 24, 12);
        _header.Tag = "surface";

        // Transparent layers so the header's aurora shows through behind the text and metrics.
        var textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _title.Text = "BoardScout";
        _title.Font = new Font("Segoe UI Semibold", 21);
        _title.ForeColor = AppTheme.Text;
        _title.BackColor = Color.Transparent;
        _title.AutoSize = true;
        _title.Location = new Point(0, 0);
        _title.Tag = "text";

        _subtitle.Text = "Portable motherboard, storage, and driver intelligence";
        _subtitle.ForeColor = AppTheme.Muted;
        _subtitle.BackColor = Color.Transparent;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(2, 38);
        _subtitle.Tag = "muted";

        _metrics.AutoSize = true;
        _metrics.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _metrics.WrapContents = false;
        _metrics.Location = new Point(0, 70);
        _metrics.BackColor = Color.Transparent;
        _metrics.Margin = new Padding(0);
        _title.Resize += (_, _) =>
        {
            _versionButton.Location = new Point(_title.Right + 10, _title.Top + (_title.Height - _versionButton.Height) / 2);
            _privacyChip.Location = new Point(_versionButton.Right + 8, _versionButton.Top);
        };
        _versionButton.Location = new Point(_title.PreferredSize.Width + 10, 6);
        textPanel.Controls.Add(_versionButton);

        // Visible whenever privacy mode is on, so a screenshot shows it was taken masked.
        _privacyChip.Text = "Privacy on";
        _privacyChip.Font = new Font("Segoe UI Semibold", 8.5f);
        _privacyChip.Size = new Size(104, 30);
        _privacyChip.Location = new Point(_versionButton.Right + 8, _versionButton.Top);
        _privacyChip.BackColor = Color.FromArgb(38, 28, 62);
        _privacyChip.ForeColor = AppTheme.Purple;
        _privacyChip.FlatAppearance.BorderColor = AppTheme.Purple;
        _privacyChip.FlatAppearance.MouseOverBackColor = Color.FromArgb(56, 42, 90);
        _privacyChip.FlatAppearance.MouseDownBackColor = Color.FromArgb(28, 20, 46);
        _privacyChip.Cursor = Cursors.Hand;
        _privacyChip.Visible = Privacy.Enabled;
        _privacyChip.AccessibleName = "Privacy mode is on. Press to turn it off.";
        _privacyChip.Click += (_, _) => AppSettings.Update(s => s.PrivacyMode = false);
        _toolTip.SetToolTip(_privacyChip,
            "Privacy mode hides your PC name, Windows owner details, serial numbers, paths, and app lists " +
            "in screens and exports. Click to turn it off (Ctrl+Shift+P).");
        textPanel.Controls.Add(_privacyChip);
        textPanel.Controls.Add(_title);
        textPanel.Controls.Add(_subtitle);
        textPanel.Controls.Add(_metrics);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 780,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent
        };

        ConfigureButton(_scanButton, "Scan now", async (_, _) => await RunScanAsync(), primary: true);
        ConfigureButton(_pimpButton, "Pimp My Build", (_, _) => OpenPimpMyBuild());
        AppTheme.StyleFeatureButton(_pimpButton);
        _pimpButton.Tag = "featureButton";
        ConfigureButton(_updatesButton, "Check drivers", async (_, _) => await RunDriverCheckAsync());
        ConfigureButton(_downloadButton, "Download drivers", async (_, _) => await DownloadDriversAsync());
        ConfigureButton(_loadButton, "Import", async (_, _) => await LoadScanFromFileAsync());
        ConfigureButton(_exportButton, "Export", (_, _) => ExportScan());
        ConfigureButton(_cancelButton, "Cancel", (_, _) => _operationCts?.Cancel());

        _cancelButton.Visible = false;
        _updatesButton.Enabled = false;
        _downloadButton.Enabled = false;
        _exportButton.Enabled = false;
        _pimpButton.Enabled = false;

        actions.Controls.AddRange([
            _exportButton, _loadButton,
            ToolbarSep(),
            _downloadButton, _updatesButton,
            ToolbarSep(),
            _pimpButton, _scanButton, _cancelButton
        ]);

        _header.Controls.Add(textPanel);
        _header.Controls.Add(actions);
        _header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = AppTheme.Border, Tag = "border" });
    }

    private TabPage BuildOverviewTab()
    {
        var page = NewPage("Overview");
        var split = new SplitContainer
        {
            Orientation = Orientation.Vertical,
            Size = new Size(1200, 600),
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Background,
            SplitterWidth = 12,
            FixedPanel = FixedPanel.Panel2,
            Panel1MinSize = 720,
            Panel2MinSize = 360
        };
        split.SplitterDistance = 1100;
        split.Tag = "background";

        split.Panel1.BackColor = AppTheme.Background;
        split.Panel1.Tag = "background";
        split.Panel2.BackColor = AppTheme.Background;
        split.Panel2.Tag = "background";
        _boardCard.Dock = DockStyle.Fill;
        _boardCard.Padding = new Padding(8);

        BuildBoardToolbar();
        _boardCard.Controls.Add(_boardMap);
        _boardCard.Controls.Add(_boardToolbar);
        split.Panel1.Controls.Add(_boardCard);

        _detailsCard.Dock = DockStyle.Fill;
        _detailsCard.Padding = new Padding(18, 16, 18, 16);

        BuildInspector();
        _factsHeading.Dock = DockStyle.Top;
        _factsHeading.Height = 40;
        _factsHeading.Text = "System details";
        _factsHeading.Font = new Font("Segoe UI Semibold", 12);
        _factsHeading.ForeColor = AppTheme.Text;
        _factsHeading.Tag = "text";
        _quickFacts.Dock = DockStyle.Fill;
        _quickFacts.BackColor = AppTheme.Surface;
        _quickFacts.FlowDirection = FlowDirection.TopDown;
        _quickFacts.WrapContents = false;
        _quickFacts.AutoScroll = true;
        _quickFacts.Padding = new Padding(0, 2, 0, 0);
        _quickFacts.Tag = "surface";
        _quickFacts.SizeChanged += (_, _) => ResizeFactRows();
        _detailsCard.Controls.Add(_quickFacts);
        _detailsCard.Controls.Add(_factsHeading);
        _detailsCard.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 14, BackColor = AppTheme.Surface, Tag = "surface" });
        _detailsCard.Controls.Add(_inspectPanel);
        split.Panel2.Controls.Add(_detailsCard);
        page.Controls.Add(split);
        return page;
    }

    private void BuildBoardToolbar()
    {
        _boardToolbar.Dock = DockStyle.Top;
        _boardToolbar.Height = 50;
        _boardToolbar.BackColor = AppTheme.Surface;
        _boardToolbar.Padding = new Padding(12, 6, 8, 6);
        _boardToolbar.Tag = "surface";

        var title = new Label
        {
            Dock = DockStyle.Left,
            Width = 175,
            Text = "Interactive board",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 11),
            ForeColor = AppTheme.Text,
            Tag = "text"
        };
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Hover for status and capability  ·  mouse wheel to zoom  ·  drag to pan",
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = AppTheme.Muted,
            Tag = "muted"
        };
        var zoom = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 210,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(0)
        };
        ConfigureButton(_zoomOutButton, "−", (_, _) => _boardMap.ZoomOut());
        ConfigureButton(_zoomResetButton, "100%", (_, _) => _boardMap.ResetView());
        ConfigureButton(_zoomInButton, "+", (_, _) => _boardMap.ZoomIn());
        foreach (var button in new[] { _zoomOutButton, _zoomResetButton, _zoomInButton })
            button.AutoSize = false;
        _zoomOutButton.Size = new Size(42, 36);
        _zoomResetButton.Size = new Size(76, 36);
        _zoomInButton.Size = new Size(42, 36);
        zoom.Controls.AddRange([_zoomOutButton, _zoomResetButton, _zoomInButton]);

        _boardToolbar.Controls.Add(hint);
        _boardToolbar.Controls.Add(title);
        _boardToolbar.Controls.Add(zoom);
    }

    private TabPage BuildWebTab(string name, GlassCard card, WebView2 view)
    {
        var page = NewPage(name);
        view.DefaultBackgroundColor = AppTheme.Surface;
        view.Visible = false;
        card.Padding = new Padding(6);
        card.Controls.Add(view);
        page.Controls.Add(card);
        _webTabs.Add((page, view));
        return page;
    }

    private async Task InitializeWebViewsAsync()
    {
        await InitializeWebViewAsync(_topologyWebView, _topologyCard, "topology.html", () =>
        {
            _topologyReady = true;
            PostSettings(_topologyWebView);
            if (_topologyPayload is not null) _topologyWebView.CoreWebView2.PostWebMessageAsJson(_topologyPayload);
        });
        await InitializeWebViewAsync(_connectionsWebView, _connectionsCard, "connections.html", () =>
        {
            _connectionsReady = true;
            PostSettings(_connectionsWebView);
            if (_connectionsPayload is not null) _connectionsWebView.CoreWebView2.PostWebMessageAsJson(_connectionsPayload);
            // Restarted as administrator from the plan: go straight back to it and measure.
            if (_measureOnStart && SystemTelemetryService.IsElevated)
                _connectionsWebView.CoreWebView2.PostWebMessageAsJson("{\"type\":\"openplan\",\"measure\":true}");
        });
        if (_connectionsWebView.CoreWebView2 is { } connections)
            connections.WebMessageReceived += OnConnectionsMessage;
        await InitializeWebViewAsync(_systemWebView, _systemCard, "system.html", () =>
        {
            _systemReady = true;
            PostSettings(_systemWebView);
            if (_systemPayload is not null) _systemWebView.CoreWebView2.PostWebMessageAsJson(_systemPayload);
        });
    }

    private bool ConnectionsVisible => _connectionsPage is not null && _tabs.SelectedTab == _connectionsPage && Visible;

    // Reads the hardware and network (fast), shows the map, then looks up router, network, and device names
    // in the background (a few seconds) and shows the map again with them. Names are reused for two minutes.
    private async Task LoadConnectionsAsync(bool rediscover = false, bool sweep = false)
    {
        if (_connectionsLoading)
        {
            _connectionsReloadQueued = true;
            _rediscoverQueued |= rediscover || sweep;
            _sweepQueued |= sweep;
            return;
        }
        _connectionsLoading = true;
        try
        {
            do
            {
                _connectionsReloadQueued = false;
                rediscover |= _rediscoverQueued;
                sweep |= _sweepQueued;
                _rediscoverQueued = _sweepQueued = false;
                PostToConnections("{\"type\":\"busy\"}");
                var capture = await Task.Run(ConnectionsService.Read);
                if (IsDisposed) return;
                _connectionsCapture = capture;
                _connectionsStale = false;
                var gateways = string.Join(",", capture.Network.Routers.Keys.Order());
                var fresh = _discovery is not null && !rediscover && !sweep && _discoveryGateways == gateways &&
                            DateTimeOffset.Now - _discovery.At < TimeSpan.FromMinutes(2);
                PostConnectionsMap(fresh ? _discovery : null);
                if (fresh) continue;

                PostToConnections(sweep ? "{\"type\":\"discovery\",\"sweep\":true}" : "{\"type\":\"discovery\"}");
                if (sweep) AppendLog("CONNECTIONS: pinging every address on the local network to find devices, at your request.");
                _discovery = await NetworkDiscovery.RunAsync(capture.Network, CancellationToken.None, sweep);
                _discoveryGateways = gateways;
                if (IsDisposed) return;
                if (ReferenceEquals(_connectionsCapture, capture)) PostConnectionsMap(_discovery);
            }
            while (_connectionsReloadQueued && !IsDisposed);
        }
        catch (Exception ex)
        {
            AppendLog("CONNECTIONS: " + ex.Message);
        }
        finally
        {
            _connectionsLoading = false;
        }
    }

    // Builds the map from what was last read, with the current privacy setting (a privacy toggle needs no new read).
    private void PostConnectionsMap(DiscoveryResult? discovery)
    {
        if (_connectionsCapture is null) return;
        var json = ConnectionsService.ToJson(ConnectionsService.Build(_connectionsCapture, Privacy.Enabled, discovery));
        _connectionsPayload = $"{{\"type\":\"connections\",\"data\":{json}}}";
        PostToConnections(_connectionsPayload);
    }

    private void PostToConnections(string json)
    {
        if (_connectionsReady && !IsDisposed) _connectionsWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private async Task RunSpeedTestAsync()
    {
        _speedTestCts?.Cancel();
        var cts = _speedTestCts = new CancellationTokenSource();
        AppendLog($"SPEED TEST: testing against Cloudflare ({SpeedTest.Server}) at your request.");
        void Send(SpeedTestUpdate update)
        {
            if (Privacy.Enabled) update.Edge = null;
            var json = System.Text.Json.JsonSerializer.Serialize(update, BoardScoutJson.Default.SpeedTestUpdate);
            if (InvokeRequired) BeginInvoke(() => PostToConnections(json));
            else PostToConnections(json);
        }
        try
        {
            var result = await Task.Run(() => SpeedTest.RunAsync(Send, cts.Token), cts.Token);
            Send(result);
            AppendLog($"SPEED TEST: {result.DownloadMbps:0.#} Mbps down, {result.UploadMbps:0.#} Mbps up, " +
                      $"{result.LatencyMs:0} ms latency; used {result.UsedMegabytes:0} MB.");
        }
        catch (OperationCanceledException)
        {
            Send(new SpeedTestUpdate { Phase = "error", Error = "Stopped." });
        }
        catch (Exception ex)
        {
            Send(new SpeedTestUpdate { Phase = "error", Error = ex.Message });
            AppendLog("SPEED TEST: " + ex.Message);
        }
    }

    private async void OnConnectionsMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var type = message.RootElement.TryGetProperty("type", out var value) ? value.GetString() : null;
            if (type == "refresh")
            {
                await LoadConnectionsAsync(rediscover: true);
            }
            else if (type == "lanscan")
            {
                await LoadConnectionsAsync(rediscover: true, sweep: true);
            }
            else if (type == "speedtest")
            {
                await RunSpeedTestAsync();
            }
            else if (type == "speedcancel")
            {
                _speedTestCts?.Cancel();
            }
            else if (type == "plan")
            {
                await BuildPlanAsync(measure: false);
            }
            else if (type == "irqprofile")
            {
                await BuildPlanAsync(measure: true);
            }
            else if (type == "elevate")
            {
                RestartElevated(MeasureArgument);
            }
            else if (type == "eject" && message.RootElement.TryGetProperty("id", out var ejectId) && ejectId.GetString() is { } id)
            {
                await EjectAsync(id);
            }
            else if (type == "wan")
            {
                _wanLookupCts?.Cancel();
                _wanLookupCts = new CancellationTokenSource();
                AppendLog($"PUBLIC IP: asked Cloudflare ({NetworkProbe.WanLookupUrl}) at your request.");
                var result = await NetworkProbe.LookupPublicAddressAsync(_wanLookupCts.Token);
                if (result.Ok && Privacy.Enabled)
                {
                    result.Ip = "Hidden (privacy mode)";
                    result.Location = null;
                    result.Edge = null;
                }
                if (!IsDisposed && _connectionsReady)
                    _connectionsWebView.CoreWebView2.PostWebMessageAsJson(
                        System.Text.Json.JsonSerializer.Serialize(result, BoardScoutJson.Default.WanLookup));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppendLog("CONNECTIONS: " + ex.Message);
        }
    }

    // Safely removes an external drive from its card. The page sends the card id (a hash), never an instance id;
    // the device is found again in the last map read. Windows flushes the drive and refuses while files are open.
    private async Task EjectAsync(string id)
    {
        var device = _connectionsCapture?.Tree?.Descendants()
            .FirstOrDefault(d => d.IdStarts(@"USB\") && ConnectionsService.DeviceNodeId(d) == id);
        string message;
        var ok = false;
        if (device is null)
        {
            message = "That drive is no longer connected.";
        }
        else
        {
            var name = device.Name;
            var outcome = await Task.Run(() => DeviceTree.Eject(device.InstanceId));
            ok = outcome.Ok;
            message = outcome.Message;
            AppendLog($"EJECT: {name}: {(outcome.Ok ? "stopped, safe to unplug" : $"not ejected (veto {outcome.Veto}, code 0x{outcome.Result:X}{(outcome.Who is null ? "" : ", " + outcome.Who)})")}, at your request.");
        }
        PostToConnections($"{{\"type\":\"eject\",\"id\":{System.Text.Json.JsonSerializer.Serialize(id, BoardScoutJson.Default.String)}," +
                          $"\"ok\":{(ok ? "true" : "false")},\"message\":{System.Text.Json.JsonSerializer.Serialize(message, BoardScoutJson.Default.String)}}}");
        // Read the hardware again so the stopped drive's card changes now, not only when Windows' device-change
        // notice arrives. A refresh already running picks this up as a queued reload.
        if (ok) await LoadConnectionsAsync();
    }

    // The optimization plan, built from the last map read plus a one-second interrupt sample. Measuring
    // interrupt time per driver runs a ten-second kernel trace, which needs administrator rights.
    private async Task BuildPlanAsync(bool measure)
    {
        _planCts?.Cancel();
        var cts = _planCts = new CancellationTokenSource();
        try
        {
            PostToConnections(measure ? "{\"type\":\"planbusy\",\"measuring\":true}" : "{\"type\":\"planbusy\"}");
            if (_connectionsCapture is null) _ = LoadConnectionsAsync();
            for (var i = 0; i < 150 && _connectionsCapture is null; i++) await Task.Delay(100, cts.Token);
            if (_connectionsCapture is not { } capture) throw new InvalidOperationException("The device map isn't ready yet.");

            var cores = InterruptStats.SampleAsync(TimeSpan.FromSeconds(1), cts.Token);
            if (measure && SystemTelemetryService.IsElevated)
            {
                AppendLog("PLAN: timing interrupts and DPCs per driver for 10 seconds with a Windows kernel trace, at your request.");
                var profile = await InterruptProfiler.MeasureAsync(TimeSpan.FromSeconds(10), capture.Tree, cts.Token);
                if (profile.Error is null || _interruptProfile is null) _interruptProfile = profile;
                AppendLog(profile.Error is { } error
                    ? "PLAN: " + error
                    : $"PLAN: {profile.Drivers.Count} drivers handled interrupts; the busiest was {profile.Drivers.FirstOrDefault()?.Driver ?? "none"}.");
            }
            var loads = await cores;
            var privacy = Privacy.Enabled;
            var discovery = _discovery;
            var measured = _interruptProfile;
            var power = _lastTelemetry?.Power;
            var json = await Task.Run(() => PlanService.ToJson(
                PlanService.Build(capture, ConnectionsService.Build(capture, privacy, discovery), loads, measured, privacy, power)), cts.Token);
            if (!IsDisposed && !cts.IsCancellationRequested) PostToConnections(json);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            AppendLog("PLAN: " + ex.Message);
            PostToConnections($"{{\"type\":\"planerror\",\"error\":{System.Text.Json.JsonSerializer.Serialize(ex.Message, BoardScoutJson.Default.String)}}}");
        }
    }

    // Live rates for the map. Network: [bytes in/s, out/s, packets in/s, out/s, dropped+errors/s,
    // bytes in, bytes out, packets in, packets out, dropped, errors since Windows started].
    // Disk: [bytes read/s, written/s, reads/s, writes/s, requests queued now].
    private void PostConnectionFlow(SystemTelemetry telemetry)
    {
        if (!_connectionsReady || !ConnectionsVisible || telemetry.InterfaceRates is null) return;
        var flow = new ConnectionFlow { CpuPercent = Math.Round(telemetry.CpuUsagePercent, 1) };
        foreach (var (id, rate) in telemetry.InterfaceRates)
        {
            if (!_netKeys.TryGetValue(id, out var key)) _netKeys[id] = key = ConnectionsService.NetKey(id);
            var totals = telemetry.InterfaceCounters?.GetValueOrDefault(id) ?? default;
            flow.Network[key] =
            [
                Math.Round(rate.In), Math.Round(rate.Out), Math.Round(rate.OpsIn), Math.Round(rate.OpsOut),
                Math.Round(totals.DroppedPerSec, 1),
                totals.BytesIn, totals.BytesOut, totals.PacketsIn, totals.PacketsOut, totals.Dropped, totals.Errors
            ];
        }
        foreach (var (number, rate) in telemetry.DiskRates ?? new Dictionary<int, LinkRate>())
        {
            var queue = telemetry.DiskQueues?.GetValueOrDefault(number) ?? 0;
            var busy = telemetry.DiskBusy?.GetValueOrDefault(number) ?? 0;
            flow.Disks[number.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                [Math.Round(rate.In), Math.Round(rate.Out), Math.Round(rate.OpsIn), Math.Round(rate.OpsOut), queue, busy];
        }
        foreach (var (zone, key) in new[] { ("CPU", "cpu"), ("GPU", "gpu"), ("Chipset", "chipset") })
            if (telemetry.Thermals.FirstOrDefault(t => t.Zone == zone) is { } reading)
                flow.Temperatures[key] = reading.TemperatureCelsius;
        _connectionsWebView.CoreWebView2.PostWebMessageAsJson(
            System.Text.Json.JsonSerializer.Serialize(flow, BoardScoutJson.Default.ConnectionFlow));
    }

    private async Task InitializeWebViewAsync(WebView2 view, GlassCard card, string page, Action ready)
    {
        try
        {
            view.NavigationCompleted += (_, e) => { if (e.IsSuccess) ready(); };
            await WebViewHost.InitializeAsync(view, _service.DataRoot, page);
            view.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
        }
        catch (Exception ex)
        {
            AppendLog($"WEBVIEW2 ({page}): {ex.Message}");
            card.Controls.Clear();
            card.Controls.Add(new Label
            {
                Text = "This view needs the Microsoft Edge WebView2 Runtime.\n" +
                       "Install the Evergreen Runtime from microsoft.com, then restart BoardScout.",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Muted,
                BackColor = AppTheme.Surface,
                Tag = "muted"
            });
        }
    }

    private void BuildInspector()
    {
        _inspectPanel.Dock = DockStyle.Top;
        _inspectPanel.Height = 270;
        _inspectPanel.Tag = "raised";

        _inspectCategory.Font = new Font("Segoe UI Semibold", 8);
        _inspectCategory.Location = new Point(16, 13);
        _inspectCategory.Height = 18;
        _inspectCategory.ForeColor = AppTheme.Accent;
        _inspectCategory.Tag = "accent";

        _inspectTitle.Font = new Font("Segoe UI Semibold", 14);
        _inspectTitle.Location = new Point(16, 34);
        _inspectTitle.Height = 30;
        _inspectTitle.ForeColor = AppTheme.Text;
        _inspectTitle.AutoEllipsis = true;
        _inspectTitle.Tag = "text";

        _inspectStatus.Font = new Font("Segoe UI Semibold", 9);
        _inspectStatus.Location = new Point(16, 69);
        _inspectStatus.Height = 26;
        _inspectStatus.Padding = new Padding(10, 0, 10, 0);

        _inspectDetail.Font = new Font("Segoe UI", 8.5f);
        _inspectDetail.Location = new Point(16, 101);
        _inspectDetail.Height = 38;
        _inspectDetail.ForeColor = AppTheme.Muted;
        _inspectDetail.Tag = "muted";

        _inspectCapabilityHeading.Text = "Capability estimate";
        _inspectCapabilityHeading.Font = new Font("Segoe UI Semibold", 8);
        _inspectCapabilityHeading.Location = new Point(16, 145);
        _inspectCapabilityHeading.Height = 18;
        _inspectCapabilityHeading.ForeColor = AppTheme.Muted;
        _inspectCapabilityHeading.Tag = "muted";

        _inspectCapability.Font = new Font("Segoe UI", 9);
        _inspectCapability.Location = new Point(16, 166);
        _inspectCapability.Height = 50;
        _inspectCapability.ForeColor = AppTheme.Text;
        _inspectCapability.Tag = "text";

        _inspectLink.Text = "Open official update page  ↗";
        _inspectLink.Font = new Font("Segoe UI Semibold", 9);
        _inspectLink.Location = new Point(16, 226);
        _inspectLink.Height = 28;
        _inspectLink.LinkColor = AppTheme.Accent;
        _inspectLink.ActiveLinkColor = AppTheme.Good;
        _inspectLink.VisitedLinkColor = AppTheme.Accent;
        _inspectLink.Visible = false;
        _inspectLink.LinkClicked += (_, _) => OpenOfficialUrl(_inspectLink.Tag as string);

        // Transparent text so the inspector's glass shows behind it.
        foreach (var label in new Label[] { _inspectCategory, _inspectTitle, _inspectDetail, _inspectCapabilityHeading, _inspectCapability, _inspectLink })
            label.BackColor = Color.Transparent;

        _inspectPanel.Controls.AddRange([
            _inspectCategory, _inspectTitle, _inspectStatus, _inspectDetail,
            _inspectCapabilityHeading, _inspectCapability, _inspectLink]);
        _inspectPanel.SizeChanged += (_, _) => LayoutInspector();
        LayoutInspector();
    }

    private void LayoutInspector()
    {
        var width = Math.Max(200, _inspectPanel.ClientSize.Width - 32);
        foreach (var label in new Label[]
                 { _inspectCategory, _inspectTitle, _inspectStatus, _inspectDetail, _inspectCapabilityHeading, _inspectCapability, _inspectLink })
            label.Width = width;
    }

    private TabPage BuildDriversTab()
    {
        var page = NewPage("Drivers");
        ConfigureGrid(_drivers,
            ("Category", 90), ("Component", 285), ("Installed", 145), ("Date", 100),
            ("Latest", 130), ("Status", 125), ("Source", 110));
        _drivers.CellDoubleClick += (_, e) => OpenDriverLink(e.RowIndex);
        _drivers.CellContentClick += (_, e) =>
        {
            if (e.ColumnIndex == 7) OpenDriverLink(e.RowIndex);
        };
        _drivers.Columns.Add(new DataGridViewLinkColumn
        {
            HeaderText = "Official update",
            Width = 125,
            MinimumWidth = 110,
            TrackVisitedState = false,
            LinkColor = AppTheme.Accent,
            ActiveLinkColor = AppTheme.Good,
            VisitedLinkColor = AppTheme.Accent
        });
        page.Controls.Add(Card(_drivers));
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            Text = "Use Official update to open the vendor or OEM page. BoardScout never installs drivers or firmware.",
            ForeColor = AppTheme.Muted,
            Padding = new Padding(4, 5, 0, 0),
            Tag = "muted"
        });
        return page;
    }

    private TabPage BuildStorageTab()
    {
        var page = NewPage("Storage");
        ConfigureGrid(_storage,
            ("Volume", 75), ("Physical disk", 315), ("Bus", 80), ("File system", 90),
            ("Capacity", 110), ("Free", 110), ("Used", 100));
        page.Controls.Add(Card(_storage));
        return page;
    }

    private TabPage BuildSuggestionsTab()
    {
        var page = NewPage("Efficiency");
        ConfigureGrid(_suggestions,
            ("Priority", 90), ("Category", 90), ("Suggestion", 260), ("Why it matters / next action", 620));
        _suggestions.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _suggestions.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        page.Controls.Add(Card(_suggestions));
        return page;
    }

    private TabPage BuildLogTab()
    {
        var page = NewPage("Scan Log");
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.BackColor = AppTheme.Surface;
        _log.ForeColor = AppTheme.Text;
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Cascadia Mono", 9);
        page.Controls.Add(Card(_log));
        return page;
    }

    private static GlassCard Card(Control content)
    {
        var card = new GlassCard { Dock = DockStyle.Fill, Padding = new Padding(8) };
        card.Controls.Add(content);
        return card;
    }

    private async Task LoadSystemInfoAsync()
    {
        try
        {
            var json = await SystemInfoService.GatherJsonAsync(_scan);
            _systemPayload = $"{{\"type\":\"system\",\"data\":{json}}}";
            if (_systemReady) _systemWebView.CoreWebView2.PostWebMessageAsJson(_systemPayload);
        }
        catch (Exception ex)
        {
            AppendLog("SYSTEM INFO: " + ex.Message);
        }
    }

    private static Control ToolbarSep() => new Panel
    {
        Width = 1,
        Height = 28,
        BackColor = AppTheme.Border,
        Margin = new Padding(6, 4, 6, 0),
        Tag = "border"
    };

    private Control BuildStatusBar()
    {
        _statusPanel.Dock = DockStyle.Bottom;
        _statusPanel.Height = 30;
        _statusPanel.BackColor = AppTheme.Surface;
        _statusPanel.BorderStyle = BorderStyle.FixedSingle;
        _statusPanel.Padding = new Padding(14, 6, 14, 0);
        _statusPanel.Tag = "surface";

        var rightLinks = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        _dataFolderLink.Text = "Data folder";
        _dataFolderLink.AutoSize = true;
        _dataFolderLink.LinkColor = AppTheme.Muted;
        _dataFolderLink.ActiveLinkColor = AppTheme.Accent;
        _dataFolderLink.VisitedLinkColor = AppTheme.Muted;
        _dataFolderLink.Font = new Font("Segoe UI", 8.25f);
        _dataFolderLink.Padding = new Padding(0, 0, 8, 0);
        _dataFolderLink.LinkClicked += (_, _) => _service.OpenDataFolder();

        var sep = new Label
        {
            Text = "|",
            AutoSize = true,
            ForeColor = AppTheme.Border,
            Font = new Font("Segoe UI", 8.25f),
            Padding = new Padding(0, 0, 8, 0)
        };

        _feedbackLink.Text = "Report issue";
        _feedbackLink.AutoSize = true;
        _feedbackLink.LinkColor = AppTheme.Muted;
        _feedbackLink.ActiveLinkColor = AppTheme.Accent;
        _feedbackLink.VisitedLinkColor = AppTheme.Muted;
        _feedbackLink.Font = new Font("Segoe UI", 8.25f);
        _feedbackLink.LinkClicked += (_, _) => OpenFeedback();

        rightLinks.Controls.AddRange([_dataFolderLink, sep, _feedbackLink]);

        _status.Dock = DockStyle.Fill;
        _status.Text = "Ready — cached results load instantly; scan only when hardware changes.";
        _status.ForeColor = AppTheme.Muted;
        _status.Tag = "muted";

        _statusPanel.Controls.Add(_status);
        _statusPanel.Controls.Add(rightLinks);
        return _statusPanel;
    }

    private static TabPage NewPage(string text) => new(text)
    {
        Name = text,
        BackColor = AppTheme.Background,
        ForeColor = AppTheme.Text,
        Padding = new Padding(12),
        Tag = "background"
    };

    private void ConfigureButton(Button button, string text, EventHandler handler, bool primary = false)
    {
        button.Text = text;
        button.Tag = primary ? "primaryButton" : "button";
        AppTheme.StyleButton(button, primary);
        button.Click += handler;
        _themedButtons.Add((button, primary));
    }

    private static void ConfigureGrid(DataGridView grid, params (string Name, int Width)[] columns)
    {
        grid.Dock = DockStyle.Fill;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.ReadOnly = true;
        grid.MultiSelect = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoGenerateColumns = false;
        foreach (var (name, width) in columns)
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = name, Width = width, MinimumWidth = 60 });
        grid.Columns[^1].AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill;
        AppTheme.StyleGrid(grid);
    }

    private async Task LoadCachedDataAsync()
    {
        var latest = _service.GetLatestScanPath();
        if (latest is null)
        {
            SetEmptyState();
            return;
        }

        try
        {
            _scanPath = latest;
            _scan = await _service.LoadScanAsync(latest, CancellationToken.None);
            var reportPath = _service.GetLatestReportPath();
            if (reportPath is not null)
            {
                var candidate = await _service.LoadReportAsync(reportPath, CancellationToken.None);
                if (candidate.BasedOnScan.Equals(Path.GetFileName(latest), StringComparison.OrdinalIgnoreCase))
                    _report = candidate;
            }
            BindSnapshot();
            _status.Text = $"Loaded cached scan from {_scan.Scan.TimestampUtc?.ToLocalTime():g}. No rescan needed unless hardware changed.";
        }
        catch (Exception ex)
        {
            AppendLog("CACHE ERROR: " + ex.Message);
            SetEmptyState();
        }
    }

    private async Task RunScanAsync()
    {
        await RunOperationAsync("Scanning hardware with DriverScout…", async token =>
        {
            _scanPath = await _service.ScanAsync(token);
            _scan = await _service.LoadScanAsync(_scanPath, token);
            _report = null;
            BindSnapshot();
            _status.Text = $"Hardware scan complete — {_scan.Components.Count} components and {_scan.Volumes.Count} volumes.";
        });
    }

    private async Task RunDriverCheckAsync()
    {
        if (_scan is null || _scanPath is null) return;
        await RunOperationAsync("Checking vendor, OEM, and catalog driver sources…", async token =>
        {
            var reportPath = await _service.CheckDriversAsync(_scanPath, token);
            _report = await _service.LoadReportAsync(reportPath, token);
            _boardMap.SetDriverReport(_report);
            BindDrivers();
            BindSuggestions();
            var updates = _report.Results.Count(r => r.Status == "update-available");
            _downloadButton.Enabled = true;
            _status.Text = $"Driver check complete — {updates} update{(updates == 1 ? "" : "s")} available for review.";
        });
    }

    private async Task DownloadDriversAsync()
    {
        if (_report is null) return;

        var candidates = _report.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.DownloadUrl ?? r.Best.DownloadUrl))
            .Select(r => (r.Model, Url: r.DownloadUrl ?? r.Best.DownloadUrl!))
            .ToList();

        if (candidates.Count == 0)
        {
            _status.Text = "No download URLs available. Run Check Drivers first.";
            return;
        }

        await RunOperationAsync($"Downloading {candidates.Count} driver package(s)…", async token =>
        {
            int downloaded = 0, opened = 0, failed = 0;
            var progress = new Progress<(long bytes, long? total)>(p =>
            {
                if (p.total.HasValue && p.total > 0)
                    _status.Text = $"Downloading… {p.bytes * 100 / p.total.Value}% ({FormatBytes(p.bytes)} / {FormatBytes(p.total.Value)})";
                else
                    _status.Text = $"Downloading… {FormatBytes(p.bytes)}";
            });

            foreach (var (model, url) in candidates)
            {
                token.ThrowIfCancellationRequested();
                _status.Text = $"Checking {model}…";
                AppendLog($"[{DateTime.Now:T}] Downloading: {model} → {url}");
                var result = await _downloadService.DownloadAsync(url, model, progress, token);

                if (result.Downloaded)
                {
                    downloaded++;
                    AppendLog($"  Saved: {Path.GetFileName(result.LocalPath)}");
                }
                else if (result.OpenedBrowser)
                {
                    opened++;
                    AppendLog("  Opened in browser (landing page)");
                }
                else
                {
                    failed++;
                    AppendLog($"  Failed: {result.Error}");
                }
            }

            var parts = new List<string>();
            if (downloaded > 0) parts.Add($"{downloaded} downloaded");
            if (opened > 0) parts.Add($"{opened} opened in browser");
            if (failed > 0) parts.Add($"{failed} failed");
            _status.Text = $"Driver downloads complete — {string.Join(", ", parts)}.";

            if (downloaded > 0)
                _downloadService.OpenDownloadFolder();
        });
    }

    private async Task LoadScanFromFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Filter = "BoardScout scan (*.json)|*.json|All files (*.*)|*.*",
            Title = "Load a BoardScout or DriverScout scan"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        try
        {
            _scan = await _service.LoadScanAsync(dialog.FileName, CancellationToken.None);
            _scanPath = dialog.FileName;
            _report = null;
            BindSnapshot();
            _status.Text = $"Loaded {Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Could not load scan", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ExportScan()
    {
        if (_scanPath is null || _scan is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "Spec sheet (*.html)|*.html|Upgrade planner (*.html)|*.html|JSON file (*.json)|*.json",
            FilterIndex = 1,
            FileName = Privacy.Enabled ? "BoardScout-specsheet" : $"{_scan.Scan.Hostname}-specsheet",
            Title = "Export hardware scan"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (dialog.FilterIndex == 2)
        {
            var html = UpgradePlannerService.GenerateReport(_scan);
            File.WriteAllText(dialog.FileName, html, Encoding.UTF8);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        else if (dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var html = SpecSheetGenerator.Generate(_scan, _report, Privacy.Enabled);
            File.WriteAllText(dialog.FileName, html, Encoding.UTF8);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        else
        {
            // Never a raw copy: the scan file carries the Windows owner email and product ID.
            File.WriteAllText(dialog.FileName,
                Privacy.SanitizeScanJson(File.ReadAllText(_scanPath), Privacy.Enabled), new UTF8Encoding(false));
        }
        _status.Text = Privacy.Enabled
            ? $"Exported {Path.GetFileName(dialog.FileName)} with privacy mode on — PC name, owner details, and serial numbers removed."
            : $"Exported to {dialog.FileName}.";
    }

    private async Task RunOperationAsync(string message, Func<CancellationToken, Task> operation)
    {
        if (_operationCts is not null) return;
        _operationCts = new CancellationTokenSource();
        SetBusy(true, message);
        AppendLog($"[{DateTime.Now:T}] {message}");

        try
        {
            await operation(_operationCts.Token);
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Operation cancelled. Existing cached results were preserved.";
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            _status.Text = "Operation failed — see Scan Log.";
            AppendLog("FAILED: " + ex);
            MessageBox.Show(this, ex.Message, "BoardScout", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _tabs.SelectedTab = _tabs.TabPages["Scan Log"] ?? _tabs.TabPages[^1];
        }
        finally
        {
            _operationCts.Dispose();
            _operationCts = null;
            SetBusy(false, _status.Text);
        }
    }

    private void BindSnapshot()
    {
        if (_scan is null) return;
        var board = _scan.SystemInfo.Baseboard;
        var cpu = _scan.Cpu;
        _title.Text = $"{board.Manufacturer} {board.Product}".Trim();
        _subtitle.Text = $"{_scan.FormFactor.ToUpperInvariant()}  •  {cpu.Name}  •  {cpu.Cores}C/{cpu.Threads}T  •  {_scan.Scan.Os.Caption}";
        _boardMap.SetSnapshot(_scan);
        _boardMap.SetDriverReport(_report);
        _ = SendScanToTopologyAsync();
        BindMetrics();
        BindQuickFacts();
        BindDrivers();
        BindStorage();
        BindSuggestions();
        _ = LoadSystemInfoAsync();
        _updatesButton.Enabled = true;
        _downloadButton.Enabled = _report is not null;
        _exportButton.Enabled = true;
        _pimpButton.Enabled = true;
    }

    private async Task SendScanToTopologyAsync()
    {
        if (_scanPath is null) return;
        var path = _scanPath;
        try
        {
            // Learn this PC's identifiers for privacy mode, and send the topology view hardware only:
            // it never needs the PC name, owner details, or serial numbers.
            var scan = await Task.Run(() =>
            {
                var raw = File.ReadAllText(path);
                Privacy.Learn(raw);
                return Privacy.SanitizeScanJson(raw, full: true);
            });
            if (path != _scanPath || IsDisposed) return;
            _topologyPayload = $"{{\"type\":\"scan\",\"scan\":{scan}}}";
            if (_topologyReady) _topologyWebView.CoreWebView2.PostWebMessageAsJson(_topologyPayload);
            if (Privacy.Enabled) RenderLog();
        }
        catch (Exception ex)
        {
            AppendLog("TOPOLOGY: " + ex.Message);
        }
    }

    private static void PostSettings(WebView2 view)
    {
        static string Flag(bool on) => on ? "true" : "false";
        view.CoreWebView2?.PostWebMessageAsJson(
            $"{{\"type\":\"settings\",\"glass\":{Flag(AppSettings.Current.GlassEffects)},\"motion\":{Flag(Motion.Enabled)},\"privacy\":{Flag(Privacy.Enabled)},\"crawlers\":{AppSettings.Current.Crawlers}}}");
    }

    private void BindMetrics()
    {
        foreach (var control in _metrics.Controls.Cast<Control>().ToList())
        {
            _metrics.Controls.Remove(control);
            if (control != _tempTile && control != _fanTile && control != _networkTile) control.Dispose();
        }
        if (_scan is null) return;
        var usedTb = _scan.Volumes.Sum(v => v.SizeBytes - v.FreeBytes) / 1_099_511_627_776d;
        var updateCount = _report?.Results.Count(r => r.Status == "update-available");
        _metrics.Controls.Add(new GlassMetricTile("MEMORY", $"{_scan.TotalMemoryGb:0.#} GB"));
        _metrics.Controls.Add(new GlassMetricTile("COMPONENTS", $"{_scan.Components.Count}"));
        _metrics.Controls.Add(new GlassMetricTile("DATA USED", $"{usedTb:0.0} TB"));
        _metrics.Controls.Add(new GlassMetricTile("UPDATES", updateCount?.ToString() ?? "—"));
        _metrics.Controls.Add(_tempTile);
        _metrics.Controls.Add(_fanTile);
        _metrics.Controls.Add(_networkTile);
    }

    private void BindQuickFacts()
    {
        _quickFacts.Controls.Clear();
        if (_scan is null) return;
        var memory = _scan.Memory.Slots.FirstOrDefault();
        AddFact("BIOS", $"{_scan.SystemInfo.Bios.Version} · {_scan.SystemInfo.Bios.ReleaseDate}");
        AddFact("Memory", $"{_scan.TotalMemoryGb:0.#} GB · {_scan.Memory.Populated} of {_scan.Memory.TotalSlots} slots");
        if (memory is not null) AddFact("Memory speed", $"{memory.SpeedMhz} / {memory.RatedMhz} MT/s");
        AddFact("Storage", $"{_scan.Volumes.Count} mounted volumes");
        AddFact("USB", $"{_scan.UsbDevices.Count(d => d.DeviceClass != "USB")} attached devices");
        var problems = _scan.ProblemDevices.Count(d => d.ErrorCode != 0);
        AddFact("Device health", problems == 0 ? "No reported errors" : $"{problems} error{(problems == 1 ? "" : "s")}",
            problems == 0 ? AppTheme.Good : AppTheme.Critical);
        AddFact("Last inventory", $"{_scan.Scan.TimestampUtc?.ToLocalTime():g}");
        var ages = UpgradePlannerService.GetComponentAges(_scan);
        if (ages.Count > 0)
        {
            AddFactSection("Component ages");
            foreach (var age in ages)
            {
                if (age.AgeYears == 0)
                {
                    AddFact(age.Category, age.Name);
                    continue;
                }
                var color = age.AgeYears switch { >= 5 => AppTheme.Critical, >= 3 => AppTheme.Warning, _ => AppTheme.Good };
                AddFact(age.Category, $"{age.AgeYears} yr — {age.Generation} ({age.Released})", color);
            }
        }
        AddFactSection("How BoardScout works");
        AddFact("Startup", "Uses the latest cached inventory");
        AddFact("Hardware scan", "Local and on demand");
        AddFact("Driver check", "Review only — nothing is installed");
    }

    private void AddFact(string label, string value, Color? valueColor = null)
    {
        var width = Math.Max(220, _quickFacts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        var row = new Panel
        {
            Width = width,
            Height = 43,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0),
            Tag = "surface"
        };
        row.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 8),
            Location = new Point(0, 2),
            Size = new Size(width, 16),
            Tag = "muted"
        });
        row.Controls.Add(new Label
        {
            Text = value,
            ForeColor = valueColor ?? AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Location = new Point(0, 18),
            Size = new Size(width, 21),
            AutoEllipsis = true,
            Tag = valueColor == AppTheme.Good ? "good" :
                valueColor == AppTheme.Critical ? "critical" :
                valueColor == AppTheme.Warning ? "warning" : "text"
        });
        _quickFacts.Controls.Add(row);
    }

    private void AddFactSection(string text)
    {
        var width = Math.Max(220, _quickFacts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        _quickFacts.Controls.Add(new Label
        {
            Text = text,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 10),
            Width = width,
            Height = 36,
            Padding = new Padding(0, 12, 0, 0),
            Margin = new Padding(0, 6, 0, 0),
            Tag = "text"
        });
    }

    private void ResizeFactRows()
    {
        var width = Math.Max(220, _quickFacts.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 10);
        foreach (Control control in _quickFacts.Controls) control.Width = width;
    }

    private void BindDrivers()
    {
        _drivers.Rows.Clear();
        if (_scan is null) return;
        var reportByKey = _report?.Results
            .Where(r => !string.IsNullOrWhiteSpace(r.ComponentKey))
            .GroupBy(r => r.ComponentKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, DriverResult>(StringComparer.OrdinalIgnoreCase);

        foreach (var component in _scan.Components)
        {
            reportByKey.TryGetValue(component.ComponentKey, out var result);
            var latest = result?.Best.LatestVersion ?? result?.Best.LatestDate ?? "";
            var status = result?.Status ?? "not checked";
            var rowIndex = _drivers.Rows.Add(
                component.Category, component.Model, component.Current.DriverVersion ?? component.Current.Firmware ?? "—",
                component.Current.DriverDate ?? "—", latest, status, result?.Best.Source ?? "—");
            var row = _drivers.Rows[rowIndex];
            row.Tag = result;
            SetDriverLink(row, result);
            ColorDriverRow(row, status, component.Current);
        }

        if (_report is null) return;
        foreach (var result in _report.Results.Where(r => !_scan.Components.Any(c =>
                     c.ComponentKey.Equals(r.ComponentKey, StringComparison.OrdinalIgnoreCase))))
        {
            var rowIndex = _drivers.Rows.Add(result.Category, result.Model, result.Best.InstalledVersion ?? "—", "—",
                result.Best.LatestVersion ?? result.Best.LatestDate ?? "—", result.Status, result.Best.Source);
            _drivers.Rows[rowIndex].Tag = result;
            SetDriverLink(_drivers.Rows[rowIndex], result);
            ColorDriverRow(_drivers.Rows[rowIndex], result.Status, new CurrentVersion());
        }
        BindMetrics();
    }

    private static void SetDriverLink(DataGridViewRow row, DriverResult? result)
    {
        if (row.Cells.Count <= 7) return;
        var url = result?.DownloadUrl ?? result?.Best.DownloadUrl;
        row.Cells[7].Value = string.IsNullOrWhiteSpace(url) ? "—" : "Official page ↗";
        row.Cells[7].Tag = url;
    }

    private static void ColorDriverRow(DataGridViewRow row, string status, CurrentVersion current)
    {
        var color = status switch
        {
            "update-available" => AppTheme.Warning,
            "current" => AppTheme.Good,
            "error" => AppTheme.Critical,
            "manual-check" => AppTheme.Purple,
            _ when current.DriverSource == "disk.inf" || current.DriverDate?.StartsWith("2006-06-") == true => AppTheme.Muted,
            _ => AppTheme.Text
        };
        row.Cells[5].Style.ForeColor = color;
        row.Cells[2].Style.ForeColor = color == AppTheme.Text ? AppTheme.Muted : color;
    }

    private void BindStorage()
    {
        _storage.Rows.Clear();
        if (_scan is null) return;
        foreach (var volume in _scan.Volumes.OrderByDescending(v => v.UsedPercent))
        {
            // Volume labels are names people pick ("Mike's backups"); privacy mode shows the letter instead.
            var name = volume.DiskModel ??
                       (Privacy.Enabled || string.IsNullOrWhiteSpace(volume.Label) ? $"Volume {volume.Letter}" : volume.Label);
            var rowIndex = _storage.Rows.Add(volume.Letter, name,
                volume.BusType ?? "Unknown", volume.FileSystem, FormatBytes(volume.SizeBytes),
                FormatBytes(volume.FreeBytes), $"{volume.UsedPercent:0}%");
            _storage.Rows[rowIndex].Cells[6].Style.ForeColor =
                volume.UsedPercent >= 95 ? AppTheme.Critical :
                volume.UsedPercent >= 85 ? AppTheme.Warning : AppTheme.Good;
        }
    }

    private void BindSuggestions()
    {
        _suggestions.Rows.Clear();
        if (_scan is null) return;
        foreach (var suggestion in SuggestionEngine.Analyze(_scan, _report))
        {
            var rowIndex = _suggestions.Rows.Add(suggestion.Severity, suggestion.Category, suggestion.Title,
                suggestion.Detail + Environment.NewLine + "Next: " + suggestion.Action);
            _suggestions.Rows[rowIndex].Cells[0].Style.ForeColor = suggestion.Severity switch
            {
                SuggestionSeverity.Critical => AppTheme.Critical,
                SuggestionSeverity.Warning => AppTheme.Warning,
                SuggestionSeverity.Improvement => AppTheme.Good,
                _ => AppTheme.Accent
            };
        }
    }

    private void OpenDriverLink(int rowIndex)
    {
        if (rowIndex < 0 || _drivers.Rows[rowIndex].Tag is not DriverResult result) return;
        var url = result.DownloadUrl ?? result.Best.DownloadUrl;
        OpenOfficialUrl(url);
    }

    private static void OpenOfficialUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)) return;
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    private void OpenPimpMyBuild()
    {
        if (_scan is null) return;
        var html = UpgradePlannerService.GenerateReport(_scan);
        var path = Path.Combine(Path.GetTempPath(), $"BoardScout-PimpMyBuild-{DateTime.Now:yyyyMMdd-HHmmss}.html");
        File.WriteAllText(path, html, Encoding.UTF8);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        _status.Text = "Pimp My Build report opened in browser.";
    }

    private static void OpenFeedback()
    {
        Process.Start(new ProcessStartInfo("https://github.com/mikedopp/BoardScout/issues") { UseShellExecute = true });
    }

    private void ToggleVersionPopout()
    {
        if (_versionPopout is not null)
        {
            _versionPopout.Close();
            return;
        }
        var popout = new VersionPopout(_versionButton, new VersionPopoutActions(
            () => _telemetryService.SensorStatus,
            () => _lastTelemetry?.Thermals ?? [],
            _service.DataRoot,
            _service.OpenDataFolder,
            CopyDiagnostics,
            RestartElevated,
            OpenNotices,
            OpenFeedback));
        popout.Closed += (_, _) => _versionPopout = null;
        _versionPopout = popout;
        popout.ShowUnder(this);
    }

    private void CopyDiagnostics()
    {
        var sensors = _telemetryService.SensorStatus;
        var settings = AppSettings.Current;
        var board = _scan is null ? "no scan" : $"{_scan.SystemInfo.Baseboard.Manufacturer} {_scan.SystemInfo.Baseboard.Product} · BIOS {_scan.SystemInfo.Bios.Version}";
        var text = new StringBuilder()
            .AppendLine($"BoardScout {VersionButton.AppVersion}")
            .AppendLine($".NET {Environment.Version} ({RuntimeInformation.ProcessArchitecture}) on {SystemInfoService.WindowsDescription()}")
            .AppendLine($"WebView2 Runtime: {WebViewHost.RuntimeVersion ?? "not found"}")
            .AppendLine($"Elevated: {(sensors.Elevated ? "yes" : "no")} · PawnIO: {(sensors.PawnIoInstalled ? sensors.PawnIoVersion ?? "installed" : "not installed")}")
            .AppendLine($"Sensors: {sensors.TemperatureZones} temperature zone(s), {sensors.Fans} fan(s){(sensors.Error is null ? "" : $" · error: {sensors.Error}")}")
            .AppendLine($"Board: {board}")
            .AppendLine($"CPU: {(_scan is null ? "unknown" : _scan.Cpu.Name)}")
            .AppendLine($"Last scan: {_scan?.Scan.TimestampUtc?.ToLocalTime():g} · driver report: {(_report is null ? "none" : "loaded")}")
            .AppendLine($"Data folder: {_service.DataRoot}")
            .AppendLine($"Settings: glass {(settings.GlassEffects ? "on" : "off")}, motion {(settings.Motion ? "on" : "off")}, refresh {settings.TelemetryIntervalMs} ms, tray {(settings.MinimizeToTray ? "on" : "off")}")
            .ToString();
        try
        {
            // Diagnostics are meant for public issue reports, so they are always scrubbed.
            Clipboard.SetText(Privacy.Scrub(text));
            _status.Text = "Diagnostics copied without your PC name, paths, or serial numbers — paste them into a GitHub issue.";
        }
        catch (ExternalException)
        {
            _status.Text = "Could not open the clipboard — another app is holding it. Try again.";
        }
    }

    private void RestartElevated() => RestartElevated(null);

    private void RestartElevated(string? arguments)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath ?? Application.ExecutablePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments ?? "",
                WorkingDirectory = AppContext.BaseDirectory
            });
            _trayIcon.Visible = false;
            Close();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            _status.Text = "Administrator restart cancelled — BoardScout keeps running without elevation.";
        }
    }

    private void OpenNotices()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.md");
        var target = File.Exists(local) ? local : "https://github.com/mikedopp/BoardScout/blob/main/THIRD-PARTY-NOTICES.md";
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (_telemetryIntervalMs != AppSettings.Current.TelemetryIntervalMs) StartTelemetry();
        _versionButton.RefreshMotion();
        _header.RefreshAurora();
        if (_topologyReady) PostSettings(_topologyWebView);
        if (_systemReady) PostSettings(_systemWebView);
        if (_connectionsReady) PostSettings(_connectionsWebView);
        if (_privacyShown != Privacy.Enabled)
        {
            _privacyShown = Privacy.Enabled;
            _privacyChip.Visible = Privacy.Enabled;
            RenderLog();
            BindStorage();
            // The map masks MACs, Wi-Fi and device names, and IPv6 on the C# side, so it is rebuilt
            // from what was last read (no new read needed).
            if (_connectionsCapture is not null) PostConnectionsMap(_discovery);
            else _connectionsStale = true;
            _status.Text = Privacy.Enabled
                ? "Privacy mode on — PC name, owner details, serial numbers, paths, and app lists are hidden in screens and exports."
                : "Privacy mode off.";
        }
        Invalidate(true);
    }

    private void SetBusy(bool busy, string message)
    {
        _scanButton.Enabled = !busy;
        _updatesButton.Enabled = !busy && _scan is not null;
        _downloadButton.Enabled = !busy && _report is not null;
        _loadButton.Enabled = !busy;
        _exportButton.Enabled = !busy && _scan is not null;
        _pimpButton.Enabled = !busy && _scan is not null;
        _cancelButton.Visible = busy;
        _progress.Visible = busy;
        _status.Text = message;
        UseWaitCursor = busy;
    }

    private void SetEmptyState()
    {
        _title.Text = "BoardScout";
        _subtitle.Text = "No cached scan yet — run Scan now or import an existing DriverScout JSON file.";
        BindMetrics();
        _metrics.Controls.Add(new GlassMetricTile("SCANS", "0"));
        _quickFacts.Controls.Clear();
        AddFact("Portable", "Runs without an installer");
        AddFact("Inventory", "Uses built-in Windows tools");
        AddFact("Safe by design", "Never installs drivers automatically", AppTheme.Good);
        _boardMap.SetSnapshot(null);
        _boardMap.SetDriverReport(null);
    }

    private void ApplyTheme()
    {
        BackColor = AppTheme.Background;
        ForeColor = AppTheme.Text;
        ApplyTaggedTheme(this);
        foreach (var (button, primary) in _themedButtons)
        {
            if (button.Tag as string == "featureButton")
                AppTheme.StyleFeatureButton(button);
            else
                AppTheme.StyleButton(button, primary);
        }

        foreach (var grid in new[] { _drivers, _storage, _suggestions }) AppTheme.StyleGrid(grid);
        _tabs.BackColor = AppTheme.Background;
        _tabs.ForeColor = AppTheme.Text;
        _log.BackColor = AppTheme.Surface;
        _log.ForeColor = AppTheme.Text;
        _quickFacts.BackColor = AppTheme.Surface;
        _inspectLink.LinkColor = AppTheme.Accent;
        _inspectLink.ActiveLinkColor = AppTheme.Good;
        _inspectLink.VisitedLinkColor = AppTheme.Accent;
        _dataFolderLink.LinkColor = AppTheme.Muted;
        _dataFolderLink.ActiveLinkColor = AppTheme.Accent;
        _feedbackLink.LinkColor = AppTheme.Muted;
        _feedbackLink.ActiveLinkColor = AppTheme.Accent;
        foreach (var column in _drivers.Columns.OfType<DataGridViewLinkColumn>())
        {
            column.LinkColor = AppTheme.Accent;
            column.ActiveLinkColor = AppTheme.Good;
            column.VisitedLinkColor = AppTheme.Accent;
        }
        _boardMap.RefreshTheme();
        _navigation.RefreshTheme();
        AppTheme.ApplyWindowTheme(this);
        _tabs.Invalidate();

        if (_scan is not null)
        {
            BindMetrics();
            BindQuickFacts();
            BindDrivers();
            BindStorage();
            BindSuggestions();
        }
        ShowPartDetails(_currentPartDetails);
    }

    private static void ApplyTaggedTheme(Control root)
    {
        switch (root.Tag as string)
        {
            case "background":
                root.BackColor = AppTheme.Background;
                root.ForeColor = AppTheme.Text;
                break;
            case "surface":
                root.BackColor = AppTheme.Surface;
                break;
            case "raised":
                root.BackColor = AppTheme.SurfaceRaised;
                break;
            case "border":
                root.BackColor = AppTheme.Border;
                break;
            case "text":
                root.ForeColor = AppTheme.Text;
                break;
            case "muted":
                root.ForeColor = AppTheme.Muted;
                break;
            case "accent":
                root.ForeColor = AppTheme.Accent;
                break;
            case "good":
                root.ForeColor = AppTheme.Good;
                break;
            case "warning":
                root.ForeColor = AppTheme.Warning;
                break;
            case "critical":
                root.ForeColor = AppTheme.Critical;
                break;
        }
        foreach (Control child in root.Controls) ApplyTaggedTheme(child);
    }

    private void ShowPartDetails(BoardPartDetails? details)
    {
        _currentPartDetails = details;
        if (details is null)
        {
            _inspectCategory.Text = "INTERACTIVE MAP";
            _inspectTitle.Text = "Hover over a component";
            _inspectStatus.Text = "Live telemetry ready";
            _inspectDetail.Text = "Move over the CPU, memory, graphics, drives, ports, or open slots.";
            _inspectCapability.Text = "BoardScout will explain current status, measured usage where available, and what each part is suited to doing.";
            _inspectLink.Visible = false;
            _inspectLink.Tag = null;
            SetInspectorTone(PartStatusTone.Info);
            return;
        }

        _inspectCategory.Text = details.Category;
        _inspectTitle.Text = details.Title;
        _inspectStatus.Text = details.Status;
        _inspectDetail.Text = details.Detail;
        _inspectCapability.Text = details.Capability;
        _inspectLink.Tag = details.OfficialUrl;
        _inspectLink.Visible = !string.IsNullOrWhiteSpace(details.OfficialUrl);
        SetInspectorTone(details.Tone);
    }

    private void SetInspectorTone(PartStatusTone tone)
    {
        var foreground = tone switch
        {
            PartStatusTone.Good => AppTheme.Good,
            PartStatusTone.Warning => AppTheme.Warning,
            PartStatusTone.Critical => AppTheme.Critical,
            PartStatusTone.Muted => AppTheme.Muted,
            _ => AppTheme.Accent
        };
        var background = (tone, AppTheme.IsDark) switch
        {
            (PartStatusTone.Good, false) => Color.FromArgb(229, 244, 236),
            (PartStatusTone.Good, true) => Color.FromArgb(24, 58, 46),
            (PartStatusTone.Warning, false) => Color.FromArgb(251, 241, 226),
            (PartStatusTone.Warning, true) => Color.FromArgb(65, 47, 24),
            (PartStatusTone.Critical, false) => Color.FromArgb(251, 232, 234),
            (PartStatusTone.Critical, true) => Color.FromArgb(68, 31, 37),
            (PartStatusTone.Muted, false) => Color.FromArgb(237, 240, 243),
            (PartStatusTone.Muted, true) => Color.FromArgb(38, 47, 57),
            _ => AppTheme.AccentSoft
        };
        _inspectStatus.ForeColor = foreground;
        _inspectStatus.PillColor = background;
        _inspectStatus.Invalidate();
        _inspectCategory.ForeColor = foreground;
        if (_inspectPanel.Tint != foreground)
        {
            _inspectPanel.Tint = foreground;
            _inspectPanel.Invalidate();
        }
    }

    private void StartTelemetry()
    {
        _telemetryCts?.Cancel();
        _telemetryCts = new CancellationTokenSource();
        _telemetryIntervalMs = AppSettings.Current.TelemetryIntervalMs;
        _ = RunTelemetryAsync(TimeSpan.FromMilliseconds(_telemetryIntervalMs), _telemetryCts.Token);
    }

    // Sampling runs on the thread pool: the sensor driver open (~0.6 s) and each hardware read
    // (~80 ms) used to run on the UI thread every second. Only the finished sample comes back here.
    private async Task RunTelemetryAsync(TimeSpan interval, CancellationToken token)
    {
        try
        {
            using var timer = new PeriodicTimer(interval);
            do
            {
                if (!Visible || WindowState == FormWindowState.Minimized) continue;
                var telemetry = await Task.Run(_telemetryService.Sample, token);
                if (token.IsCancellationRequested || IsDisposed) return;
                ApplyTelemetry(telemetry);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            AppendLog("TELEMETRY: " + ex.Message);
        }
    }

    private void ApplyTelemetry(SystemTelemetry telemetry)
    {
        _lastTelemetry = telemetry;
        _boardMap.SetTelemetry(telemetry);
        var sensors = _telemetryService.SensorStatus;
        var sensorHelp = sensors.MotherboardSensorsAvailable
            ? null
            : "CPU, VRM, and motherboard fan sensors need the PawnIO driver and administrator rights. " +
              "Open the version button for details.";

        if (telemetry.Thermals.Count > 0)
        {
            var parts = new List<string>();
            foreach (var zone in new[] { "CPU", "GPU", "VRM" })
            {
                var reading = telemetry.Thermals.FirstOrDefault(t => t.Zone == zone);
                if (reading is not null) parts.Add($"{zone} {reading.TemperatureCelsius:0}°");
            }
            if (parts.Count == 0) parts.Add($"{telemetry.Thermals[0].TemperatureCelsius:0}°C");
            _tempTile.Value = string.Join(" · ", parts);
            var hottest = telemetry.Thermals.Max(t => t.TemperatureCelsius);
            _tempTile.ValueColor = hottest >= 90 ? AppTheme.Critical : hottest >= 75 ? AppTheme.Warning : null;
        }
        else
        {
            _tempTile.Value = sensors.Started ? "Unavailable" : "—";
            _tempTile.ValueColor = sensors.Started ? AppTheme.Muted : null;
        }

        var activeFans = telemetry.Fans.Where(f => f.Rpm > 0).ToList();
        if (activeFans.Count > 0)
        {
            _fanTile.Value = string.Join(" · ", activeFans.Select(f => $"{f.Name} {f.Rpm}"));
            _fanTile.ValueColor = null;
        }
        else if (sensors.MotherboardSensorsAvailable && telemetry.Fans.Count > 0)
        {
            // Board fan headers are readable and every one reads 0 RPM: worth a warning.
            _fanTile.Value = "Fans stopped";
            _fanTile.ValueColor = AppTheme.Warning;
        }
        else
        {
            // Without PawnIO only the GPU fan is visible, and it idles at 0 RPM by design.
            _fanTile.Value = !sensors.Started ? "—"
                : !sensors.PawnIoInstalled ? "Needs PawnIO"
                : !sensors.Elevated ? "Needs admin"
                : "N/A";
            _fanTile.ValueColor = sensors.Started ? AppTheme.Muted : null;
        }
        _toolTip.SetToolTip(_tempTile, sensorHelp);
        _toolTip.SetToolTip(_fanTile, sensorHelp);

        var netDown = telemetry.NetworkReceivedBytesPerSec;
        _networkTile.Value = $"{FormatRate(netDown)}↓  {FormatRate(telemetry.NetworkSentBytesPerSec)}↑";
        _networkTile.ValueColor = netDown > 100_000_000 ? AppTheme.Warning : null;
        PostConnectionFlow(telemetry);
    }

    private static string FormatRate(double bytesPerSec)
    {
        if (bytesPerSec >= 1_073_741_824) return $"{bytesPerSec / 1_073_741_824:0.0} GB/s";
        if (bytesPerSec >= 1_048_576) return $"{bytesPerSec / 1_048_576:0.0} MB/s";
        if (bytesPerSec >= 1024) return $"{bytesPerSec / 1024:0} KB/s";
        return $"{bytesPerSec:0} B/s";
    }

    private void ServiceOnOutputReceived(object? sender, string line)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLog(line));
            return;
        }
        AppendLog(line);
    }

    // The log keeps the raw lines; what it shows depends on privacy mode.
    private void AppendLog(string line)
    {
        _logLines.Add(line);
        _log.AppendText(ForDisplay(line) + Environment.NewLine);
    }

    private void RenderLog() =>
        _log.Text = string.Concat(_logLines.Select(line => ForDisplay(line) + Environment.NewLine));

    private static string ForDisplay(string text) => Privacy.Enabled ? Privacy.Scrub(text) : text;

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
