using System.Drawing.Drawing2D;

namespace BoardScout.UI;

public sealed record SidebarDestination(string Title, string Description);

public sealed class SidebarNavigationControl : Control
{
    private readonly IReadOnlyList<SidebarDestination> _items =
    [
        new("Overview", "Hover parts · zoom and pan"),
        new("Topology", "Follow lanes and shared bandwidth"),
        new("Drivers", "Review official update links"),
        new("Storage", "Find full and external drives"),
        new("Efficiency", "See fixes and upgrade ideas"),
        new("Scan Log", "Troubleshoot inventory checks")
    ];

    private readonly List<Rectangle> _itemBounds = [];
    private int _selectedIndex;
    private int _hoveredIndex = -1;

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
    }

    public event EventHandler? SelectedIndexChanged;

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            var next = Math.Clamp(value, 0, _items.Count - 1);
            if (next == _selectedIndex) return;
            _selectedIndex = next;
            Invalidate();
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void RefreshTheme()
    {
        BackColor = AppTheme.Background;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _itemBounds.Clear();

        using var headingFont = new Font("Segoe UI Semibold", 8.25f);
        using var introFont = new Font("Segoe UI", 8.5f);
        using var titleFont = new Font("Segoe UI Semibold", 11.5f);
        using var detailFont = new Font("Segoe UI", 8.25f);
        using var rulePen = new Pen(AppTheme.Border, 1);

        DrawText(e.Graphics, "EXPLORE YOUR PC", headingFont, AppTheme.Accent,
            new Rectangle(18, 18, ClientSize.Width - 36, 20));
        DrawText(e.Graphics, "Start with Overview. Each screen keeps a different kind of work out of your way.",
            introFont, AppTheme.Muted, new Rectangle(18, 42, ClientSize.Width - 36, 48), wrap: true);
        e.Graphics.DrawLine(rulePen, 18, 102, ClientSize.Width - 18, 102);

        const int itemHeight = 66;
        const int gap = 8;
        var y = 116;
        for (var i = 0; i < _items.Count; i++)
        {
            var rect = new Rectangle(12, y, ClientSize.Width - 24, itemHeight);
            _itemBounds.Add(rect);
            DrawPill(e.Graphics, rect, _items[i], titleFont, detailFont,
                selected: i == _selectedIndex, hovered: i == _hoveredIndex);
            y += itemHeight + gap;
        }

        var guideTop = Math.Max(y + 8, ClientSize.Height - 145);
        if (guideTop + 130 <= ClientSize.Height)
        {
            e.Graphics.DrawLine(rulePen, 18, guideTop, ClientSize.Width - 18, guideTop);
            DrawText(e.Graphics, "QUICK START", headingFont, AppTheme.Accent,
                new Rectangle(18, guideTop + 14, ClientSize.Width - 36, 18));
            DrawText(e.Graphics,
                "1  Scan after hardware changes\n2  Hover parts for capability\n3  Check Drivers for official links",
                introFont, AppTheme.Muted,
                new Rectangle(18, guideTop + 38, ClientSize.Width - 36, 78), wrap: true);
        }

        using var divider = new Pen(AppTheme.Border, 1);
        e.Graphics.DrawLine(divider, ClientSize.Width - 1, 0, ClientSize.Width - 1, ClientSize.Height);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = _itemBounds.FindIndex(r => r.Contains(e.Location));
        if (index == _hoveredIndex) return;
        _hoveredIndex = index;
        Cursor = index >= 0 ? Cursors.Hand : Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredIndex = -1;
        Cursor = Cursors.Default;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        var index = _itemBounds.FindIndex(r => r.Contains(e.Location));
        if (index < 0) return;
        Focus();
        SelectedIndex = index;
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

    private static void DrawPill(Graphics g, Rectangle rect, SidebarDestination item, Font titleFont,
        Font detailFont, bool selected, bool hovered)
    {
        var fill = selected
            ? AppTheme.AccentSoft
            : hovered ? Color.FromArgb(31, 43, 55) : AppTheme.Surface;
        var border = selected ? AppTheme.Accent : hovered ? AppTheme.Muted : AppTheme.Border;
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border, selected ? 1.7f : 1f);
        g.FillRoundedRectangle(brush, rect, 14);
        g.DrawRoundedRectangle(pen, rect, 14);

        if (selected)
        {
            using var accent = new SolidBrush(AppTheme.Accent);
            g.FillRoundedRectangle(accent, new Rectangle(rect.X + 6, rect.Y + 10, 4, rect.Height - 20), 2);
        }

        var left = rect.X + (selected ? 22 : 17);
        DrawText(g, item.Title, titleFont, selected ? AppTheme.Text : AppTheme.Text,
            new Rectangle(left, rect.Y + 8, rect.Right - left - 12, 24));
        DrawText(g, item.Description, detailFont, selected ? AppTheme.Accent : AppTheme.Muted,
            new Rectangle(left, rect.Y + 34, rect.Right - left - 12, 20));
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
