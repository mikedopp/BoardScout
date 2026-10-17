using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using BoardScout.Models;
using BoardScout.Remote;
using BoardScout.Services;
using BoardScout.Views;
using Glimmer.WinForms;
using Microsoft.Web.WebView2.WinForms;

namespace BoardScout.UI;

public sealed class MainForm : Form
{
    private readonly DriverScoutService _service = new();
    private readonly DriverDownloadService _downloadService;
    private readonly SystemTelemetryService _telemetryService = new();
    private readonly WebView2 _overviewWebView = new() { Dock = DockStyle.Fill };
    private readonly Panel _overviewHost = new();
    private readonly WebView2 _topologyWebView = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _systemWebView = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _topologyCard = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _systemCard = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _connectionsWebView = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _connectionsCard = new() { Dock = DockStyle.Fill };
    private readonly WebView2 _machinesWebView = new() { Dock = DockStyle.Fill };
    private readonly GlassCard _machinesCard = new() { Dock = DockStyle.Fill };
    private readonly List<RemoteWindow> _remoteWindows = [];
    private MachinesController? _machines;
    private bool _machinesReady;
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
    private readonly GaugeTile _cpuGauge = new("CPU load", spark: true, Color.Empty) { SparkFloor = 25, Width = 188 };
    private readonly GaugeTile _memoryGauge = new("Memory", spark: false, Color.Empty) { Width = 196 };
    private readonly GaugeTile _tempGauge = new("Temperature", spark: false, Color.Empty) { Width = 188 };
    private readonly GaugeTile _networkGauge = new("Network", spark: true, Color.Empty) { SparkFloor = 12_500, SparkShare = 0.27f, Width = 280 };
    private readonly ToolTip _toolTip = new() { InitialDelay = 300, AutoPopDelay = 12000 };
    private readonly RoundedButton _privacyChip = new();
    private readonly List<string> _logLines = [];
    private bool _privacyShown;
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly FlowLayoutPanel _metrics = new();
    private readonly DataGridView _drivers = new();
    private readonly DataGridView _storage = new();
    private readonly DataGridView _suggestions = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly RoundedButton _scanButton = new();
    private readonly RoundedButton _driversButton = new();
    private readonly RoundedButton _moreButton = new();
    private readonly RoundedButton _cancelButton = new();
    private readonly RoundedButton _pimpButton = new();
    private readonly RoundedButton _healthChip = new();
    private readonly ContextMenuStrip _driversMenu = new();
    private readonly ContextMenuStrip _moreMenu = new();
    private readonly ToolStripMenuItem _checkDriversItem = new("Check for newer drivers");
    private readonly ToolStripMenuItem _downloadDriversItem = new("Download the newer drivers");
    private readonly ToolStripMenuItem _importItem = new("Import a scan…");
    private readonly ToolStripMenuItem _exportItem = new("Export…");
    private readonly LinkLabel _feedbackLink = new();
    private readonly LinkLabel _dataFolderLink = new();
    private readonly VersionButton _versionButton = new();
    private readonly ContentTabControl _tabs = new();
    private readonly SidebarNavigationControl _navigation = new();
    private readonly List<(Button Button, bool Primary)> _themedButtons = [];

    private readonly AuroraPanel _header = new();
    private readonly Panel _statusPanel = new();
    private readonly NotifyIcon _trayIcon = new();

    private ScanManifest? _scan;
    private DriverReport? _report;
    private string? _scanPath;
    private CancellationTokenSource? _operationCts;
    private CancellationTokenSource? _telemetryCts;
    private int _telemetryIntervalMs;
    private SystemTelemetry? _lastTelemetry;

    // The Overview: the shared web view, the payload it was last sent, and whether the hardware changed since.
    private TabPage? _overviewPage;
    private bool _overviewReady;
    private string? _overviewPayload;
    private OverviewPayload? _overview;
    private bool _overviewStale = true;
    private bool _overviewLoading;
    private bool _overviewReloadQueued;
    private int _overviewVersion;
    private int _overviewFans = -1;
    private VersionPopout? _versionPopout;

    private readonly string? _snapshotDir;

