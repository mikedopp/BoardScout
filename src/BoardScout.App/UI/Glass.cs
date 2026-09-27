using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BoardScout.UI;

/// <summary>
/// GDI+ approximation of the liquid-glass look used by the web views (QuickLiquid): a tinted body,
/// a specular sheen across the top, light pooling along the bottom, and a rim that is bright above
/// and fades below. With glass effects off it degrades to the flat surface-and-border style.
/// </summary>
internal static class Glass
{
    public static bool Enabled => AppSettings.Current.GlassEffects;

    public static void Surface(Graphics g, RectangleF rect, float radius, Color tint,
        float tintStrength = 0.14f, float sheen = 1f, Color? baseColor = null)
    {
        if (rect.Width < 2 || rect.Height < 2) return;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = RoundedPath(rect, radius);
        var surface = baseColor ?? AppTheme.Surface;

        if (!Enabled)
        {
            using var flat = new SolidBrush(Mix(surface, tint, tintStrength * 0.6f));
            using var edge = new Pen(Mix(AppTheme.Border, tint, 0.35f), 1f);
            g.FillPath(flat, path);
            g.DrawPath(edge, path);
            g.SmoothingMode = smoothing;
            return;
        }

        using (var body = new LinearGradientBrush(Pad(rect),
                   Mix(surface, tint, tintStrength * 1.3f), Mix(surface, tint, tintStrength * 0.5f),
                   LinearGradientMode.Vertical))
            g.FillPath(body, path);

        var state = g.Save();
        g.SetClip(path, CombineMode.Intersect);
        var sheenRect = new RectangleF(rect.X, rect.Y, rect.Width, Math.Max(2, rect.Height * 0.5f));
        using (var sheenBrush = new LinearGradientBrush(Pad(sheenRect),
                   Color.FromArgb(Math.Clamp((int)(30 * sheen), 0, 255), 255, 255, 255),
                   Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical))
            g.FillRectangle(sheenBrush, sheenRect);
        var poolHeight = Math.Max(3, rect.Height * 0.3f);
        var poolRect = new RectangleF(rect.X, rect.Bottom - poolHeight, rect.Width, poolHeight);
        using (var pool = new LinearGradientBrush(Pad(poolRect),
                   Color.FromArgb(0, tint), Color.FromArgb(40, tint), LinearGradientMode.Vertical))
            g.FillRectangle(pool, poolRect);
        g.Restore(state);

        using var rimBrush = new LinearGradientBrush(Pad(rect),
            Color.FromArgb(92, 255, 255, 255), Color.FromArgb(16, 255, 255, 255), LinearGradientMode.Vertical);
        using var rim = new Pen(rimBrush, 1f);
        g.DrawPath(rim, path);
        g.SmoothingMode = smoothing;
    }

    /// <summary>A deep background with soft colored light, cached per size (the header repaints often).</summary>
    public static Bitmap RenderAurora(Size size)
    {
        var bitmap = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Point.Empty, bitmap.Size);
        using (var baseBrush = new LinearGradientBrush(Pad(bounds), AppTheme.Surface, AppTheme.Background, LinearGradientMode.Vertical))
            g.FillRectangle(baseBrush, bounds);

        if (Enabled)
        {
            Blob(g, new RectangleF(-bounds.Width * 0.08f, -bounds.Height * 1.1f, bounds.Width * 0.5f, bounds.Height * 2.4f), AppTheme.Accent, 46);
            Blob(g, new RectangleF(bounds.Width * 0.38f, -bounds.Height * 0.6f, bounds.Width * 0.34f, bounds.Height * 1.9f), AppTheme.Purple, 30);
            Blob(g, new RectangleF(bounds.Width * 0.7f, -bounds.Height * 0.4f, bounds.Width * 0.42f, bounds.Height * 2.2f), AppTheme.Teal, 26);
        }
        return bitmap;
    }

    private static void Blob(Graphics g, RectangleF ellipse, Color color, int alpha)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(ellipse);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(alpha, color),
            SurroundColors = [Color.FromArgb(0, color)],
            FocusScales = new PointF(0.15f, 0.15f)
        };
        g.FillPath(brush, path);
    }

    public static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var r = Math.Max(0.5f, Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2));
        var d = r * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (int)(a.A + (b.A - a.A) * t),
            (int)(a.R + (b.R - a.R) * t),
            (int)(a.G + (b.G - a.G) * t),
            (int)(a.B + (b.B - a.B) * t));
    }

    // GDI+ gradients wrap a stray line of the end color onto the first row unless the brush
    // rectangle is a little larger than the area it paints.
    private static RectangleF Pad(RectangleF rect) => RectangleF.Inflate(rect, 1, 1);
}
