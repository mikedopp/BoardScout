using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Reflection;
using BoardScout.Models;

namespace BoardScout.UI;

internal sealed record VersionPopoutActions(
    Func<SensorStatus> Sensors,
    Func<IReadOnlyList<ThermalReading>> Thermals,
    string DataFolder,
    Action OpenDataFolder,
    Action CopyDiagnostics,
    Action RestartElevated,
    Action OpenNotices,
    Action ReportIssue);

/// <summary>
/// Everything that is not the dashboard itself — runtime, sensor troubleshooting, settings,
/// dependencies, requirements, legal — lives under the version button.
/// </summary>
internal sealed class VersionPopout : Panel, IMessageFilter
{
    public const string PawnIoUrl = "https://pawnio.eu/";
    private const int PopoutWidth = 410;
    private readonly Control _anchor;
    private readonly FlowLayoutPanel _flow;
    private readonly string _dataFolder;
    private Label? _dataFolderLabel;
    private GlassToggle? _privacyToggle;
    private bool _closed;

    public VersionPopout(Control anchor, VersionPopoutActions actions)
    {
        _anchor = anchor;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Surface;
        Width = PopoutWidth;
        Padding = new Padding(1);
        AccessibleName = "BoardScout version details";

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            Padding = new Padding(18, 14, 14, 16),
            BackColor = AppTheme.Surface
        };
        Controls.Add(_flow);
        AppTheme.UseDarkScrollbars(_flow);
        _dataFolder = actions.DataFolder;
        Build(actions);
        AppSettings.Changed += OnSettingsChanged;
    }

    private string DataFolderText() =>
        $"Data folder: {CompactPath(Services.Privacy.Enabled ? Services.Privacy.Scrub(_dataFolder) : _dataFolder)}";

    // Ctrl+Shift+P can flip privacy mode while the pop-out is open.
    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_closed) return;
        if (_dataFolderLabel is not null) _dataFolderLabel.Text = DataFolderText();
        _privacyToggle?.SetCheckedSilently(AppSettings.Current.PrivacyMode);
    }

    public event EventHandler? Closed;

    public static IReadOnlyList<(string Name, string Version, string License)> Dependencies { get; } =
    [
        ("LibreHardwareMonitorLib", PackageVersion("LibreHardwareMonitorLib") ?? "0.9.6", "MPL-2.0"),
        ("Microsoft.Web.WebView2", PackageVersion("Microsoft.Web.WebView2.Core") ?? "1.0.4191.47", "BSD-3-Clause"),
        ("System.Management", PackageVersion("System.Management") ?? "10.0.12", "MIT"),
        ("QuickLiquid", "0.1.2", "MIT"),
        ("D3.js", "7.9.0", "ISC")
    ];

    private void Build(VersionPopoutActions actions)
    {
        var sensors = actions.Sensors();
        var thermals = actions.Thermals();

        Add(Heading($"BoardScout v{VersionButton.AppVersion}", AppTheme.Accent, 12f));
        Add(Detail("Portable motherboard, storage, and driver intelligence."));

        Add(Section("Runtime"));
        Add(Detail($".NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · self-contained"));
        Add(Detail(Services.SystemInfoService.WindowsDescription()));
        var webView = WebViewHost.RuntimeVersion;
        Add(Detail(webView is null ? "WebView2 Runtime: not found — Topology, Connections, and System need it" : $"WebView2 Runtime {webView}",
            webView is null ? AppTheme.Warning : null));

        Add(Section("Sensors"));
        var temperatureText = thermals.Count == 0
            ? "No temperature sensors readable"
            : $"Temperatures: {string.Join(", ", thermals.Select(t => t.Zone))}";
        Add(Detail(temperatureText, thermals.Count == 0 ? AppTheme.Warning : AppTheme.Good));
        Add(Detail(sensors.Fans == 0 ? "No fan speeds readable" : $"Fans: {sensors.Fans} reporting",
            sensors.Fans == 0 ? AppTheme.Warning : AppTheme.Good));
        if (!sensors.MotherboardSensorsAvailable)
        {
            Add(Detail("CPU, VRM, and motherboard fan sensors need the PawnIO driver and administrator rights. " +
                       "GPU sensors work without them.", width: PopoutWidth - 44));
            Add(Detail(sensors.PawnIoInstalled
                    ? $"PawnIO driver: installed{(sensors.PawnIoVersion is null ? "" : $" ({sensors.PawnIoVersion})")}"
                    : "PawnIO driver: not installed",
                sensors.PawnIoInstalled ? AppTheme.Good : AppTheme.Warning));
            if (!sensors.PawnIoInstalled)
                Add(Link("Get PawnIO from pawnio.eu (namazso)  ↗", () => OpenUrl(PawnIoUrl)));
            Add(Detail(sensors.Elevated ? "Administrator: yes" : "Administrator: no",
                sensors.Elevated ? AppTheme.Good : AppTheme.Warning));
            if (!sensors.Elevated)
                Add(Link("Restart BoardScout as administrator", actions.RestartElevated));
        }
        if (sensors.Error is not null)
            Add(Detail($"Sensor library error: {sensors.Error}", AppTheme.Critical, PopoutWidth - 44));

        Add(Section("Troubleshooting"));
        _dataFolderLabel = Detail(DataFolderText(), width: PopoutWidth - 44);
        Add(_dataFolderLabel);
        Add(Link("Open data folder", actions.OpenDataFolder));
        Add(Link("Copy diagnostics to clipboard", actions.CopyDiagnostics));
        Add(Link("Report an issue on GitHub  ↗", actions.ReportIssue));

        Add(Section("Settings"));
        var settings = AppSettings.Current;
        _privacyToggle = new GlassToggle("Privacy mode", settings.PrivacyMode,
            on => AppSettings.Update(s => s.PrivacyMode = on))
        {
            AccessibleDescription = "Hides your PC name, owner details, serial numbers, paths, and app lists in screens and exports"
        };
        Add(_privacyToggle);
        Add(Detail("Hides your PC name, Windows owner email, product ID, serial numbers, paths, and app lists " +
                   "in screens and exports, for screenshots and sharing. Ctrl+Shift+P toggles it.",
            width: PopoutWidth - 44));
        Add(new GlassToggle("Glass effects", settings.GlassEffects,
            on => AppSettings.Update(s => s.GlassEffects = on)) { AccessibleDescription = "Liquid glass surfaces and QuickLiquid refraction" });
        Add(new GlassToggle("Motion", settings.Motion,
            on => AppSettings.Update(s => s.Motion = on)) { AccessibleDescription = "Gliding selection, hover fades, animated zoom and topology flow" });
        Add(new GlassToggle("Minimize to tray", settings.MinimizeToTray,
            on => AppSettings.Update(s => s.MinimizeToTray = on)));
        Add(Detail("Live telemetry refresh", AppTheme.Muted));
        int[] intervals = [500, 1000, 2000, 5000];
        var selected = Array.IndexOf(intervals, settings.TelemetryIntervalMs);
        Add(new GlassSegmented(["0.5 s", "1 s", "2 s", "5 s"], selected < 0 ? 1 : selected,
            index => AppSettings.Update(s => s.TelemetryIntervalMs = intervals[index])) { AccessibleName = "Live telemetry refresh" });

        Add(Section("Dependencies"));
        foreach (var (name, version, license) in Dependencies)
            Add(Detail($"{name} {version} ({license})"));

        Add(Section("Requirements"));
        Add(Detail("Windows 10 or 11, x64 or ARM64"));
        Add(Detail("WebView2 Runtime (Evergreen) for Topology, Connections, and System"));
        Add(Detail("Administrator + PawnIO for CPU, VRM, and fan sensors"));

        Add(Section("Legal"));
        Add(Detail("MIT License · trademarks belong to their owners"));
        Add(Link("Third-party notices", actions.OpenNotices));
    }

    /// <summary>Opens under the anchor, sized to its content and clamped to the window.</summary>
    public void ShowUnder(Form form)
    {
        form.Controls.Add(this);
        var anchorScreen = _anchor.PointToScreen(new Point(0, _anchor.Height + 6));
        var location = form.PointToClient(anchorScreen);
        location.X = Math.Clamp(location.X, 8, Math.Max(8, form.ClientSize.Width - Width - 8));
        _flow.PerformLayout();
        var content = _flow.Controls.Cast<Control>().Select(c => c.Bottom + c.Margin.Bottom).DefaultIfEmpty(0).Max() +
                      _flow.Padding.Bottom + _flow.AutoScrollPosition.Y * -1;
        Height = Math.Min(content + Padding.Vertical + 4, form.ClientSize.Height - location.Y - 12);
        Location = location;
        BringToFront();
        Application.AddMessageFilter(this);
        form.Deactivate += OnFormDeactivate;
        _flow.Focus();
    }

    public void Close()
    {
        if (_closed) return;
        _closed = true;
        AppSettings.Changed -= OnSettingsChanged;
        Application.RemoveMessageFilter(this);
        if (FindForm() is { } form) form.Deactivate -= OnFormDeactivate;
        Parent?.Controls.Remove(this);
        Closed?.Invoke(this, EventArgs.Empty);
        Dispose();
    }

    private void OnFormDeactivate(object? sender, EventArgs e) => BeginInvoke(Close);

    // Clicking anywhere else in the window, or pressing Escape, closes the pop-out.
    public bool PreFilterMessage(ref Message m)
    {
        const int WmKeyDown = 0x0100, WmLButtonDown = 0x0201, WmRButtonDown = 0x0204,
            WmMButtonDown = 0x0207, WmNcLButtonDown = 0x00A1;
        if (_closed) return false;
        if (m.Msg == WmKeyDown && (Keys)(int)m.WParam == Keys.Escape)
        {
            Close();
            return true;
        }
        if (m.Msg is WmLButtonDown or WmRButtonDown or WmMButtonDown or WmNcLButtonDown)
        {
            var target = FromChildHandle(m.HWnd);
            if (target is not null && (target == this || Contains(target) || target == _anchor)) return false;
            BeginInvoke(Close);
        }
        return false;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(AppTheme.Surface);
        var band = new RectangleF(0, 0, Width, 64);
        using (var sheen = new LinearGradientBrush(RectangleF.Inflate(band, 1, 1),
                   Glass.Mix(AppTheme.Surface, AppTheme.Accent, Glass.Enabled ? 0.18f : 0.06f), AppTheme.Surface,
                   LinearGradientMode.Vertical))
            g.FillRectangle(sheen, band);
        using var border = new Pen(Color.FromArgb(150, AppTheme.Accent), 1f);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private void Add(Control control) => _flow.Controls.Add(control);

    private static Label Heading(string text, Color color, float size) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI Semibold", size),
        Margin = new Padding(0, 0, 0, 2)
    };

    private static Label Section(string text) => new()
    {
        Text = text.ToUpperInvariant(),
        AutoSize = true,
        ForeColor = AppTheme.Accent,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI Semibold", 8f),
        Margin = new Padding(0, 14, 0, 4)
    };

    private static Label Detail(string text, Color? color = null, int width = 0)
    {
        var label = new Label
        {
            Text = text,
            ForeColor = color ?? AppTheme.Muted,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI", 8.75f),
            Margin = new Padding(0, 1, 0, 1)
        };
        if (width > 0)
        {
            label.AutoSize = false;
            label.Width = width;
            label.Height = TextRenderer.MeasureText(text, label.Font, new Size(width, 0), TextFormatFlags.WordBreak).Height + 2;
        }
        else
        {
            label.AutoSize = true;
        }
        return label;
    }

    private static LinkLabel Link(string text, Action action)
    {
        var link = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = AppTheme.Accent,
            ActiveLinkColor = AppTheme.Good,
            VisitedLinkColor = AppTheme.Accent,
            BackColor = Color.Transparent,
            Font = new Font("Segoe UI Semibold", 8.75f),
            Margin = new Padding(0, 3, 0, 3),
            LinkBehavior = LinkBehavior.HoverUnderline
        };
        link.LinkClicked += (_, _) => action();
        return link;
    }

    // A path has no spaces to wrap at; keep the drive and the last two folders.
    private static string CompactPath(string path)
    {
        if (path.Length <= 52) return path;
        var parts = path.TrimEnd('\\').Split('\\');
        return parts.Length <= 3 ? path : $"{parts[0]}\\…\\{parts[^2]}\\{parts[^1]}";
    }

    private static void OpenUrl(string url) =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });

    private static string? PackageVersion(string assemblyName)
    {
        try
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName) ?? Assembly.Load(assemblyName);
            var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational)) return assembly.GetName().Version?.ToString(3);
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
        catch
        {
            return null;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_closed)
        {
            _closed = true;
            AppSettings.Changed -= OnSettingsChanged;
            Application.RemoveMessageFilter(this);
        }
        base.Dispose(disposing);
    }
}

