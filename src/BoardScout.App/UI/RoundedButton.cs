using System.Drawing.Drawing2D;

namespace BoardScout.UI;

internal sealed class RoundedButton : Button
{
    private bool _hovering;
    private bool _pressing;
    private float _hover;
    private readonly Func<float, bool> _animate;

    public RoundedButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        _animate = AnimateHover;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Paint whatever is really behind the button (the header's glow, a card) instead of a flat
        // color guessed from the nearest opaque parent.
        ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var rect = new RectangleF(1f, 1f, Width - 2f, Height - 2f);
        var radius = rect.Height / 2f;
        var fill = !Enabled ? Color.FromArgb(60, BackColor)
            : _pressing ? FlatAppearance.MouseDownBackColor
            : Glass.Mix(BackColor, FlatAppearance.MouseOverBackColor, _hover);
        var border = Enabled ? FlatAppearance.BorderColor : Color.FromArgb(60, FlatAppearance.BorderColor);

        if (Glass.Enabled && Enabled)
        {
            Glass.Surface(g, rect, radius, border, tintStrength: 0.1f + 0.1f * _hover,
                sheen: _pressing ? 0.4f : 0.9f + 0.5f * _hover, baseColor: fill);
            using var rim = new Pen(Color.FromArgb(110 + (int)(90 * _hover), border), 1.2f);
            using var path = Glass.RoundedPath(rect, radius);
            g.DrawPath(rim, path);
        }
        else
        {
            using var path = Glass.RoundedPath(rect, radius);
            using var brush = new SolidBrush(fill);
            using var pen = new Pen(border, 1.4f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);
        }

        var foreground = Enabled ? ForeColor : Color.FromArgb(100, ForeColor);
        using var textBrush = new SolidBrush(foreground);
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(Text, Font, textBrush, new RectangleF(0, 0, Width, Height), format);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; StartHover(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressing = false; StartHover(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressing = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { _hovering = false; _pressing = false; _hover = 0; }
        base.OnEnabledChanged(e);
    }

    private void StartHover()
    {
        if (Motion.Enabled) Motion.Start(_animate);
        else { _hover = _hovering ? 1 : 0; Invalidate(); }
    }

    private bool AnimateHover(float seconds)
    {
        if (IsDisposed) return false;
        var target = _hovering ? 1f : 0f;
        _hover = Motion.Approach(_hover, target, 16f, seconds);
        if (Math.Abs(_hover - target) < 0.01f) _hover = target;
        Invalidate();
        return _hover != target;
    }
}
