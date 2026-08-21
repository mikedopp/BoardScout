using System.Diagnostics;
using BoardScout.Models;
using BoardScout.Services;

namespace BoardScout.UI;

public sealed class MainForm : Form
{
    private readonly DriverScoutService _service = new();
    private readonly BoardMapControl _boardMap = new() { Dock = DockStyle.Fill };
    private readonly Label _title = new();
    private readonly Label _subtitle = new();
    private readonly FlowLayoutPanel _metrics = new();
    private readonly ListBox _quickFacts = new();
    private readonly DataGridView _drivers = new();
    private readonly DataGridView _storage = new();
    private readonly DataGridView _suggestions = new();
    private readonly TextBox _log = new();
    private readonly Label _status = new();
    private readonly ProgressBar _progress = new();
    private readonly Button _scanButton = new();
    private readonly Button _updatesButton = new();
    private readonly Button _loadButton = new();
    private readonly Button _exportButton = new();
    private readonly Button _cancelButton = new();
    private readonly TabControl _tabs = new();

    private ScanManifest? _scan;
    private DriverReport? _report;
    private string? _scanPath;
    private CancellationTokenSource? _operationCts;

    public MainForm()
    {
        Text = "BoardScout";
        Icon = SystemIcons.Application;
        MinimumSize = new Size(980, 680);
        Size = new Size(1320, 880);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.Background;
        ForeColor = AppTheme.Text;
        Font = new Font("Segoe UI", 9);

        BuildUi();
        _service.OutputReceived += ServiceOnOutputReceived;
        Shown += async (_, _) => await LoadCachedDataAsync();
        FormClosing += (_, _) => _operationCts?.Cancel();
    }

    private void BuildUi()
    {
        var header = BuildHeader();
        Controls.Add(_tabs);
        Controls.Add(_progress);
        Controls.Add(BuildStatusBar());
        Controls.Add(header);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(18, 7);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.ItemSize = new Size(150, 34);
        _tabs.DrawItem += DrawTab;

        _tabs.TabPages.Add(BuildOverviewTab());
        _tabs.TabPages.Add(BuildDriversTab());
        _tabs.TabPages.Add(BuildStorageTab());
        _tabs.TabPages.Add(BuildSuggestionsTab());
        _tabs.TabPages.Add(BuildLogTab());

        _progress.Dock = DockStyle.Bottom;
        _progress.Height = 3;
        _progress.Style = ProgressBarStyle.Marquee;
        _progress.MarqueeAnimationSpeed = 25;
        _progress.Visible = false;
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 150,
            BackColor = AppTheme.Background,
            Padding = new Padding(22, 18, 22, 12)
        };

        var textPanel = new Panel { Dock = DockStyle.Fill };
        _title.Text = "BoardScout";
        _title.Font = new Font("Segoe UI Semibold", 22);
        _title.ForeColor = AppTheme.Text;
        _title.AutoSize = true;
        _title.Location = new Point(0, 2);

        _subtitle.Text = "Portable motherboard, storage, and driver intelligence";
        _subtitle.ForeColor = AppTheme.Muted;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(3, 43);

