using System.Drawing.Drawing2D;

namespace BoardScout.UI;

internal sealed class RoundedButton : Button
{
    private bool _hovering;
    private bool _pressing;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Parent?.BackColor ?? AppTheme.Background);

        var rect = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using var path = CreateRoundedPath(rect, 10);

        var bgColor = !Enabled ? Color.FromArgb(60, BackColor)
                    : _pressing ? FlatAppearance.MouseDownBackColor
                    : _hovering ? FlatAppearance.MouseOverBackColor
                    : BackColor;
        var borderAlpha = Enabled ? 255 : 60;

        using var fill = new SolidBrush(bgColor);
        using var border = new Pen(Color.FromArgb(borderAlpha, FlatAppearance.BorderColor), 1.2f);
        g.FillPath(fill, path);
        g.DrawPath(border, path);

        var fgColor = Enabled ? ForeColor : Color.FromArgb(100, ForeColor);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, fgColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
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
