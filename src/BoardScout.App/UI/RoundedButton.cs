using System.Drawing.Drawing2D;

namespace BoardScout.UI;

internal sealed class RoundedButton : Button
{
    private bool _hovering;
    private bool _pressing;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(ResolveParentBackground());

        var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
        var radius = rect.Height / 2f;
        using var path = CreateRoundedPath(rect, radius);

        var bgColor = !Enabled ? Color.FromArgb(60, BackColor)
                    : _pressing ? FlatAppearance.MouseDownBackColor
                    : _hovering ? FlatAppearance.MouseOverBackColor
                    : BackColor;

        using var fill = new SolidBrush(bgColor);
        g.FillPath(fill, path);

        var borderColor = Enabled ? FlatAppearance.BorderColor : Color.FromArgb(60, FlatAppearance.BorderColor);
        using var border = new Pen(borderColor, 1.4f);
        g.DrawPath(border, path);

        var fgColor = Enabled ? ForeColor : Color.FromArgb(100, ForeColor);
        using var textBrush = new SolidBrush(fgColor);
        using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), sf);
    }

    private Color ResolveParentBackground()
    {
        for (var c = Parent; c != null; c = c.Parent)
            if (c.BackColor.A == 255) return c.BackColor;
        return AppTheme.Background;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressing = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; Invalidate(); base.OnMouseUp(e); }

    private static GraphicsPath CreateRoundedPath(RectangleF bounds, float radius)
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
}
