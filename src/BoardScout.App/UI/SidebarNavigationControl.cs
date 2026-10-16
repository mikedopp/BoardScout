using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

public sealed record SidebarDestination(string Title, string Description);

/// <summary>
/// The navigation rail: an icon and a name per screen, with what each screen is for as its tooltip. The selection
/// is one pill that glides between items.
/// </summary>
public sealed class SidebarNavigationControl : Control
{
    private const int ItemTop = 14;
    private const int ItemHeight = 42;
    private const int ItemGap = 4;
    private const int Radius = 11;

    private readonly IReadOnlyList<SidebarDestination> _items =
    [
        new("Overview", "The board: hover a part to see what it is and how it's doing"),
        new("Topology", "Follow lanes and shared bandwidth"),
        new("Connections", "Every device, its link, and live traffic"),
        new("Drivers", "Installed drivers and official update pages"),
        new("Storage", "Find full and external drives"),
        new("Efficiency", "Fixes and upgrade ideas, worst first"),
        new("System", "Windows, updates, apps, and scheduled tasks"),
        new("Machines", "Other PCs and servers, over SSH"),
        new("Scan Log", "What was read, and when")
    ];

    private readonly float[] _hoverLevel;
    private readonly Func<float, bool> _animate;
    private readonly ToolTip _toolTip = new() { InitialDelay = 500, ReshowDelay = 150, AutoPopDelay = 8000 };
    private static readonly Font TitleFont = new("Segoe UI Semibold", 10.25f);
    private int _selectedIndex;
    private int _hoveredIndex = -1;
    private float _blobTop = float.NaN;
    private float _blobBottom = float.NaN;

    public SidebarNavigationControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Dock = DockStyle.Left;
        Width = 206;
        MinimumSize = new Size(190, 420);
        BackColor = AppTheme.Background;
        TabStop = true;
        AccessibleName = "BoardScout navigation";
        AccessibleDescription = "Choose a screen. Up and down arrows move between them.";
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
            AccessibleName = $"BoardScout navigation: {_items[next].Title}";
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
        new(10, ItemTop + index * (ItemHeight + ItemGap), Math.Max(40, ClientSize.Width - 20), ItemHeight);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        for (var i = 0; i < _items.Count; i++)
            if (_hoverLevel[i] > 0.01f && i != _selectedIndex) DrawHover(g, ItemRect(i), _hoverLevel[i]);
        DrawSelection(g);

