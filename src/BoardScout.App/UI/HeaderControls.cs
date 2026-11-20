using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

internal enum GaugeKind { Cpu, Memory, Temperature, Network, Disk }

/// <summary>
/// The colour a reading has at each point of its range: cool blues and greens when there is room, through
/// violet, to amber and red near the top. Each kind of reading has its own ramp, so a meter, a graph, and a
/// drive's fill bar can be told apart at a glance and still agree on what "hot" looks like.
/// </summary>
internal static class Heat
{
    private static readonly (float At, Color Color)[] Cpu =
        [(0f, C(34, 211, 238)), (0.45f, C(59, 130, 246)), (0.75f, C(139, 92, 246)), (0.85f, C(245, 158, 11)), (0.95f, C(239, 68, 68))];
    private static readonly (float At, Color Color)[] Memory =
        [(0f, C(129, 140, 248)), (0.45f, C(167, 139, 250)), (0.75f, C(236, 72, 153)), (0.85f, C(245, 158, 11)), (0.95f, C(239, 68, 68))];
    // 20 to 100 degrees C across the range.
    private static readonly (float At, Color Color)[] Temperature =
        [(0.125f, C(90, 167, 255)), (0.3125f, C(92, 214, 196)), (0.5f, C(233, 190, 119)), (0.65f, C(255, 140, 66)), (0.8125f, C(255, 72, 72)), (0.9375f, C(255, 40, 90))];
    private static readonly (float At, Color Color)[] Network =
        [(0f, C(45, 212, 191)), (0.5f, C(56, 189, 248)), (1f, C(167, 139, 250))];
    private static readonly (float At, Color Color)[] Disk =
        [(0f, C(52, 211, 153)), (0.5f, C(45, 212, 191)), (0.8f, C(250, 204, 21)), (0.9f, C(245, 158, 11)), (0.98f, C(239, 68, 68))];

    /// <summary>The colour of a download and of an upload, wherever the two are drawn against each other.</summary>
    public static readonly Color Down = C(56, 189, 248);
    public static readonly Color Up = C(167, 139, 250);

    private static Color C(int r, int g, int b) => Color.FromArgb(r, g, b);

    public static Color At(GaugeKind kind, float fraction)
    {
        var stops = kind switch
        {
            GaugeKind.Memory => Memory,
            GaugeKind.Temperature => Temperature,
            GaugeKind.Network => Network,
            GaugeKind.Disk => Disk,
            _ => Cpu
        };
        if (!(fraction > stops[0].At)) return stops[0].Color;
        for (var i = 1; i < stops.Length; i++)
            if (fraction <= stops[i].At)
                return Glass.Mix(stops[i - 1].Color, stops[i].Color, (fraction - stops[i - 1].At) / (stops[i].At - stops[i - 1].At));
        return stops[^1].Color;
    }
}

/// <summary>
/// A live header instrument, laid out like a channel on a mixing desk: a level meter whose lights climb through
/// the reading's colours and ease to each new value; the reading in figures, with one line of detail under it;
/// and a graph of the last minute and a half whose line takes the colour of its height, with a mark held at the
/// highest point. The network instrument draws download above its centre line and upload below. Every part is a
/// readout: nothing on it looks like a control, because nothing on it can be set.
/// </summary>
internal sealed class GaugeTile : Control
{
    private const int History = 90;
    private static readonly Font CaptionFont = new("Segoe UI Semibold", 7.25f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 17.5f);
    private static readonly Font SmallValueFont = new("Segoe UI Semibold", 13f);
    private static readonly Font UnitFont = new("Segoe UI", 9f);
    private static readonly Font NoteFont = new("Segoe UI", 8.25f);
    private readonly List<float> _primary = [];
    private readonly List<float> _secondary = [];
    private readonly GaugeKind _kind;
    private readonly Func<float, bool> _animate;
    private string _caption;
    private string _value = "—";
    private string _unit = "";
    private string _note = "";
    private float? _level;
    private float? _level2;
    private float _shown;
    private float _shown2;
    private float _meterPeak;
    private float _meterPeak2;
    private Color? _tone;
    private Rectangle _meterBox;
    private Rectangle _bridgeBox;
    private bool _hover;
    private float[] _bars = [];
    private float[] _barsShown = [];
    private float[] _barPeaks = [];

