using System.Drawing.Drawing2D;
using System.Text.RegularExpressions;
using BoardScout.Models;

namespace BoardScout.UI;

public enum PartStatusTone { Info, Good, Warning, Critical, Muted }

public sealed record BoardPartDetails(
    string Id,
    string Category,
    string Title,
    string Status,
    string Detail,
    string Capability,
    PartStatusTone Tone,
    string? OfficialUrl = null);

public sealed class BoardMapControl : Control
{
    private sealed record HitRegion(RectangleF Bounds, string Id, string Type, int Index = -1);

    private readonly List<HitRegion> _regions = [];
    private readonly ToolTip _toolTip = new()
    {
        InitialDelay = 350,
        ReshowDelay = 100,
        AutoPopDelay = 10000,
        ShowAlways = true
    };

    private ScanManifest? _snapshot;
    private DriverReport? _report;
    private SystemTelemetry? _telemetry;
    private string? _hoveredId;
    private float _zoom = 1f;
    private PointF _pan;
    private Point _dragStart;
    private PointF _panStart;
    private bool _dragging;

    public BoardMapControl()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = AppTheme.Surface;
        MinimumSize = new Size(620, 440);
        TabStop = true;
    }

    public event EventHandler<BoardPartDetails?>? PartHovered;
    public event EventHandler? ZoomChanged;

    public int ZoomPercent => (int)Math.Round(_zoom * 100);

    public void SetSnapshot(ScanManifest? snapshot)
    {
        _snapshot = snapshot;
        _hoveredId = null;
        _regions.Clear();
        Invalidate();
    }

    public void SetDriverReport(DriverReport? report)
    {
        _report = report;
        RefreshHoveredDetails();
        Invalidate();
    }

    public void SetTelemetry(SystemTelemetry telemetry)
    {
        _telemetry = telemetry;
        RefreshHoveredDetails();
        Invalidate();
    }

    public void ZoomIn() => SetZoom(_zoom + 0.2f);
    public void ZoomOut() => SetZoom(_zoom - 0.2f);

    public void ResetView()
    {
        _zoom = 1f;
        _pan = PointF.Empty;
        ZoomChanged?.Invoke(this, EventArgs.Empty);
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
        _regions.Clear();

        var viewport = RectangleF.Inflate(ClientRectangle, -22, -22);
        if (_snapshot is null)
        {
            DrawCentered(e.Graphics, "Run a hardware scan to build the interactive motherboard map.",
                Rectangle.Round(viewport), AppTheme.Muted, 12);
            return;
        }

        const float designAspect = 1120f / 720f;
        var fittedWidth = viewport.Width;
        var fittedHeight = fittedWidth / designAspect;
        if (fittedHeight > viewport.Height)
        {
            fittedHeight = viewport.Height;
            fittedWidth = fittedHeight * designAspect;
        }
        var width = fittedWidth * _zoom;
        var height = fittedHeight * _zoom;
        var board = new RectangleF(
            viewport.Left + (viewport.Width - width) / 2 + _pan.X,
            viewport.Top + (viewport.Height - height) / 2 + _pan.Y,
            width,
            height);

        DrawBoard(e.Graphics, board, _snapshot);
        DrawHover(e.Graphics);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        SetZoom(_zoom + (e.Delta > 0 ? 0.15f : -0.15f));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left || _zoom <= 1.01f) return;
        _dragging = true;
        _dragStart = e.Location;
        _panStart = _pan;
        Capture = true;
        Cursor = Cursors.SizeAll;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
        UpdateHover(e.Location);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _pan = new PointF(
                _panStart.X + e.X - _dragStart.X,
                _panStart.Y + e.Y - _dragStart.Y);
            ClampPan();
            Invalidate();
            return;
        }
        UpdateHover(e.Location);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_dragging) return;
        _hoveredId = null;
        Cursor = Cursors.Default;
        _toolTip.SetToolTip(this, "");
        PartHovered?.Invoke(this, null);
        Invalidate();
    }

    private void SetZoom(float value)
    {
        var next = Math.Clamp(value, 1f, 2.5f);
        if (Math.Abs(next - _zoom) < 0.001f) return;
        _zoom = next;
        if (_zoom <= 1.01f) _pan = PointF.Empty;
        ClampPan();
        ZoomChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void ClampPan()
    {
        if (_zoom <= 1.01f)
        {
            _pan = PointF.Empty;
            return;
        }
        var maxX = ClientSize.Width * (_zoom - 1) / 2 + 60;
        var maxY = ClientSize.Height * (_zoom - 1) / 2 + 60;
        _pan = new PointF(Math.Clamp(_pan.X, -maxX, maxX), Math.Clamp(_pan.Y, -maxY, maxY));
    }

    private void UpdateHover(Point location)
    {
        var region = _regions.LastOrDefault(r => r.Bounds.Contains(location));
        if (region?.Id == _hoveredId)
        {
            Cursor = region is null ? Cursors.Default : Cursors.Hand;
            return;
        }

        _hoveredId = region?.Id;
        Cursor = region is null ? Cursors.Default : Cursors.Hand;
        var details = region is null ? null : CreateDetails(region);
        _toolTip.SetToolTip(this, details is null
            ? ""
            : $"{details.Title}\n{details.Status}\n{details.Capability}");
        PartHovered?.Invoke(this, details);
        Invalidate();
    }

    private void RefreshHoveredDetails()
    {
        if (_hoveredId is null) return;
        var region = _regions.FirstOrDefault(r => r.Id == _hoveredId);
        if (region is not null) PartHovered?.Invoke(this, CreateDetails(region));
    }

    private void DrawBoard(Graphics g, RectangleF bounds, ScanManifest scan)
    {
        var boardFill = AppTheme.IsDark ? Color.FromArgb(18, 29, 38) : Color.FromArgb(251, 252, 253);
        var boardBorder = AppTheme.IsDark ? Color.FromArgb(58, 78, 92) : Color.FromArgb(196, 207, 218);
        var gridColor = AppTheme.IsDark ? Color.FromArgb(31, 46, 57) : Color.FromArgb(238, 242, 245);
        using var boardBrush = new SolidBrush(boardFill);
        using var boardPen = new Pen(boardBorder, 1.5f);
        g.FillRoundedRectangle(boardBrush, bounds, 14);
        g.DrawRoundedRectangle(boardPen, bounds, 14);

        var scaleX = bounds.Width / 1120f;
        var scaleY = bounds.Height / 720f;
        var fontScale = Math.Min(scaleX, scaleY);
        RectangleF Box(float x, float y, float w, float h) =>
            new(bounds.Left + x * scaleX, bounds.Top + y * scaleY, w * scaleX, h * scaleY);

        using var labelFont = new Font("Segoe UI", Math.Max(9.5f, 10 * fontScale));
        using var smallFont = new Font("Segoe UI", Math.Max(8.5f, 9 * fontScale));
        using var titleFont = new Font("Segoe UI Semibold", Math.Max(10.5f, 11.5f * fontScale));
        using var traceFont = new Font("Segoe UI", Math.Max(7.5f, 8 * fontScale));

        DrawDotPattern(g, bounds, scaleX, scaleY, gridColor);
        DrawCircuitTraces(g, bounds, scaleX, scaleY, fontScale, traceFont);

        DrawTextFit(g, "BOARD LAYOUT · VERIFIED CONNECTORS", smallFont, AppTheme.Accent,
            Box(24, 10, 350, 22), ContentAlignment.MiddleLeft);
        DrawTextFit(g, $"{scan.SystemInfo.Baseboard.Product} · {scan.FormFactor.ToUpperInvariant()}", titleFont,
            AppTheme.Muted, Box(700, 10, 390, 24), ContentAlignment.MiddleRight);

        var ioRect = Box(25, 42, 245, 112);
        DrawBox(g, ioRect,
            AppTheme.IsDark ? Color.FromArgb(38, 49, 60) : Color.FromArgb(236, 239, 243),
            AppTheme.IsDark ? Color.FromArgb(99, 118, 134) : Color.FromArgb(167, 178, 189),
            "REAR I/O", "DP · HDMI · USB-C · USB-A · 2.5G LAN · audio", labelFont, smallFont);
        AddRegion(ioRect, "rear-io", "rear-io");

        var heatsinkGroove = AppTheme.IsDark ? Color.FromArgb(37, 83, 98) : Color.FromArgb(204, 225, 231);
        DrawHeatsink(g, Box(285, 42, 250, 35), boardBorder, heatsinkGroove);
        DrawHeatsink(g, Box(275, 72, 36, 165), boardBorder, heatsinkGroove);

        var cpu = scan.Cpu;
        var cpuRect = Box(330, 82, 225, 166);
        var cpuLive = _telemetry is null ? $"{cpu.Cores} cores · {cpu.Threads} threads" :
            $"{cpu.Cores} cores · {cpu.Threads} threads · {_telemetry.CpuUsagePercent:0}% live";
        DrawBox(g, cpuRect, AppTheme.AccentSoft, AppTheme.Accent,
            ShortCpu(cpu.Name), cpuLive, titleFont, labelFont);
        if (_telemetry is not null) DrawUsageBar(g, cpuRect, _telemetry.CpuUsagePercent, AppTheme.Accent);
        AddRegion(cpuRect, "cpu", "cpu");

        var dimmCount = Math.Clamp(Math.Max(scan.Memory.TotalSlots, Math.Max(scan.Memory.Slots.Count, 2)), 2, 8);
        var dimmGroup = Box(800, 45, Math.Min(245, dimmCount * 36 + 80), 215);
        DrawTextFit(g,
            _telemetry is null
                ? $"MEMORY · {scan.Memory.Populated}/{scan.Memory.TotalSlots} · {scan.TotalMemoryGb:0.#} GB"
                : $"MEMORY · {_telemetry.MemoryUsedGb:0.0}/{scan.TotalMemoryGb:0.#} GB live",
            titleFont, AppTheme.Muted, Box(785, 38, 285, 26), ContentAlignment.MiddleCenter);
        AddRegion(dimmGroup, "memory", "memory");
        for (var i = 0; i < dimmCount; i++)
        {
            var occupied = i < scan.Memory.Slots.Count;
            var rect = Box(830 + i * 34, 76, 25, 166);
            DrawThinSlot(g, rect, occupied ? AppTheme.Accent : boardBorder,
                occupied ? $"{scan.Memory.Slots[i].CapacityGb:0} GB" : "OPEN", smallFont);
            AddRegion(rect, $"memory-{i}", "memory", i);
        }

        var storage = scan.Components.Where(c => c.Category == "storage").ToList();
        var nvme = storage.Where(c => string.Equals(c.LookupHints.BusType, "NVMe", StringComparison.OrdinalIgnoreCase)).ToList();
        for (var i = 0; i < Math.Max(2, nvme.Count); i++)
        {
            var rect = i == 0 ? Box(120, 280, 410, 49) : Box(285, 530, 365, 48);
            var slotName = i == 0 ? "M2_1 · KEY M · PCIe 3.0 x4" : "M2_2 · KEY M · PCIe 3.0 x2";
            if (i < nvme.Count)
            {
                DrawBox(g, rect,
                    AppTheme.IsDark ? Color.FromArgb(24, 62, 49) : Color.FromArgb(231, 244, 237),
                    AppTheme.Good, slotName, nvme[i].Model, labelFont, smallFont);
                AddRegion(rect, $"nvme-{i}", "nvme", storage.IndexOf(nvme[i]));
            }
            else
            {
                DrawDashed(g, rect, AppTheme.Accent, $"{slotName} · OPEN", labelFont);
                AddRegion(rect, $"nvme-open-{i}", "nvme-open", i);
            }
        }

        var gpu = scan.Components.FirstOrDefault(c => c.Category == "gpu");
        var pcieY = 350;
        if (gpu is not null)
        {
            var gpuRect = Box(55, pcieY, 570, 52);
            DrawBox(g, gpuRect, AppTheme.AccentSoft, AppTheme.Accent,
                "PCIE1 · PCIe 3.0 x16 · GRAPHICS", gpu.Model, titleFont, labelFont);
            AddRegion(gpuRect, "gpu", "gpu", scan.Components.IndexOf(gpu));
            pcieY += 78;
        }
        if (scan.FormFactor != "mini-itx")
        {
            var x1Rect = Box(55, pcieY, 245, 32);
            DrawDashed(g, x1Rect, AppTheme.Purple, "PCIE2 · PCIe 3.0 x1 · OPEN", smallFont);
            AddRegion(x1Rect, "pcie-open-x1", "pcie-open", 0);

            var x4Rect = Box(55, 595, 570, 38);
            DrawDashed(g, x4Rect, AppTheme.Accent, "PCIE3 · PCIe 3.0 x4 in x16 body · BEST NVMe EXPANSION", labelFont);
            AddRegion(x4Rect, "pcie-open-x4", "pcie-open", 1);
        }

        var wifi = FindWifi(scan);
        var wifiRect = Box(55, 485, 205, 78);
        DrawBox(g, wifiRect,
            AppTheme.IsDark ? Color.FromArgb(24, 52, 74) : Color.FromArgb(229, 241, 250),
            AppTheme.Accent,
            "M2_3 · KEY E · WiFi/BT", wifi?.Model ?? "Type 2230 socket · open", labelFont, smallFont);
        AddRegion(wifiRect, "wifi-key-e", "wifi-slot", wifi is null ? -1 : scan.Components.IndexOf(wifi));

        var chipsetComponent = scan.Components.FirstOrDefault(c => c.Category == "chipset");
        var chipsetName = chipsetComponent?.Model ?? "Chipset";
        var chipsetRect = Box(590, 385, 175, 118);
        DrawBox(g, chipsetRect,
            AppTheme.IsDark ? Color.FromArgb(50, 40, 67) : Color.FromArgb(240, 237, 247),
            AppTheme.Purple,
            chipsetName.Replace("AMD ", "").Replace("Intel ", ""), "platform controller", titleFont, smallFont);
        AddRegion(chipsetRect, "chipset", "chipset",
            chipsetComponent is null ? -1 : scan.Components.IndexOf(chipsetComponent));

        var sata = storage.Where(c => string.Equals(c.LookupHints.BusType, "SATA", StringComparison.OrdinalIgnoreCase)).ToList();
        DrawTextFit(g, "SATA3 · 6 Gb/s", titleFont, AppTheme.Muted, Box(845, 285, 225, 24), ContentAlignment.MiddleCenter);
        for (var i = 0; i < 6; i++)
        {
            var rect = Box(850, 315 + i * 42, 220, 34);
            var disabledByM2 = IsB550MSteelLegend(scan) && nvme.Count >= 2 && i >= 4;
            if (disabledByM2)
            {
                DrawDashed(g, rect, AppTheme.Muted, $"Port {i + 1} · disabled by M2_2", smallFont);
                AddRegion(rect, $"sata-disabled-{i}", "sata-disabled", i);
            }
            else if (i < sata.Count)
            {
                DrawPortBox(g, rect, $"{i + 1}", sata[i].Model, smallFont);
                AddRegion(rect, $"sata-{i}", "sata", storage.IndexOf(sata[i]));
            }
            else
            {
                DrawDashed(g, rect, AppTheme.Warning, $"Port {i + 1} · open", smallFont);
                AddRegion(rect, $"sata-open-{i}", "sata-open", i);
            }
        }

        DrawHeaderGroup(g, Box(700, 585, 365, 65), smallFont);
        DrawFanGroup(g, Box(655, 90, 115, 175), smallFont);

        var boardIdentityRect = Box(710, 665, 380, 30);
        DrawTextFit(g, "M2_3 is WiFi/BT only · it cannot accept an NVMe SSD", titleFont,
            AppTheme.Accent, boardIdentityRect, ContentAlignment.MiddleRight);
        AddRegion(boardIdentityRect, "motherboard", "motherboard");
    }

    private void AddRegion(RectangleF bounds, string id, string type, int index = -1) =>
        _regions.Add(new HitRegion(bounds, id, type, index));

    private void DrawHover(Graphics g)
    {
        if (_hoveredId is null) return;
        var region = _regions.FirstOrDefault(r => r.Id == _hoveredId);
        if (region is null) return;
        var rect = RectangleF.Inflate(region.Bounds, 4, 4);
        using var fill = new SolidBrush(Color.FromArgb(AppTheme.IsDark ? 42 : 24, AppTheme.Accent));
        using var border = new Pen(AppTheme.Accent, 2.5f);
        g.FillRoundedRectangle(fill, rect, 8);
        g.DrawRoundedRectangle(border, rect, 8);
    }

    private BoardPartDetails CreateDetails(HitRegion region)
    {
        if (_snapshot is null) return EmptyDetails(region.Id);
        var scan = _snapshot;
        return region.Type switch
        {
            "cpu" => CpuDetails(scan, region.Id),
            "memory" => MemoryDetails(scan, region),
            "gpu" => ComponentDetails(scan.Components.ElementAtOrDefault(region.Index), region.Id, "Graphics", GpuCapability),
            "chipset" => ComponentDetails(scan.Components.ElementAtOrDefault(region.Index), region.Id, "Chipset",
                _ => "Coordinates PCIe, storage, USB, and platform I/O. It affects expansion and connectivity more than raw compute speed."),
            "motherboard" => ComponentDetails(scan.Components.FirstOrDefault(c => c.Category == "bios"), region.Id,
                "Motherboard firmware",
                _ => "BIOS firmware initializes the board, CPU, memory, storage, security, and boot process. Update only for a needed fix, compatibility change, or security release."),
            "wifi-slot" => WifiDetails(scan, region),
            "nvme" or "sata" => StorageDetails(scan.Components.Where(c => c.Category == "storage").ElementAtOrDefault(region.Index), region),
            "rear-io" => new BoardPartDetails(region.Id, "CONNECTIVITY", "Rear I/O",
                $"{scan.UsbDevices.Count(d => d.DeviceClass != "USB")} attached USB devices",
                "Detected display, USB, network, and audio connectivity.",
                "Connects external devices. Port speed and display capability depend on the exact motherboard headers and controllers.",
                PartStatusTone.Good),
            "nvme-open" => OpenSlot(region, "M.2 slot", "Can accept compatible compact NVMe or SATA storage; exact key and lane support should be checked in the board manual."),
            "sata-open" => OpenSlot(region, $"SATA port {region.Index + 1}", "Can connect a compatible SATA SSD, hard drive, or optical drive."),
            "sata-disabled" => new BoardPartDetails(region.Id, "LANE SHARING", $"SATA port {region.Index + 1}",
                "Unavailable while M2_2 is occupied",
                "On the B550M Steel Legend, M2_2 shares lanes with SATA3_5 and SATA3_6.",
                "Remove the M2_2 drive to restore these two SATA ports; otherwise use SATA ports 1–4, PCIe expansion, or external USB storage.",
                PartStatusTone.Muted),
            "pcie-open" => PcieSlotDetails(scan, region),
            _ => EmptyDetails(region.Id)
        };
    }

    private BoardPartDetails CpuDetails(ScanManifest scan, string id)
    {
        var cpu = scan.Cpu;
        var usage = _telemetry?.CpuUsagePercent ?? 0;
        var tone = usage >= 95 ? PartStatusTone.Critical : usage >= 85 ? PartStatusTone.Warning : PartStatusTone.Good;
        var status = _telemetry is null ? "Detected · live usage starting" : $"Live usage · {usage:0}%";
        var capability = cpu.Cores switch
        {
            >= 16 => "Workstation-class core count for heavy rendering, compiling, simulation, and parallel workloads.",
            >= 8 => "Strong multi-core capacity for gaming, content creation, streaming, and heavy multitasking.",
            >= 6 => "Well-balanced capacity for modern gaming, productivity, and everyday creative work.",
            >= 4 => "Suitable for everyday productivity, media, and moderate multitasking.",
            _ => "Best suited to light everyday workloads."
        };
        return new BoardPartDetails(id, "PROCESSOR", ShortCpu(cpu.Name), status,
            $"{cpu.Cores} physical cores · {cpu.Threads} logical threads", capability, tone);
    }

    private BoardPartDetails MemoryDetails(ScanManifest scan, HitRegion region)
    {
        var first = scan.Memory.Slots.FirstOrDefault();
        var used = _telemetry?.MemoryUsedGb;
        var percent = _telemetry?.MemoryUsagePercent ?? 0;
        var status = used.HasValue ? $"Live usage · {used:0.0} of {scan.TotalMemoryGb:0.#} GB ({percent:0}%)" : "Detected · live usage starting";
        var tone = percent >= 92 ? PartStatusTone.Critical : percent >= 80 ? PartStatusTone.Warning : PartStatusTone.Good;
        var speed = first is null ? "Speed unavailable" : $"{first.SpeedMhz} MT/s active · {first.RatedMhz} MT/s rated";
        var slot = region.Index >= 0 && region.Index < scan.Memory.Slots.Count
            ? $" · Slot {region.Index + 1}: {scan.Memory.Slots[region.Index].CapacityGb:0.#} GB"
            : "";
        var capability = scan.TotalMemoryGb switch
        {
            >= 64 => "High-capacity memory for large creative projects, virtual machines, datasets, and demanding multitasking.",
            >= 32 => "Comfortable capacity for modern gaming, content creation, development, and heavy multitasking.",
            >= 16 => "Solid mainstream capacity for gaming, office work, and moderate creative workloads.",
            _ => "Usable for light workloads; memory-heavy apps may benefit from more capacity."
        };
        if (first is not null && first.RatedMhz > first.SpeedMhz + 100)
            capability += " Modules are currently running below their reported rated speed; firmware memory-profile settings may be worth reviewing.";
        return new BoardPartDetails(region.Id, "MEMORY", $"System memory{slot}", status,
            $"{scan.Memory.Populated} of {scan.Memory.TotalSlots} slots populated · {speed}", capability, tone);
    }

    private BoardPartDetails StorageDetails(HardwareComponent? component, HitRegion region)
    {
        if (component is null) return EmptyDetails(region.Id);
        var volumes = FindVolumes(component).ToList();
        var total = volumes.Sum(v => v.SizeBytes);
        var free = volumes.Sum(v => v.FreeBytes);
        var usedPercent = total <= 0 ? (double?)null : (total - free) * 100d / total;
        var status = usedPercent.HasValue
            ? $"Capacity · {usedPercent:0}% used · {FormatBytes(free)} free"
            : "Detected · no mounted volume matched";
        var tone = usedPercent >= 95 ? PartStatusTone.Critical : usedPercent >= 85 ? PartStatusTone.Warning : PartStatusTone.Good;
        var bus = component.LookupHints.BusType ?? "Storage";
        var media = component.LookupHints.MediaType ?? "drive";
        var capability = bus.Equals("NVMe", StringComparison.OrdinalIgnoreCase)
            ? "Low-latency storage suited to the operating system, applications, games, project files, and scratch workloads. Actual speed depends on its PCIe generation and lane width."
            : media.Contains("HDD", StringComparison.OrdinalIgnoreCase)
                ? "High-capacity storage suited to archives and bulk data; slower random access than solid-state storage."
                : "SATA-class storage suited to applications, games, and general data. It is typically slower than NVMe but remains responsive as an SSD.";
        return new BoardPartDetails(region.Id, bus.ToUpperInvariant(), component.Model, status,
            $"{media} · {bus} bus" + (total > 0 ? $" · {FormatBytes(total)} mounted" : ""), capability, tone);
    }

    private BoardPartDetails ComponentDetails(
        HardwareComponent? component,
        string id,
        string category,
        Func<HardwareComponent, string> capability)
    {
        if (component is null) return EmptyDetails(id);
        var result = _report?.Results.FirstOrDefault(r =>
            r.ComponentKey.Equals(component.ComponentKey, StringComparison.OrdinalIgnoreCase));
        var (status, tone) = result?.Status switch
        {
            "update-available" => ("Driver update available for review", PartStatusTone.Warning),
            "current" => ("Driver checked · current", PartStatusTone.Good),
            "error" => ("Driver check reported an error", PartStatusTone.Critical),
            "manual-check" => ("Detected · vendor review recommended", PartStatusTone.Info),
            _ => ("Detected · driver not checked", PartStatusTone.Good)
        };
        var version = component.Current.DriverVersion ?? component.Current.Firmware ?? "Version unavailable";
        var officialUrl = result?.DownloadUrl ?? result?.Best.DownloadUrl;
        return new BoardPartDetails(id, category.ToUpperInvariant(), component.Model, status,
            $"Installed version · {version}", capability(component), tone, officialUrl);
    }

    private static BoardPartDetails OpenSlot(HitRegion region, string title, string capability) =>
        new(region.Id, "EXPANSION", title, "Available · physical layout estimated",
            "BoardScout inferred this slot from the reported form factor and detected devices.", capability, PartStatusTone.Info);

    private static BoardPartDetails EmptyDetails(string id) =>
        new(id, "COMPONENT", "Hardware component", "Detected", "Details unavailable.",
            "Run a fresh inventory scan if the hardware changed.", PartStatusTone.Muted);

    private static BoardPartDetails PcieSlotDetails(ScanManifest scan, HitRegion region)
    {
        if (IsB550MSteelLegend(scan) && region.Index == 1)
            return new BoardPartDetails(region.Id, "EXPANSION", "PCIe 3.0 x4 slot (PCIE3)",
                "Appears available · verify physical clearance",
                "This is the lower full-length slot. It is electrically PCIe 3.0 x4 with the installed Ryzen 7 5700G.",
                "A single-drive PCIe-to-M.2 NVMe adapter is a good fit. Cheap passive multi-drive cards may require lane bifurcation the board does not advertise.",
                PartStatusTone.Info);
        if (IsB550MSteelLegend(scan))
            return new BoardPartDetails(region.Id, "EXPANSION", "PCIe 3.0 x1 slot (PCIE2)",
                "Appears available · chipset connected",
                "This short slot supplies one PCIe 3.0 lane.",
                "Suitable for sound, USB, capture, or network cards. It is too narrow to make a good NVMe storage slot.",
                PartStatusTone.Info);
        return OpenSlot(region, region.Index == 0 ? "PCIe expansion slot" : "PCIe x1 slot",
            "Can add compatible expansion hardware such as capture, network, storage, or sound cards. Slot layout is estimated.");
    }

    private BoardPartDetails WifiDetails(ScanManifest scan, HitRegion region)
    {
        var component = region.Index >= 0 ? scan.Components.ElementAtOrDefault(region.Index) : null;
        if (component is null)
            return new BoardPartDetails(region.Id, "WIRELESS SLOT", "M2_3 · M.2 Key-E",
                "Available · type 2230 WiFi/BT only",
                "This socket is electrically and mechanically different from an M.2 Key-M storage socket.",
                "Accepts a compatible WiFi/Bluetooth module. It cannot accept an NVMe or SATA SSD.",
                PartStatusTone.Info);
        var result = _report?.Results.FirstOrDefault(r =>
            r.ComponentKey.Equals(component.ComponentKey, StringComparison.OrdinalIgnoreCase));
        var status = result?.Status == "update-available" ? "Wireless driver update available" :
            result?.Status == "current" ? "Installed · driver current" : "Installed · driver not checked";
        var tone = result?.Status == "update-available" ? PartStatusTone.Warning : PartStatusTone.Good;
        return new BoardPartDetails(region.Id, "WIRELESS SLOT", component.Model, status,
            $"M2_3 · M.2 Key-E type 2230 · driver {component.Current.DriverVersion ?? "unknown"}",
            "WiFi uses PCIe while Bluetooth uses an internal USB connection. Removing this card does not create an NVMe storage slot.",
            tone, result?.DownloadUrl ?? result?.Best.DownloadUrl);
    }

    private static HardwareComponent? FindWifi(ScanManifest scan) =>
        scan.Components.FirstOrDefault(c => c.Category.Equals("network", StringComparison.OrdinalIgnoreCase) &&
            Regex.IsMatch(c.Model, @"Wi-?Fi|Wireless|AX\d{3}", RegexOptions.IgnoreCase));

    private static bool IsB550MSteelLegend(ScanManifest scan) =>
        scan.SystemInfo.Baseboard.Product.Contains("B550M Steel Legend", StringComparison.OrdinalIgnoreCase);

    private IEnumerable<VolumeInfo> FindVolumes(HardwareComponent component)
    {
        if (_snapshot is null) return [];
        var model = Normalize(component.Model);
        return _snapshot.Volumes.Where(v =>
        {
            var disk = Normalize(v.DiskModel ?? "");
            return disk.Length > 0 && (disk.Contains(model) || model.Contains(disk));
        });
    }

    private static string GpuCapability(HardwareComponent component)
    {
        var model = component.Model;
        var match = Regex.Match(model, @"(?:RTX|RX)\s*(\d{4})", RegexOptions.IgnoreCase);
        if (match.Success && int.TryParse(match.Groups[1].Value, out var number))
        {
            var tier = model.Contains("RX", StringComparison.OrdinalIgnoreCase)
                ? (number % 1000) / 100 * 10
                : number % 100;
            if (tier >= 80) return "High-end graphics tier for demanding high-resolution gaming, GPU rendering, and accelerated creative or compute workloads.";
            if (tier >= 70) return "Performance graphics tier for high-refresh gaming, content creation, and GPU-accelerated workloads.";
            if (tier >= 60) return "Mainstream graphics tier for modern gaming and useful acceleration in creative and compute applications.";
            return "Entry graphics tier suited to esports, lighter 1080p gaming, media acceleration, and supported GPU compute workloads.";
        }
        if (model.Contains("integrated", StringComparison.OrdinalIgnoreCase) ||
            model.Contains("UHD", StringComparison.OrdinalIgnoreCase))
            return "Integrated graphics suited to desktop work, media playback, multiple displays, and light 3D workloads.";
        return "Provides hardware-accelerated graphics, media, and supported compute. Actual performance depends on the GPU configuration, workload, power, and cooling.";
    }

    private static string ShortCpu(string value) =>
        value.Replace(" with Radeon Graphics", "", StringComparison.OrdinalIgnoreCase)
            .Replace("AMD ", "", StringComparison.OrdinalIgnoreCase)
            .Replace("Intel(R) ", "", StringComparison.OrdinalIgnoreCase);

    private static string Normalize(string value) =>
        Regex.Replace(value.ToLowerInvariant(), @"[^a-z0-9]", "");

    private static string FormatBytes(long bytes)
    {
        var value = (double)Math.Max(0, bytes);
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.#} {units[unit]}";
    }

    private static void DrawDotPattern(Graphics g, RectangleF bounds, float scaleX, float scaleY, Color baseColor)
    {
        var spacing = Math.Max(24, 42 * Math.Min(scaleX, scaleY));
        var dotSize = Math.Max(1.2f, 2f * Math.Min(scaleX, scaleY));
        using var brush = new SolidBrush(Color.FromArgb(AppTheme.IsDark ? 28 : 40, baseColor));
        for (var x = bounds.Left + spacing; x < bounds.Right - 8; x += spacing)
            for (var y = bounds.Top + spacing; y < bounds.Bottom - 8; y += spacing)
                g.FillEllipse(brush, x - dotSize / 2, y - dotSize / 2, dotSize, dotSize);
    }

    private static void DrawCircuitTraces(Graphics g, RectangleF bounds, float scaleX, float scaleY, float fontScale, Font labelFont)
    {
        PointF Pt(float x, float y) => new(bounds.Left + x * scaleX, bounds.Top + y * scaleY);

        var cpuDirect = AppTheme.IsDark ? Color.FromArgb(50, 160, 235) : Color.FromArgb(26, 120, 218);
        var uplinkColor = AppTheme.IsDark ? Color.FromArgb(45, 195, 180) : Color.FromArgb(13, 140, 128);
        var chipsetColor = AppTheme.IsDark ? Color.FromArgb(150, 120, 230) : Color.FromArgb(120, 60, 220);
        var sataColor = AppTheme.IsDark ? Color.FromArgb(230, 140, 50) : Color.FromArgb(194, 100, 16);

        var w = fontScale;

        DrawGlowTrace(g, [Pt(555, 140), Pt(795, 140)],
            cpuDirect, 3f * w, "DDR4-3200", labelFont);

        DrawGlowTrace(g, [Pt(442, 248), Pt(442, 350)],
            cpuDirect, 3.5f * w, "PCIe ×16", labelFont);

        DrawGlowTrace(g, [Pt(380, 248), Pt(380, 268), Pt(325, 268), Pt(325, 280)],
            cpuDirect, 2.5f * w, "PCIe ×4", labelFont);

        DrawGlowTrace(g, [Pt(520, 248), Pt(520, 340), Pt(677, 340), Pt(677, 385)],
            uplinkColor, 2.5f * w, "×4 uplink", labelFont);

        DrawGlowTrace(g, [Pt(765, 425), Pt(815, 425), Pt(815, 350), Pt(850, 350)],
            sataColor, 1.8f * w, "SATA 6Gb/s", labelFont);

        DrawGlowTrace(g, [Pt(640, 503), Pt(640, 540), Pt(467, 540), Pt(467, 530)],
            chipsetColor, 2f * w, "PCIe ×2", labelFont);

        DrawGlowTrace(g, [Pt(590, 444), Pt(300, 444)],
            chipsetColor, 1.5f * w, "PCIe ×1", labelFont);

        DrawGlowTrace(g, [Pt(620, 503), Pt(620, 583), Pt(340, 583), Pt(340, 595)],
            chipsetColor, 2.5f * w, "PCIe ×4", labelFont);

        DrawGlowTrace(g, [Pt(720, 503), Pt(720, 578), Pt(865, 578), Pt(865, 600)],
            chipsetColor, 1.5f * w, "USB 3.2", labelFont);
    }

    private static void DrawGlowTrace(Graphics g, PointF[] points, Color color, float width, string? label, Font labelFont)
    {
        if (points.Length < 2) return;

        using var path = new GraphicsPath();
        path.AddLines(points);

        using (var pen = new Pen(Color.FromArgb(12, color), width * 5)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawPath(pen, path);

        using (var pen = new Pen(Color.FromArgb(35, color), width * 2.5f)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawPath(pen, path);

        using (var pen = new Pen(Color.FromArgb(190, color), width)
            { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawPath(pen, path);

        if (!string.IsNullOrEmpty(label))
            DrawTraceLabel(g, points, label, color, labelFont);
    }

    private static void DrawTraceLabel(Graphics g, PointF[] points, string text, Color color, Font font)
    {
        var maxLen = 0f;
        var midPt = PointF.Empty;
        for (var i = 0; i < points.Length - 1; i++)
        {
            var dx = points[i + 1].X - points[i].X;
            var dy = points[i + 1].Y - points[i].Y;
            var len = MathF.Sqrt(dx * dx + dy * dy);
            if (len > maxLen)
            {
                maxLen = len;
                midPt = new PointF(
                    (points[i].X + points[i + 1].X) / 2,
                    (points[i].Y + points[i + 1].Y) / 2);
            }
        }

        if (maxLen < 40) return;

        var size = g.MeasureString(text, font);
        var pill = new RectangleF(
            midPt.X - size.Width / 2 - 5,
            midPt.Y - size.Height / 2 - 2,
            size.Width + 10,
            size.Height + 4);

        using var bg = new SolidBrush(Color.FromArgb(210, AppTheme.Surface));
        using var border = new Pen(Color.FromArgb(60, color), 0.8f);
        g.FillRoundedRectangle(bg, pill, 4);
        g.DrawRoundedRectangle(border, pill, 4);

        using var brush = new SolidBrush(Color.FromArgb(200, color));
        using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, midPt, format);
    }

    private static void DrawHeatsink(Graphics g, RectangleF rect, Color border, Color groove)
    {
        using var fill = new LinearGradientBrush(rect,
            AppTheme.IsDark ? Color.FromArgb(83, 94, 105) : Color.FromArgb(220, 226, 232),
            AppTheme.IsDark ? Color.FromArgb(42, 51, 60) : Color.FromArgb(177, 187, 197),
            LinearGradientMode.Horizontal);
        using var outline = new Pen(border, 1.1f);
        using var line = new Pen(groove, 0.8f);
        g.FillRoundedRectangle(fill, rect, 4);
        g.DrawRoundedRectangle(outline, rect, 4);
        for (var x = rect.Left + 8; x < rect.Right - 4; x += 10)
            g.DrawLine(line, x, rect.Top + 3, x, rect.Bottom - 3);
    }

    private static void DrawFanGroup(Graphics g, RectangleF rect, Font font)
    {
        DrawTextFit(g, "FAN HEADERS", font, AppTheme.Muted,
            new RectangleF(rect.X, rect.Y, rect.Width, 22), ContentAlignment.MiddleCenter);
        string[] labels = ["CPU_FAN", "CPU/WP", "CHA_1", "CHA_2", "CHA_3", "CHA_4"];
        for (var i = 0; i < labels.Length; i++)
        {
            var box = new RectangleF(rect.X + 8, rect.Y + 25 + i * 23, rect.Width - 16, 18);
            using var pen = new Pen(AppTheme.Good, 1);
            using var brush = new SolidBrush(Color.FromArgb(AppTheme.IsDark ? 55 : 24, AppTheme.Good));
            g.FillRoundedRectangle(brush, box, 3);
            g.DrawRoundedRectangle(pen, box, 3);
            DrawTextFit(g, labels[i], font, AppTheme.Good, box, ContentAlignment.MiddleCenter);
        }
    }

    private static void DrawHeaderGroup(Graphics g, RectangleF rect, Font font)
    {
        DrawTextFit(g, "INTERNAL HEADERS", font, AppTheme.Muted,
            new RectangleF(rect.X, rect.Y, rect.Width, 20), ContentAlignment.MiddleLeft);
        string[] labels = ["USB3", "USB2", "F_AUDIO", "PANEL", "RGB", "ARGB"];
        var width = (rect.Width - 15) / 3;
        for (var i = 0; i < labels.Length; i++)
        {
            var col = i % 3;
            var row = i / 3;
            var box = new RectangleF(rect.X + col * (width + 5), rect.Y + 24 + row * 21, width, 16);
            var color = labels[i] is "RGB" or "ARGB" ? AppTheme.Purple : AppTheme.Good;
            using var pen = new Pen(color, 1) { DashStyle = DashStyle.Dash };
            g.DrawRoundedRectangle(pen, box, 3);
            DrawTextFit(g, labels[i], font, color, box, ContentAlignment.MiddleCenter);
        }
    }

    private static void DrawBox(
        Graphics g, RectangleF rect, Color fill, Color border, string title, string subtitle, Font titleFont, Font subtitleFont)
    {
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border, 1.5f);
        g.FillRoundedRectangle(brush, rect, 12);
        g.DrawRoundedRectangle(pen, rect, 12);
        var top = new RectangleF(rect.X + 8, rect.Y + 5, rect.Width - 16, rect.Height * 0.46f - 3);
        var bottom = new RectangleF(rect.X + 8, rect.Y + rect.Height * 0.46f, rect.Width - 16, rect.Height * 0.48f - 5);
        DrawTextFit(g, title, titleFont, AppTheme.Text, top, ContentAlignment.MiddleCenter);
        DrawTextFit(g, subtitle, subtitleFont, AppTheme.Muted, bottom, ContentAlignment.MiddleCenter);
    }

    private static void DrawPortBox(Graphics g, RectangleF rect, string port, string model, Font font)
    {
        var fill = AppTheme.IsDark ? Color.FromArgb(65, 48, 25) : Color.FromArgb(250, 241, 229);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(AppTheme.Warning, 1.3f);
        g.FillRoundedRectangle(brush, rect, 10);
        g.DrawRoundedRectangle(pen, rect, 10);
        DrawTextFit(g, port, font, AppTheme.Warning,
            new RectangleF(rect.X + 5, rect.Y + 3, rect.Width * 0.18f, rect.Height - 6), ContentAlignment.MiddleCenter);
        DrawTextFit(g, model, font, AppTheme.Text,
            new RectangleF(rect.X + rect.Width * 0.2f, rect.Y + 3, rect.Width * 0.76f, rect.Height - 6), ContentAlignment.MiddleLeft);
    }

    private static void DrawDashed(Graphics g, RectangleF rect, Color color, string text, Font font)
    {
        using var pen = new Pen(color, 1.3f) { DashStyle = DashStyle.Dash };
        g.DrawRoundedRectangle(pen, rect, 8);
        DrawTextFit(g, text, font, color, RectangleF.Inflate(rect, -5, -3), ContentAlignment.MiddleCenter);
    }

    private static void DrawThinSlot(Graphics g, RectangleF rect, Color color, string text, Font font)
    {
        var fill = AppTheme.IsDark
            ? Color.FromArgb(28, 45, 55)
            : Color.FromArgb(245, 249, 251);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(color, 1.2f);
        g.FillRectangle(brush, rect);
        g.DrawRectangle(pen, rect.X, rect.Y, rect.Width, rect.Height);
        DrawVerticalText(g, text, font, AppTheme.Text, rect);
    }

    private static void DrawUsageBar(Graphics g, RectangleF host, double percent, Color color)
    {
        var track = new RectangleF(host.X + 10, host.Bottom - 10, host.Width - 20, 4);
        using var trackBrush = new SolidBrush(Color.FromArgb(AppTheme.IsDark ? 70 : 34, color));
        using var valueBrush = new SolidBrush(color);
        g.FillRoundedRectangle(trackBrush, track, 2);
        g.FillRoundedRectangle(valueBrush,
            new RectangleF(track.X, track.Y, track.Width * (float)Math.Clamp(percent / 100d, 0, 1), track.Height), 2);
    }

    private static void DrawVerticalText(Graphics g, string text, Font font, Color color, RectangleF bounds)
    {
        var state = g.Save();
        g.TranslateTransform(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        g.RotateTransform(-90);
        DrawTextFit(g, text, font, color,
            new RectangleF(-bounds.Height / 2 + 4, -bounds.Width / 2, bounds.Height - 8, bounds.Width),
            ContentAlignment.MiddleCenter);
        g.Restore(state);
    }

    private static void DrawCentered(Graphics g, string text, Rectangle bounds, Color color, float size)
    {
        using var font = new Font("Segoe UI", size);
        DrawTextFit(g, text, font, color, bounds, ContentAlignment.MiddleCenter);
    }

    private static void DrawTextFit(
        Graphics g, string text, Font font, Color color, RectangleF bounds, ContentAlignment align)
    {
        if (string.IsNullOrWhiteSpace(text) || bounds.Width <= 1 || bounds.Height <= 1) return;
        using var format = new StringFormat
        {
            Alignment = align is ContentAlignment.MiddleLeft ? StringAlignment.Near :
                align is ContentAlignment.MiddleRight ? StringAlignment.Far : StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };
        var measured = g.MeasureString(text, font, int.MaxValue, format);
        var ratio = Math.Min(1f, Math.Min(bounds.Width / Math.Max(1, measured.Width), bounds.Height / Math.Max(1, measured.Height)));
        var size = Math.Max(6.25f, font.Size * ratio);
        using var fitted = Math.Abs(size - font.Size) < 0.05f
            ? (Font)font.Clone()
            : new Font(font.FontFamily, size, font.Style, GraphicsUnit.Point);
        using var brush = new SolidBrush(color);
        g.DrawString(text, fitted, brush, bounds, format);
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
