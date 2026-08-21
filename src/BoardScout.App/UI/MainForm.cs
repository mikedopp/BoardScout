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
    private readonly FlowLayoutPanel _quickFacts = new();
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
        Icon = AppTheme.CreateAppIcon();
        MinimumSize = new Size(980, 680);
        Size = new Size(1320, 880);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = AppTheme.Background;
        ForeColor = AppTheme.Text;
        Font = new Font("Segoe UI", 9.25f);

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
        _tabs.Padding = new Point(16, 8);
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.ItemSize = new Size(136, 38);
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
            Height = 124,
            BackColor = AppTheme.Surface,
            Padding = new Padding(24, 16, 24, 10)
        };

        var textPanel = new Panel { Dock = DockStyle.Fill };
        _title.Text = "BoardScout";
        _title.Font = new Font("Segoe UI Semibold", 21);
        _title.ForeColor = AppTheme.Text;
        _title.AutoSize = true;
        _title.Location = new Point(0, 0);

        _subtitle.Text = "Portable motherboard, storage, and driver intelligence";
        _subtitle.ForeColor = AppTheme.Muted;
        _subtitle.AutoSize = true;
        _subtitle.Location = new Point(2, 38);

        _metrics.AutoSize = false;
        _metrics.Size = new Size(500, 40);
        _metrics.WrapContents = false;
        _metrics.Location = new Point(0, 66);
        _metrics.BackColor = Color.Transparent;
        textPanel.Controls.Add(_title);
        textPanel.Controls.Add(_subtitle);
        textPanel.Controls.Add(_metrics);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Right,
            Width = 610,
            FlowDirection = FlowDirection.RightToLeft,
            WrapContents = false,
            Padding = new Padding(0, 4, 0, 0),
            BackColor = Color.Transparent
        };

        ConfigureButton(_scanButton, "Scan now", async (_, _) => await RunScanAsync(), primary: true);
        ConfigureButton(_updatesButton, "Check drivers", async (_, _) => await RunDriverCheckAsync());
        ConfigureButton(_loadButton, "Import", async (_, _) => await LoadScanFromFileAsync());
        ConfigureButton(_exportButton, "Export", (_, _) => ExportScan());
        ConfigureButton(_cancelButton, "Cancel", (_, _) => _operationCts?.Cancel());
        var dataButton = new Button();
        ConfigureButton(dataButton, "Data folder", (_, _) => _service.OpenDataFolder());

        _cancelButton.Visible = false;
        _updatesButton.Enabled = false;
        _exportButton.Enabled = false;
        actions.Controls.AddRange([dataButton, _exportButton, _loadButton, _updatesButton, _scanButton, _cancelButton]);

        header.Controls.Add(textPanel);
        header.Controls.Add(actions);
        header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = AppTheme.Border });
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
            BackColor = AppTheme.Background,
            SplitterWidth = 12,
            Panel1MinSize = 520,
            Panel2MinSize = 250
        };
        split.SplitterDistance = 880;

        split.Panel1.BackColor = AppTheme.Background;
        var boardCard = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(4)
        };
        boardCard.Controls.Add(_boardMap);
        split.Panel1.Controls.Add(boardCard);

        var right = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = AppTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Padding = new Padding(18, 16, 18, 16)
        };
        var heading = new Label
        {
            Dock = DockStyle.Top,
            Height = 38,
            Text = "System details",
            Font = new Font("Segoe UI Semibold", 12),
            ForeColor = AppTheme.Text
        };
        _quickFacts.Dock = DockStyle.Fill;
        _quickFacts.BackColor = AppTheme.Surface;
        _quickFacts.FlowDirection = FlowDirection.TopDown;
        _quickFacts.WrapContents = false;
        _quickFacts.AutoScroll = true;
        _quickFacts.Padding = new Padding(0, 2, 0, 0);
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
        _log.BackColor = Color.FromArgb(250, 251, 252);
        _log.ForeColor = Color.FromArgb(45, 63, 74);
        _log.BorderStyle = BorderStyle.FixedSingle;
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
            BorderStyle = BorderStyle.FixedSingle,
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
        using var brush = new SolidBrush(selected ? AppTheme.Surface : AppTheme.Background);
        e.Graphics.FillRectangle(brush, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, Font, e.Bounds,
            selected ? AppTheme.Text : AppTheme.Muted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (selected)
        {
            using var underline = new SolidBrush(AppTheme.Accent);
            e.Graphics.FillRectangle(underline, e.Bounds.Left + 18, e.Bounds.Bottom - 3, e.Bounds.Width - 36, 3);
        }
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
        _title.Text = $"{board.Manufacturer} {board.Product}".Trim();
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
            Width = 114,
            Height = 38,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0, 0, 8, 0)
        };
        panel.Controls.Add(new Panel
        {
            Dock = DockStyle.Right,
            Width = 1,
            BackColor = AppTheme.Border
        });
        panel.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 7.5f),
            AutoSize = false,
            Location = new Point(1, 20),
            Size = new Size(96, 14),
            TextAlign = ContentAlignment.MiddleLeft
        });
        panel.Controls.Add(new Label
        {
            Text = value,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 10.5f),
            AutoSize = false,
            Location = new Point(0, 2),
            Size = new Size(98, 20),
            TextAlign = ContentAlignment.MiddleLeft
        });
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
        AddFactSection("How BoardScout works");
        AddFact("Startup", "Uses the latest cached inventory");
        AddFact("Hardware scan", "Local and on demand");
        AddFact("Driver check", "Review only — nothing is installed");
    }

    private void AddFact(string label, string value, Color? valueColor = null)
    {
        var width = Math.Max(220, _quickFacts.ClientSize.Width - 4);
        var row = new Panel
        {
            Width = width,
            Height = 43,
            BackColor = AppTheme.Surface,
            Margin = new Padding(0)
        };
        row.Controls.Add(new Label
        {
            Text = label,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 8),
            Location = new Point(0, 2),
            Size = new Size(width, 16)
        });
        row.Controls.Add(new Label
        {
            Text = value,
            ForeColor = valueColor ?? AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 9.5f),
            Location = new Point(0, 18),
            Size = new Size(width, 21),
            AutoEllipsis = true
        });
        _quickFacts.Controls.Add(row);
    }

    private void AddFactSection(string text)
    {
        var width = Math.Max(220, _quickFacts.ClientSize.Width - 4);
        _quickFacts.Controls.Add(new Label
        {
            Text = text,
            ForeColor = AppTheme.Text,
            Font = new Font("Segoe UI Semibold", 10),
            Width = width,
            Height = 36,
            Padding = new Padding(0, 12, 0, 0),
            Margin = new Padding(0, 6, 0, 0)
        });
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
        _quickFacts.Controls.Clear();
        AddFact("Portable", "Runs without an installer");
        AddFact("Inventory", "Uses built-in Windows tools");
        AddFact("Safe by design", "Never installs drivers automatically", AppTheme.Good);
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
