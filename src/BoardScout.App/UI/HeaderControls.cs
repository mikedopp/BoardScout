using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

/// <summary>
/// A live header gauge: a caption, the value with a quieter unit, and either a sparkline of the last minute
/// (for what changes quickly) or a meter (for what fills up).
/// </summary>
internal sealed class GaugeTile : Control
{
    private const int History = 40;
    private static readonly Font CaptionFont = new("Segoe UI", 8.5f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 13.5f);
    private static readonly Font UnitFont = new("Segoe UI", 9f);
    private readonly List<double> _samples = [];
    private readonly bool _spark;
    private string _caption;
    private string _value = "—";
    private string _unit = "";
    private float? _fraction;
    private Color? _tone;

    public GaugeTile(string caption, bool spark, Color accent)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        _caption = caption;
        _spark = spark;
        Accent = accent;
        Size = new Size(214, 54);
        Margin = new Padding(0, 0, 10, 0);
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = caption;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Accent { get; set; }

    /// <summary>How much of the tile's width the sparkline takes.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float SparkShare { get; set; } = 0.36f;

    /// <summary>The floor a sparkline scales to, so a quiet link draws flat instead of magnifying noise.</summary>
    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public double SparkFloor { get; set; } = 1;

    /// <summary>Sets what the gauge shows; tone colors the value and meter (null keeps the accent).</summary>
    public void Show(string caption, string value, string unit = "", float? fraction = null, Color? tone = null)
    {
        if (_caption == caption && _value == value && _unit == unit && _fraction == fraction && _tone == tone) return;
        _caption = caption;
        _value = value;
        _unit = unit;
        _fraction = fraction is { } f ? Math.Clamp(f, 0, 1) : null;
        _tone = tone;
        AccessibleName = $"{caption}: {value} {unit}".Trim();
        Invalidate();
    }

    public void AddSample(double value)
    {
        _samples.Add(Math.Max(0, value));
        if (_samples.Count > History) _samples.RemoveAt(0);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _tone ?? Accent;
        Glass.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12, Accent, tintStrength: 0.06f, sheen: 0.7f);

        TextRenderer.DrawText(g, _caption, CaptionFont, new Rectangle(13, 6, Width - 26, 16), AppTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        var valueSize = TextRenderer.MeasureText(g, _value, ValueFont, Size.Empty, TextFormatFlags.NoPadding);
        var sparkLeft = _spark ? Width - 12 - (int)(Width * SparkShare) : Width - 12;
        TextRenderer.DrawText(g, _value, ValueFont, new Rectangle(12, 21, sparkLeft - 16, 25), _tone ?? AppTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        if (_unit.Length > 0 && 12 + valueSize.Width + 4 < sparkLeft - 20)
            TextRenderer.DrawText(g, _unit, UnitFont, new Rectangle(12 + valueSize.Width + 4, 27, sparkLeft - valueSize.Width - 20, 18), AppTheme.Muted,
                TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);

        if (_spark) DrawSpark(g, new RectangleF(sparkLeft, 12, Width - 12 - sparkLeft, Height - 22), color);
        else if (_fraction is { } fraction)
        {
            var track = new RectangleF(13, Height - 10, Width - 26, 4);
            using var trackBrush = new SolidBrush(Color.FromArgb(28, 255, 255, 255));
            g.FillRoundedRectangle(trackBrush, track, 2);
            using var fill = new SolidBrush(color);
            g.FillRoundedRectangle(fill, new RectangleF(track.X, track.Y, Math.Max(4, track.Width * fraction), track.Height), 2);
        }
    }

    private void DrawSpark(Graphics g, RectangleF box, Color color)
    {
        if (_samples.Count < 2) return;
        var max = Math.Max(SparkFloor, _samples.Max());
        var step = box.Width / (History - 1);
        var start = box.Right - step * (_samples.Count - 1);
        var points = _samples.Select((v, i) => new PointF(start + i * step, box.Bottom - (float)(v / max) * box.Height)).ToArray();
        using var area = new GraphicsPath();
        area.AddLine(points[0].X, box.Bottom, points[0].X, points[0].Y);
        area.AddLines(points);
        area.AddLine(points[^1].X, points[^1].Y, points[^1].X, box.Bottom);
        area.CloseFigure();
        using (var fill = new LinearGradientBrush(RectangleF.Inflate(box, 0, 1), Color.FromArgb(70, color), Color.FromArgb(0, color), LinearGradientMode.Vertical))
            g.FillPath(fill, area);
        using var pen = new Pen(color, 1.6f) { LineJoin = LineJoin.Round };
        g.DrawLines(pen, points);
    }
}

/// <summary>Dark drop-down menus for the header's Drivers and More buttons.</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new Colors())
{
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? AppTheme.Text : Color.FromArgb(120, AppTheme.Muted);
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = AppTheme.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientBegin => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientEnd => AppTheme.SurfaceRaised;
        public override Color MenuBorder => AppTheme.Border;
        public override Color MenuItemBorder => Color.FromArgb(90, AppTheme.Accent);
        public override Color MenuItemSelected => AppTheme.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => AppTheme.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => AppTheme.AccentSoft;
        public override Color MenuItemPressedGradientBegin => AppTheme.AccentSoft;
        public override Color MenuItemPressedGradientEnd => AppTheme.AccentSoft;
        public override Color SeparatorDark => AppTheme.Border;
        public override Color SeparatorLight => AppTheme.Border;
    }
}
