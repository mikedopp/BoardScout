using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using BoardScout.Models;

namespace BoardScout.UI;

public sealed class TopologyMapControl : Control
{
    private sealed record Node(string Id, RectangleF Bounds, string Title, string Detail, Color Color, string Tip);

    private readonly List<Node> _nodes = [];
    private readonly ToolTip _toolTip = new()
    {
        InitialDelay = 250,
        ReshowDelay = 100,
        AutoPopDelay = 12000,
        ShowAlways = true
    };
    private ScanManifest? _scan;
    private string? _hoveredId;

    public TopologyMapControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppTheme.Surface;
        MinimumSize = new Size(760, 520);
    }

    public void SetSnapshot(ScanManifest? scan)
    {
        _scan = scan;
        _hoveredId = null;
        Invalidate();
    }

    public void RefreshTheme()
    {
        BackColor = AppTheme.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _nodes.Clear();
        if (_scan is null)
        {
            TextRenderer.DrawText(e.Graphics, "Run a hardware scan to build the bandwidth topology.", Font,
                ClientRectangle, AppTheme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            return;
        }

        var viewport = RectangleF.Inflate(ClientRectangle, -24, -20);
        const float designWidth = 1150;
        const float designHeight = 720;
        var scale = Math.Min(viewport.Width / designWidth, viewport.Height / designHeight);
        var content = new RectangleF(
            viewport.Left + (viewport.Width - designWidth * scale) / 2,
            viewport.Top + (viewport.Height - designHeight * scale) / 2,
            designWidth * scale,
            designHeight * scale);
        RectangleF Box(float x, float y, float w, float h) =>
            new(content.X + x * scale, content.Y + y * scale, w * scale, h * scale);
        PointF Point(float x, float y) => new(content.X + x * scale, content.Y + y * scale);

        using var titleFont = new Font("Segoe UI Semibold", Math.Max(9, 12 * scale));
        using var detailFont = new Font("Segoe UI", Math.Max(7.5f, 9 * scale));
        using var sectionFont = new Font("Segoe UI Semibold", Math.Max(8, 9 * scale));
        var direct = AppTheme.IsDark ? Color.FromArgb(64, 139, 235) : Color.FromArgb(35, 100, 181);
        var chipset = AppTheme.Purple;
        var storage = AppTheme.Good;
        var usb = AppTheme.IsDark ? Color.FromArgb(47, 196, 185) : Color.FromArgb(18, 127, 120);
        var sata = AppTheme.Warning;
        var memory = AppTheme.IsDark ? Color.FromArgb(235, 194, 53) : Color.FromArgb(163, 124, 8);

        DrawSectionLabel(e.Graphics, "BANDWIDTH TOPOLOGY", Box(0, 0, 290, 24), sectionFont);
        DrawText(e.Graphics, "CPU-direct paths avoid the chipset uplink · hover any node for an explanation",
            detailFont, AppTheme.Muted, Box(300, 0, 850, 24), ContentAlignment.MiddleRight);

        using (var pen = BusPen(direct, scale))
        {
            DrawBus(e.Graphics, pen, Point(575, 105), Point(575, 135), Point(180, 135), Point(180, 165));
            DrawBus(e.Graphics, pen, Point(575, 105), Point(575, 165));
        }
        using (var pen = BusPen(memory, scale))
            DrawBus(e.Graphics, pen, Point(725, 70), Point(835, 70));
        using (var pen = BusPen(chipset, scale))
        {
            DrawBus(e.Graphics, pen, Point(575, 245), Point(575, 315));
            DrawBus(e.Graphics, pen, Point(420, 405), Point(420, 455));
            DrawBus(e.Graphics, pen, Point(455, 405), Point(155, 425), Point(155, 455));
            DrawBus(e.Graphics, pen, Point(485, 405), Point(155, 520), Point(155, 565));
            DrawBus(e.Graphics, pen, Point(675, 405), Point(935, 425));
            DrawBus(e.Graphics, pen, Point(700, 405), Point(935, 515));
        }
        using (var pen = BusPen(usb, scale))
            DrawBus(e.Graphics, pen, Point(610, 405), Point(610, 595));
        using (var pen = BusPen(sata, scale))
            DrawBus(e.Graphics, pen, Point(725, 385), Point(1090, 385), Point(1090, 605), Point(935, 605));

        DrawBadge(e.Graphics, "PCIe 3.0 x16 · 15.8 GB/s", Point(185, 130), direct, detailFont);
        DrawBadge(e.Graphics, "PCIe 3.0 x4 · 3.9 GB/s", Point(575, 132), direct, detailFont);
        DrawBadge(e.Graphics, "DDR4 dual channel", Point(778, 65), memory, detailFont);
        DrawBadge(e.Graphics, "PCIe 3.0 x4 uplink · 3.9 GB/s shared", Point(575, 275), chipset, detailFont);
        DrawBadge(e.Graphics, "x2 · 1.97 GB/s", Point(420, 430), chipset, detailFont);
        DrawBadge(e.Graphics, "x1", Point(790, 420), chipset, detailFont);
        DrawBadge(e.Graphics, "USB", Point(610, 548), usb, detailFont);

        var cpu = _scan.Cpu;
        AddNode(e.Graphics, "cpu", Box(425, 30, 300, 80), ShortCpu(cpu.Name),
            $"{cpu.Cores} cores / {cpu.Threads} threads · 24 PCIe lanes", direct,
            "The 5700G provides CPU-direct lanes for graphics and M2_1, plus the chipset uplink.", titleFont, detailFont);

        var firstMemory = _scan.Memory.Slots.FirstOrDefault();
        AddNode(e.Graphics, "memory", Box(835, 32, 280, 76),
            $"DDR4 · {_scan.Memory.Populated}×{_scan.TotalMemoryGb / Math.Max(1, _scan.Memory.Populated):0} GB",
            firstMemory is null ? "Speed unavailable" : $"{firstMemory.SpeedMhz} active / {firstMemory.RatedMhz} rated MT/s",
            memory, "Memory is directly attached to the CPU. Enabling the rated memory profile can improve CPU and integrated-graphics performance.",
            titleFont, detailFont);

        var gpu = _scan.Components.FirstOrDefault(c => c.Category == "gpu");
        AddNode(e.Graphics, "gpu", Box(35, 165, 290, 80), gpu?.Model ?? "Graphics slot",
            "PCIE1 · Gen3 x16 · CPU direct", storage,
            "The Ryzen 7 5700G limits this motherboard graphics slot to PCIe 3.0 x16. The RTX 3050 is installed here.",
            titleFont, detailFont);

        var drives = _scan.Components.Where(c => c.Category == "storage").ToList();
        var nvme = drives.Where(c => string.Equals(c.LookupHints.BusType, "NVMe", StringComparison.OrdinalIgnoreCase)).ToList();
        AddNode(e.Graphics, "m2-1", Box(420, 165, 310, 80), nvme.ElementAtOrDefault(0)?.Model ?? "M2_1 open",
            "M2_1 · Gen3 x4 · CPU direct", storage,
            "M2_1 is the fastest native storage socket with the 5700G: PCIe 3.0 x4 and no chipset-uplink sharing.",
            titleFont, detailFont);

        AddNode(e.Graphics, "chipset", Box(420, 315, 310, 90), "AMD B550 chipset",
            "PCIe/SATA/USB hub · 3.9 GB/s CPU uplink", chipset,
            "Devices below this point can operate at their own link speeds but share the chipset-to-CPU uplink when active together.",
            titleFont, detailFont);

        AddNode(e.Graphics, "pcie2", Box(35, 455, 240, 66), "PCIE2 · OPEN",
            "PCIe 3.0 x1 · chipset", AppTheme.Muted,
            "Good for a low-bandwidth expansion card such as sound, USB, or networking. Not recommended for NVMe storage.",
            titleFont, detailFont, dashed: true);
        AddNode(e.Graphics, "pcie3", Box(35, 565, 240, 76), "PCIE3 · OPEN",
            "PCIe 3.0 x4 in x16 body", direct,
            "Best storage expansion slot. Use a single-drive PCIe-to-M.2 adapter, or an active multi-drive card with its own PCIe switch.",
            titleFont, detailFont, dashed: true);
        AddNode(e.Graphics, "m2-2", Box(295, 455, 260, 80), nvme.ElementAtOrDefault(1)?.Model ?? "M2_2 open",
            "M2_2 · Gen3 x2 · disables SATA 5/6", storage,
            "M2_2 is limited to PCIe 3.0 x2. While occupied, SATA3_5 and SATA3_6 are unavailable.",
            titleFont, detailFont);

        var wifi = FindWifi(_scan);
        AddNode(e.Graphics, "wifi", Box(790, 425, 310, 72), wifi?.Model ?? "M2_3 Key-E open",
            "M2_3 · Key-E · PCIe WiFi + USB Bluetooth", storage,
            "This is the wireless-card socket. Key-E is not compatible with an NVMe Key-M SSD.",
            titleFont, detailFont);
        var lan = _scan.Components.FirstOrDefault(c => c.Category == "network" &&
            !ReferenceEquals(c, wifi) && Regex.IsMatch(c.Model, "Ethernet|GbE|Realtek", RegexOptions.IgnoreCase));
        AddNode(e.Graphics, "lan", Box(790, 515, 310, 70), lan?.Model ?? "Onboard LAN",
            "PCIe 3.0 x1 · up to 2.5 Gb/s", storage,
            "The onboard Ethernet controller is chipset-connected and does not occupy a user-accessible expansion slot.",
            titleFont, detailFont);
        var sataDrives = drives.Count(c => string.Equals(c.LookupHints.BusType, "SATA", StringComparison.OrdinalIgnoreCase));
        AddNode(e.Graphics, "sata", Box(790, 605, 310, 70), "SATA storage",
            $"{sataDrives} installed · ports 5/6 disabled by M2_2", sata,
            "SATA ports 1–4 remain usable. Two currently contain SSDs, leaving two likely available after cabling is verified.",
            titleFont, detailFont);
        AddNode(e.Graphics, "usb", Box(445, 595, 330, 80), "Chipset USB controllers",
            $"{_scan.UsbDevices.Count(d => d.DeviceClass != "USB")} attached devices · shared bandwidth", usb,
            "External storage is convenient but devices on the same controller can share bandwidth. Powered enclosures are preferable for multiple drives.",
            titleFont, detailFont);

        DrawText(e.Graphics, "M2_3 / Key-E is the AX210 wireless slot — removing it does not create a third NVMe socket.",
            sectionFont, AppTheme.Accent, Box(280, 686, 820, 24), ContentAlignment.MiddleCenter);

        var hovered = _nodes.FirstOrDefault(n => n.Id == _hoveredId);
        if (hovered is not null)
        {
            using var glow = new Pen(hovered.Color, Math.Max(2, 3 * scale));
            e.Graphics.DrawRoundedRectangle(glow, RectangleF.Inflate(hovered.Bounds, 4, 4), 9);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var node = _nodes.LastOrDefault(n => n.Bounds.Contains(e.Location));
        if (node?.Id == _hoveredId) return;
        _hoveredId = node?.Id;
        Cursor = node is null ? Cursors.Default : Cursors.Hand;
        _toolTip.SetToolTip(this, node is null ? "" : $"{node.Title}\n{node.Detail}\n{node.Tip}");
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hoveredId = null;
        Cursor = Cursors.Default;
        _toolTip.SetToolTip(this, "");
        Invalidate();
    }

    private void AddNode(Graphics g, string id, RectangleF rect, string title, string detail, Color color,
        string tip, Font titleFont, Font detailFont, bool dashed = false)
    {
        var fill = AppTheme.IsDark ? Color.FromArgb(24, 39, 45) : Color.FromArgb(244, 249, 248);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(color, 1.5f) { DashStyle = dashed ? DashStyle.Dash : DashStyle.Solid };
        g.FillRoundedRectangle(brush, rect, 8);
        g.DrawRoundedRectangle(pen, rect, 8);
        DrawText(g, title, titleFont, AppTheme.Text,
            new RectangleF(rect.X + 10, rect.Y + 6, rect.Width - 20, rect.Height * 0.48f), ContentAlignment.MiddleCenter);
        DrawText(g, detail, detailFont, color,
            new RectangleF(rect.X + 10, rect.Y + rect.Height * 0.47f, rect.Width - 20, rect.Height * 0.43f), ContentAlignment.MiddleCenter);
        _nodes.Add(new Node(id, rect, title, detail, color, tip));
    }

    private static Pen BusPen(Color color, float scale) => new(color, Math.Max(1.3f, 2.2f * scale))
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    private static void DrawBus(Graphics g, Pen pen, params PointF[] points)
    {
        if (points.Length >= 2) g.DrawLines(pen, points);
    }

    private static void DrawBadge(Graphics g, string text, PointF center, Color color, Font font)
    {
        var measured = g.MeasureString(text, font);
        var rect = new RectangleF(center.X - measured.Width / 2 - 7, center.Y - measured.Height / 2 - 2,
            measured.Width + 14, measured.Height + 4);
        using var brush = new SolidBrush(AppTheme.IsDark ? Color.FromArgb(230, 25, 34, 44) : Color.FromArgb(242, 255, 255, 255));
        using var pen = new Pen(color, 1);
        g.FillRoundedRectangle(brush, rect, rect.Height / 2);
        g.DrawRoundedRectangle(pen, rect, rect.Height / 2);
        DrawText(g, text, font, color, rect, ContentAlignment.MiddleCenter);
    }

    private static void DrawSectionLabel(Graphics g, string text, RectangleF rect, Font font)
    {
        DrawText(g, text, font, AppTheme.Accent, rect, ContentAlignment.MiddleLeft);
        using var pen = new Pen(AppTheme.Border, 1);
        g.DrawLine(pen, rect.Right + 10, rect.Top + rect.Height / 2, rect.Right + 150, rect.Top + rect.Height / 2);
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, RectangleF bounds, ContentAlignment align)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = align == ContentAlignment.MiddleLeft ? StringAlignment.Near :
                align == ContentAlignment.MiddleRight ? StringAlignment.Far : StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter
        };
        g.DrawString(text, font, brush, bounds, format);
    }

    private static HardwareComponent? FindWifi(ScanManifest scan) =>
        scan.Components.FirstOrDefault(c => c.Category.Equals("network", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(c.Model, @"Wi-?Fi|Wireless|AX\d{3}", RegexOptions.IgnoreCase));

    private static string ShortCpu(string value) =>
        value.Replace(" with Radeon Graphics", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase);
}
