using System.Drawing.Drawing2D;

namespace BoardScout.UI;

/// <summary>
/// Two things a table cell can be besides text: a status as a tinted pill with a dot, and how full something is
/// as a row of cells that light up in the colour of that fullness, with the figure beside them.
/// </summary>
internal static class GridPaint
{
    private static readonly Font PillFont = new("Segoe UI Semibold", 8.5f);
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix;

    /// <summary>The cell's text in a pill of the cell's own text colour. A dash (nothing to say) stays a dash.</summary>
    public static void Pill(DataGridViewCellPaintingEventArgs e, bool top = false)
    {
        if (e.RowIndex < 0 || e.Graphics is not { } g || e.FormattedValue is not string { Length: > 0 } text || text == "—") return;
        e.PaintBackground(e.CellBounds, true);
        var tone = e.CellStyle?.ForeColor ?? AppTheme.Text;
        var padding = e.CellStyle?.Padding ?? Padding.Empty;
        var textWidth = TextRenderer.MeasureText(g, text, PillFont, Size.Empty, Flags).Width;
        const int height = 22;
        var y = top ? e.CellBounds.Y + Math.Max(3, padding.Top - 2) : e.CellBounds.Y + (e.CellBounds.Height - height) / 2;
        var pill = new RectangleF(e.CellBounds.X + padding.Left - 2 + 0.5f, y + 0.5f, textWidth + 30, height - 1);

        var state = g.Save();
        g.SetClip(e.CellBounds, CombineMode.Intersect);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Glass.RoundedPath(pill, pill.Height / 2f))
        using (var fill = new SolidBrush(Glass.Mix(AppTheme.Surface, tone, 0.17f)))
        using (var edge = new Pen(Color.FromArgb(105, tone), 1f))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }
        using (var dot = new SolidBrush(tone))
            g.FillEllipse(dot, pill.X + 9, pill.Y + pill.Height / 2f - 3.5f, 7, 7);
        g.Restore(state);
        TextRenderer.DrawText(g, text, PillFont, new Rectangle((int)pill.X + 21, (int)pill.Y, textWidth + 4, (int)pill.Height),
            AppTheme.IsDark ? Glass.Mix(tone, Color.White, 0.22f) : Glass.Mix(tone, Color.Black, 0.15f), Flags | TextFormatFlags.PreserveGraphicsClipping);
        e.Handled = true;
    }

    /// <summary>
    /// How full, 0 to 1 (kept in the cell's Tag): sixteen cells, the used ones lit from green through amber to red,
    /// then the cell's text right-aligned.
    /// </summary>
    public static void FillBar(DataGridViewCellPaintingEventArgs e, DataGridViewCell cell)
    {
        if (e.RowIndex < 0 || e.Graphics is not { } g || cell.Tag is not float fraction) return;
        e.PaintBackground(e.CellBounds, true);
        var padding = e.CellStyle?.Padding ?? Padding.Empty;
        const int cells = 16, cellWidth = 5, gap = 2, cellHeight = 13;
        var x = e.CellBounds.X + padding.Left;
        var y = e.CellBounds.Y + (e.CellBounds.Height - cellHeight) / 2f;
        var used = fraction <= 0 ? 0 : Math.Clamp((int)MathF.Round(fraction * cells), 1, cells);

        var state = g.Save();
        g.SetClip(e.CellBounds, CombineMode.Intersect);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var brush = new SolidBrush(Color.Empty))
        {
            for (var i = 0; i < cells; i++)
            {
                // Each lit cell has the colour of its own place on the scale, like the lights of a level meter.
                brush.Color = i < used
                    ? Heat.At(GaugeKind.Disk, (i + 1f) / cells)
                    : Color.FromArgb(AppTheme.IsDark ? 26 : 40, AppTheme.Text);
                using var path = Glass.RoundedPath(new RectangleF(x + i * (cellWidth + gap), y, cellWidth, cellHeight), 1.5f);
                g.FillPath(brush, path);
            }
        }
        g.Restore(state);
        var barRight = x + cells * (cellWidth + gap);
        var text = e.FormattedValue as string ?? "";
        TextRenderer.DrawText(g, text, e.CellStyle?.Font ?? Control.DefaultFont,
            new Rectangle(barRight, e.CellBounds.Y, Math.Max(0, e.CellBounds.Right - padding.Right - barRight), e.CellBounds.Height),
            e.CellStyle?.ForeColor ?? AppTheme.Text, Flags | TextFormatFlags.Right | TextFormatFlags.PreserveGraphicsClipping);
        e.Handled = true;
    }
}
