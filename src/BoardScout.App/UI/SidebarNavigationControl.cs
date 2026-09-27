using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

public sealed record SidebarDestination(string Title, string Description);

public sealed class SidebarNavigationControl : Control
{
    private const int ItemTop = 116;
    private const int ItemHeight = 66;
    private const int ItemGap = 8;
    private const int Radius = 14;

    private readonly IReadOnlyList<SidebarDestination> _items =
    [
        new("Overview", "Hover parts · zoom and pan"),
        new("Topology", "Follow lanes and shared bandwidth"),
        new("Drivers", "Review official update links"),
        new("Storage", "Find full and external drives"),
        new("Efficiency", "See fixes and upgrade ideas"),
        new("System", "OS personality, patches, and software"),
        new("Scan Log", "Troubleshoot inventory checks")
    ];

    private readonly float[] _hoverLevel;
    private readonly Func<float, bool> _animate;
    private int _selectedIndex;
    private int _hoveredIndex = -1;
    private float _blobTop = float.NaN;
    private float _blobBottom = float.NaN;

    public SidebarNavigationControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = DockStyle.Left;
        Width = 255;
        MinimumSize = new Size(230, 500);
        BackColor = AppTheme.Background;
        TabStop = true;
        AccessibleName = "BoardScout navigation";
        AccessibleDescription = "Choose a workspace. Use the descriptions beneath each name to learn what it does.";
        Cursor = Cursors.Default;
        _hoverLevel = new float[_items.Count];
        _animate = Animate;
    }

    public event EventHandler? SelectedIndexChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = Math.Clamp(value, 0, _items.Count - 1);
            if (next == _selectedIndex) return;
            _selectedIndex = next;
            StartMotion();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RefreshTheme()
    {
        BackColor = AppTheme.Background;
        Invalidate();
    }

    private Rectangle ItemRect(int index) =>
        new(12, ItemTop + index * (ItemHeight + ItemGap), Math.Max(40, ClientSize.Width - 24), ItemHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        if (Glass.Enabled)
        {
            using var wash = new LinearGradientBrush(RectangleF.Inflate(ClientRectangle, 1, 1),
                AppTheme.Background, Glass.Mix(AppTheme.Background, AppTheme.Accent, 0.06f), LinearGradientMode.Vertical);
            g.FillRectangle(wash, ClientRectangle);
        }

        using var headingFont = new Font("Segoe UI Semibold", 8.25f);
        using var introFont = new Font("Segoe UI", 8.5f);
        using var titleFont = new Font("Segoe UI Semibold", 11.5f);
        using var detailFont = new Font("Segoe UI", 8.25f);
        using var rulePen = new Pen(AppTheme.Border, 1);

        DrawText(g, "EXPLORE YOUR PC", headingFont, AppTheme.Accent,
            new Rectangle(18, 18, ClientSize.Width - 36, 20));
        DrawText(g, "Start with Overview. Each screen keeps a different kind of work out of your way.",
            introFont, AppTheme.Muted, new Rectangle(18, 42, ClientSize.Width - 36, 48), wrap: true);
        g.DrawLine(rulePen, 18, 102, ClientSize.Width - 18, 102);

        for (var i = 0; i < _items.Count; i++)
            DrawPill(g, ItemRect(i), _hoverLevel[i]);

        DrawSelection(g);

        for (var i = 0; i < _items.Count; i++)
        {
            var rect = ItemRect(i);
            var selected = i == _selectedIndex;
            DrawText(g, _items[i].Title, titleFont, AppTheme.Text,
                new Rectangle(rect.X + 22, rect.Y + 8, rect.Width - 34, 24));
            DrawText(g, _items[i].Description, detailFont, selected ? AppTheme.Accent : AppTheme.Muted,
                new Rectangle(rect.X + 22, rect.Y + 34, rect.Width - 34, 20));
        }

        var y = ItemRect(_items.Count - 1).Bottom + ItemGap;
        var guideTop = Math.Max(y + 8, ClientSize.Height - 145);
        if (guideTop + 130 <= ClientSize.Height)
        {
            g.DrawLine(rulePen, 18, guideTop, ClientSize.Width - 18, guideTop);
            DrawText(g, "QUICK START", headingFont, AppTheme.Accent,
                new Rectangle(18, guideTop + 14, ClientSize.Width - 36, 18));
            DrawText(g,
                "1  Scan after hardware changes\n2  Hover parts for capability\n3  Check Drivers for official links",
                introFont, AppTheme.Muted,
                new Rectangle(18, guideTop + 38, ClientSize.Width - 36, 78), wrap: true);
        }

        using var divider = new Pen(AppTheme.Border, 1);
        g.DrawLine(divider, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
    }

    private static void DrawPill(Graphics g, Rectangle rect, float hover)
    {
        if (Glass.Enabled)
        {
            Glass.Surface(g, rect, Radius, AppTheme.Accent, tintStrength: 0.03f + 0.07f * hover, sheen: 0.45f + 0.55f * hover);
            return;
        }
        var fill = Glass.Mix(AppTheme.Surface, Color.FromArgb(31, 43, 55), hover);
        var border = Glass.Mix(AppTheme.Border, AppTheme.Muted, hover);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border, 1f);
        using var path = Glass.RoundedPath(rect, Radius);
        g.FillPath(brush, path);
        g.DrawPath(pen, path);
    }

    // The selection is one liquid pill that glides between items instead of jumping.
    private void DrawSelection(Graphics g)
    {
        var target = ItemRect(_selectedIndex);
        if (float.IsNaN(_blobTop)) { _blobTop = target.Top; _blobBottom = target.Bottom; }

        var height = Math.Max(ItemHeight * 0.5f, _blobBottom - _blobTop);
        var stretch = Math.Clamp((height - ItemHeight) / ItemHeight, 0, 1);
        var squeeze = 5f * stretch; // stretched droplets get a little narrower
        var blob = new RectangleF(target.X + squeeze, _blobTop, target.Width - squeeze * 2, height);

        if (Glass.Enabled)
        {
            Glass.Surface(g, blob, Radius, AppTheme.Accent, tintStrength: 0.24f, sheen: 1.25f);
            using var path = Glass.RoundedPath(blob, Radius);
            using var rim = new Pen(Color.FromArgb(190, AppTheme.Accent), 1.6f);
            g.DrawPath(rim, path);
        }
        else
        {
            using var path = Glass.RoundedPath(blob, Radius);
            using var fill = new SolidBrush(AppTheme.AccentSoft);
            using var pen = new Pen(AppTheme.Accent, 1.7f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        using var bar = new SolidBrush(AppTheme.Accent);
        using var barPath = Glass.RoundedPath(new RectangleF(blob.X + 6, blob.Y + 10, 4, Math.Max(4, blob.Height - 20)), 2);
        g.FillPath(bar, barPath);
    }

    private void StartMotion()
    {
        if (Motion.Enabled)
        {
            Motion.Start(_animate);
            return;
        }
        var target = ItemRect(_selectedIndex);
        _blobTop = target.Top;
        _blobBottom = target.Bottom;
        for (var i = 0; i < _hoverLevel.Length; i++) _hoverLevel[i] = i == _hoveredIndex ? 1 : 0;
        Invalidate();
    }

    private bool Animate(float seconds)
    {
        if (IsDisposed) return false;
        var target = ItemRect(_selectedIndex);
        if (float.IsNaN(_blobTop)) { _blobTop = target.Top; _blobBottom = target.Bottom; }

        // The leading edge races ahead and the trailing edge catches up: the pill stretches
        // like a droplet, then settles.
        var movingDown = target.Top > _blobTop;
        _blobTop = Motion.Approach(_blobTop, target.Top, movingDown ? 9f : 22f, seconds);
        _blobBottom = Motion.Approach(_blobBottom, target.Bottom, movingDown ? 22f : 9f, seconds);
        var settled = Math.Abs(_blobTop - target.Top) < 0.4f && Math.Abs(_blobBottom - target.Bottom) < 0.4f;
        if (settled) { _blobTop = target.Top; _blobBottom = target.Bottom; }

        var hoverMoving = false;
        for (var i = 0; i < _hoverLevel.Length; i++)
        {
            var goal = i == _hoveredIndex ? 1f : 0f;
            _hoverLevel[i] = Motion.Approach(_hoverLevel[i], goal, 14f, seconds);
            if (Math.Abs(_hoverLevel[i] - goal) < 0.01f) _hoverLevel[i] = goal;
            else hoverMoving = true;
        }

        Invalidate();
        return !settled || hoverMoving;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        var target = ItemRect(_selectedIndex);
        _blobTop = target.Top;
        _blobBottom = target.Bottom;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = HitTest(e.Location);
        if (index == _hoveredIndex) return;
        _hoveredIndex = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        StartMotion();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Cursor = Cursors.Default;
        StartMotion();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var index = HitTest(e.Location);
        if (index < 0) return;
        Focus();
        SelectedIndex = index;
    }

    private int HitTest(Point location)
    {
        for (var i = 0; i < _items.Count; i++)
            if (ItemRect(i).Contains(location)) return i;
        return -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Down or Keys.Right)
        {
            SelectedIndex = (_selectedIndex + 1) % _items.Count;
            e.Handled = true;
        }
        else if (e.KeyCode is Keys.Up or Keys.Left)
        {
            SelectedIndex = (_selectedIndex - 1 + _items.Count) % _items.Count;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Home)
        {
            SelectedIndex = 0;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.End)
        {
            SelectedIndex = _items.Count - 1;
            e.Handled = true;
        }
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds, bool wrap = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Near,
            LineAlignment = StringAlignment.Near,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = wrap ? StringFormatFlags.LineLimit : StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, bounds, format);
    }
}
