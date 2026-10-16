using System.Drawing.Drawing2D;

namespace BoardScout.UI;

/// <summary>Rounded rectangles for GDI+ (the app icon, cards, and chips).</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = Rounded(bounds, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0) return;
        using var path = Rounded(bounds, Math.Min(radius, Math.Min(bounds.Width, bounds.Height) / 2));
        graphics.DrawPath(pen, path);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
