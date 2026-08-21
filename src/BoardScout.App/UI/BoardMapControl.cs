using System.Drawing.Drawing2D;
using BoardScout.Models;

namespace BoardScout.UI;

public sealed class BoardMapControl : Control
{
    private ScanManifest? _snapshot;

    public BoardMapControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppTheme.Surface;
        MinimumSize = new Size(520, 380);
    }

    public void SetSnapshot(ScanManifest? snapshot)
    {
        _snapshot = snapshot;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        var area = Rectangle.Inflate(ClientRectangle, -18, -18);
        if (_snapshot is null)
        {
            DrawCentered(e.Graphics, "Run a hardware scan to map this motherboard.", area, AppTheme.Muted, 12);
            return;
        }

        DrawBoard(e.Graphics, area, _snapshot);
    }

    private static void DrawBoard(Graphics g, Rectangle bounds, ScanManifest scan)
    {
        using var boardBrush = new SolidBrush(Color.FromArgb(8, 35, 27));
        using var boardPen = new Pen(Color.FromArgb(34, 92, 58), 2);
        g.FillRoundedRectangle(boardBrush, bounds, 12);
        g.DrawRoundedRectangle(boardPen, bounds, 12);

        var scaleX = bounds.Width / 900f;
        var scaleY = bounds.Height / 620f;
        RectangleF Box(float x, float y, float w, float h) =>
            new(bounds.Left + x * scaleX, bounds.Top + y * scaleY, w * scaleX, h * scaleY);

        using var labelFont = new Font("Segoe UI", Math.Max(7, 8 * Math.Min(scaleX, scaleY)));
        using var smallFont = new Font("Segoe UI", Math.Max(6, 7 * Math.Min(scaleX, scaleY)));
        using var titleFont = new Font("Segoe UI Semibold", Math.Max(8, 10 * Math.Min(scaleX, scaleY)));

        DrawBox(g, Box(25, 20, 210, 80), Color.FromArgb(106, 115, 128), Color.FromArgb(180, 190, 202),
            "REAR I/O", "display • USB • LAN • audio", labelFont, smallFont);

        var cpu = scan.Cpu;
        DrawBox(g, Box(285, 45, 180, 145), Color.FromArgb(22, 24, 27), Color.FromArgb(105, 112, 124),
            ShortCpu(cpu.Name), $"{cpu.Cores} cores / {cpu.Threads} threads", titleFont, labelFont);

        var dimmCount = Math.Max(scan.Memory.TotalSlots, Math.Max(scan.Memory.Slots.Count, 2));
        for (var i = 0; i < Math.Min(dimmCount, 8); i++)
        {
            var occupied = i < scan.Memory.Slots.Count;
            var rect = Box(700 + i * 24, 35, 16, 155);
            DrawThinSlot(g, rect, occupied ? AppTheme.Good : Color.FromArgb(70, 85, 78),
                occupied ? $"{scan.Memory.Slots[i].CapacityGb:0}G" : "");
        }
        DrawText(g, $"DIMM  {scan.Memory.Populated}/{scan.Memory.TotalSlots}  {scan.TotalMemoryGb:0.#} GB",
            titleFont, AppTheme.Muted, Box(660, 6, 210, 22), ContentAlignment.MiddleCenter);

        var nvme = scan.Components.Where(c => c.Category == "storage" &&
            string.Equals(c.LookupHints.BusType, "NVMe", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < Math.Max(2, nvme.Count); i++)
        {
            var rect = Box(55, 225 + i * 52, 315, 28);
            if (i < nvme.Count)
                DrawBox(g, rect, Color.FromArgb(21, 92, 54), AppTheme.Good,
                    $"M.2 {i + 1}", Ellipsis(nvme[i].Model, 30), labelFont, smallFont);
            else
                DrawDashed(g, rect, AppTheme.Accent, $"M.2 {i + 1} — empty", labelFont);
        }

        var gpu = scan.Components.FirstOrDefault(c => c.Category == "gpu");
        var pcieY = 365;
        if (gpu is not null)
        {
            DrawBox(g, Box(45, pcieY, 500, 42), Color.FromArgb(19, 82, 49), AppTheme.Good,
                "PCIe x16", Ellipsis(gpu.Model, 44), titleFont, labelFont);
            pcieY += 62;
        }
        for (var i = 0; i < (scan.FormFactor == "mini-itx" ? 0 : 2); i++)
        {
            DrawDashed(g, Box(45, pcieY + i * 38, i == 0 ? 480 : 170, 22),
                AppTheme.Accent, i == 0 ? "PCIe expansion (estimated)" : "PCIe x1 (estimated)", smallFont);
        }

        var chipset = scan.Components.FirstOrDefault(c => c.Category == "chipset")?.Model ?? "Chipset";
        DrawBox(g, Box(470, 265, 140, 90), Color.FromArgb(35, 34, 43), AppTheme.Purple,
            Ellipsis(chipset.Replace("AMD ", "").Replace("Intel ", ""), 20), "platform controller", titleFont, smallFont);

        var sata = scan.Components.Where(c => c.Category == "storage" &&
            string.Equals(c.LookupHints.BusType, "SATA", StringComparison.OrdinalIgnoreCase)).ToList();
        DrawText(g, "SATA", titleFont, AppTheme.Muted, Box(720, 240, 120, 22), ContentAlignment.MiddleCenter);
        for (var i = 0; i < 6; i++)
        {
            var rect = Box(710, 270 + i * 34, 135, 24);
            if (i < sata.Count)
                DrawBox(g, rect, Color.FromArgb(83, 58, 17), AppTheme.Warning,
                    $"{i + 1}", Ellipsis(sata[i].Model, 16), smallFont, smallFont);
            else
                DrawDashed(g, rect, AppTheme.Warning, $"{i + 1} — open", smallFont);
        }

        DrawText(g,
            $"{scan.SystemInfo.Baseboard.Manufacturer} {scan.SystemInfo.Baseboard.Product}  •  {scan.FormFactor.ToUpperInvariant()}",
            titleFont, Color.FromArgb(105, 150, 120), Box(20, 575, 850, 28), ContentAlignment.MiddleRight);
    }

    private static string ShortCpu(string value) =>
        Ellipsis(value.Replace(" with Radeon Graphics", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase), 28);

    private static string Ellipsis(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? "Unknown" : value.Length <= max ? value : value[..(max - 1)] + "…";

    private static void DrawBox(
        Graphics g, RectangleF rect, Color fill, Color border, string title, string subtitle, Font titleFont, Font subtitleFont)
    {
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border, 1.5f);
        g.FillRoundedRectangle(brush, rect, 7);
        g.DrawRoundedRectangle(pen, rect, 7);
        var top = new RectangleF(rect.X + 5, rect.Y + 4, rect.Width - 10, rect.Height / 2);
        var bottom = new RectangleF(rect.X + 5, rect.Y + rect.Height / 2 - 1, rect.Width - 10, rect.Height / 2);
        DrawText(g, title, titleFont, AppTheme.Text, top, ContentAlignment.MiddleCenter);
        DrawText(g, subtitle, subtitleFont, Color.FromArgb(178, 196, 185), bottom, ContentAlignment.MiddleCenter);
    }

    private static void DrawDashed(Graphics g, RectangleF rect, Color color, string text, Font font)
    {
        using var pen = new Pen(color, 1.3f) { DashStyle = DashStyle.Dash };
        g.DrawRoundedRectangle(pen, rect, 5);
        DrawText(g, text, font, color, rect, ContentAlignment.MiddleCenter);
    }

    private static void DrawThinSlot(Graphics g, RectangleF rect, Color color, string text)
    {
        using var brush = new SolidBrush(Color.FromArgb(color.R / 3, color.G / 2, color.B / 2));
        using var pen = new Pen(color, 1.1f);
        g.FillRectangle(brush, rect);
        g.DrawRectangle(pen, rect);
        if (text.Length > 0)
        {
            using var font = new Font("Segoe UI Semibold", 7);
            DrawText(g, text, font, Color.White, rect, ContentAlignment.MiddleCenter);
        }
    }

    private static void DrawCentered(Graphics g, string text, Rectangle bounds, Color color, float size)
    {
        using var font = new Font("Segoe UI", size);
        DrawText(g, text, font, color, bounds, ContentAlignment.MiddleCenter);
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds, ContentAlignment align)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align is ContentAlignment.MiddleLeft ? StringAlignment.Near :
                align is ContentAlignment.MiddleRight ? StringAlignment.Far : StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        g.DrawString(text, font, brush, bounds, format);
    }
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, RectangleF bounds, float radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.FillPath(brush, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, Rectangle bounds, int radius)
    {
        using var path = Rounded(bounds, radius);
        graphics.DrawPath(pen, path);
    }

    public static void DrawRoundedRectangle(this Graphics graphics, Pen pen, RectangleF bounds, float radius)
    {
        using var path = Rounded(bounds, radius);
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