    public GaugeTile(string caption, GaugeKind kind)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        _caption = caption;
        _kind = kind;
        _animate = Animate;
        Size = new Size(280, 76);
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = caption;
    }

    /// <summary>The floor a graph scales to, so a quiet line draws low instead of magnifying noise.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float GraphFloor { get; set; } = 1f;

    /// <summary>False for a reading with no history to draw: the instrument is its meter, figure, and note only.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowGraph { get; set; } = true;

    /// <summary>For a reading where full is the good end (a battery): the colours run from red at empty to green at full.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Invert { get; set; }

    private Color HeatAt(float fraction) => Heat.At(_kind, Invert ? 1f - fraction : fraction);

    /// <summary>The highest reading in the graph's window (0 when there is none yet).</summary>
    public float Peak => _primary.Count == 0 ? 0 : _primary.Max();
    public float SecondaryPeak => _secondary.Count == 0 ? 0 : _secondary.Max();

    /// <summary>
    /// What the instrument shows. <paramref name="level"/> is how far up the meter stands, 0 to 1 (null: no reading,
    /// the meter stays dark); <paramref name="level2"/> is the network meter's second column. <paramref name="tone"/>
    /// marks a reading that needs attention: it colours the figures and the rim.
    /// </summary>
    public void Show(string caption, string value, string unit = "", float? level = null, string note = "", Color? tone = null, float? level2 = null)
    {
        level = level is { } l && float.IsFinite(l) ? Math.Clamp(l, 0, 1) : null;
        level2 = level2 is { } l2 && float.IsFinite(l2) ? Math.Clamp(l2, 0, 1) : null;
        // The held light sinks a little with every reading until the level passes it again.
        _meterPeak = Math.Max(level ?? 0, _meterPeak - 0.02f);
        _meterPeak2 = Math.Max(level2 ?? 0, _meterPeak2 - 0.02f);
        var same = _caption == caption && _value == value && _unit == unit && _note == note && _tone == tone;
        if (same && _level == level && _level2 == level2) { Invalidate(Rectangle.Inflate(_meterBox, 5, 5)); return; }
        _level2 = level2;
        _caption = caption;
        _value = value;
        _unit = unit;
        _note = note;
        _tone = tone;
        _level = level;
        AccessibleName = $"{caption}: {value} {unit}. {note}".Trim();
        if (Motion.Enabled && Visible && IsHandleCreated) Motion.Start(_animate);
        else { _shown = level ?? 0; _shown2 = level2 ?? 0; }
        Invalidate();
    }

    // An instrument that goes somewhere when pressed shows it: the panel catches more light under the pointer.
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Cursor == Cursors.Hand) { _hover = true; Invalidate(); } }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (_hover) { _hover = false; Invalidate(); } }

    /// <summary>Adds a reading to the graph: one series for most instruments, download and upload for the network.</summary>
    public void AddSample(double primary, double secondary = 0)
    {
        _primary.Add((float)Math.Max(0, primary));
        _secondary.Add((float)Math.Max(0, secondary));
        if (_primary.Count > History) { _primary.RemoveAt(0); _secondary.RemoveAt(0); }
        Invalidate();
    }

    /// <summary>
    /// A row of level columns beside the graph, one per part of the whole (a CPU's threads), each 0 to 1. More than
    /// 32 are averaged in groups so the row stays readable. A cap rests at each column's recent high and sinks slowly.
    /// </summary>
    public void SetBars(IReadOnlyList<double>? levels)
    {
        if (levels is null || levels.Count == 0)
        {
            if (_bars.Length > 0) { _bars = []; Invalidate(); }
            return;
        }
        var group = (levels.Count + 31) / 32;
        var count = (levels.Count + group - 1) / group;
        if (_bars.Length != count)
        {
            _bars = new float[count];
            _barsShown = new float[count];
            _barPeaks = new float[count];
        }
        for (var i = 0; i < count; i++)
        {
            double sum = 0;
            var n = 0;
            for (var j = i * group; j < Math.Min(levels.Count, (i + 1) * group); j++, n++) sum += levels[j];
            _bars[i] = (float)Math.Clamp(sum / Math.Max(1, n), 0, 1);
            _barPeaks[i] = Math.Max(_bars[i], _barPeaks[i] - 0.07f);
        }
        if (Motion.Enabled && Visible && IsHandleCreated) Motion.Start(_animate);
        else _bars.CopyTo(_barsShown, 0);
        Invalidate(_bridgeBox);
    }

    // The meter and the per-thread columns ease to each new reading; only they are redrawn while they move.
    private bool Animate(float seconds)
    {
        if (IsDisposed) return false;
        float target = _level ?? 0, target2 = _level2 ?? 0;
        _shown = Motion.Approach(_shown, target, 9f, seconds);
        _shown2 = Motion.Approach(_shown2, target2, 9f, seconds);
        var settled = Math.Abs(_shown - target) < 0.003f && Math.Abs(_shown2 - target2) < 0.003f;
        if (settled) { _shown = target; _shown2 = target2; }
        Invalidate(Rectangle.Inflate(_meterBox, 5, 5));
        for (var i = 0; i < _bars.Length; i++)
        {
            _barsShown[i] = Motion.Approach(_barsShown[i], _bars[i], 12f, seconds);
            if (Math.Abs(_barsShown[i] - _bars[i]) < 0.01f) _barsShown[i] = _bars[i];
            else settled = false;
        }
        if (_bars.Length > 0) Invalidate(_bridgeBox);
        return !settled;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var heat = HeatAt(_shown);
        var body = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Glass.Surface(g, body, 12, _tone ?? heat, tintStrength: (_tone is null ? 0.06f : 0.13f) + (_hover ? 0.05f : 0), sheen: _hover ? 1.1f : 0.7f);
        if (_tone is { } alarm)
        {
            using var rim = new Pen(Color.FromArgb(150, alarm), 1.2f);
            using var path = Glass.RoundedPath(body, 12);
            g.DrawPath(rim, path);
        }

        _meterBox = new Rectangle(15, 12, 14, Height - 25);
        DrawMeter(g, _meterBox);

        // Caption, the reading, and one line of detail, in a column of a fixed width so the graph never shifts.
        var compact = Height < 70;
        var textLeft = _meterBox.Right + 15;
        // With no graph beside it, the text has the rest of the panel.
        var column = ShowGraph ? Math.Min(_kind == GaugeKind.Network ? 138 : 116, Width - textLeft - 10) : Width - textLeft - 12;
        var top = compact ? 6 : 9;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
        TextRenderer.DrawText(g, _caption.ToUpperInvariant(), CaptionFont, new Rectangle(textLeft, top, column, 13), AppTheme.Muted, flags);
        var valueFont = compact || _kind == GaugeKind.Network ? SmallValueFont : ValueFont;
        var valueTop = top + (valueFont == ValueFont ? 10 : 14);
        // Measured without the ellipsis flag: with it, and no width to fit, the measurement is of a clipped text.
        var valueSize = TextRenderer.MeasureText(g, _value, valueFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(g, _value, valueFont, new Rectangle(textLeft - 1, valueTop, column, valueSize.Height), _tone ?? AppTheme.Text, flags);
        if (_unit.Length > 0 && valueSize.Width + 16 < column)
            TextRenderer.DrawText(g, _unit, UnitFont, new Rectangle(textLeft + valueSize.Width + 5, valueTop + valueSize.Height - 22, column - valueSize.Width - 3, 18), AppTheme.Muted, flags);
        if (_note.Length > 0)
            TextRenderer.DrawText(g, _note, NoteFont, new Rectangle(textLeft, Height - (compact ? 20 : 23), column, 15), AppTheme.Muted, flags);

        var well = new RectangleF(textLeft + column + 8, 7.5f, Width - (textLeft + column + 8) - 9.5f, Height - 16f);
        if (!ShowGraph || well.Width < 96) return;
        DrawWell(g, well);
        var graph = RectangleF.Inflate(well, -11, -8);
        var state = g.Save();
        g.SetClip(RectangleF.Inflate(well, -1.5f, -1.5f));
        if (_kind == GaugeKind.Network) DrawMirrored(g, graph);
        else
        {
            _bridgeBox = Rectangle.Empty;
            // The per-thread columns are drawn only when the graph beside them keeps a useful width: a 32-thread
            // processor needs 158 px for its columns however narrow the window is.
            if (_bars.Length > 0 && graph.Width - BridgeWidth(graph.Width).Total - 16 >= 90)
            {
                var used = DrawBridge(g, graph);
                graph = new RectangleF(graph.X + used + 16, graph.Y, graph.Width - used - 16, graph.Height);
            }
            DrawGraph(g, graph);
        }
        g.Restore(state);
    }

    // The meter bridge: a column of eight lights per thread, lit from the bottom in the colours of the range, with
    // a bright cap resting at each column's recent high. Returns the width it took.
    private const float BridgeGap = 2f;

    // How wide each column is, and all of them together, in a graph window of this width: about half of it, with
    // columns never thinner than 3 px or wider than 6.
    private (float Column, float Total) BridgeWidth(float boxWidth)
    {
        var column = Math.Clamp(MathF.Floor((boxWidth * 0.48f + BridgeGap) / Math.Max(1, _bars.Length) - BridgeGap), 3f, 6f);
        return (column, _bars.Length * (column + BridgeGap) - BridgeGap);
    }

    private float DrawBridge(Graphics g, RectangleF box)
    {
        const int rows = 8;
        const float gap = BridgeGap, rowGap = 1.5f;
        var (column, width) = BridgeWidth(box.Width);
        var cell = (box.Height - rowGap * (rows - 1)) / rows;
        _bridgeBox = Rectangle.Inflate(Rectangle.Round(new RectangleF(box.X, box.Y, width, box.Height)), 2, 3);
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(Color.Empty);
        var dark = Color.FromArgb(AppTheme.IsDark ? 22 : 36, AppTheme.Text);
        for (var i = 0; i < _bars.Length; i++)
        {
            var x = MathF.Round(box.X + i * (column + gap));
            var lit = _barsShown[i] <= 0.01f ? 0 : Math.Clamp((int)MathF.Ceiling(_barsShown[i] * rows - 0.35f), 1, rows);
            for (var row = 0; row < rows; row++)
            {
                brush.Color = row < lit ? Heat.At(_kind, (row + 0.5f) / rows) : dark;
                g.FillRectangle(brush, x, box.Bottom - (row + 1) * cell - row * rowGap, column, cell);
            }
            var peak = _barPeaks[i];
            if (peak > _barsShown[i] + 0.1f)
            {
                brush.Color = Glass.Mix(Heat.At(_kind, peak), Color.White, 0.35f);
                g.FillRectangle(brush, x, box.Bottom - peak * box.Height, column, 1.6f);
            }
        }
        g.SmoothingMode = smoothing;
        return width;
    }

    // A level meter, as on a channel of a mixing desk: a column of lights in a shallow recess that fills from the
    // bottom in the colours of the range, with a brighter light held at the recent high. It shows a level and
    // nothing else; there is nothing on it to take hold of or turn. Two readings (download and upload) get a
    // column each.
    private void DrawMeter(Graphics g, RectangleF box)
    {
        DrawWell(g, RectangleF.Inflate(box, 4f, 4f));
        var rows = box.Height >= 50 ? 14 : 11;
        const float rowGap = 1.5f, columnGap = 2f;
        var two = _kind == GaugeKind.Network;
        var width = two ? (box.Width - columnGap) / 2f : box.Width;
        var cell = (box.Height - rowGap * (rows - 1)) / rows;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.None;
        using var brush = new SolidBrush(Color.Empty);
        var dark = Color.FromArgb(AppTheme.IsDark ? 24 : 40, AppTheme.Text);
        for (var column = 0; column < (two ? 2 : 1); column++)
        {
            var has = column == 0 ? _level is not null : _level2 is not null;
            var level = column == 0 ? _shown : _shown2;
            var peak = column == 0 ? _meterPeak : _meterPeak2;
            var x = MathF.Round(box.X + column * (width + columnGap));
            var lit = has && level > 0.004f ? Math.Clamp((int)MathF.Ceiling(level * rows - 0.35f), 1, rows) : 0;
            var held = has && peak > level + 0.06f ? Math.Clamp((int)(peak * rows), 0, rows - 1) : -1;
            for (var row = 0; row < rows; row++)
            {
                // A reading where full is good (a battery) lights every cell in the colour of its level: green when
                // there is plenty, amber and red as it runs down.
                var color = two ? (column == 0 ? Heat.Down : Heat.Up) : Invert ? HeatAt(level) : HeatAt((row + 0.5f) / rows);
                brush.Color = row < lit ? color : row == held ? Glass.Mix(color, Color.White, 0.3f) : dark;
                g.FillRectangle(brush, x, box.Bottom - (row + 1) * cell - row * rowGap, MathF.Round(width), cell);
            }
        }
        g.SmoothingMode = smoothing;
    }
    // The window the graph sits in: a shallow recess, darker than the panel, shaded along its top edge and
    // catching light along its bottom one, like the glass over a meter.
    private static void DrawWell(Graphics g, RectangleF box)
    {
        using var path = Glass.RoundedPath(box, 7);
        using (var fill = new LinearGradientBrush(RectangleF.Inflate(box, 1, 1), Color.FromArgb(AppTheme.IsDark ? 112 : 26, 0, 0, 0), Color.FromArgb(AppTheme.IsDark ? 62 : 12, 0, 0, 0), LinearGradientMode.Vertical))
            g.FillPath(fill, path);
        using var edgeBrush = new LinearGradientBrush(RectangleF.Inflate(box, 1, 1), Color.FromArgb(120, 0, 0, 0), Color.FromArgb(AppTheme.IsDark ? 34 : 90, 255, 255, 255), LinearGradientMode.Vertical);
        using var edge = new Pen(edgeBrush, 1f);
        g.DrawPath(edge, path);
    }

    private static void DrawGuides(Graphics g, RectangleF box, params float[] fractions)
    {
        using var guide = new Pen(Color.FromArgb(AppTheme.IsDark ? 15 : 26, AppTheme.Text), 1f) { DashStyle = DashStyle.Dot };
        foreach (var fraction in fractions)
        {
            var y = MathF.Round(box.Bottom - box.Height * fraction) + 0.5f;
            g.DrawLine(guide, box.Left, y, box.Right, y);
        }
    }

    // One series, newest on the right. The line and the fill under it take their colour from their height, so a
    // climb into the amber and red shows as amber and red; a short mark stays at the highest point in the window.
    private void DrawGraph(Graphics g, RectangleF box)
    {
        DrawGuides(g, box, 0.25f, 0.5f, 0.75f, 1f);
        if (_primary.Count < 2) return;
        var peak = _primary.Max();
        var scale = Math.Max(GraphFloor, peak * 1.1f);
        var step = box.Width / (History - 1);
        var start = box.Right - step * (_primary.Count - 1);
        var points = new PointF[_primary.Count];
        for (var i = 0; i < points.Length; i++)
            points[i] = new PointF(start + i * step, box.Bottom - Math.Clamp(_primary[i] / scale, 0, 1) * box.Height);

        // Top of the box is `scale`, the bottom is zero: a brush that runs the reading's colours down that height.
        ColorBlend Blend(int topAlpha, int bottomAlpha)
        {
            const int steps = 9;
            var colors = new Color[steps];
            var positions = new float[steps];
            for (var i = 0; i < steps; i++)
            {
                var p = i / (float)(steps - 1);
                positions[i] = p;
                colors[i] = Color.FromArgb((int)(topAlpha + (bottomAlpha - topAlpha) * p), Heat.At(_kind, (1 - p) * scale));
            }
            return new ColorBlend { Colors = colors, Positions = positions };
        }
        var brushBox = RectangleF.Inflate(box, 2, 2);

        using (var area = new GraphicsPath())
        {
            area.AddLine(points[0].X, box.Bottom, points[0].X, points[0].Y);
            area.AddCurve(points, 0.3f);
            area.AddLine(points[^1].X, points[^1].Y, points[^1].X, box.Bottom);
            area.CloseFigure();
            using var fill = new LinearGradientBrush(brushBox, Color.Black, Color.Black, LinearGradientMode.Vertical) { InterpolationColors = Blend(96, 4) };
            g.FillPath(fill, area);
        }
        using (var soft = new LinearGradientBrush(brushBox, Color.Black, Color.Black, LinearGradientMode.Vertical) { InterpolationColors = Blend(62, 62) })
        using (var glow = new Pen(soft, 5f) { LineJoin = LineJoin.Round })
            g.DrawCurve(glow, points, 0.3f);
        using (var ink = new LinearGradientBrush(brushBox, Color.Black, Color.Black, LinearGradientMode.Vertical) { InterpolationColors = Blend(255, 255) })
        using (var line = new Pen(ink, 1.8f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawCurve(line, points, 0.3f);

        var now = _primary[^1];
        if (peak - now > scale * 0.06f)
        {
            var y = box.Bottom - Math.Clamp(peak / scale, 0, 1) * box.Height;
            using var mark = new Pen(Heat.At(_kind, peak), 2f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(mark, box.Right - 9, y, box.Right, y);
        }
        DrawHead(g, points[^1], Heat.At(_kind, now));
    }

    // Download rises from the centre line and upload hangs below it, both against the larger of the two.
    private void DrawMirrored(Graphics g, RectangleF box)
    {
        var middle = MathF.Round(box.Top + box.Height * 0.58f) + 0.5f;
        using (var axis = new Pen(Color.FromArgb(AppTheme.IsDark ? 34 : 50, AppTheme.Text), 1f))
            g.DrawLine(axis, box.Left, middle, box.Right, middle);
        if (_primary.Count < 2) return;
        var scale = Math.Max(GraphFloor, Math.Max(_primary.Max(), _secondary.Max()) * 1.1f);
        var step = box.Width / (History - 1);
        var start = box.Right - step * (_primary.Count - 1);

        void Series(List<float> values, float room, int direction, Color color)
        {
            var points = new PointF[values.Count];
            for (var i = 0; i < points.Length; i++)
                points[i] = new PointF(start + i * step, middle - direction * Math.Clamp(values[i] / scale, 0, 1) * room);
            using (var area = new GraphicsPath())
            {
                area.AddLine(points[0].X, middle, points[0].X, points[0].Y);
                area.AddLines(points);
                area.AddLine(points[^1].X, points[^1].Y, points[^1].X, middle);
                area.CloseFigure();
                var span = direction > 0 ? new RectangleF(box.Left, middle - room - 1, box.Width, room + 2) : new RectangleF(box.Left, middle - 1, box.Width, room + 2);
                using var fill = new LinearGradientBrush(span, Color.FromArgb(direction > 0 ? 110 : 18, color), Color.FromArgb(direction > 0 ? 18 : 110, color), LinearGradientMode.Vertical);
                g.FillPath(fill, area);
            }
            using (var glow = new Pen(Color.FromArgb(56, color), 4.4f) { LineJoin = LineJoin.Round })
                g.DrawLines(glow, points);
            using (var line = new Pen(color, 1.6f) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLines(line, points);
            DrawHead(g, points[^1], color);
        }
        Series(_secondary, box.Bottom - middle - 1, -1, Heat.Up);
        Series(_primary, middle - box.Top - 1, 1, Heat.Down);
    }

    // The newest point: a white dot in a halo of the reading's colour.
    private static void DrawHead(Graphics g, PointF at, Color color)
    {
        using var halo = new SolidBrush(Color.FromArgb(78, color));
        g.FillEllipse(halo, at.X - 5f, at.Y - 5f, 10f, 10f);
        using var dot = new SolidBrush(Color.White);
        g.FillEllipse(dot, at.X - 2.1f, at.Y - 2.1f, 4.2f, 4.2f);
    }
}

/// <summary>
/// One line of header text on the glow. It starts exactly at its left edge (a Label insets its text by a few
/// pixels that change with the font size, which left the title, the detail line, and the gauges on three
/// different edges), and it ends in an ellipsis when the window is too narrow for it.
/// </summary>
internal sealed class HeaderLabel : Control
{
    private const TextFormatFlags Flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                                          TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    public HeaderLabel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>The width the whole text needs.</summary>
    public int PreferredWidth => TextRenderer.MeasureText(Text, Font, Size.Empty, Flags & ~TextFormatFlags.EndEllipsis).Width + 2;

    protected override void OnTextChanged(EventArgs e)
    {
        AccessibleName = Text;
        Invalidate();
        base.OnTextChanged(e);
    }

    protected override void OnPaint(PaintEventArgs e) =>
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, Flags);
}

/// <summary>
/// The rig's headline parts under its name, one plate each: a small label, the figure worth showing in bold, and
/// a quieter line of detail, with a bar of the part's own colour at the plate's edge. When the window is too
/// narrow for all of it, details go first (from the right), then whole plates; the full text stays in the tooltip.
/// </summary>
internal sealed class SpecStrip : Control
{
    private static readonly Font LabelFont = new("Segoe UI Semibold", 7.25f);
    private static readonly Font ValueFont = new("Segoe UI Semibold", 10.75f);
    private static readonly Font DetailFont = new("Segoe UI", 8.75f);
    private static readonly Color[] Accents =
        [Color.FromArgb(34, 211, 238), Color.FromArgb(52, 211, 153), Color.FromArgb(167, 139, 250), Color.FromArgb(250, 204, 21), Color.FromArgb(96, 165, 250)];
    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
    private IReadOnlyList<Services.RigSpec> _specs = [];

    public SpecStrip()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
        AccessibleRole = AccessibleRole.StaticText;
    }

    /// <summary>Everything the plates say, for the tooltip and for screen readers.</summary>
    public string FullText { get; private set; } = "";

    public void Set(IReadOnlyList<Services.RigSpec> specs)
    {
        _specs = specs;
        FullText = string.Join("   ·   ", specs.Select(s => $"{s.Label}: {s.Value}{(s.Detail.Length > 0 ? ", " + s.Detail : "")}"));
        AccessibleName = FullText;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_specs.Count == 0) return;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        const int padLeft = 10, bar = 3, afterBar = 9, afterLabel = 8, beforeDetail = 9, padRight = 13, between = 8;

        var count = _specs.Count;
        var label = new int[count];
        var value = new int[count];
        var detail = new int[count];
        var showDetail = new bool[count];
        for (var i = 0; i < count; i++)
        {
            label[i] = TextRenderer.MeasureText(g, _specs[i].Label.ToUpperInvariant(), LabelFont, Size.Empty, Flags).Width;
            value[i] = TextRenderer.MeasureText(g, _specs[i].Value, ValueFont, Size.Empty, Flags).Width;
            detail[i] = _specs[i].Detail.Length > 0 ? TextRenderer.MeasureText(g, _specs[i].Detail, DetailFont, Size.Empty, Flags).Width : 0;
            showDetail[i] = detail[i] > 0;
        }
        int PlateWidth(int i) => padLeft + bar + afterBar + label[i] + afterLabel + value[i] + (showDetail[i] ? beforeDetail + detail[i] : 0) + padRight;
        int Total() => Enumerable.Range(0, count).Sum(PlateWidth) + between * Math.Max(0, count - 1);
        while (Total() > Width)
        {
            var last = Array.FindLastIndex(showDetail, 0 + count - 1, count, shown => shown);
            if (last >= 0) showDetail[last] = false;
            else if (count > 1) count--;
            else break;
        }

        var x = 0;
        var sheet = AppTheme.IsDark ? Color.White : Color.FromArgb(10, 30, 60);
        for (var i = 0; i < count; i++)
        {
            var width = Math.Min(PlateWidth(i), Width - x);
            if (width < 40 || Height < 8) break; // no room left for a plate (a window being minimized or restored)
            var plate = new RectangleF(x + 0.5f, 0.5f, width - 1f, Height - 1f);
            using (var path = Glass.RoundedPath(plate, 8))
            {
                using var fill = new LinearGradientBrush(RectangleF.Inflate(plate, 1, 1),
                    Color.FromArgb(Glass.Enabled ? 22 : 13, sheet), Color.FromArgb(Glass.Enabled ? 7 : 13, sheet), LinearGradientMode.Vertical);
                using var edge = new Pen(Color.FromArgb(44, sheet), 1f);
                g.FillPath(fill, path);
                g.DrawPath(edge, path);
            }
            var accent = Accents[i % Accents.Length];
            using (var glow = new SolidBrush(Color.FromArgb(Glass.Enabled ? 50 : 0, accent)))
                g.FillRoundedRectangle(glow, new RectangleF(x + padLeft - 2, 6, bar + 4, Height - 12), 3.5f);
            using (var mark = new SolidBrush(accent))
                g.FillRoundedRectangle(mark, new RectangleF(x + padLeft, 8, bar, Height - 16), 1.5f);

            var at = x + padLeft + bar + afterBar;
            TextRenderer.DrawText(g, _specs[i].Label.ToUpperInvariant(), LabelFont, new Rectangle(at, 1, label[i] + 2, Height), AppTheme.Muted, Flags);
            at += label[i] + afterLabel;
            TextRenderer.DrawText(g, _specs[i].Value, ValueFont, new Rectangle(at, 0, Math.Max(0, Math.Min(value[i] + 2, x + width - padRight - at + 2)), Height),
                AppTheme.Text, Flags | TextFormatFlags.EndEllipsis);
            at += value[i] + beforeDetail;
            if (showDetail[i])
                TextRenderer.DrawText(g, _specs[i].Detail, DetailFont, new Rectangle(at, 1, detail[i] + 2, Height), AppTheme.Muted, Flags);
            x += width + between;
        }
    }
}

/// <summary>
/// The line under the header. At rest it is the header's hairline; while a scan, driver check, or download runs,
/// a band of the accent colour sweeps along it (or the whole line lights, when motion is off).
/// </summary>
internal sealed class BusyBar : Control
{
    private readonly Func<float, bool> _animate;
    private bool _busy;
    private float _phase;

    public BusyBar()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.Selectable, false);
        Height = 2;
        _animate = Animate;
        AccessibleRole = AccessibleRole.ProgressBar;
        AccessibleName = "Working";
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            _phase = 0;
            if (value && Motion.Enabled) Motion.Start(_animate);
            Invalidate();
        }
    }

    private bool Animate(float seconds)
    {
        if (IsDisposed || !_busy) return false;
        _phase = (_phase + seconds * 0.55f) % 1f;
        Invalidate();
        return true;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(AppTheme.Background);
        using (var line = new SolidBrush(AppTheme.Border))
            g.FillRectangle(line, 0, Height - 1, Width, 1);
        if (!_busy || Width < 4) return;

        using (var track = new SolidBrush(Color.FromArgb(70, AppTheme.Accent)))
            g.FillRectangle(track, ClientRectangle);
        if (!Motion.Enabled)
        {
            using var solid = new SolidBrush(AppTheme.Accent);
            g.FillRectangle(solid, ClientRectangle);
            return;
        }
        // A band a third of the width that enters from the left, leaves on the right, and fades at both ends.
        var band = Width / 3f;
        var x = -band + (Width + band) * _phase;
        var rect = new RectangleF(x, 0, band, Height);
        using var sweep = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 0), Color.Transparent, Color.Transparent, LinearGradientMode.Horizontal)
        {
            InterpolationColors = new ColorBlend
            {
                Colors = [Color.FromArgb(0, AppTheme.Accent), AppTheme.Accent, Color.FromArgb(0, AppTheme.Accent)],
                Positions = [0f, 0.5f, 1f]
            }
        };
        g.FillRectangle(sweep, rect);
    }
}