/// <summary>A small liquid switch: the knob glides between off and on.</summary>
internal sealed class GlassToggle : Control
{
    private readonly Action<bool> _changed;
    private readonly Func<float, bool> _animate;
    private bool _checked;
    private float _position;

    public GlassToggle(string text, bool isChecked, Action<bool> changed)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Text = text;
        _checked = isChecked;
        _position = isChecked ? 1 : 0;
        _changed = changed;
        _animate = Animate;
        Size = new Size(PopoutContentWidth, 28);
        Margin = new Padding(0, 2, 0, 2);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleName = text;
    }

    private const int PopoutContentWidth = 360;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
            if (Motion.Enabled) Motion.Start(_animate);
            else { _position = value ? 1 : 0; Invalidate(); }
            _changed(value);
        }
    }

    /// <summary>Shows a state changed elsewhere without reporting it back as a new change.</summary>
    public void SetCheckedSilently(bool value)
    {
        if (_checked == value) return;
        _checked = value;
        AccessibilityNotifyClients(AccessibleEvents.StateChange, -1);
        if (Motion.Enabled) Motion.Start(_animate);
        else { _position = value ? 1 : 0; Invalidate(); }
    }

    protected override AccessibleObject CreateAccessibilityInstance() => new ToggleAccessible(this);

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Focus();
        Checked = !Checked;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { Checked = !Checked; e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    private bool Animate(float seconds)
    {
        if (IsDisposed) return false;
        var target = _checked ? 1f : 0f;
        _position = Motion.Approach(_position, target, 18f, seconds);
        if (Math.Abs(_position - target) < 0.01f) _position = target;
        Invalidate();
        return _position != target;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, (Height - 18) / 2f, 36, 18);
        var trackColor = Glass.Mix(AppTheme.Border, AppTheme.Accent, _position);
        Glass.Surface(g, track, 9, trackColor, tintStrength: 0.45f + 0.35f * _position, sheen: 0.9f, baseColor: AppTheme.SurfaceRaised);
        var knobX = track.X + 2 + (track.Width - 18) * _position;
        using (var knob = new SolidBrush(Glass.Mix(AppTheme.Muted, Color.White, _position)))
            g.FillEllipse(knob, knobX, track.Y + 2, 14, 14);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(46, 0, Width - 46, Height), AppTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(150, AppTheme.Text), 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }

    private sealed class ToggleAccessible(GlassToggle owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleStates State =>
            base.State | (owner.Checked ? AccessibleStates.Checked : AccessibleStates.None);

        public override string DefaultAction => owner.Checked ? "Turn off" : "Turn on";

        public override void DoDefaultAction() => owner.Checked = !owner.Checked;
    }
}

