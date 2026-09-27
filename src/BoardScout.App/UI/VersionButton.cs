using System.Drawing.Drawing2D;
using System.Reflection;

namespace BoardScout.UI;

/// <summary>
/// The version number as a Google chasing-colors pill. Clicking it opens the version pop-out
/// (runtime, sensors, troubleshooting, settings, dependencies, requirements, legal).
/// </summary>
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
    private float _angle = 35f;

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
        AccessibleName = $"BoardScout version {AppVersion}";
        AccessibleDescription = "Opens runtime, sensor, troubleshooting, settings, and dependency details.";
        AccessibleRole = AccessibleRole.PushButton;
        TabStop = true;
        _timer.Tick += (_, _) =>
        {
            if (!Visible || FindForm() is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
            _angle = (_angle + 1.5f) % 360f;
            Invalidate();
        };
        RefreshMotion();
    }

    /// <summary>The chase stops when motion is turned off; the colors stay.</summary>
    public void RefreshMotion()
    {
        if (Motion.Enabled) _timer.Start();
        else _timer.Stop();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using var outerPath = Glass.RoundedPath(new RectangleF(0, 0, Width - 1, Height - 1), 14);

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

        using var innerPath = Glass.RoundedPath(new RectangleF(3, 3, Width - 7, Height - 7), 11);
        using var fill = new SolidBrush(AppTheme.Surface);
        g.FillPath(fill, innerPath);

        var text = $"v{AppVersion}";
        var textSize = g.MeasureString(text, Font);
        using var textBrush = new SolidBrush(Color.FromArgb(168, 205, 231));
        g.DrawString(text, Font, textBrush, (Width - textSize.Width) / 2, (Height - textSize.Height) / 2);

        if (Focused)
        {
            using var focus = new Pen(Color.FromArgb(160, AppTheme.Text), 1f) { DashStyle = DashStyle.Dot };
            using var focusPath = Glass.RoundedPath(new RectangleF(4.5f, 4.5f, Width - 10, Height - 10), 9);
            g.DrawPath(focus, focusPath);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
        }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    // Screen readers and UI Automation can press the button, not only a mouse.
    protected override AccessibleObject CreateAccessibilityInstance() => new VersionAccessible(this);

    private sealed class VersionAccessible(VersionButton owner) : ControlAccessibleObject(owner)
    {
        public override AccessibleRole Role => AccessibleRole.PushButton;
        public override string DefaultAction => "Press";
        public override void DoDefaultAction() => owner.OnClick(EventArgs.Empty);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