internal enum StatusTone { Idle, Busy, Good, Bad }

/// <summary>The status bar's message, led by a dot that says how the last thing went.</summary>
internal sealed class StatusLine : Label
{
    private StatusTone _tone;

    public StatusLine()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoSize = false;
        AccessibleRole = AccessibleRole.StatusBar;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StatusTone Tone
    {
        get => _tone;
        set { if (_tone != value) { _tone = value; Invalidate(); } }
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var color = _tone switch
        {
            StatusTone.Busy => AppTheme.Accent,
            StatusTone.Good => AppTheme.Good,
            StatusTone.Bad => AppTheme.Critical,
            _ => Color.FromArgb(150, AppTheme.Muted)
        };
        using (var dot = new SolidBrush(color))
            g.FillEllipse(dot, 1, Height / 2f - 3.5f, 7, 7);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(16, 0, Math.Max(0, Width - 16), Height), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Dark drop-down menus for the header's Drivers and More buttons, and the tray icon's menu.</summary>
internal sealed class DarkMenuRenderer() : ToolStripProfessionalRenderer(new Colors())
{
    /// <summary>One look for every menu: the renderer, the font, room around each item, and no empty icon gutter.</summary>
    public static void Apply(ContextMenuStrip menu)
    {
        menu.Renderer = new DarkMenuRenderer();
        menu.ShowImageMargin = false;
        menu.ShowItemToolTips = true;
        menu.Font = new Font("Segoe UI", 9.5f);
        menu.Padding = new Padding(5);
        menu.BackColor = AppTheme.SurfaceRaised;
        menu.ForeColor = AppTheme.Text;
        foreach (ToolStripItem item in menu.Items)
            if (item is not ToolStripSeparator) item.Padding = new Padding(8, 6, 18, 6);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // A shortcut ("F5", "Ctrl+E") is drawn as a second, quieter piece of text beside the item's name.
        var shortcut = e.Item is ToolStripMenuItem { ShortcutKeyDisplayString: { Length: > 0 } keys } && e.Text == keys;
        e.TextColor = !e.Item.Enabled ? Color.FromArgb(120, AppTheme.Muted) : shortcut ? AppTheme.Muted : AppTheme.Text;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = AppTheme.Muted;
        base.OnRenderArrow(e);
    }

    // The highlight is a rounded block set in from the menu's edge, like the navigation rail's selection.
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;
        var g = e.Graphics;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new RectangleF(1.5f, 0.5f, e.Item.Width - 3f, e.Item.Height - 1f);
        using var path = Glass.RoundedPath(rect, 6);
        using var fill = new SolidBrush(Glass.Mix(AppTheme.SurfaceRaised, AppTheme.Accent, 0.26f));
        using var edge = new Pen(Color.FromArgb(110, AppTheme.Accent), 1f);
        g.FillPath(fill, path);
        g.DrawPath(edge, path);
        g.SmoothingMode = smoothing;
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        using var line = new Pen(Glass.Mix(AppTheme.Border, AppTheme.Text, 0.14f), 1f);
        var y = e.Item.Height / 2;
        e.Graphics.DrawLine(line, 8, y, e.Item.Width - 8, y);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var edge = new Pen(Glass.Mix(AppTheme.Border, AppTheme.Text, 0.22f), 1f);
        e.Graphics.DrawRectangle(edge, 0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientBegin => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientMiddle => AppTheme.SurfaceRaised;
        public override Color ImageMarginGradientEnd => AppTheme.SurfaceRaised;
        public override Color MenuBorder => AppTheme.Border;
        public override Color MenuItemBorder => Color.FromArgb(90, AppTheme.Accent);
        public override Color MenuItemSelected => AppTheme.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => AppTheme.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => AppTheme.AccentSoft;
        public override Color MenuItemPressedGradientBegin => AppTheme.AccentSoft;
        public override Color MenuItemPressedGradientEnd => AppTheme.AccentSoft;
        public override Color SeparatorDark => AppTheme.Border;
        public override Color SeparatorLight => AppTheme.Border;
    }
}
