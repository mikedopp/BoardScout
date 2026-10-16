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