    public MainForm(string? snapshotDir = null)
    {
        _snapshotDir = snapshotDir;
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

        if (_snapshotDir is not null)
        {
            // Off every monitor, at a fixed size, so the renders are the same on any PC and nothing flashes on screen.
            WindowState = FormWindowState.Normal;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            Bounds = new Rectangle(SystemInformation.VirtualScreen.Right + 200, SystemInformation.VirtualScreen.Top, 1920, 1080);
        }

        BuildUi();
        BuildTrayIcon();
        HandleCreated += (_, _) => AppTheme.ApplyWindowTheme(this);
        _service.OutputReceived += ServiceOnOutputReceived;
        _versionButton.Click += (_, _) => ToggleVersionPopout();
        AppSettings.Changed += OnSettingsChanged;
        Privacy.Learn(null);
        _privacyShown = Privacy.Enabled;
        Shown += async (_, _) =>
        {
            await LoadCachedDataAsync();
            if (_scan is null) _ = LoadSystemInfoAsync();
            StartTelemetry();
            // The Overview reads the device tree (a fraction of a second) while the web views start.
            var overview = LoadOverviewAsync(read: true);
            await InitializeWebViewsAsync();
            await overview;
            _telemetryService.DetailedRates = ConnectionsVisible || OverviewVisible;
            if (_snapshotDir is not null) await SnapshotAsync(_snapshotDir);
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
            // Remote dashboards end with this window: their SSH sessions close, and BoardScout stops on each machine.
            foreach (var window in _remoteWindows.ToList()) window.Close();
            _machines?.Dispose();
            _overviewWebView.Dispose();
            _topologyWebView.Dispose();
            _systemWebView.Dispose();
            _connectionsWebView.Dispose();
            _machinesWebView.Dispose();
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
        _overviewStale = true;
        if (ConnectionsVisible) _ = LoadConnectionsAsync();
        else if (OverviewVisible) _ = LoadOverviewAsync(read: true);
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
        _tabs.TabPages.Add(BuildWebTab("Machines", _machinesCard, _machinesWebView));
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
            // Per-disk and per-adapter rates are only sampled while the Connections map or the Overview is on screen.
            _telemetryService.DetailedRates = ConnectionsVisible || OverviewVisible;
            if (ConnectionsVisible && _connectionsStale) _ = LoadConnectionsAsync();
            if (OverviewVisible && _overviewStale) _ = LoadOverviewAsync(read: true);
        };

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 3;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 25;
        _progress.Visible = false;
        foreach (var scrolling in new Control[] { _drivers, _storage, _suggestions, _log })
            AppTheme.UseDarkScrollbars(scrolling);
        ApplyTheme();
    }

    private void BuildHeader()
    {
        _header.Dock = DockStyle.Top;
        _header.Height = 150;
        _header.BackColor = AppTheme.Surface;
        _header.Padding = new Padding(24, 16, 24, 14);
        _header.Tag = "surface";

        // Transparent layers so the header's aurora shows through behind the text and gauges.
        var textPanel = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent };
        _title.Text = "BoardScout";
        _title.Font = new Font("Segoe UI Semibold", 20);
        _title.ForeColor = AppTheme.Text;
        _title.BackColor = Color.Transparent;
        _title.AutoSize = true;
        _title.Location = new Point(0, 0);
        _title.Tag = "text";

        _subtitle.Text = "Portable motherboard, storage, and driver intelligence";
        _subtitle.ForeColor = AppTheme.Muted;
        _subtitle.BackColor = Color.Transparent;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(2, 39);
        _subtitle.Tag = "muted";

        // Four live gauges: what the PC is doing right now. Storage, drivers, and fixes live in the Overview's panel.
        _metrics.AutoSize = true;
        _metrics.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _metrics.WrapContents = false;
        _metrics.Location = new Point(0, 64);
        _metrics.BackColor = Color.Transparent;
        _metrics.Margin = new Padding(0);
        _metrics.Controls.AddRange([_cpuGauge, _memoryGauge, _tempGauge, _networkGauge]);

        _title.Resize += (_, _) => LayoutTitleRow();
        textPanel.Controls.Add(_versionButton);

        // How the PC is doing, in one chip; it opens the Efficiency tab.
        _healthChip.Font = new Font("Segoe UI Semibold", 8.75f);
        _healthChip.Height = 30;
        _healthChip.Cursor = Cursors.Hand;
        _healthChip.Visible = false;
        _healthChip.Click += (_, _) => SelectTab("Efficiency");
        textPanel.Controls.Add(_healthChip);

