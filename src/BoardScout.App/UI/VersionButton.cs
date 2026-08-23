using System.Drawing.Drawing2D;
using System.Reflection;

namespace BoardScout.UI;

internal sealed class VersionButton : Control
{
    private static readonly Color[] ChasingColors =
    [
        Color.FromArgb(66, 133, 244),   // Google blue
        Color.FromArgb(219, 68, 55),    // Google red
        Color.FromArgb(244, 180, 0),    // Google yellow
        Color.FromArgb(15, 157, 88)     // Google green
    ];

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 50 };
    private int _colorIndex;
    private Panel? _popout;
    private bool _popoutVisible;

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public VersionButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(100, 28);
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI Semibold", 8.5f);
        _timer.Tick += (_, _) => { _colorIndex = (_colorIndex + 1) % (ChasingColors.Length * 30); Invalidate(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var baseIdx = _colorIndex / 30;
        var t = (_colorIndex % 30) / 30f;

        var topColor = Lerp(ChasingColors[baseIdx % 4], ChasingColors[(baseIdx + 1) % 4], t);
        var rightColor = Lerp(ChasingColors[(baseIdx + 1) % 4], ChasingColors[(baseIdx + 2) % 4], t);
        var bottomColor = Lerp(ChasingColors[(baseIdx + 2) % 4], ChasingColors[(baseIdx + 3) % 4], t);
        var leftColor = Lerp(ChasingColors[(baseIdx + 3) % 4], ChasingColors[(baseIdx + 4) % 4], t);

        var inner = new Rectangle(3, 3, Width - 7, Height - 7);
        var innerPath = RoundedRect(inner, 11);
        using var fillBrush = new SolidBrush(AppTheme.Surface);
        g.FillPath(fillBrush, innerPath);

        var r = 14;
        using var topPen = new Pen(topColor, 2.5f);
        using var rightPen = new Pen(rightColor, 2.5f);
        using var bottomPen = new Pen(bottomColor, 2.5f);
        using var leftPen = new Pen(leftColor, 2.5f);

        g.DrawArc(topPen, 0, 0, r * 2, r * 2, 180, 45);
        g.DrawArc(leftPen, 0, 0, r * 2, r * 2, 225, 45);
        g.DrawLine(topPen, r, 0, Width - 1 - r, 0);
        g.DrawArc(topPen, Width - 1 - r * 2, 0, r * 2, r * 2, 270, 45);
        g.DrawArc(rightPen, Width - 1 - r * 2, 0, r * 2, r * 2, 315, 45);
        g.DrawLine(rightPen, Width - 1, r, Width - 1, Height - 1 - r);
        g.DrawArc(rightPen, Width - 1 - r * 2, Height - 1 - r * 2, r * 2, r * 2, 0, 45);
        g.DrawArc(bottomPen, Width - 1 - r * 2, Height - 1 - r * 2, r * 2, r * 2, 45, 45);
        g.DrawLine(bottomPen, Width - 1 - r, Height - 1, r, Height - 1);
        g.DrawArc(bottomPen, 0, Height - 1 - r * 2, r * 2, r * 2, 90, 45);
        g.DrawArc(leftPen, 0, Height - 1 - r * 2, r * 2, r * 2, 135, 45);
        g.DrawLine(leftPen, 0, Height - 1 - r, 0, r);

        var text = $"v{AppVersion}";
        var textColor = Lerp(ChasingColors[baseIdx % 4], ChasingColors[(baseIdx + 1) % 4], t);
        using var textBrush = new SolidBrush(textColor);
        var textSize = g.MeasureString(text, Font);
        var x = (Width - textSize.Width) / 2;
        var y = (Height - textSize.Height) / 2;
        g.DrawString(text, Font, textBrush, x, y);
    }

    private static Color Lerp(Color a, Color b, float t) =>
        Color.FromArgb(
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        TogglePopout();
    }

    private void TogglePopout()
    {
        if (_popoutVisible && _popout is not null)
        {
            _popout.Visible = false;
            _popoutVisible = false;
            FindForm()?.Controls.Remove(_popout);
            _popout.Dispose();
            _popout = null;
            return;
        }

        var form = FindForm();
        if (form is null) return;

        _popout = new Panel
        {
            Size = new Size(380, 260),
            BackColor = AppTheme.Surface,
            BorderStyle = BorderStyle.FixedSingle,
            Tag = "surface"
        };

        var screenPt = PointToScreen(new Point(0, Height + 4));
        var formPt = form.PointToClient(screenPt);
        if (formPt.X + 380 > form.ClientSize.Width)
            formPt.X = form.ClientSize.Width - 390;
        _popout.Location = formPt;

        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            Padding = new Padding(16, 12, 16, 12),
            AutoScroll = true
        };

        AddSection(content, $"BoardScout v{AppVersion}", AppTheme.Accent, true);
        AddSection(content, "Runtime", AppTheme.Muted);
        AddDetail(content, $".NET {Environment.Version}  ·  {(Environment.Is64BitProcess ? "64-bit" : "32-bit")}");
        AddDetail(content, $"OS: {Environment.OSVersion}");

        AddSection(content, "Dependencies", AppTheme.Muted);
        AddDetail(content, "LibreHardwareMonitorLib 0.9.6 (MIT)");
        AddDetail(content, "Microsoft.Web.WebView2 1.0.2903.40");
        AddDetail(content, "System.Management 10.0.2");
        AddDetail(content, "D3.js v7 (BSD-3)");

        AddSection(content, "Requirements", AppTheme.Muted);
        AddDetail(content, "WebView2 Runtime (Win 10 21H2+)");
        AddDetail(content, "Admin elevation for sensor access");

        _popout.Controls.Add(content);
        form.Controls.Add(_popout);
        _popout.BringToFront();
        _popout.Visible = true;
        _popoutVisible = true;

        form.Click += ClosePopout;
        form.Deactivate += ClosePopout;
    }

    private void ClosePopout(object? sender, EventArgs e)
    {
        if (!_popoutVisible) return;
        var form = FindForm();
        if (form is not null)
        {
            form.Click -= ClosePopout;
            form.Deactivate -= ClosePopout;
        }
        _popout?.Dispose();
        _popout = null;
        _popoutVisible = false;
    }

    private static void AddSection(TableLayoutPanel panel, string text, Color color, bool title = false)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = color,
            Font = new Font("Segoe UI Semibold", title ? 11f : 8f),
            Padding = new Padding(0, title ? 0 : 8, 0, 2)
        };
        if (!title)
            label.Text = text.ToUpperInvariant();
        panel.Controls.Add(label);
    }

    private static void AddDetail(TableLayoutPanel panel, string text)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 8.75f),
            Padding = new Padding(0, 1, 0, 1)
        };
        panel.Controls.Add(label);
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
            _popout?.Dispose();
        }
        base.Dispose(disposing);
    }
}
