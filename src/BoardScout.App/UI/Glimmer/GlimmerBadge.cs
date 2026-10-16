/*! Glimmer badge for WinForms 0.5.0 | MIT | (c) 2026 mikedopp | orb look after "orb" by LerSent001 (MIT) */
// A native rendition of dist/glimmer-badge.css for apps whose header has no WebView2: the version pill with
// the mini orb in its round left cap, drawn with GDI+ (no GPU, no WebView2). The orb look follows "orb" by
// LerSent001 (MIT): a flowing four-colour body under a glass shell with a cool/warm rim and a key highlight.
//
// One file, WinForms only. Reference hosts/winforms/Glimmer.WinForms, or copy this file into an app and keep
// this header, whose first line says which Glimmer it came from. Not both: the two copies would clash.
// The preset table at the bottom is written by scripts/build.mjs from the same registry as the CSS.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Glimmer.WinForms;

/// <summary>What the orb is saying. The same four states as the splash and glimmer-badge.css.</summary>
public enum GlimmerState
{
    Idle,
    Thinking,
    Success,
    Error,
}

/// <summary>The nine colours of a badge in one state (the --gd-* set in glimmer-badge.css).</summary>
public readonly record struct GlimmerBadgeColors(
    Color A, Color B, Color C, Color D, Color Hi, Color Glow, Color Canvas, Color RimA, Color RimB)
{
    /// <summary>Nine hex colours in the order a b c d hi glow canvas rim-a rim-b, separated by spaces.</summary>
    public static GlimmerBadgeColors Parse(string hex)
    {
        var c = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (c.Length != 9) throw new FormatException("A badge colour set has nine colours.");
        return new(Hex(c[0]), Hex(c[1]), Hex(c[2]), Hex(c[3]), Hex(c[4]), Hex(c[5]), Hex(c[6]), Hex(c[7]), Hex(c[8]));
    }

    private static Color Hex(string s)
    {
        var v = int.Parse(s.TrimStart('#'), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb(255, (v >> 16) & 255, (v >> 8) & 255, v & 255);
    }
}

/// <summary>A badge look: its colours at rest and while thinking, and the mint and red it turns for success and error.</summary>
public sealed record GlimmerBadgeLook(GlimmerBadgeColors Base, GlimmerBadgeColors Success, GlimmerBadgeColors Error)
{
    public GlimmerBadgeColors For(GlimmerState state) => state switch
    {
        GlimmerState.Success => Success,
        GlimmerState.Error => Error,
        _ => Base,
    };

    /// <summary>Three colour sets, "base / success / error", as the preset table stores them.</summary>
    public static GlimmerBadgeLook Parse(string sets)
    {
        var s = sets.Split('/');
        if (s.Length != 3) throw new FormatException("A badge look has three colour sets.");
        return new(GlimmerBadgeColors.Parse(s[0]), GlimmerBadgeColors.Parse(s[1]), GlimmerBadgeColors.Parse(s[2]));
    }
}

/// <summary>
/// The version button with a Glimmer mini orb. Drive <see cref="State"/> from real work: Thinking while something
/// runs, Success for a moment when it finishes (<see cref="Flash"/>), Error when it fails. Clicks, Enter, Space and
/// UI Automation all raise <see cref="Control.Click"/>.
/// </summary>
[DefaultEvent(nameof(Click))]
public class GlimmerBadge : Control
{
    /// <summary>The Glimmer release this file came from.</summary>
    public const string GlimmerVersion = "0.5.0";

    private const float ToneSeconds = 0.6f; // colour and filter transitions, as in the CSS
    private const float PopSeconds = 0.6f;
    private const float PulseSeconds = 1.7f;
    private const float WobbleSeconds = 2.4f;
    private const uint SpiGetClientAreaAnimation = 0x1042;

    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 33 };
    private readonly System.Windows.Forms.Timer _revert = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;
    private GlimmerBadgeLook _look = GlimmerBadgePresets.Default;
    private string _preset = GlimmerBadgePresets.DefaultName;
    private GlimmerState _state = GlimmerState.Idle;
    private GlimmerState _revertFrom;
    private bool _motionEnabled = true;
    private bool _windowsMotion = WindowsAnimations();
    private bool _glass = true;
    private bool _autoWidth = true;
    private bool _hover;
    private bool _pressed;

    // The swirl angle in degrees, the thinking pulse and error wobble clocks, and seconds into the success pop.
    private float _angle = 35f;
    private float _pulse;
    private float _wobble;
    private float _pop = PopSeconds;
    private Tone _toneFrom;
    private Tone _toneTo;
    private float _toneT = 1f;
    private Bitmap? _dot;

    public GlimmerBadge()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        ForeColor = Color.FromArgb(214, 226, 240);
        Size = new Size(96, 28);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        _toneFrom = _toneTo = TargetTone();
        _timer.Tick += (_, _) => OnTick();
        _revert.Tick += (_, _) =>
        {
            _revert.Stop();
            if (_state == _revertFrom) State = GlimmerState.Idle;
        };
    }

    /// <summary>Raised whenever <see cref="State"/> changes, including the return to Idle after <see cref="Flash"/>.</summary>
    public event EventHandler? StateChanged;

    [Category("Glimmer"), DefaultValue(GlimmerState.Idle)]
    public GlimmerState State
    {
        get => _state;
        set
        {
            _revert.Stop();
            if (_state == value) return;
            _state = value;
            if (value == GlimmerState.Success) _pop = MotionActive ? 0f : PopSeconds;
            if (value == GlimmerState.Thinking) _pulse = 0f;
            if (value == GlimmerState.Error) _wobble = 0f;
            BeginTone();
            AccessibilityNotifyClients(AccessibleEvents.ValueChange, -1);
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Shows a state for a while, then goes back to Idle unless another state was set meanwhile.</summary>
    public void Flash(GlimmerState state, int milliseconds = 1600)
    {
        State = state;
        if (state == GlimmerState.Idle || milliseconds <= 0) return;
        _revertFrom = state;
        _revert.Interval = milliseconds;
        _revert.Start();
    }

    /// <summary>Any Glimmer preset name (aurora, sonar, harbor, …). Unknown names fall back to siri, as on the web.</summary>
    [Category("Glimmer"), DefaultValue(GlimmerBadgePresets.DefaultName)]
    public string Preset
    {
        get => _preset;
        set
        {
            _preset = GlimmerBadgePresets.Contains(value) ? value : GlimmerBadgePresets.DefaultName;
            Look = GlimmerBadgePresets.Get(_preset);
        }
    }

    /// <summary>The colours in use. Set it directly for a custom look; <see cref="Preset"/> sets it from the table.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public GlimmerBadgeLook Look
    {
        get => _look;
        set
        {
            _look = value ?? GlimmerBadgePresets.Default;
            BeginTone();
        }
    }

    /// <summary>False holds the orb still in its state's colours. Windows' "Show animations" setting and high
    /// contrast also stop it, whatever this says.</summary>
    [Category("Glimmer"), DefaultValue(true)]
    public bool MotionEnabled
    {
        get => _motionEnabled;
        set
        {
            if (_motionEnabled == value) return;
            _motionEnabled = value;
            MotionChanged();
        }
    }

    /// <summary>False draws the pill flat and opaque, without the glow wash and sheen. High contrast draws it in
    /// system colours instead.</summary>
    [Category("Glimmer"), DefaultValue(true)]
    public bool GlassEffects
    {
        get => _glass;
        set
        {
            if (_glass == value) return;
            _glass = value;
            Invalidate();
        }
    }

    /// <summary>Keeps the width fitted to the label. The height stays the host's; the orb scales with it.</summary>
    [Category("Glimmer"), DefaultValue(true)]
    public bool AutoWidth
    {
        get => _autoWidth;
        set
        {
            _autoWidth = value;
            FitWidth();
        }
    }

    /// <summary>Whether the orb is moving: motion allowed here and in Windows, and no high contrast.</summary>
    [Browsable(false)]
    public bool MotionActive => _motionEnabled && _windowsMotion && !SystemInformation.HighContrast;

    /// <summary>Moves the animation on. The control's timer calls this; QA renders call it to reach a given frame.</summary>
    public void AdvanceAnimation(float seconds)
    {
        if (!MotionActive)
        {
            _toneT = 1f;
            _pop = PopSeconds;
            return;
        }
        seconds = Math.Max(0f, seconds);
        _angle = (_angle + 360f * seconds / Period) % 360f;
        _pulse = (_pulse + seconds) % PulseSeconds;
        _wobble = (_wobble + seconds) % WobbleSeconds;
        _pop = Math.Min(PopSeconds, _pop + seconds);
        _toneT = Math.Min(1f, _toneT + seconds / ToneSeconds);
    }

    /// <summary>Draws the current frame into <paramref name="bounds"/>. Draw onto an opaque surface: the label is
    /// GDI text.</summary>
    public void DrawTo(Graphics g, Rectangle bounds)
    {
        var saved = g.Save();
        g.TranslateTransform(bounds.X, bounds.Y);
        PaintBadge(g, bounds.Size);
        g.Restore(saved);
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var height = Height > 0 ? Height : 28;
        var dot = DotRect(new SizeF(Width, height));
        var text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        return new Size((int)MathF.Ceiling(dot.Right + Gap(dot.Width) + text.Width + RightPad(dot.Width)), height);
    }

    // ---- state and motion ---------------------------------------------------------------------------------------

    private float Period => _state switch
    {
        GlimmerState.Thinking => 1.7f,
        GlimmerState.Success => 5f,
        GlimmerState.Error => 12f,
        _ => _hover && Enabled ? 3f : 9f,
    };

    private Tone TargetTone()
    {
        var (saturate, brightness, glow) = _state switch
        {
            GlimmerState.Thinking => (1.1f, 1.08f, 6f),
            GlimmerState.Success => (1f, 1f, 7f),
            GlimmerState.Error => (1f, 1f, 6f),
            _ => _hover && Enabled ? (1f, 1f, 3f) : (0.8f, 0.88f, 3f),
        };
        if (!Enabled) (saturate, brightness) = (0.3f, 0.72f);
        return new Tone(_look.For(_state), saturate, brightness, glow);
    }

    private Tone CurrentTone => _toneT >= 1f ? _toneTo : Tone.Lerp(_toneFrom, _toneTo, Smooth(_toneT));

    private void BeginTone()
    {
        _toneFrom = CurrentTone;
        _toneTo = TargetTone();
        _toneT = MotionActive && IsHandleCreated ? 0f : 1f;
        UpdateTimer();
        Invalidate();
    }

    private void MotionChanged()
    {
        if (!MotionActive)
        {
            _toneT = 1f;
            _pop = PopSeconds;
        }
        UpdateTimer();
        Invalidate();
    }

    private void UpdateTimer()
    {
        var run = MotionActive && IsHandleCreated && Visible;
        if (run == _timer.Enabled) return;
        if (run)
        {
            _lastTick = _clock.Elapsed.TotalSeconds;
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void OnTick()
    {
        var now = _clock.Elapsed.TotalSeconds;
        var seconds = (float)Math.Min(0.1, now - _lastTick);
        _lastTick = now;
        if (!Visible || FindForm() is not { Visible: true, WindowState: not FormWindowState.Minimized }) return;
        var settling = _toneT < 1f;
        AdvanceAnimation(seconds);
        // While colours are changing the rim and wash change too; otherwise only the orb and its glow move.
        if (settling) Invalidate();
        else Invalidate(Rectangle.Ceiling(RectangleF.Inflate(DotRect(ClientSize), 12f * DpiScale, 12f * DpiScale)));
    }

    private static bool WindowsAnimations()
    {
        try
        {
            return !SystemParametersInfo(SpiGetClientAreaAnimation, 0, out var on, 0) || on;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return true;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        if (InvokeRequired)
        {
            try { BeginInvoke(new MethodInvoker(() => OnUserPreferenceChanged(sender, e))); }
            catch (InvalidOperationException) { }
            return;
        }
        _windowsMotion = WindowsAnimations();
        MotionChanged(); // also repaints for a high-contrast switch
    }

    private void FitWidth()
    {
        if (!_autoWidth) return;
        var width = GetPreferredSize(Size.Empty).Width;
        if (width != Width) Width = width;
    }

    // ---- painting -----------------------------------------------------------------------------------------------

    private float DpiScale => DeviceDpi / 96f;

    // The orb sits concentric in the pill's round left cap; the spacing follows the CSS's em values for a 1.35em dot.
    private static RectangleF DotRect(SizeF size)
    {
        var d = MathF.Max(8f, MathF.Round(size.Height * 0.6f));
        var inset = (size.Height - d) / 2f;
        return new RectangleF(inset, inset, d, d);
    }

    private static float Gap(float dot) => dot * 0.333f;      // 0.45em
    private static float RightPad(float dot) => dot * 0.593f; // 0.8em

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        PaintBadge(e.Graphics, ClientSize);
    }

    private void PaintBadge(Graphics g, Size size)
    {
        if (size.Width < 8 || size.Height < 8) return;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var pill = new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f);
        using var pillPath = PillPath(pill);
        var lit = Enabled && (_hover || (Focused && ShowFocusCues));

        if (SystemInformation.HighContrast)
        {
            PaintHighContrast(g, size, pillPath, lit);
            return;
        }

        var tone = CurrentTone;
        var scale = DpiScale;
        var glow = tone.Colors.Glow;

        // The pill: the preset's canvas over a neutral ink, an orb-coloured wash from the left, a glow-tinted rim.
        var fill = Mix(tone.Colors.Canvas, Color.FromArgb(18, 22, 30), 0.45f);
        if (_pressed) fill = Mix(fill, Color.Black, 0.2f);
        using (var brush = new SolidBrush(fill)) g.FillPath(brush, pillPath);
        if (_glass)
        {
            // radial-gradient(120% 140% at 0% 50%, glow 16%, transparent 60%)
            float rx = pill.Width * 0.72f, ry = pill.Height * 0.84f;
            using var wash = new GraphicsPath();
            wash.AddEllipse(pill.X - rx, pill.Y + pill.Height / 2f - ry, rx * 2f, ry * 2f);
            using (var washBrush = new PathGradientBrush(wash)
                   {
                       CenterColor = Color.FromArgb(41, glow),
                       SurroundColors = [Color.FromArgb(0, glow)],
                   })
                g.FillPath(washBrush, pillPath);

            // A faint sheen across the top half, the glass catching the light.
            using var sheen = new LinearGradientBrush(RectangleF.Inflate(pill, 0f, 1f),
                Color.FromArgb(22, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), LinearGradientMode.Vertical)
            {
                Blend = new Blend { Factors = [0f, 1f, 1f], Positions = [0f, 0.55f, 1f] },
            };
            g.FillPath(sheen, pillPath);
        }
        var rimAlpha = _state switch
        {
            GlimmerState.Success or GlimmerState.Error => 0.75f,
            GlimmerState.Thinking => 0.6f,
            _ => 0.38f,
        };
        if (lit) rimAlpha = Math.Max(rimAlpha, 0.7f);
        using (var rim = new Pen(Color.FromArgb((int)(rimAlpha * 255f), glow), Math.Max(1f, scale)))
            g.DrawPath(rim, pillPath);

        PaintOrb(g, size, tone, scale);
        PaintLabel(g, size, Enabled ? ForeColor : Mix(ForeColor, fill, 0.5f));

        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(Color.FromArgb(170, ForeColor), 1f) { DashStyle = DashStyle.Dot };
            using var ring = PillPath(RectangleF.Inflate(pill, -2.5f * scale, -2.5f * scale));
            g.DrawPath(focus, ring);
        }
    }

    private void PaintOrb(Graphics g, Size size, Tone tone, float scale)
    {
        var dot = DotRect(size);
        float cx = dot.X + dot.Width / 2f, cy = dot.Y + dot.Height / 2f;
        var moving = MotionActive;
        var pop = moving ? PopScale(_pop) : 1f;
        var r = dot.Width / 2f * pop;

        // The glow, box-shadow 0 0 <size> glow 70%. Thinking pulses it from 3 px at 60% to 9 px at 90%.
        float glowPx = tone.Glow, glowAlpha = 0.7f, hairAlpha = 0.25f;
        if (_state == GlimmerState.Thinking)
        {
            var p = moving ? 0.5f - 0.5f * MathF.Cos(MathF.Tau * _pulse / PulseSeconds) : 0.5f;
            glowPx = 3f + 6f * p;
            glowAlpha = 0.6f + 0.3f * p;
            hairAlpha = 0.25f + 0.15f * p;
        }
        var blur = glowPx * scale * pop;
        var glow = Filter(tone.Colors.Glow, tone);
        using (var halo = new GraphicsPath())
        {
            var outer = r + blur;
            halo.AddEllipse(cx - outer, cy - outer, outer * 2f, outer * 2f);
            var edge = blur / outer; // where the orb's edge falls, measured in from the halo's edge
            using var brush = new PathGradientBrush(halo)
            {
                CenterColor = Color.FromArgb((int)(glowAlpha * 255f), glow),
                SurroundColors = [Color.FromArgb(0, glow)],
                Blend = new Blend
                {
                    Factors = [0f, 0.14f, 0.5f, 1f, 1f],
                    Positions = [0f, edge * 0.45f, edge, Math.Min(0.99f, edge * 1.6f), 1f],
                },
            };
            g.FillPath(brush, halo);
        }

        // The body is painted square into a small bitmap, then filled through the round (or wobbling) outline,
        // which gives it an antialiased edge that a GDI+ clip would not.
        var n = (int)MathF.Ceiling(dot.Width * 1.25f) + 4;
        if (_dot is null || _dot.Width < n)
        {
            _dot?.Dispose();
            _dot = new Bitmap(n, n, PixelFormat.Format32bppPArgb);
        }
        float ox = MathF.Floor(cx - n / 2f), oy = MathF.Floor(cy - n / 2f);
        using (var dg = Graphics.FromImage(_dot))
        {
            dg.SmoothingMode = SmoothingMode.AntiAlias;
            dg.PixelOffsetMode = g.PixelOffsetMode;
            PaintOrbBody(dg, new PointF(cx - ox, cy - oy), r, tone);
        }
        using var body = OrbPath(cx, cy, r, moving && _state == GlimmerState.Error);
        using (var texture = new TextureBrush(_dot, WrapMode.Clamp))
        {
            texture.TranslateTransform(ox, oy);
            g.FillPath(texture, body);
        }

        // 0 0 0 0.5px hi 25%: a hairline round the glass.
        using var hair = new Pen(Color.FromArgb((int)(hairAlpha * 255f), Filter(tone.Colors.Hi, tone)), Math.Max(0.6f, 0.5f * scale));
        g.DrawPath(hair, body);
    }

    private void PaintOrbBody(Graphics g, PointF c, float r, Tone tone)
    {
        var k = tone.Colors;
        var size = r * 2f;
        Color Glass(Color color) => Filter(color, tone);
        Color Fluid(Color color) => Filter(Saturate(color, 1.15f), tone);

        g.Clear(Glass(k.Canvas));

        // The fluid: a four-colour swirl (conic-gradient from 20deg a b c d a) with three soft blobs over it, on a
        // layer 1.6x the orb that turns and breathes. Blurred in the CSS; here the colours fade to their mean in
        // the middle and the blobs have long soft edges, which reads the same at this size.
        var saved = g.Save();
        g.TranslateTransform(c.X, c.Y);
        g.RotateTransform(_angle);
        var swirl = 1f + 0.12f * (1f - Math.Abs(2f * (_angle / 360f) - 1f));
        g.ScaleTransform(swirl, swirl);
        var layer = size * 0.8f;
        Color[] stops = [Fluid(k.A), Fluid(k.B), Fluid(k.C), Fluid(k.D)];
        const int steps = 36;
        var points = new PointF[steps];
        var colors = new Color[steps];
        for (var i = 0; i < steps; i++)
        {
            var degrees = i * 360f / steps; // clockwise from the top, as CSS measures
            var radians = degrees * MathF.PI / 180f;
            points[i] = new PointF(MathF.Sin(radians) * layer, -MathF.Cos(radians) * layer);
            var t = ((degrees - 20f) % 360f + 360f) % 360f / 90f;
            var segment = (int)t;
            colors[i] = Mix(stops[segment % 4], stops[(segment + 1) % 4], t - segment);
        }
        using (var ring = new GraphicsPath())
        {
            ring.AddPolygon(points);
            using var conic = new PathGradientBrush(ring)
            {
                CenterPoint = PointF.Empty,
                CenterColor = Mean(stops),
                SurroundColors = colors,
                Blend = new Blend { Factors = [0f, 0.05f, 0.22f, 0.53f, 1f], Positions = [0f, 0.25f, 0.5f, 0.75f, 1f] },
            };
            g.FillPath(conic, ring);
        }
        // radial-gradient(circle at x% y%, colour 0 solid%, transparent fade%), bottom one first
        Blob(g, stops[3], -layer + 0.42f * 2f * layer, -layer + 0.78f * 2f * layer, 0.218f * size, 0.653f * size);
        Blob(g, stops[2], -layer + 0.70f * 2f * layer, -layer + 0.62f * 2f * layer, 0.24f * size, 0.658f * size);
        Blob(g, stops[1], -layer + 0.30f * 2f * layer, -layer + 0.35f * 2f * layer, 0.275f * size, 0.70f * size);
        g.Restore(saved);

        // The glass. Shading toward the limb: transparent to 56% of the corner distance, black 38% at the corner.
        using (var limb = new GraphicsPath())
        {
            var lr = r * 1.06f;
            limb.AddEllipse(c.X - lr, c.Y - lr, lr * 2f, lr * 2f);
            using var brush = new PathGradientBrush(limb)
            {
                CenterColor = Color.FromArgb(0, 0, 0, 0),
                SurroundColors = [Color.FromArgb(38, 0, 0, 0)],
                Blend = new Blend { Factors = [0f, 1f, 1f], Positions = [0f, 0.25f, 1f] },
            };
            g.FillPath(brush, limb);
        }

        // The key light: radial-gradient(ellipse 34% 24% at 33% 24%, hi 85%, transparent 70%).
        using (var key = new GraphicsPath())
        {
            float kx = c.X - r + 0.33f * size, ky = c.Y - r + 0.24f * size;
            float rx = 0.34f * 0.7f * size, ry = 0.24f * 0.7f * size;
            key.AddEllipse(kx - rx, ky - ry, rx * 2f, ry * 2f);
            var hi = Glass(k.Hi);
            using var brush = new PathGradientBrush(key)
            {
                CenterColor = Color.FromArgb(217, hi),
                SurroundColors = [Color.FromArgb(0, hi)],
            };
            g.FillPath(brush, key);
        }

        // The inset shadows, last listed first: a faint white inner line, then the warm rim upper left
        // (inset 0.08em 0.08em 0.12em -0.05em rim-b 80%), then the cool rim lower right
        // (inset -0.09em -0.07em 0.12em -0.04em rim-a 85%).
        var em = size / 1.35f;
        var line = Math.Max(0.8f, 0.06f * em);
        using (var white = new Pen(Color.FromArgb(56, 255, 255, 255), line))
            g.DrawEllipse(white, c.X - r + line / 2f, c.Y - r + line / 2f, size - line, size - line);
        InsetRim(g, c, r, Glass(k.RimB), 0.80f, 0.08f * em, 0.08f * em, 0.12f * em, -0.05f * em);
        InsetRim(g, c, r, Glass(k.RimA), 0.85f, -0.09f * em, -0.07f * em, 0.12f * em, -0.04f * em);
    }

    private static void Blob(Graphics g, Color color, float x, float y, float solid, float fade)
    {
        using var path = new GraphicsPath();
        path.AddEllipse(x - fade, y - fade, fade * 2f, fade * 2f);
        var p = 1f - solid / fade;
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = [Color.FromArgb(0, color)],
            Blend = new Blend
            {
                Factors = [0f, 0.16f, 0.5f, 0.84f, 1f, 1f],
                Positions = [0f, p * 0.25f, p * 0.5f, p * 0.75f, p, 1f],
            },
        };
        g.FillPath(brush, path);
    }

    // An inset box-shadow on a circle: the shadow shows inside the orb wherever the offset "hole" (the orb, grown by
    // a negative spread) doesn't cover it. Three passes with the hole pulled in and out by the blur soften the edge.
    private static readonly float[] RimPulls = [-0.5f, 0f, 0.5f];

    private static void InsetRim(Graphics g, PointF c, float r, Color color, float alpha, float dx, float dy, float blur, float spread)
    {
        using var brush = new SolidBrush(Color.FromArgb((int)(alpha * 255f / 2.2f), color));
        foreach (var pull in RimPulls)
        {
            var hole = r - spread - pull * blur;
            using var path = new GraphicsPath(FillMode.Alternate);
            path.AddEllipse(c.X - r * 1.1f, c.Y - r * 1.1f, r * 2.2f, r * 2.2f);
            path.AddEllipse(c.X + dx - hole, c.Y + dy - hole, hole * 2f, hole * 2f);
            g.FillPath(brush, path);
        }
    }

    private GraphicsPath OrbPath(float cx, float cy, float r, bool wobble)
    {
        var path = new GraphicsPath();
        if (!wobble)
        {
            path.AddEllipse(cx - r, cy - r, r * 2f, r * 2f);
            return path;
        }
        // The error wobble: the edge breathes out of round, as the CSS's border-radius keyframes do.
        const int count = 24;
        var w = MathF.Tau * _wobble / WobbleSeconds;
        var points = new PointF[count];
        for (var i = 0; i < count; i++)
        {
            var a = MathF.Tau * i / count;
            var k = 1f + 0.035f * MathF.Sin(2f * a + w) + 0.02f * MathF.Sin(3f * a - 2f * w);
            points[i] = new PointF(cx + MathF.Cos(a) * r * k, cy + MathF.Sin(a) * r * k);
        }
        path.AddClosedCurve(points, 0.5f);
        return path;
    }

    private void PaintLabel(Graphics g, Size size, Color color)
    {
        var dot = DotRect(size);
        var left = (int)MathF.Round(dot.Right + Gap(dot.Width));
        var bounds = new Rectangle(left, 0, Math.Max(0, size.Width - left - (int)(RightPad(dot.Width) / 2f)), size.Height);
        TextRenderer.DrawText(g, Text, Font, bounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding |
            TextFormatFlags.EndEllipsis | TextFormatFlags.PreserveGraphicsTranslateTransform | TextFormatFlags.PreserveGraphicsClipping);
    }

    // High contrast: system colours, no orb. A filled mark at rest, success and error; a hollow ring while thinking.
    private void PaintHighContrast(Graphics g, Size size, GraphicsPath pill, bool lit)
    {
        using (var fill = new SolidBrush(SystemColors.ButtonFace)) g.FillPath(fill, pill);
        using (var edge = new Pen(lit ? SystemColors.Highlight : SystemColors.ControlText, lit ? 2f : 1f)) g.DrawPath(edge, pill);
        var dot = RectangleF.Inflate(DotRect(size), -1f, -1f);
        if (_state != GlimmerState.Thinking)
        {
            using var mark = new SolidBrush(Enabled ? SystemColors.Highlight : SystemColors.GrayText);
            g.FillEllipse(mark, dot);
        }
        using (var ring = new Pen(Enabled ? SystemColors.ControlText : SystemColors.GrayText, 1.5f)) g.DrawEllipse(ring, dot);
        PaintLabel(g, size, Enabled ? SystemColors.ControlText : SystemColors.GrayText);
        if (Focused && ShowFocusCues)
        {
            using var focus = new Pen(SystemColors.ControlText, 1f) { DashStyle = DashStyle.Dot };
            using var ring = PillPath(RectangleF.Inflate(new RectangleF(0.5f, 0.5f, size.Width - 1f, size.Height - 1f), -3f, -3f));
            g.DrawPath(focus, ring);
        }
    }

    private static GraphicsPath PillPath(RectangleF rect)
    {
        var path = new GraphicsPath();
        var d = Math.Min(rect.Height, rect.Width);
        path.AddArc(rect.X, rect.Y, d, d, 90, 180);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 180);
        path.CloseFigure();
        return path;
    }

    private static float PopScale(float seconds)
    {
        if (seconds >= PopSeconds) return 1f;
        // scale 0.82, then 1.12 at 60%, then 1, overshooting like the CSS's cubic-bezier(.2,.9,.3,1.4)
        var u = seconds / PopSeconds;
        return u < 0.6f ? 0.82f + 0.30f * EaseOut(u / 0.6f) : 1.12f - 0.12f * EaseOut((u - 0.6f) / 0.4f);
    }

    private static float EaseOut(float x) => 1f - (1f - x) * (1f - x) * (1f - x);

    private static float Smooth(float x) => x * x * (3f - 2f * x);

    // CSS filter: saturate(s) brightness(b), applied to the orb's colours.
    private static Color Filter(Color color, Tone tone) => Brighten(Saturate(color, tone.Saturate), tone.Brightness);

    private static Color Saturate(Color c, float s)
    {
        float r = c.R, g = c.G, b = c.B;
        return Color.FromArgb(c.A,
            Byte((0.213f + 0.787f * s) * r + (0.715f - 0.715f * s) * g + (0.072f - 0.072f * s) * b),
            Byte((0.213f - 0.213f * s) * r + (0.715f + 0.285f * s) * g + (0.072f - 0.072f * s) * b),
            Byte((0.213f - 0.213f * s) * r + (0.715f - 0.715f * s) * g + (0.072f + 0.928f * s) * b));
    }

    private static Color Brighten(Color c, float k) => Color.FromArgb(c.A, Byte(c.R * k), Byte(c.G * k), Byte(c.B * k));

    private static Color Mix(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(Byte(a.A + (b.A - a.A) * t), Byte(a.R + (b.R - a.R) * t), Byte(a.G + (b.G - a.G) * t), Byte(a.B + (b.B - a.B) * t));
    }

    private static Color Mean(Color[] colors) => Color.FromArgb(
        Byte((float)colors.Average(c => c.R)), Byte((float)colors.Average(c => c.G)), Byte((float)colors.Average(c => c.B)));

    private static int Byte(float v) => (int)Math.Clamp(MathF.Round(v), 0f, 255f);

    private readonly record struct Tone(GlimmerBadgeColors Colors, float Saturate, float Brightness, float Glow)
    {
        public static Tone Lerp(Tone a, Tone b, float t) => new(
            new GlimmerBadgeColors(
                Mix(a.Colors.A, b.Colors.A, t), Mix(a.Colors.B, b.Colors.B, t), Mix(a.Colors.C, b.Colors.C, t),
                Mix(a.Colors.D, b.Colors.D, t), Mix(a.Colors.Hi, b.Colors.Hi, t), Mix(a.Colors.Glow, b.Colors.Glow, t),
                Mix(a.Colors.Canvas, b.Colors.Canvas, t), Mix(a.Colors.RimA, b.Colors.RimA, t), Mix(a.Colors.RimB, b.Colors.RimB, t)),
            a.Saturate + (b.Saturate - a.Saturate) * t,
            a.Brightness + (b.Brightness - a.Brightness) * t,
            a.Glow + (b.Glow - a.Glow) * t);
    }

    // ---- input, focus, lifetime ---------------------------------------------------------------------------------

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hover = true;
        BeginTone();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = false;
        _pressed = false;
        BeginTone();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        _pressed = true;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_pressed) return;
        _pressed = false;
        Invalidate();
    }

    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Enter or Keys.Space || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is not (Keys.Enter or Keys.Space) || e.Modifiers != Keys.None) return;
        OnClick(EventArgs.Empty);
        e.Handled = true;
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); BeginTone(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); FitWidth(); Invalidate(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); FitWidth(); Invalidate(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        FitWidth(); // the orb, and so the label's start, follow the height
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        _windowsMotion = WindowsAnimations();
        UpdateTimer();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _timer.Stop();
        base.OnHandleDestroyed(e);
    }

    // Screen readers and UI Automation can press the badge, and read its state as the value.
    protected override AccessibleObject CreateAccessibilityInstance() => new BadgeAccessible(this);

    private sealed class BadgeAccessible : ControlAccessibleObject
    {
        private readonly GlimmerBadge _badge;

        public BadgeAccessible(GlimmerBadge badge) : base(badge) => _badge = badge;

        public override string DefaultAction => "Press";
        public override void DoDefaultAction() => _badge.OnClick(EventArgs.Empty);
        public override string? Value => _badge.State.ToString();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            _timer.Dispose();
            _revert.Dispose();
            _dot?.Dispose();
            _dot = null;
        }
        base.Dispose(disposing);
    }
}

