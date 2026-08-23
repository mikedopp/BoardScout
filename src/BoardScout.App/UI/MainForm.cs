using System.Diagnostics;
using BoardScout.Models;
using BoardScout.Services;

namespace BoardScout.UI;

public sealed class MainForm : Form
{
    private readonly DriverScoutService _service = new();
    private readonly DriverDownloadService _downloadService;
    private readonly SystemTelemetryService _telemetryService = new();
    private readonly System.Windows.Forms.Timer _telemetryTimer = new() { Interval = 1000 };
    private readonly BoardMapControl _boardMap = new() { Dock = DockStyle.Fill };
    private Microsoft.Web.WebView2.WinForms.WebView2? _topologyWebView;
    private bool _topologyReady;
    private string? _pendingScanJson;
    private readonly Label _tempLabel = new();
    private readonly Label _fanLabel = new();
    private readonly Label _networkLabel = new();
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
    private readonly Button _zoomOutButton = new();
    private readonly Button _zoomResetButton = new();
    private readonly Button _zoomInButton = new();
    private readonly VersionButton _versionButton = new();
    private readonly ContentTabControl _tabs = new();
    private readonly SidebarNavigationControl _navigation = new();
    private readonly List<(Button Button, bool Primary)> _themedButtons = [];

    private readonly Panel _header = new();
    private readonly Panel _statusPanel = new();
    private readonly Panel _boardCard = new();
    private readonly Panel _detailsCard = new();
    private readonly Panel _boardToolbar = new();
    private readonly Panel _inspectPanel = new();
    private readonly Label _factsHeading = new();
    private readonly Label _inspectCategory = new();
    private readonly Label _inspectTitle = new();
    private readonly Label _inspectStatus = new();
    private readonly Label _inspectDetail = new();
    private readonly Label _inspectCapabilityHeading = new();
    private readonly Label _inspectCapability = new();
    private readonly LinkLabel _inspectLink = new();
    private readonly NotifyIcon _trayIcon = new();

    private ScanManifest? _scan;
    private DriverReport? _report;
    private string? _scanPath;
    private CancellationTokenSource? _operationCts;
    private BoardPartDetails? _currentPartDetails;