        // Visible whenever privacy mode is on, so a screenshot shows it was taken masked.
        _privacyChip.Text = "Privacy on";
        _privacyChip.Font = new Font("Segoe UI Semibold", 8.5f);
        _privacyChip.Size = new Size(104, 30);
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
        LayoutTitleRow();

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 470,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent
        };

        // One primary action; the rest wait in two menus.
        ConfigureButton(_scanButton, "Scan now", async (_, _) => await RunScanAsync(), primary: true);
        ConfigureButton(_pimpButton, "Pimp My Build", (_, _) => OpenPimpMyBuild());
        AppTheme.StyleFeatureButton(_pimpButton);
        _pimpButton.Tag = "featureButton";
        ConfigureButton(_driversButton, "Drivers  ▾", (_, _) => _driversMenu.Show(_driversButton, new Point(0, _driversButton.Height + 4)));
        ConfigureButton(_moreButton, "More  ▾", (_, _) => _moreMenu.Show(_moreButton, new Point(0, _moreButton.Height + 4)));
        ConfigureButton(_cancelButton, "Cancel", (_, _) => _operationCts?.Cancel());
        _toolTip.SetToolTip(_scanButton, "Take a fresh hardware inventory (about half a minute). Only needed after hardware changes.");
        _toolTip.SetToolTip(_pimpButton, "Open an upgrade report for this PC in your browser.");

        _checkDriversItem.Click += async (_, _) => await RunDriverCheckAsync();
        _downloadDriversItem.Click += async (_, _) => await DownloadDriversAsync();
        _checkDriversItem.ToolTipText = "Ask vendor, OEM, and Windows Update catalog pages for newer drivers. Nothing is installed.";
        _downloadDriversItem.ToolTipText = "Download what the last check found, or open its official page.";
        var driversTab = new ToolStripMenuItem("Open the Drivers tab", null, (_, _) => SelectTab("Drivers"));
        _driversMenu.Items.AddRange([_checkDriversItem, _downloadDriversItem, new ToolStripSeparator(), driversTab]);
        _importItem.Click += async (_, _) => await LoadScanFromFileAsync();
        _exportItem.Click += (_, _) => ExportScan();
        _exportItem.ToolTipText = "A spec sheet, an upgrade planner, or the raw scan as JSON.";
        _moreMenu.Items.AddRange([_exportItem, _importItem]);
        foreach (var menu in new[] { _driversMenu, _moreMenu })
        {
            menu.Renderer = new DarkMenuRenderer();
            menu.ShowItemToolTips = true;
            menu.Font = new Font("Segoe UI", 9.5f);
            menu.Padding = new Padding(4);
            foreach (ToolStripItem item in menu.Items) item.Padding = new Padding(4, 5, 12, 5);
        }

        _cancelButton.Visible = false;
        _checkDriversItem.Enabled = false;
        _downloadDriversItem.Enabled = false;
        _exportItem.Enabled = false;
        _pimpButton.Enabled = false;

        actions.Controls.AddRange([_moreButton, _driversButton, ToolbarSep(), _pimpButton, _scanButton, _cancelButton]);

        _header.Controls.Add(textPanel);
        _header.Controls.Add(actions);
        _header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = AppTheme.Border, Tag = "border" });
    }

    // The title, then the version button, the health chip, and the privacy chip, in one row.
    private void LayoutTitleRow()
    {
        var x = _title.Left + _title.PreferredSize.Width + 10;
        var top = _title.Top + (_title.PreferredSize.Height - _versionButton.Height) / 2;
        _versionButton.Location = new Point(x, top);
        x = _versionButton.Right + 8;
        if (_healthChip.Visible)
        {
            _healthChip.Location = new Point(x, top);
            x = _healthChip.Right + 8;
        }
        _privacyChip.Location = new Point(x, top);
    }

    // The board and its inspector are the shared web view (Assets/overview.html), the same page the browser
    // dashboard shows. It brings its own cards, so it sits straight on the window's background.
    private TabPage BuildOverviewTab()
    {
        var page = NewPage("Overview");
        page.Padding = new Padding(0);
        _overviewWebView.DefaultBackgroundColor = AppTheme.Background;
        _overviewWebView.Visible = false;
        _overviewHost.Dock = DockStyle.Fill;
        _overviewHost.BackColor = AppTheme.Background;
        _overviewHost.Tag = "background";
        _overviewHost.Controls.Add(_overviewWebView);
        page.Controls.Add(_overviewHost);
        _webTabs.Add((page, _overviewWebView));
        return _overviewPage = page;
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
        await InitializeWebViewAsync(_overviewWebView, _overviewHost, "overview.html", () =>
        {
            _overviewReady = true;
            PostSettings(_overviewWebView);
            if (_overviewPayload is not null) _overviewWebView.CoreWebView2.PostWebMessageAsJson(_overviewPayload);
            if (_lastTelemetry is not null) PostOverviewTelemetry(_lastTelemetry);
        });
        if (_overviewWebView.CoreWebView2 is { } overview)
            overview.WebMessageReceived += OnOverviewMessage;
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
        await InitializeWebViewAsync(_machinesWebView, _machinesCard, "machines.html", () =>
        {
            _machinesReady = true;
            PostSettings(_machinesWebView);
            Machines.SendState();
        });
        if (_machinesWebView.CoreWebView2 is { } machines)
            machines.WebMessageReceived += async (_, e) =>
            {
                try
                {
                    using var message = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
                    await Machines.HandleAsync(message.RootElement.Clone());
                }
                catch (Exception ex)
                {
                    AppendLog("MACHINES: " + ex.Message);
                }
            };
    }

    // ---- Machines: other PCs and servers over SSH --------------------------------------------------------

    private MachinesController Machines
    {
        get
        {
            if (_machines is not null) return _machines;
            var controller = new MachinesController(_service.DataRoot, PostToMachines, line => OnUiThread(() => AppendLog(line)));
            // Remote dashboards open in a window of their own here, not in a browser tab.
            controller.OpenNative = session =>
            {
                OnUiThread(() => ShowRemote(session));
                return true;
            };
            controller.SessionEnded += session => OnUiThread(() => _remoteWindows.FirstOrDefault(w => w.Session == session)?.Close());
            return _machines = controller;
        }
    }

    private void OnUiThread(Action action)
    {
        if (IsDisposed || !IsHandleCreated) return;
        try
        {
            if (InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (InvalidOperationException) { } // also ObjectDisposedException: the window is closing
    }

    private void PostToMachines(string json) => OnUiThread(() =>
    {
        if (_machinesReady) _machinesWebView.CoreWebView2?.PostWebMessageAsJson(json);
    });

    private void ShowRemote(RemoteSession session)
    {
        if (_remoteWindows.FirstOrDefault(w => w.Session == session) is { } open)
        {
            if (open.WindowState == FormWindowState.Minimized) open.WindowState = FormWindowState.Normal;
            open.Activate();
            return;
        }
        var window = new RemoteWindow(session, _service.DataRoot);
        window.FormClosed += (_, _) =>
        {
            _remoteWindows.Remove(window);
            _machines?.Closed(session);
        };
        _remoteWindows.Add(window);
        window.Show();
    }

    private bool ConnectionsVisible => _connectionsPage is not null && _tabs.SelectedTab == _connectionsPage && Visible;

    private bool OverviewVisible => _overviewPage is not null && _tabs.SelectedTab == _overviewPage && Visible;

    // ---- Overview: the board, from the device tree, the inventory, and the plan -----------------------------

    /// <summary>Reads the device tree again when asked (or when there is no read yet), then sends the Overview.</summary>
    private async Task LoadOverviewAsync(bool read)
    {
        if (_overviewLoading)
        {
            _overviewReloadQueued = true;
            return;
        }
        _overviewLoading = true;
        try
        {
            do
            {
                _overviewReloadQueued = false;
                if (read || _connectionsCapture is null)
                {
                    var capture = await Task.Run(ConnectionsService.Read);
                    if (IsDisposed) return;
                    _connectionsCapture = capture;
                    _overviewStale = false;
                }
                read = false;
                await PostOverviewAsync();
            }
            while (_overviewReloadQueued && !IsDisposed);
        }
        catch (Exception ex)
        {
            AppendLog("OVERVIEW: " + ex.Message);
        }
        finally
        {
            _overviewLoading = false;
        }
    }

    /// <summary>Builds the Overview from what was last read, with the current privacy setting (no new read).</summary>
    private async Task PostOverviewAsync()
    {
        if (_connectionsCapture is not { } capture) return;
        var version = ++_overviewVersion;
        var (privacy, discovery, scan, report, telemetry, measured) = (Privacy.Enabled, _discovery, _scan, _report, _lastTelemetry, _interruptProfile);
        var overview = await Task.Run(() =>
        {
            var map = ConnectionsService.Build(capture, privacy, discovery);
            var plan = PlanService.Build(capture, map, [], measured, privacy, telemetry);
            return WindowsOverview.Build(capture, map, scan, report, plan, telemetry, privacy);
        });
        if (IsDisposed || version != _overviewVersion) return;
        _overview = overview;
        _overviewFans = BoardFans(telemetry);
        _overviewPayload = Json.Message("overview", System.Text.Json.JsonSerializer.Serialize(overview, Json.Options));
        if (_overviewReady) _overviewWebView.CoreWebView2.PostWebMessageAsJson(_overviewPayload);
        UpdateHealthChip();
        BindSuggestions();
    }

    private static int BoardFans(SystemTelemetry? telemetry) =>
        telemetry?.Fans.Count(f => !f.Name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) ?? 0;

    private void PostOverviewTelemetry(SystemTelemetry telemetry)
    {
        if (!_overviewReady || IsDisposed) return;
        var core = _overviewWebView.CoreWebView2;
        core.PostWebMessageAsJson(Json.Message("telemetry", System.Text.Json.JsonSerializer.Serialize(WindowsOverview.Header(telemetry, _connectionsCapture), Json.Options)));
        if (telemetry.InterfaceRates is not null)
            core.PostWebMessageAsJson(Json.Message("flow", System.Text.Json.JsonSerializer.Serialize(WindowsOverview.Flow(telemetry, _netKeys), Json.Options)));
    }

    private void OnOverviewMessage(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var root = message.RootElement;
            var type = root.TryGetProperty("type", out var value) ? value.GetString() : null;
            if (type == "show" && root.TryGetProperty("node", out var node) && node.GetString() is { } id) ShowOnConnections(id);
            else if (type == "tab" && root.TryGetProperty("name", out var name) && name.GetString() is { } tab) SelectTab(tab);
        }
        catch (Exception ex)
        {
            AppendLog("OVERVIEW: " + ex.Message);
        }
    }

    // Opens Connections and highlights the part's card once the map has drawn it.
    private void ShowOnConnections(string id)
    {
        if (_connectionsPage is null) return;
        _tabs.SelectedTab = _connectionsPage;
        if (!_connectionsReady) return;
        var quoted = System.Text.Json.JsonSerializer.Serialize(id, BoardScoutJson.Default.String);
        _ = _connectionsWebView.CoreWebView2.ExecuteScriptAsync(
            $"(function(){{let n=40;(function a(){{if(document.getElementById('c-'+{quoted})&&window.showNodes){{showNodes([{quoted}]);return}}if(--n>0)setTimeout(a,250)}})()}})()");
    }

    private void SelectTab(string name)
    {
        var page = _tabs.TabPages.Cast<TabPage>().FirstOrDefault(p => p.Text.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (page is not null) _tabs.SelectedTab = page;
    }

    // "Healthy", or how many things the plan and the inventory checks think deserve a look.
    private void UpdateHealthChip()
    {
        if (_overview is not { } overview)
        {
            _healthChip.Visible = false;
            LayoutTitleRow();
            return;
        }
        var count = overview.Suggestions.Count(s => s.Severity != "info") +
                    overview.Parts.Count(p => p.Tone == "warn" && !overview.Suggestions.Any(s => s.Title.Contains(p.Name, StringComparison.Ordinal)));
        var good = count == 0;
        _healthChip.Text = good ? "●  Healthy" : count == 1 ? "●  One thing worth a look" : $"●  {count} things worth a look";
        _healthChip.ForeColor = good ? AppTheme.Good : AppTheme.Warning;
        _healthChip.BackColor = good ? Color.FromArgb(16, 44, 36) : Color.FromArgb(52, 38, 18);
        _healthChip.FlatAppearance.BorderColor = Color.FromArgb(150, good ? AppTheme.Good : AppTheme.Warning);
        _healthChip.FlatAppearance.MouseOverBackColor = good ? Color.FromArgb(24, 58, 46) : Color.FromArgb(68, 50, 24);
        _healthChip.FlatAppearance.MouseDownBackColor = _healthChip.BackColor;
        _healthChip.Width = TextRenderer.MeasureText(_healthChip.Text, _healthChip.Font).Width + 30;
        _healthChip.AccessibleName = good ? "Healthy. Opens the Efficiency tab." : $"{count} things worth a look. Opens the Efficiency tab.";
        _toolTip.SetToolTip(_healthChip, good ? "Every check passed. Click for the Efficiency tab." : "The Overview's panel lists them; click for the Efficiency tab.");
        _healthChip.Visible = true;
        LayoutTitleRow();
    }

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
                _overviewStale = false;
                _ = PostOverviewAsync();
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
            var telemetry = _lastTelemetry;
            var json = await Task.Run(() => PlanService.ToJson(
                PlanService.Build(capture, ConnectionsService.Build(capture, privacy, discovery), loads, measured, privacy, telemetry)), cts.Token);
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
            var io = telemetry.DiskDetail?.GetValueOrDefault(number) ?? default;
            // [read B/s, write B/s, reads/s, writes/s, queue, busy %, ms per read (-1 none), ms per write (-1 none), read since boot, written since boot]
            flow.Disks[number.ToString(System.Globalization.CultureInfo.InvariantCulture)] =
                [Math.Round(rate.In), Math.Round(rate.Out), Math.Round(rate.OpsIn), Math.Round(rate.OpsOut), queue, busy,
                 Math.Round(io.ReadMs ?? -1, 2), Math.Round(io.WriteMs ?? -1, 2), io.TotalRead, io.TotalWritten];
        }
        foreach (var (zone, key) in new[] { ("CPU", "cpu"), ("GPU", "gpu"), ("Chipset", "chipset") })
            if (telemetry.Thermals.FirstOrDefault(t => t.Zone == zone) is { } reading)
                flow.Temperatures[key] = reading.TemperatureCelsius;
        _connectionsWebView.CoreWebView2.PostWebMessageAsJson(
            System.Text.Json.JsonSerializer.Serialize(flow, BoardScoutJson.Default.ConnectionFlow));
    }

    private async Task InitializeWebViewAsync(WebView2 view, Control card, string page, Action ready)
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

        _versionButton.State = GlimmerState.Thinking;
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
            _versionButton.Flash(GlimmerState.Success);
        }
        catch (Exception ex)
        {
            _versionButton.State = GlimmerState.Error;
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
            BindDrivers();
            BindSuggestions();
            _ = PostOverviewAsync();
            var updates = _report.Results.Count(r => r.Status == "update-available");
            _downloadDriversItem.Enabled = true;
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
        // The version orb says what is happening: thinking now, mint for a moment when done, red after a failure
        // until the next run or a click on it.
        _versionButton.State = GlimmerState.Thinking;

        try
        {
            await operation(_operationCts.Token);
            _versionButton.Flash(GlimmerState.Success);
        }
        catch (OperationCanceledException)
        {
            _versionButton.State = GlimmerState.Idle;
            _status.Text = "Operation cancelled. Existing cached results were preserved.";
            AppendLog("Cancelled.");
        }
        catch (Exception ex)
        {
            _versionButton.State = GlimmerState.Error;
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
        _subtitle.Text = $"{_scan.FormFactor.ToUpperInvariant()}  •  {WindowsOverview.ShortCpuName(cpu.Name)}  •  {cpu.Cores}C/{cpu.Threads}T  •  {_scan.Scan.Os.Caption}";
        _ = SendScanToTopologyAsync();
        _ = PostOverviewAsync();
        BindDrivers();
        BindStorage();
        BindSuggestions();
        _ = LoadSystemInfoAsync();
        _checkDriversItem.Enabled = true;
        _downloadDriversItem.Enabled = _report is not null;
        _exportItem.Enabled = true;
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
        if (_overview is { } overview)
        {
            foreach (var suggestion in overview.Suggestions)
            {
                var (label, color) = suggestion.Severity switch
                {
                    "warning" => ("Fix", AppTheme.Warning),
                    "improvement" => ("Improve", AppTheme.Good),
                    _ => ("Info", AppTheme.Accent)
                };
                var rowIndex = _suggestions.Rows.Add(label, suggestion.Category, suggestion.Title,
                    suggestion.Detail + (suggestion.Action is { Length: > 0 } action ? Environment.NewLine + "Next: " + action : ""));
                _suggestions.Rows[rowIndex].Cells[0].Style.ForeColor = color;
            }
            return;
        }
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
        if (_versionButton.State == GlimmerState.Error) _versionButton.State = GlimmerState.Idle; // seen
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

    // ---- --snapshot: QA renders ------------------------------------------------------------------------------

    // The window with glass on, with a part pinned in the Overview's panel, and with glass off; then BoardScout
    // closes. Native controls draw through DrawToBitmap and the web view through its own capture, so nothing is
    // read from the screen.
    private async Task SnapshotAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var glassBefore = AppSettings.Current.GlassEffects;
        try
        {
            AppSettings.Update(s => s.GlassEffects = true);
            await Task.Delay(7000);
            await SaveSnapshotAsync(Path.Combine(directory, "overview-glass.png"));
            SaveVersionStates(Path.Combine(directory, "version-states.png"));
            if (_overviewReady)
            {
                await _overviewWebView.CoreWebView2.ExecuteScriptAsync(
                    "(()=>{const g=[...document.querySelectorAll('#board .part')].find(p=>/^Graphics/.test(p.getAttribute('aria-label')||''))" +
                    "||document.querySelector('#board .part');g&&g.dispatchEvent(new MouseEvent('click',{bubbles:true}));})()");
                await Task.Delay(800);
                await SaveSnapshotAsync(Path.Combine(directory, "overview-pinned.png"));
                await _overviewWebView.CoreWebView2.ExecuteScriptAsync("document.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape'}))");
            }
            AppSettings.Update(s => s.GlassEffects = false);
            await Task.Delay(2500);
            await SaveSnapshotAsync(Path.Combine(directory, "overview-solid.png"));
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "snapshot-error.txt"), ex.ToString());
        }
        finally
        {
            AppSettings.Update(s => s.GlassEffects = glassBefore);
            _trayIcon.Visible = false;
            Close();
        }
    }

    // The version badge in each state (idle, thinking, success, error), cropped from the window and enlarged 3x
    // with no smoothing, so the orb can be judged pixel by pixel.
    private void SaveVersionStates(string path)
    {
        GlimmerState[] states = [GlimmerState.Idle, GlimmerState.Thinking, GlimmerState.Success, GlimmerState.Error];
        const int zoom = 3;
        var before = _versionButton.State;
        var crop = Rectangle.Inflate(RectangleToClient(_versionButton.RectangleToScreen(_versionButton.ClientRectangle)), 10, 8);
        var client = PointToScreen(Point.Empty);
        crop.Offset(client.X - Left, client.Y - Top);
        using var strip = new Bitmap(crop.Width * zoom, crop.Height * zoom * states.Length);
        using (var g = Graphics.FromImage(strip))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (var i = 0; i < states.Length; i++)
            {
                _versionButton.State = states[i];
                _versionButton.AdvanceAnimation(1f); // past the colour change and the success pop
                using var window = new Bitmap(Width, Height);
                DrawToBitmap(window, new Rectangle(Point.Empty, Size));
                g.DrawImage(window, new Rectangle(0, i * crop.Height * zoom, crop.Width * zoom, crop.Height * zoom), crop, GraphicsUnit.Pixel);
            }
        }
        _versionButton.State = before;
        strip.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private async Task SaveSnapshotAsync(string path)
    {
        using var window = new Bitmap(Width, Height);
        DrawToBitmap(window, new Rectangle(Point.Empty, Size));
        var client = PointToScreen(Point.Empty);
        var offset = new Point(client.X - Left, client.Y - Top);
        using var picture = new Bitmap(ClientSize.Width, ClientSize.Height);
        using (var g = Graphics.FromImage(picture))
        {
            g.DrawImage(window, new Rectangle(Point.Empty, ClientSize), new Rectangle(offset, ClientSize), GraphicsUnit.Pixel);
            // DrawToBitmap leaves web views blank; each visible one is captured by WebView2 and drawn in its place.
            foreach (var (_, view) in _webTabs.Where(t => t.View.Visible && t.View.CoreWebView2 is not null))
            {
                using var stream = new MemoryStream();
                await view.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream);
                stream.Position = 0;
                using var page = Image.FromStream(stream);
                var at = PointToClient(view.PointToScreen(Point.Empty));
                g.DrawImage(page, new Rectangle(at, view.ClientSize));
            }
        }
        picture.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed) return;
        if (_telemetryIntervalMs != AppSettings.Current.TelemetryIntervalMs) StartTelemetry();
        _versionButton.RefreshMotion();
        _header.RefreshAurora();
        if (_overviewReady) PostSettings(_overviewWebView);
        if (_topologyReady) PostSettings(_topologyWebView);
        if (_systemReady) PostSettings(_systemWebView);
        if (_connectionsReady) PostSettings(_connectionsWebView);
        if (_machinesReady) PostSettings(_machinesWebView);
        if (_privacyShown != Privacy.Enabled)
        {
            _privacyShown = Privacy.Enabled;
            _machines?.SendState();
            _privacyChip.Visible = Privacy.Enabled;
            RenderLog();
            BindStorage();
            // The map masks MACs, Wi-Fi and device names, and IPv6 on the C# side, so it is rebuilt
            // from what was last read (no new read needed).
            if (_connectionsCapture is not null)
            {
                PostConnectionsMap(_discovery);
                _ = PostOverviewAsync();
            }
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
        _checkDriversItem.Enabled = !busy && _scan is not null;
        _downloadDriversItem.Enabled = !busy && _report is not null;
        _importItem.Enabled = !busy;
        _exportItem.Enabled = !busy && _scan is not null;
        _pimpButton.Enabled = !busy && _scan is not null;
        _cancelButton.Visible = busy;
        _progress.Visible = busy;
        _status.Text = message;
        UseWaitCursor = busy;
    }

    private void SetEmptyState()
    {
        _title.Text = "BoardScout";
        _subtitle.Text = "No inventory yet: Scan now takes one (about half a minute), or import a DriverScout JSON file from More.";
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
        _cpuGauge.Accent = AppTheme.Accent;
        _memoryGauge.Accent = AppTheme.Accent;
        _tempGauge.Accent = AppTheme.Good;
        _networkGauge.Accent = AppTheme.Purple;
        foreach (var gauge in new[] { _cpuGauge, _memoryGauge, _tempGauge, _networkGauge }) gauge.Invalidate();
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
        _navigation.RefreshTheme();
        AppTheme.ApplyWindowTheme(this);
        _tabs.Invalidate();

        if (_scan is not null)
        {
            BindDrivers();
            BindStorage();
            BindSuggestions();
        }
        UpdateHealthChip();
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
        var sensors = _telemetryService.SensorStatus;

        _cpuGauge.AddSample(telemetry.CpuUsagePercent);
        _cpuGauge.Show("CPU load", $"{telemetry.CpuUsagePercent:0}%", tone: telemetry.CpuUsagePercent >= 90 ? AppTheme.Warning : null);

        var totalGb = telemetry.MemoryTotalBytes / 1_073_741_824d;
        var memory = totalGb > 0 ? telemetry.MemoryUsedGb / totalGb : 0;
        _memoryGauge.Show("Memory", $"{telemetry.MemoryUsedGb:0.0}", $"/ {totalGb:0.#} GB", (float)memory,
            memory >= 0.92 ? AppTheme.Critical : memory >= 0.8 ? AppTheme.Warning : null);

        // The CPU's temperature when the sensors can read it, else the graphics card's.
        var reading = telemetry.Thermals.FirstOrDefault(t => t.Zone == "CPU") ?? telemetry.Thermals.FirstOrDefault(t => t.Zone == "GPU") ??
                      telemetry.Thermals.FirstOrDefault();
        if (reading is not null)
        {
            var celsius = reading.TemperatureCelsius;
            _tempGauge.Show($"{(reading.Zone is "CPU" or "GPU" or "VRM" ? reading.Zone : "Board")} temp", $"{celsius:0}°C", "",
                (float)(celsius / 100), celsius >= 90 ? AppTheme.Critical : celsius >= 75 ? AppTheme.Warning : AppTheme.Good);
        }
        else _tempGauge.Show("Temperature", sensors.Started ? "Not reported" : "—", "", 0);
        _toolTip.SetToolTip(_tempGauge, sensors.MotherboardSensorsAvailable ? null :
            "CPU, VRM, and motherboard fan sensors need the PawnIO driver and administrator rights. Open the version button for details.");

        var down = telemetry.NetworkReceivedBytesPerSec;
        var up = telemetry.NetworkSentBytesPerSec;
        _networkGauge.AddSample(down + up);
        _networkGauge.Show("Network", $"↓ {FormatRate(down)}", $"↑ {FormatRate(up)}", tone: down > 100_000_000 ? AppTheme.Warning : null);

        PostConnectionFlow(telemetry);
        if (OverviewVisible) PostOverviewTelemetry(telemetry);
        // Board fan readings appear once PawnIO and administrator rights let the sensors read them.
        if (_overviewFans >= 0 && BoardFans(telemetry) != _overviewFans) _ = PostOverviewAsync();
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