        for (var i = 0; i < _items.Count; i++)
        {
            var rect = ItemRect(i);
            var selected = i == _selectedIndex;
            var ink = selected ? AppTheme.Text : Glass.Mix(AppTheme.Muted, AppTheme.Text, _hoverLevel[i] * 0.6f);
            DrawIcon(g, i, new RectangleF(rect.X + 16, rect.Y + (rect.Height - 18) / 2f, 18, 18), selected ? AppTheme.Accent : ink);
            TextRenderer.DrawText(g, _items[i].Title, TitleFont, new Rectangle(rect.X + 46, rect.Y, rect.Width - 52, rect.Height), ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }

        // Keyboard focus shows as a ring around the current screen.
        if (Focused && ShowFocusCues)
        {
            using var ring = new Pen(Color.FromArgb(200, 124, 196, 255), 2f);
            using var path = Glass.RoundedPath(RectangleF.Inflate(ItemRect(_selectedIndex), 2, 2), Radius + 2);
            g.DrawPath(ring, path);
        }

        using var divider = new Pen(AppTheme.Border, 1);
        g.DrawLine(divider, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    private static void DrawHover(Graphics g, Rectangle rect, float level)
    {
        using var path = Glass.RoundedPath(rect, Radius);
        using var fill = new SolidBrush(Color.FromArgb((int)(16 * level), 255, 255, 255));
        g.FillPath(fill, path);
    }

    // The selection is one pill that glides between items instead of jumping.
    private void DrawSelection(Graphics g)
    {
        var target = ItemRect(_selectedIndex);
        if (float.IsNaN(_blobTop)) { _blobTop = target.Top; _blobBottom = target.Bottom; }
        var height = Math.Max(ItemHeight * 0.5f, _blobBottom - _blobTop);
        var blob = new RectangleF(target.X, _blobTop, target.Width, height);

        if (Glass.Enabled)
        {
            Glass.Surface(g, blob, Radius, AppTheme.Accent, tintStrength: 0.2f, sheen: 0.9f);
            using var path = Glass.RoundedPath(blob, Radius);
            using var rim = new Pen(Color.FromArgb(110, AppTheme.Accent), 1.2f);
            g.DrawPath(rim, path);
        }
        else
        {
            using var path = Glass.RoundedPath(blob, Radius);
            using var fill = new SolidBrush(AppTheme.AccentSoft);
            using var pen = new Pen(Color.FromArgb(150, AppTheme.Accent), 1.2f);
            g.FillPath(fill, path);
            g.DrawPath(pen, path);
        }

        using var bar = new SolidBrush(AppTheme.Accent);
        using var barPath = Glass.RoundedPath(new RectangleF(blob.X + 4, blob.Y + 11, 3, Math.Max(4, blob.Height - 22)), 1.5f);
        g.FillPath(bar, barPath);
    }

    // Line icons drawn in an 18 × 18 box, so they match the text at any DPI and need no icon font.
    private static void DrawIcon(Graphics g, int index, RectangleF box, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        PointF P(float x, float y) => new(box.X + x, box.Y + y);
        RectangleF R(float x, float y, float w, float h) => new(box.X + x, box.Y + y, w, h);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));
        void Rounded(RectangleF r, float radius) { using var path = Glass.RoundedPath(r, radius); g.DrawPath(pen, path); }
        void Dot(float x, float y, float r) => g.FillEllipse(brush, box.X + x - r, box.Y + y - r, r * 2, r * 2);

        switch (index)
        {
            case 0: // Overview: a chip
                Rounded(R(4, 4, 10, 10), 2);
                foreach (var v in new[] { 7f, 11f })
                {
                    Line(v, 1.5f, v, 4); Line(v, 14, v, 16.5f); Line(1.5f, v, 4, v); Line(14, v, 16.5f, v);
                }
                break;
            case 1: // Topology: a small tree
                g.DrawEllipse(pen, R(6.8f, 1.3f, 4.4f, 4.4f));
                g.DrawEllipse(pen, R(1.8f, 12.3f, 4.4f, 4.4f));
                g.DrawEllipse(pen, R(11.8f, 12.3f, 4.4f, 4.4f));
                Line(8, 5.6f, 4.8f, 12.4f); Line(10, 5.6f, 13.2f, 12.4f);
                break;
            case 2: // Connections: a plug
                Line(6.5f, 1.5f, 6.5f, 5); Line(11.5f, 1.5f, 11.5f, 5);
                Rounded(R(4, 5, 10, 6.5f), 2);
                g.DrawBezier(pen, P(9, 11.5f), P(9, 15), P(11, 16.5f), P(14.5f, 16.5f));
                break;
            case 3: // Drivers: a download into a tray
                Line(9, 2, 9, 11); Line(5.5f, 7.5f, 9, 11); Line(12.5f, 7.5f, 9, 11);
                g.DrawLines(pen, [P(3, 12), P(3, 16), P(15, 16), P(15, 12)]);
                break;
            case 4: // Storage: a drive cylinder
                g.DrawEllipse(pen, R(3, 2, 12, 4.5f));
                Line(3, 4.25f, 3, 13.5f); Line(15, 4.25f, 15, 13.5f);
                g.DrawArc(pen, R(3, 7, 12, 4.5f), 0, 180);
                g.DrawArc(pen, R(3, 11.3f, 12, 4.5f), 0, 180);
                break;
            case 5: // Efficiency: a bolt
                g.DrawPolygon(pen, [P(10.5f, 1.5f), P(4, 10), P(8.5f, 10), P(7.5f, 16.5f), P(14, 7.5f), P(9.5f, 7.5f)]);
                break;
            case 6: // System: a window
                Rounded(R(2, 3, 14, 12), 2);
                Line(2, 6.8f, 16, 6.8f);
                Dot(4.6f, 4.9f, 0.8f); Dot(6.9f, 4.9f, 0.8f);
                break;
            case 7: // Machines: two servers
                Rounded(R(2.5f, 2.5f, 13, 5.5f), 1.5f);
                Rounded(R(2.5f, 10, 13, 5.5f), 1.5f);
                Dot(5.5f, 5.25f, 1); Dot(5.5f, 12.75f, 1);
                Line(9, 5.25f, 12.5f, 5.25f); Line(9, 12.75f, 12.5f, 12.75f);
                break;
            default: // Scan log: a list
                foreach (var y in new[] { 4.5f, 9, 13.5f })
                {
                    Dot(3.5f, y, 1.1f);
                    Line(7, y, 15, y);
                }
                break;
        }
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

        // The leading edge races ahead and the trailing edge catches up: the pill stretches a little, then settles.
        var movingDown = target.Top > _blobTop;
        _blobTop = Motion.Approach(_blobTop, target.Top, movingDown ? 11f : 24f, seconds);
        _blobBottom = Motion.Approach(_blobBottom, target.Bottom, movingDown ? 24f : 11f, seconds);
        var settled = Math.Abs(_blobTop - target.Top) < 0.4f && Math.Abs(_blobBottom - target.Bottom) < 0.4f;
        if (settled) { _blobTop = target.Top; _blobBottom = target.Bottom; }

        var hoverMoving = false;
        for (var i = 0; i < _hoverLevel.Length; i++)
        {
            var goal = i == _hoveredIndex ? 1f : 0f;
            _hoverLevel[i] = Motion.Approach(_hoverLevel[i], goal, 16f, seconds);
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
        _toolTip.SetToolTip(this, index >= 0 ? _items[index].Description : null);
        StartMotion();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Cursor = Cursors.Default;
        _toolTip.SetToolTip(this, null);
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

    protected override void Dispose(bool disposing)
    {
        if (disposing) _toolTip.Dispose();
        base.Dispose(disposing);
    }
}