    public MainForm()
    {
        _downloadService = new DriverDownloadService(_service.DataRoot);
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
        _telemetryTimer.Tick += (_, _) => SampleTelemetry();
        Shown += async (_, _) =>
        {
            await LoadCachedDataAsync();
            SampleTelemetry();
            _telemetryTimer.Start();
        };
        FormClosing += (_, _) =>
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
            _operationCts?.Cancel();
            _telemetryTimer.Stop();
            _telemetryService.Dispose();
            _topologyWebView?.Dispose();
        };
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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized)
        {
            _trayIcon.Visible = true;
            Hide();
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
        _tabs.TabPages.Add(BuildTopologyTab());
        _tabs.TabPages.Add(BuildDriversTab());
        _tabs.TabPages.Add(BuildStorageTab());
        _tabs.TabPages.Add(BuildSuggestionsTab());
        _tabs.TabPages.Add(BuildLogTab());
        _navigation.SelectedIndexChanged += (_, _) =>
        {
            if (_tabs.SelectedIndex != _navigation.SelectedIndex)
                _tabs.SelectedIndex = _navigation.SelectedIndex;
        };
        _tabs.SelectedIndexChanged += (_, _) =>
        {
            if (_navigation.SelectedIndex != _tabs.SelectedIndex)
                _navigation.SelectedIndex = _tabs.SelectedIndex;
        };

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 3;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 25;
        _progress.Visible = false;
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

        var textPanel = new Panel { Dock = DockStyle.Fill };
        _title.Text = "BoardScout";
        _title.Font = new Font("Segoe UI Semibold", 21);
        _title.ForeColor = AppTheme.Text;
        _title.AutoSize = true;
        _title.Location = new Point(0, 0);
        _title.Tag = "text";

        _subtitle.Text = "Portable motherboard, storage, and driver intelligence";
        _subtitle.ForeColor = AppTheme.Muted;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(2, 38);
        _subtitle.Tag = "muted";

        _metrics.AutoSize = false;
        _metrics.Size = new Size(910, 50);
        _metrics.WrapContents = false;
        _metrics.Location = new Point(0, 70);
        _metrics.BackColor = Color.Transparent;
        _title.Resize += (_, _) => _versionButton.Location = new Point(_title.Right + 10, _title.Top + (_title.Height - _versionButton.Height) / 2);
        _versionButton.Location = new Point(_title.PreferredSize.Width + 10, 6);
        textPanel.Controls.Add(_versionButton);
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
        _boardCard.BackColor = AppTheme.Surface;
        _boardCard.BorderStyle = BorderStyle.FixedSingle;
        _boardCard.Padding = new Padding(4);
        _boardCard.Tag = "surface";

        BuildBoardToolbar();
        _boardCard.Controls.Add(_boardMap);
        _boardCard.Controls.Add(_boardToolbar);
        split.Panel1.Controls.Add(_boardCard);

        _detailsCard.Dock = DockStyle.Fill;
        _detailsCard.BackColor = AppTheme.Surface;
        _detailsCard.BorderStyle = BorderStyle.FixedSingle;
        _detailsCard.Padding = new Padding(18, 16, 18, 16);
        _detailsCard.Tag = "surface";

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
        _zoomOutButton.Width = 42;
        _zoomResetButton.Width = 76;
        _zoomInButton.Width = 42;
        zoom.Controls.AddRange([_zoomOutButton, _zoomResetButton, _zoomInButton]);

        _boardToolbar.Controls.Add(hint);
        _boardToolbar.Controls.Add(title);
        _boardToolbar.Controls.Add(zoom);
    }

    private TabPage BuildTopologyTab()
    {
        var page = NewPage("Topology");
        var card = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(0),
            Tag = "surface"
        };

        try
        {
            _topologyWebView = new Microsoft.Web.WebView2.WinForms.WebView2 { Dock = DockStyle.Fill };
            _topologyWebView.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) return;
                _topologyWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _topologyWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
                _topologyWebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
                var htmlPath = Path.Combine(AppContext.BaseDirectory, "Assets", "topology.html");
                if (File.Exists(htmlPath))
                {
                    _topologyWebView.CoreWebView2.Navigate(new Uri(htmlPath).AbsoluteUri);
                    _topologyWebView.CoreWebView2.NavigationCompleted += (_, _) =>
                    {
                        _topologyReady = true;
                        if (_pendingScanJson is not null)
                        {
                            _topologyWebView.CoreWebView2.PostWebMessageAsJson(_pendingScanJson);
                            _pendingScanJson = null;
                        }
                    };
                }
            };
            _ = _topologyWebView.EnsureCoreWebView2Async();
            card.Controls.Add(_topologyWebView);
        }
        catch
        {
            card.Controls.Add(new Label
            {
                Text = "WebView2 runtime not available — install the Evergreen Runtime from microsoft.com",
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = AppTheme.Muted,
                Tag = "muted"
            });
        }

        page.Controls.Add(card);
        return page;
    }

    private void BuildInspector()
    {
        _inspectPanel.Dock = DockStyle.Top;
        _inspectPanel.Height = 270;
        _inspectPanel.BackColor = AppTheme.SurfaceRaised;
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
        _inspectStatus.TextAlign = ContentAlignment.MiddleLeft;
        _inspectStatus.Padding = new Padding(8, 0, 8, 0);
        _inspectStatus.AutoEllipsis = true;

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

        _inspectPanel.Controls.AddRange([
            _inspectCategory, _inspectTitle, _inspectStatus, _inspectDetail,
            _inspectCapabilityHeading, _inspectCapability, _inspectLink]);
        _inspectPanel.SizeChanged += (_, _) => LayoutInspector();
        LayoutInspector();
    }

    private void LayoutInspector()
    {
        var width = Math.Max(200, _inspectPanel.ClientSize.Width - 32);
        foreach (var label in new[]
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
        page.Controls.Add(_drivers);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
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
        page.Controls.Add(_storage);
        return page;
    }

    private TabPage BuildSuggestionsTab()
    {
        var page = NewPage("Efficiency");
        ConfigureGrid(_suggestions,
            ("Priority", 90), ("Category", 90), ("Suggestion", 260), ("Why it matters / next action", 620));
        _suggestions.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _suggestions.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
        page.Controls.Add(_suggestions);
        return page;
    }

    private TabPage BuildLogTab()
    {
        var page = NewPage("Scan Log");
        _log.Dock = DockStyle.Fill;
        _log.Multiline = true;
        _log.ReadOnly = true;
        _log.ScrollBars = ScrollBars.Both;
        _log.BackColor = AppTheme.SurfaceRaised;
        _log.ForeColor = AppTheme.Text;
        _log.BorderStyle = BorderStyle.FixedSingle;
        _log.Font = new Font("Cascadia Mono", 9);
        page.Controls.Add(_log);
        return page;
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
                    AppendLog($"  Opened in browser (landing page)");
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
            FileName = $"{_scan.Scan.Hostname}-specsheet",
            Title = "Export hardware scan"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        if (dialog.FilterIndex == 2)
        {
            var html = UpgradePlannerService.GenerateReport(_scan);
            File.WriteAllText(dialog.FileName, html, System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        else if (dialog.FileName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            var html = SpecSheetGenerator.Generate(_scan, _report);
            File.WriteAllText(dialog.FileName, html, System.Text.Encoding.UTF8);
            Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        }
        else
        {
            File.Copy(_scanPath, dialog.FileName, overwrite: true);
        }
        _status.Text = $"Exported to {dialog.FileName}.";
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
        SendScanToTopology();
        BindMetrics();
        BindQuickFacts();
        BindDrivers();
        BindStorage();
        BindSuggestions();
        _updatesButton.Enabled = true;
        _downloadButton.Enabled = _report is not null;
        _exportButton.Enabled = true;
        _pimpButton.Enabled = true;
    }

    private void SendScanToTopology()
    {
        if (_scanPath is null) return;
        try
        {
            var json = File.ReadAllText(_scanPath);
            if (_topologyReady && _topologyWebView?.CoreWebView2 is not null)
                _topologyWebView.CoreWebView2.PostWebMessageAsJson(json);
            else
                _pendingScanJson = json;
        }
        catch { }
    }

    private void BindMetrics()
    {
        _metrics.Controls.Clear();
        if (_scan is null) return;
        var usedTb = _scan.Volumes.Sum(v => v.SizeBytes - v.FreeBytes) / 1_099_511_627_776d;
        var updateCount = _report?.Results.Count(r => r.Status == "update-available");
        _metrics.Controls.Add(Metric($"{_scan.TotalMemoryGb:0.#} GB", "MEMORY"));
        _metrics.Controls.Add(Metric($"{_scan.Components.Count}", "COMPONENTS"));
        _metrics.Controls.Add(Metric($"{usedTb:0.0} TB", "DATA USED"));
        _metrics.Controls.Add(Metric(updateCount?.ToString() ?? "—", "UPDATES"));
        _metrics.Controls.Add(MetricLive("—", "TEMP", _tempLabel));
        _metrics.Controls.Add(MetricLive("—", "FANS", _fanLabel));
        _metrics.Controls.Add(MetricLive("—", "NET", _networkLabel));
    }

    private static Control Metric(string value, string label)
    {
        var panel = new Panel
        {
            Width = 122,
            Height = 46,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0, 0, 8, 0),
            Tag = "surface"
        };
        panel.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 1,
            BackColor = AppTheme.Border,
            Tag = "border"
        });
        panel.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = false,
            Location = new Point(1, 26),
            Size = new Size(108, 17),
            TextAlign = ContentAlignment.TopLeft,
            Tag = "muted"
        });
        panel.Controls.Add(new Label
        {
            Text = value,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 11.5f),
            AutoSize = false,
            Location = new Point(0, 2),
            Size = new Size(108, 24),
            TextAlign = ContentAlignment.MiddleLeft,
            Tag = "text"
        });
        return panel;
    }

    private static Control MetricLive(string initialValue, string label, Label valueLabel)
    {
        var panel = new Panel
        {
            Width = 122,
            Height = 46,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0, 0, 8, 0),
            Tag = "surface"
        };
        panel.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 1,
            BackColor = AppTheme.Border,
            Tag = "border"
        });
        panel.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = false,
            Location = new Point(1, 26),
            Size = new Size(108, 17),
            TextAlign = ContentAlignment.TopLeft,
            Tag = "muted"
        });
        valueLabel.Text = initialValue;
        valueLabel.ForeColor = AppTheme.Text;
        valueLabel.Font = new Font("Segoe UI Semibold", 11.5f);
        valueLabel.AutoSize = false;
        valueLabel.Location = new Point(0, 2);
        valueLabel.Size = new Size(108, 24);
        valueLabel.TextAlign = ContentAlignment.MiddleLeft;
        valueLabel.Tag = "text";
        panel.Controls.Add(valueLabel);
        return panel;
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
            var rowIndex = _storage.Rows.Add(volume.Letter, volume.DiskModel ?? volume.Label ?? "Local disk",
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
        File.WriteAllText(path, html, System.Text.Encoding.UTF8);
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        _status.Text = "Pimp My Build report opened in browser.";
    }

    private static void OpenFeedback()
    {
        Process.Start(new ProcessStartInfo("https://github.com/mikedopp/BoardScout/issues") { UseShellExecute = true });
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
        _subtitle.Text = "No cached scan yet — run Scan Hardware or load an existing DriverScout JSON file.";
        _metrics.Controls.Clear();
        _metrics.Controls.Add(Metric("0", "SCANS"));
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
        _log.BackColor = AppTheme.SurfaceRaised;
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
            (_, false) => AppTheme.AccentSoft,
            _ => AppTheme.AccentSoft
        };
        _inspectStatus.ForeColor = foreground;
        _inspectStatus.BackColor = background;
        _inspectCategory.ForeColor = foreground;
    }

    private void SampleTelemetry()
    {
        if (WindowState == FormWindowState.Minimized) return;
        try
        {
            var telemetry = _telemetryService.Sample();
            _boardMap.SetTelemetry(telemetry);

            if (telemetry.Thermals.Count > 0)
            {
                var cpuTemp = telemetry.Thermals.FirstOrDefault(t => t.Zone == "CPU");
                var gpuTemp = telemetry.Thermals.FirstOrDefault(t => t.Zone == "GPU");
                var vrmTemp = telemetry.Thermals.FirstOrDefault(t => t.Zone == "VRM");
                var parts = new List<string>();
                if (cpuTemp is not null) parts.Add($"CPU {cpuTemp.TemperatureCelsius:0}°");
                if (gpuTemp is not null) parts.Add($"GPU {gpuTemp.TemperatureCelsius:0}°");
                if (vrmTemp is not null) parts.Add($"VRM {vrmTemp.TemperatureCelsius:0}°");
                if (parts.Count == 0) parts.Add($"{telemetry.Thermals[0].TemperatureCelsius:0}°C");
                _tempLabel.Text = string.Join(" · ", parts);
                var hottest = telemetry.Thermals.Max(t => t.TemperatureCelsius);
                _tempLabel.ForeColor = hottest >= 90 ? AppTheme.Critical : hottest >= 75 ? AppTheme.Warning : AppTheme.Text;
            }

            var fans = _telemetryService.LastFanReadings;
            if (fans.Count > 0)
            {
                var activeFans = fans.Where(f => f.Rpm > 0).ToList();
                _fanLabel.Text = activeFans.Count > 0
                    ? string.Join(" · ", activeFans.Select(f => $"{f.Name} {f.Rpm}"))
                    : "Fans idle";
                _fanLabel.ForeColor = activeFans.Count == 0 ? AppTheme.Warning : AppTheme.Text;
            }
            else
            {
                _fanLabel.Text = "N/A";
            }

            var netDown = telemetry.NetworkReceivedBytesPerSec;
            _networkLabel.Text = $"{FormatRate(netDown)}↓";
            _networkLabel.ForeColor = netDown > 100_000_000 ? AppTheme.Warning : AppTheme.Text;
        }
        catch (Exception ex)
        {
            _telemetryTimer.Stop();
            AppendLog("TELEMETRY: " + ex.Message);
        }
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

    private void AppendLog(string line)
    {
        _log.AppendText(line + Environment.NewLine);
    }

    private static string FormatBytes(long bytes)
    {
        var value = (double)bytes;
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }
}
