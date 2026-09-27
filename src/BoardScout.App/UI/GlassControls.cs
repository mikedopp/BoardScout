using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

/// <summary>Header band with a soft aurora glow behind the title, metrics, and toolbar.</summary>
internal sealed class AuroraPanel : Panel
{
    private Bitmap? _aurora;

    public AuroraPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    public void RefreshAurora()
    {
        _aurora?.Dispose();
        _aurora = null;
        Invalidate(true);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_aurora is null || _aurora.Size != ClientSize)
        {
            _aurora?.Dispose();
            _aurora = Glass.RenderAurora(ClientSize);
        }
        // Transparent children ask for their slice of this background; copy only that slice.
        e.Graphics.DrawImage(_aurora, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _aurora?.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>A rounded card whose luminous glass rim replaces the old square 1 px border.</summary>
internal sealed class GlassCard : Panel
{
    private const float CornerRadius = 16;

    public GlassCard()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.Surface;
        Padding = new Padding(7);
        Tag = "surface";
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? AppTheme.Background);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = Glass.RoundedPath(rect, CornerRadius);
        using (var fill = new SolidBrush(BackColor))
            g.FillPath(fill, path);

        if (!Glass.Enabled)
        {
            using var border = new Pen(AppTheme.Border, 1f);
            g.DrawPath(border, path);
            return;
        }

        var sheenRect = new RectangleF(rect.X, rect.Y, rect.Width, Math.Min(rect.Height, 28));
        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        using (var sheen = new LinearGradientBrush(RectangleF.Inflate(sheenRect, 1, 1),
                   Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            g.FillRectangle(sheen, sheenRect);
        g.Restore(state);

        using var rimBrush = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1),
            Color.FromArgb(84, 255, 255, 255), Color.FromArgb(22, AppTheme.Accent), LinearGradientMode.Vertical);
        using var rim = new Pen(rimBrush, 1f);
        g.DrawPath(rim, path);
    }
}

/// <summary>Raised glass surface for panels such as the part inspector.</summary>
internal sealed class GlassPanel : Panel
{
    public GlassPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = AppTheme.SurfaceRaised;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Tint { get; set; } = AppTheme.Accent;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? AppTheme.Surface);
        Glass.Surface(e.Graphics, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 14, Tint,
            tintStrength: 0.1f, sheen: 1f, baseColor: BackColor);
    }
}

/// <summary>Header metric with a glass chip; widens to fit values such as "CPU 52° · GPU 41°".</summary>
internal sealed class GlassMetricTile : Control
{
    private const int MinTileWidth = 122;
    private static readonly Font ValueFont = new("Segoe UI Semibold", 11.5f);
    private static readonly Font CaptionFont = new("Segoe UI Semibold", 7.25f);
    private string _value;
    private Color? _valueColor;

    public GlassMetricTile(string caption, string value)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
        Caption = caption;
        _value = value;
        Height = 48;
        Margin = new Padding(0, 0, 8, 0);
        AccessibleRole = AccessibleRole.StaticText;
        FitWidth();
    }

    public string Caption { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            FitWidth();
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color? ValueColor
    {
        get => _valueColor;
        set
        {
            if (_valueColor == value) return;
            _valueColor = value;
            Invalidate();
        }
    }

    private void FitWidth()
    {
        Width = Math.Clamp(TextRenderer.MeasureText(_value, ValueFont).Width + 28, MinTileWidth, 340);
        AccessibleName = $"{Caption}: {_value}";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        var accent = _valueColor ?? AppTheme.Accent;
        Glass.Surface(g, new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), 12, accent,
            tintStrength: 0.08f, sheen: 0.8f);
        TextRenderer.DrawText(g, _value, ValueFont, new Rectangle(12, 4, Width - 20, 25), _valueColor ?? AppTheme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        TextRenderer.DrawText(g, Caption, CaptionFont, new Rectangle(12, 28, Width - 20, 15), AppTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
    }
}

/// <summary>Status text drawn in a rounded pill sized to the text, not the full label width.</summary>
internal sealed class PillLabel : Label
{
    public PillLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        BackColor = Color.Transparent;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color PillColor { get; set; } = AppTheme.AccentSoft;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                      TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding;
        // Measure without EndEllipsis: against an empty proposed size it "fits" the text into
        // nothing and reports the width of "…".
        var textWidth = TextRenderer.MeasureText(g, Text, Font, new Size(int.MaxValue, Height),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        var pill = new RectangleF(0.5f, 0.5f, Math.Min(Width - 1.5f, textWidth + Padding.Horizontal + 2), Height - 1.5f);
        using var path = Glass.RoundedPath(pill, pill.Height / 2);
        using var fill = new SolidBrush(PillColor);
        using var rim = new Pen(Color.FromArgb(90, ForeColor), 1f);
        g.FillPath(fill, path);
        g.DrawPath(rim, path);
        var textRect = Rectangle.Round(new RectangleF(pill.X + Padding.Left, 0, pill.Width - Padding.Horizontal, Height));
        TextRenderer.DrawText(g, Text, Font, textRect, ForeColor, flags);
    }
}