/// <summary>Every Glimmer preset's badge colours, generated from the same registry as glimmer-badge.css.</summary>
public static class GlimmerBadgePresets
{
    public const string DefaultName = "siri";

    private static Dictionary<string, GlimmerBadgeLook>? _looks;

    /// <summary>Preset names in registry order.</summary>
    public static IReadOnlyList<string> Names { get; } = Table.Select(t => t.Name).ToArray();

    public static bool Contains(string? name) => name is not null && Looks.ContainsKey(name);

    public static GlimmerBadgeLook Default => Looks[DefaultName];

    /// <summary>The preset's look, or siri's for an unknown name.</summary>
    public static GlimmerBadgeLook Get(string? name) =>
        name is not null && Looks.TryGetValue(name, out var look) ? look : Default;

    private static Dictionary<string, GlimmerBadgeLook> Looks => _looks ??=
        Table.ToDictionary(t => t.Name, t => GlimmerBadgeLook.Parse(t.Sets), StringComparer.OrdinalIgnoreCase);

    // Each entry: base, success and error, each as a b c d hi glow canvas rim-a rim-b.
    // @@BADGE-PRESETS-BEGIN (written by scripts/build.mjs; change src/badge.ts or src/presets.ts instead)
    private static (string Name, string Sets)[] Table =>
    [
        ("nebula", "04030C 3B1C6E E2508C FFD9A0 FFF4E0 8A4DFF 020108 B48CFF FF8FB1 / 18D08D 3DDEC3 BEEBC2 77A97C F4FDF4 22E39A 020108 89EFDB 81E4CB / EA4343 EB7F53 FA3F74 C46553 FFE7DE FF4545 020108 F499A7 FF667E"),
        ("sonar", "010A08 06402F 19C98A C8FFE6 E6FFF4 19C98A 010604 5CFFC1 38B6FF / 18D08D 37DFBF A6F6C1 5CB390 F0FFF7 22E39A 010604 78FFCF 3CE9D9 / EA4343 EA8048 EA6974 B77771 FBE9E2 FF4545 010604 ECB396 EB7297"),
        ("harbor", "01100F 0B4F5C 2FB7B0 F2D59A FFF3D6 2FB7B0 010807 5FE3D6 E0B565 / 18D08D 37E0C2 A7F4C6 70A87A F4FDF2 22E39A 010807 78FAD3 73E9C2 / EA4444 EA824F EA627D C06350 FFE7DD FF4545 010807 ECAC9B FA726C"),
        ("ember", "120300 7A1405 FF5A1F FFD27A FFE7C2 FF5A1F 060100 FF8A3D FF3D3D / 1AD08D 4DDEBE C5ECBA 77A773 F4FBEF 22E39A 060100 9FEFC2 81DFC0 / EA4343 EE7E45 FF4265 C46244 FFE5D9 FF4545 060100 FF9980 FF5667"),
        ("siri", "FFD86B 82F4FF FF7BD5 8E6CFF FFFFFF 956CFF 030409 9BF4FF C5A9FF / 78E193 4FF2D9 C5EECD 409599 F4FFF9 22E39A 030409 83FDDB 68E7D9 / FF7651 EFA486 FF4C88 AD327C FFE9E4 FF4545 030409 F1B0A7 F66E97"),
        ("voiceWave", "09030E CE2CCB FF5C71 7B53FF FFD9F0 CE2CCB 020105 E48BFF FF7890 / 19D08D 6CDECF C5ECBF 379299 F4F9F7 22E39A 020105 97EFDB 81E2C6 / EA4343 F77F72 FF426F AB277C FFE2E1 FF4545 020105 FA99A7 FF6075"),
        ("particleRibbon", "63F1FF 4A9DFF 8566FF F15DE1 F5FBFF 765CFF 010208 9BF4FF C5A9FF / 32E6B2 40E6D9 AEEDD6 70938F F3FEF9 22E39A 010208 83FDDB 68E7D9 / EC8085 EB8E86 EF4597 C02B6F FDE8E4 FF4545 010208 F1B0A7 F66E97"),
        ("blueDrop", "020B1D 0756B8 1EC8FF DDFBFF EAFBFF 168DFF 010207 4FD7FF 466DFF / 18D08D 37E0CC A6F6D6 66B299 F1FEF9 22E39A 010207 76F8DB 3FE1D9 / EA4344 EA836A EA6897 BB757C FBE8E4 FF4545 010207 EBA9A7 EB5E97"),
        ("violetEmber", "100016 4A0E8F A52EFF F1A7FF FFD6FF A52EFF 030006 C257FF 6C2DFF / 19D08D 40DEC6 B2EAD6 709E99 F4F8F9 22E39A 030006 8DECDB 48DED9 / EA4344 EB7E5D F23997 C04D7C FFE2E4 FF4545 030006 F593A7 ED5497"),
        ("refractiveBlob", "1B102B 7056A8 BFA5F5 F1E8FF FFFFFF B18CFF 050208 D9C7FF B59AE8 / 1BD08E 49E0CA B7F2D3 70AD99 F4FFF9 22E39A 050208 93F6DB 62E5D5 / EA4446 ED8365 F55B93 C06C7C FFE9E4 FF4545 050208 F9A5A7 F4698F"),
        ("chromaticMetal", "FBFCFB 7F8683 D6DAD8 33373A FFFFFF BDEFFF 050606 6EDCFF FF806D / 76E8B1 4EE3C5 BCF9CD 18906B F4FFF9 22E39A 050606 7AF9DB 81E3C3 / FE8484 EE8959 F87089 A51C30 FFE9E4 FF4545 050606 EDAAA7 FF626E"),
        ("aurora", "030816 20F0B6 32A8FF A34BFF FFFFFF 20F0B6 010207 32A8FF 20F0B6 / 18D08D 39F2CC A7F2D6 4A9199 F4FFF9 22E39A 010207 74F2DB 39F2CC / EA4344 EAA36A EA5C97 B0247C FFE9E4 FF4545 010207 EA9EA7 EA877F"),
        ("frost", "F7FBFF D6E8F7 A8C8F0 6F9EE8 FFFFFF 6F9EE8 000000 D6E8F7 6F9EE8 / 75E8B2 6FF0D8 B3F6D2 329C91 F4FFF9 22E39A 000000 92FBD9 49E6D5 / FE8485 F8A083 F26891 A94972 FFE9E4 FF4545 000000 F8ADA5 ED6A8F"),
        ("chrome", "FFFFFF B9C0CA 343A43 030405 FFFFFF FFFFFF 050608 B9C0CA FFFFFF / 78E8B2 63EACF A7EBBB 0A8F68 F4FFF9 22E39A 050608 8AF5D1 81F4D9 / FF8585 F49671 EA3B68 A4102A FFE9E4 FF4545 050608 F4A398 FF8C97"),
        ("opal", "FFF6E8 6EF2CF FF91D8 756BFF FFFFFF 9E8CFF 07080D CDE5FF D9C8FF / 78E7AC 49F2D0 C5F0CD 359599 F4FFF9 22E39A 07080D 90FBDB 70EBD9 / FF827C EDA373 FF5389 AA327C FFE9E4 FF4545 07080D F7ACA7 F97897"),
        ("spectrum", "FFFFFF 1677FF F249A0 35E6B2 FFFFFF 1677FF 03040A 66E8FF D26CFF / 78E8B2 38E2D9 C2EBC4 18AC81 F4FFF9 22E39A 03040A 79FBDB 6DE1D9 / FF8585 EA8786 FD3E79 A56B5A FFE9E4 FF4545 03040A EDADA7 F85E97"),
        ("plasma", "06020E 0099FF 258BFF 1375FF FFFFFF 0099FF 020105 1951C2 00E9FF / 18D08D 37E5D9 A6EFD6 0D9699 F4FFF9 22E39A 020105 73ECCF 37F0D9 / EA4343 EA8D86 EA5197 A4367C FFE9E4 FF4545 020105 EA9296 EA8497"),
    ];
    // @@BADGE-PRESETS-END
}