        _metrics.AutoSize = true;
        _metrics.WrapContents = false;
        _metrics.Location = new Point(0, 75);
        _metrics.BackColor = Color.Transparent;
        textPanel.Controls.Add(_title);
        textPanel.Controls.Add(_subtitle);
        textPanel.Controls.Add(_metrics);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 595,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = true,
            Padding = new Padding(0, 3, 0, 0),
            BackColor = Color.Transparent
        };

        ConfigureButton(_scanButton, "Scan Hardware", async (_, _) => await RunScanAsync(), primary: true);
        ConfigureButton(_updatesButton, "Check Drivers", async (_, _) => await RunDriverCheckAsync());
        ConfigureButton(_loadButton, "Load Scan", async (_, _) => await LoadScanFromFileAsync());
        ConfigureButton(_exportButton, "Export Scan", (_, _) => ExportScan());
        ConfigureButton(_cancelButton, "Cancel", (_, _) => _operationCts?.Cancel());
        var dataButton = new Button();
        ConfigureButton(dataButton, "Open Data", (_, _) => _service.OpenDataFolder());

        _cancelButton.Visible = false;
        _updatesButton.Enabled = false;
        _exportButton.Enabled = false;
        actions.Controls.AddRange([dataButton, _exportButton, _loadButton, _updatesButton, _scanButton, _cancelButton]);

        header.Controls.Add(textPanel);
        header.Controls.Add(actions);
        return header;
    }

    private TabPage BuildOverviewTab()
    {
        var page = NewPage("Overview");
        var split = new SplitContainer
        {
            Orientation = Orientation.Vertical,
            Size = new Size(1200, 600),
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Border,
            Panel1MinSize = 520,
            Panel2MinSize = 250
        };
        split.SplitterDistance = 880;

        split.Panel1.BackColor = AppTheme.Surface;
        split.Panel1.Padding = new Padding(8);
        split.Panel1.Controls.Add(_boardMap);

        var right = new Panel { Dock = DockStyle.Fill, BackColor = AppTheme.Surface, Padding = new Padding(16) };
        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 34,
            Text = "QUICK FACTS",
            Font = new Font("Segoe UI Semibold", 10),
            ForeColor = AppTheme.Muted
        };
        _quickFacts.Dock = DockStyle.Fill;
        _quickFacts.BackColor = AppTheme.Surface;
        _quickFacts.ForeColor = AppTheme.Text;
        _quickFacts.BorderStyle = BorderStyle.None;
        _quickFacts.Font = new Font("Segoe UI", 10);
        _quickFacts.ItemHeight = 28;
        right.Controls.Add(_quickFacts);
        right.Controls.Add(heading);
        split.Panel2.Controls.Add(right);
        page.Controls.Add(split);
        return page;
    }

    private TabPage BuildDriversTab()
    {
        var page = NewPage("Drivers");
        ConfigureGrid(_drivers,
            ("Category", 90), ("Component", 285), ("Installed", 145), ("Date", 100),
            ("Latest", 130), ("Status", 125), ("Source", 110));
        _drivers.CellDoubleClick += (_, e) => OpenDriverLink(e.RowIndex);
        page.Controls.Add(_drivers);
        page.Controls.Add(new Label
        {
            Dock = DockStyle.Top,
            Height = 28,
            Text = "Double-click an update row to open its vendor or OEM page. BoardScout never installs drivers.",
            ForeColor = AppTheme.Muted,
            Padding = new Padding(4, 5, 0, 0)
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
        _log.BackColor = Color.FromArgb(5, 10, 14);
        _log.ForeColor = Color.FromArgb(173, 207, 185);
        _log.BorderStyle = BorderStyle.None;
        _log.Font = new Font("Cascadia Mono", 9);
        page.Controls.Add(_log);
        return page;
    }

    private Control BuildStatusBar()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Bottom,
            Height = 30,
            BackColor = AppTheme.Surface,
            Padding = new Padding(14, 6, 14, 0)
        };
        _status.Dock = DockStyle.Fill;
        _status.Text = "Ready — cached results load instantly; scan only when hardware changes.";
        _status.ForeColor = AppTheme.Muted;
        panel.Controls.Add(_status);
        return panel;
    }

    private static TabPage NewPage(string text) => new(text)
    {
        Name = text,
        BackColor = AppTheme.Background,
        ForeColor = AppTheme.Text,
        Padding = new Padding(12)
    };

    private static void ConfigureButton(Button button, string text, EventHandler handler, bool primary = false)
    {
        button.Text = text;
        AppTheme.StyleButton(button, primary);
        button.Click += handler;
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

    private void DrawTab(object? sender, DrawItemEventArgs e)
    {
        var selected = e.Index == _tabs.SelectedIndex;
        using var brush = new SolidBrush(selected ? AppTheme.SurfaceRaised : AppTheme.Background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, Font, e.Bounds,
            selected ? AppTheme.Accent : AppTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
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
            BindDrivers();
            BindSuggestions();
            var updates = _report.Results.Count(r => r.Status == "update-available");
            _status.Text = $"Driver check complete — {updates} update{(updates == 1 ? "" : "s")} available for review.";
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
        if (_scanPath is null) return;
        using var dialog = new SaveFileDialog
        {
            Filter = "JSON file (*.json)|*.json",
            FileName = Path.GetFileName(_scanPath),
            Title = "Export hardware scan"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        File.Copy(_scanPath, dialog.FileName, overwrite: true);
        _status.Text = $"Exported scan to {dialog.FileName}.";
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
        _title.Text = $"{board.Manufacturer}  {board.Product}".Trim();
        _subtitle.Text = $"{_scan.FormFactor.ToUpperInvariant()}  •  {cpu.Name}  •  {cpu.Cores}C/{cpu.Threads}T  •  {_scan.Scan.Os.Caption}";
        _boardMap.SetSnapshot(_scan);
        BindMetrics();
        BindQuickFacts();
        BindDrivers();
        BindStorage();
        BindSuggestions();
        _updatesButton.Enabled = true;
        _exportButton.Enabled = true;
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
    }

    private static Control Metric(string value, string label)
    {
        var panel = new Panel
        {
            Width = 105,
            Height = 46,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0, 0, 7, 0)
        };
        panel.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 7),
            AutoSize = false,
            TextAlign = ContentAlignment.TopCenter,
            Dock = DockStyle.Bottom,
            Height = 17
        });
        panel.Controls.Add(new Label
        {
            Text = value,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 13),
            AutoSize = false,
            TextAlign = ContentAlignment.BottomCenter,
            Dock = DockStyle.Fill
        });
        return panel;
    }

    private void BindQuickFacts()
    {
        _quickFacts.Items.Clear();
        if (_scan is null) return;
        var memory = _scan.Memory.Slots.FirstOrDefault();
        _quickFacts.Items.Add($"BIOS       {_scan.SystemInfo.Bios.Version}  ({_scan.SystemInfo.Bios.ReleaseDate})");
        _quickFacts.Items.Add($"Memory     {_scan.TotalMemoryGb:0.#} GB in {_scan.Memory.Populated}/{_scan.Memory.TotalSlots} slots");
        if (memory is not null) _quickFacts.Items.Add($"RAM speed  {memory.SpeedMhz} / {memory.RatedMhz} MT/s");
        _quickFacts.Items.Add($"Storage    {_scan.Volumes.Count} mounted volumes");
        _quickFacts.Items.Add($"USB        {_scan.UsbDevices.Count(d => d.DeviceClass != "USB")} attached devices");
        _quickFacts.Items.Add($"Problems   {_scan.ProblemDevices.Count(d => d.ErrorCode != 0)} Device Manager errors");
        _quickFacts.Items.Add($"Last scan  {_scan.Scan.TimestampUtc?.ToLocalTime():g}");
        _quickFacts.Items.Add("");
        _quickFacts.Items.Add("Efficiency model");
        _quickFacts.Items.Add("• Cached startup: instant");
        _quickFacts.Items.Add("• Inventory scan: local only");
        _quickFacts.Items.Add("• Driver check: on demand");
        _quickFacts.Items.Add("• Updates: review only");
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
            ColorDriverRow(row, status, component.Current);
        }

        if (_report is null) return;
        foreach (var result in _report.Results.Where(r => !_scan.Components.Any(c =>
                     c.ComponentKey.Equals(r.ComponentKey, StringComparison.OrdinalIgnoreCase))))
        {
            var rowIndex = _drivers.Rows.Add(result.Category, result.Model, result.Best.InstalledVersion ?? "—", "—",
                result.Best.LatestVersion ?? result.Best.LatestDate ?? "—", result.Status, result.Best.Source);
            _drivers.Rows[rowIndex].Tag = result;
            ColorDriverRow(_drivers.Rows[rowIndex], result.Status, new CurrentVersion());
        }
        BindMetrics();
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
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out _)) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private void SetBusy(bool busy, string message)
    {
        _scanButton.Enabled = !busy;
        _updatesButton.Enabled = !busy && _scan is not null;
        _loadButton.Enabled = !busy;
        _exportButton.Enabled = !busy && _scan is not null;
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
        _quickFacts.Items.Clear();
        _quickFacts.Items.Add("Portable and install-free");
        _quickFacts.Items.Add("Uses built-in Windows PowerShell");
        _quickFacts.Items.Add("Never installs drivers automatically");
        _boardMap.SetSnapshot(null);
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
