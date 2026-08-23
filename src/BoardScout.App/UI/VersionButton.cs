using System.Drawing.Drawing2D;
using System.Reflection;

namespace BoardScout.UI;

internal sealed class VersionButton : Control
{
    private static readonly Color[] Chase =
    [
        Color.FromArgb(0x42, 0x85, 0xF4), // blue
        Color.FromArgb(0xEA, 0x43, 0x35), // red
        Color.FromArgb(0xFB, 0xBC, 0x05), // yellow
        Color.FromArgb(0x34, 0xA8, 0x53), // green
        Color.FromArgb(0x42, 0x85, 0xF4)  // back to blue for seamless loop
    ];

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private float _angle;
    private Panel? _popout;
    private bool _popoutVisible;

    public static string AppVersion { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public VersionButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Size = new Size(100, 30);
        Cursor = Cursors.Hand;
        Font = new Font("Cascadia Mono, Consolas", 9f);
        _timer.Tick += (_, _) => { _angle = (_angle + 1.5f) % 360f; Invalidate(); };
        _timer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var outer = new Rectangle(0, 0, Width - 1, Height - 1);
        var outerPath = RoundedRect(outer, 14);

        var cx = Width / 2f;
        var cy = Height / 2f;
        var radius = Math.Max(Width, Height);
        var rad = _angle * MathF.PI / 180f;
        var dx = MathF.Cos(rad) * radius;
        var dy = MathF.Sin(rad) * radius;

        using var gradBrush = new LinearGradientBrush(
            new PointF(cx - dx, cy - dy),
            new PointF(cx + dx, cy + dy),
            Chase[0], Chase[^1]);

        var blend = new ColorBlend(Chase.Length);
        for (var i = 0; i < Chase.Length; i++)
        {
            blend.Colors[i] = Chase[i];
            blend.Positions[i] = i / (float)(Chase.Length - 1);
        }
        gradBrush.InterpolationColors = blend;

        using var pen = new Pen(gradBrush, 2.5f);
        g.DrawPath(pen, outerPath);

        var inner = new Rectangle(3, 3, Width - 7, Height - 7);
        var innerPath = RoundedRect(inner, 11);
        using var fill = new SolidBrush(AppTheme.Surface);
        g.FillPath(fill, innerPath);

        var text = $"v{AppVersion}";
        var textSize = g.MeasureString(text, Font);
        var x = (Width - textSize.Width) / 2;
        var y = (Height - textSize.Height) / 2;
        using var textBrush = new SolidBrush(Color.FromArgb(168, 205, 231));
        g.DrawString(text, Font, textBrush, x, y);
    }

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
            Size = new Size(380, 280),
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
        AddDetail(content, "LibreHardwareMonitorLib 0.9.6 (MPL-2.0)");
        AddDetail(content, "Microsoft.Web.WebView2 1.0.2903.40");
        AddDetail(content, "System.Management 10.0.2 (MIT)");
        AddDetail(content, "D3.js v7 (ISC)");

        AddSection(content, "Requirements", AppTheme.Muted);
        AddDetail(content, "WebView2 Runtime (Win 10 21H2+)");
        AddDetail(content, "Admin elevation for full sensor access");

        AddSection(content, "Legal", AppTheme.Muted);
        AddDetail(content, "MIT License · See THIRD-PARTY-NOTICES.md");
        AddDetail(content, "All trademarks belong to their respective owners");

        var feedbackLink = new LinkLabel
        {
            Text = "Report an issue on GitHub",
            AutoSize = true,
            LinkColor = AppTheme.Accent,
            ActiveLinkColor = AppTheme.Good,
            VisitedLinkColor = AppTheme.Accent,
            Font = new Font("Segoe UI Semibold", 8.75f),
            Padding = new Padding(0, 10, 0, 2)
        };
        feedbackLink.LinkClicked += (_, _) =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "https://github.com/mikedopp/BoardScout/issues") { UseShellExecute = true });
        content.Controls.Add(feedbackLink);

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
        panel.Controls.Add(new Label
        {
            Text = title ? text : text.ToUpperInvariant(),
            AutoSize = true,
            ForeColor = color,
            Font = new Font("Segoe UI Semibold", title ? 11f : 8f),
            Padding = new Padding(0, title ? 0 : 8, 0, 2)
        });
    }

    private static void AddDetail(TableLayoutPanel panel, string text)
    {
        panel.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            ForeColor = AppTheme.Muted,
            Font = new Font("Segoe UI", 8.75f),
            Padding = new Padding(0, 1, 0, 1)
        });
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