/// <summary>Segmented choice whose selection pill glides between options.</summary>
internal sealed class GlassSegmented : Control
{
    private readonly string[] _options;
    private readonly Action<int> _changed;
    private readonly Func<float, bool> _animate;
    private int _selected;
    private float _pillX = float.NaN;

    public GlassSegmented(string[] options, int selected, Action<int> changed)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        _options = options;
        _selected = Math.Clamp(selected, 0, options.Length - 1);
        _changed = changed;
        _animate = Animate;
        Size = new Size(64 * options.Length + 8, 32);
        Margin = new Padding(0, 2, 0, 4);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.List;
    }

    private RectangleF SegmentRect(int index) => new(4 + index * 64, 4, 64, Height - 8);

    private void Select(int index)
    {
        index = Math.Clamp(index, 0, _options.Length - 1);
        if (index == _selected) return;
        _selected = index;
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        if (Motion.Enabled) Motion.Start(_animate);
        else { _pillX = SegmentRect(index).X; Invalidate(); }
        _changed(index);
    }

    private bool Animate(float seconds)
    {
        if (IsDisposed) return false;
        var target = SegmentRect(_selected).X;
        _pillX = Motion.Approach(_pillX, target, 18f, seconds);
        if (Math.Abs(_pillX - target) < 0.3f) _pillX = target;
        Invalidate();
        return _pillX != target;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        for (var i = 0; i < _options.Length; i++)
            if (SegmentRect(i).Contains(e.Location)) Select(i);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Up) { Select(_selected - 1); e.Handled = true; }
        else if (e.KeyCode is Keys.Right or Keys.Down) { Select(_selected + 1); e.Handled = true; }
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (float.IsNaN(_pillX)) _pillX = SegmentRect(_selected).X;
        Glass.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), (Height - 1) / 2f, AppTheme.Border,
            tintStrength: 0.2f, sheen: 0.6f, baseColor: AppTheme.SurfaceRaised);
        var pill = new RectangleF(_pillX, 4, 64, Height - 8);
        Glass.Surface(g, pill, pill.Height / 2, AppTheme.Accent, tintStrength: 0.5f, sheen: 1.2f);
        for (var i = 0; i < _options.Length; i++)
        {
            var color = i == _selected ? Color.White : AppTheme.Muted;
            TextRenderer.DrawText(g, _options[i], Font, Rectangle.Round(SegmentRect(i)), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(150, AppTheme.Text), 1f) { DashStyle = DashStyle.Dot };
            g.DrawRectangle(focus, 0, 0, Width - 1, Height - 1);
        }
    }
}
