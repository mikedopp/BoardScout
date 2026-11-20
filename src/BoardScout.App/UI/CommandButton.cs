using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace BoardScout.UI;

/// <summary>What a command button is for; the look follows from it and from the theme, never from per-button colours.</summary>
internal enum CommandKind
{
    /// <summary>The one filled button in a group: what most people came to do.</summary>
    Primary,
    /// <summary>An outlined button for everything else.</summary>
    Secondary,
    /// <summary>Stops or undoes something.</summary>
    Danger,
    /// <summary>A status pill that is also a button (health, privacy).</summary>
    Chip
}

internal enum CommandGlyph { None, Scan, Stop, Upgrade, Drivers, More }

/// <summary>
/// The header's buttons and chips. One control draws them all, so heights, corners, text, icons, hover, press,
/// disabled, and keyboard focus are the same everywhere. Text is drawn by GDI (the same renderer as the rest of the
/// window), menu buttons get a drawn chevron, and a button whose menu is open stays lit.
/// </summary>
internal sealed class CommandButton : Button
{
    public const int CommandHeight = 34;
    public const int ChipHeight = 30;
    private const int IconBox = 16;
    private const int PadX = 14;
    private const int Gap = 8;
    private const int ChevronWidth = 8;

    private static readonly Font CommandFont = new("Segoe UI Semibold", 9.25f);
    private static readonly Font ChipFont = new("Segoe UI Semibold", 8.75f);

    private bool _hovering;
    private bool _pressing;
    private float _hover;
    private readonly Func<float, bool> _animate;
    private CommandKind _kind = CommandKind.Secondary;
    private CommandGlyph _glyph;
    private bool _dropDown;
    private bool _compact;
    private bool _active;
    private Color _tone = Color.Empty;

    public CommandButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = CommandFont;
        Height = CommandHeight;
        UseMnemonic = true;
        _animate = AnimateHover;
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CommandKind Kind
    {
        get => _kind;
        set
        {
            if (_kind == value) return;
            _kind = value;
            Font = value == CommandKind.Chip ? ChipFont : CommandFont;
            Height = value == CommandKind.Chip ? ChipHeight : CommandHeight;
            FitToContent();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public CommandGlyph Glyph
    {
        get => _glyph;
        set { if (_glyph != value) { _glyph = value; FitToContent(); } }
    }

    /// <summary>The button opens a menu: it gets a chevron, and Alt+Down opens it too.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool DropDown
    {
        get => _dropDown;
        set { if (_dropDown != value) { _dropDown = value; FitToContent(); } }
    }

    /// <summary>Icon only, for a narrow window. The name stays in the tooltip and for screen readers.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Compact
    {
        get => _compact;
        set { if (_compact != value) { _compact = value; FitToContent(); } }
    }

    /// <summary>Lit while the button's menu is open.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active
    {
        get => _active;
        set { if (_active != value) { _active = value; Invalidate(); } }
    }

    /// <summary>A chip's status colour: its dot, its text, and a tint of its body.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Tone
    {
        get => _tone;
        set { if (_tone != value) { _tone = value; FitToContent(); } }
    }

    /// <summary>Draws the keyboard-focus ring without the focus, for the off-screen QA renders only.</summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowFocusRing { get; set; }

    /// <summary>The narrowest this button can be with its label showing, and with only its icon.</summary>
    public int FullWidth => MeasureWidth(compact: false);
    public int CompactWidth => MeasureWidth(compact: true);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        FitToContent();
    }

    public override Size GetPreferredSize(Size proposedSize) => new(MeasureWidth(_compact), Height);

    private void FitToContent()
    {
        Width = MeasureWidth(_compact);
        Invalidate();
    }

    private bool ShowsIcon => _kind == CommandKind.Chip ? !_tone.IsEmpty : _glyph != CommandGlyph.None;
    private int IconWidth => _kind == CommandKind.Chip ? 8 : IconBox;
    private TextFormatFlags TextFlags =>
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter |
        (ShowKeyboardCues ? TextFormatFlags.Default : TextFormatFlags.HidePrefix);

    private int MeasureWidth(bool compact)
    {
        var pad = _kind == CommandKind.Chip ? 12 : PadX;
        var width = pad * 2;
        var icon = ShowsIcon;
        if (icon) width += IconWidth;
        if (compact && icon)
        {
            // An icon-only button is a square, a little wider when it also carries a chevron.
            if (_dropDown) width += 6 + ChevronWidth;
            return Math.Max(width - 4, Height);
        }
        if (Text.Length > 0)
        {
            if (icon) width += Gap;
            width += TextRenderer.MeasureText(Text, Font, Size.Empty, TextFlags).Width;
        }
        if (_dropDown) width += Gap + ChevronWidth;
        return width;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        // Whatever is really behind the button (the header's glow, a card) rather than a flat guess.
        ButtonRenderer.DrawParentBackground(g, ClientRectangle, this);
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var radius = _kind == CommandKind.Chip ? rect.Height / 2f : 8f;
        var look = Look();
        using var path = Glass.RoundedPath(rect, radius);

        if (Glass.Enabled && Enabled && _kind != CommandKind.Chip)
        {
            // A soft top-to-bottom fall of light. A see-through sheet gets it from its opacity, a solid fill from its colour.
            var (top, bottom) = look.Fill.A < 255
                ? (Color.FromArgb(Math.Min(255, look.Fill.A + 12), look.Fill), Color.FromArgb(Math.Max(0, look.Fill.A - 8), look.Fill))
                : (Glass.Mix(look.Fill, Color.White, 0.12f), Glass.Mix(look.Fill, Color.Black, 0.10f));
            using var body = new LinearGradientBrush(RectangleF.Inflate(rect, 1, 1), top, bottom, LinearGradientMode.Vertical);
            g.FillPath(body, path);
        }
        else
        {
            using var body = new SolidBrush(look.Fill);
            g.FillPath(body, path);
        }
        using (var edge = new Pen(look.Border, 1f))
            g.DrawPath(edge, path);

        // Keyboard focus: a ring just inside the edge, so it never gets clipped by a neighbour.
        if ((Focused && ShowFocusCues || ShowFocusRing) && Enabled)
        {
            using var ringPath = Glass.RoundedPath(RectangleF.Inflate(rect, -1.5f, -1.5f), Math.Max(1, radius - 1.5f));
            using var ring = new Pen(_kind == CommandKind.Primary ? Color.FromArgb(235, 255, 255, 255) : Color.FromArgb(230, 124, 196, 255), 2f);
            g.DrawPath(ring, ringPath);
        }

        var pad = _kind == CommandKind.Chip ? 12 : PadX;
        var icon = ShowsIcon;
        var iconOnly = _compact && icon;
        var content = MeasureWidth(_compact) - pad * 2;
        if (iconOnly) content = IconWidth + (_dropDown ? 6 + ChevronWidth : 0);
        var x = (Width - content) / 2f;
        var midY = Height / 2f;

        if (icon)
        {
            if (_kind == CommandKind.Chip)
            {
                using var dot = new SolidBrush(look.Ink);
                g.FillEllipse(dot, x, midY - 4, 8, 8);
            }
            else DrawGlyph(g, _glyph, new RectangleF(x, midY - IconBox / 2f, IconBox, IconBox), look.Ink);
            x += IconWidth + (iconOnly ? 6 : Gap);
        }

        if (!iconOnly && Text.Length > 0)
        {
            var textWidth = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFlags).Width;
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)Math.Round(x), 0, textWidth + 2, Height - 1), look.Ink, TextFlags);
            x += textWidth + Gap;
        }

        if (_dropDown)
        {
            using var pen = new Pen(Color.FromArgb(Enabled ? 220 : 110, look.Ink), 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(x, midY - 1.5f), new PointF(x + ChevronWidth / 2f, midY + 2.5f), new PointF(x + ChevronWidth, midY - 1.5f)]);
        }
    }

    private readonly record struct Colors(Color Fill, Color Border, Color Ink);

    // Every state of every kind, from the theme. Outlined buttons are a translucent sheet, so the header's glow
    // shows through them; the filled one is a deeper blue than the accent so white text on it stays readable.
    private Colors Look()
    {
        var dark = AppTheme.IsDark;
        var lit = _pressing || _active;
        switch (_kind)
        {
            case CommandKind.Primary:
            {
                var rest = dark ? Color.FromArgb(31, 111, 214) : Color.FromArgb(22, 98, 196);
                var over = dark ? Color.FromArgb(48, 130, 235) : Color.FromArgb(30, 112, 214);
                var down = dark ? Color.FromArgb(24, 92, 182) : Color.FromArgb(17, 80, 162);
                if (!Enabled)
                    return new(Color.FromArgb(90, rest), Color.FromArgb(60, 255, 255, 255), Color.FromArgb(150, 255, 255, 255));
                var fill = lit ? down : Glass.Mix(rest, over, _hover);
                return new(fill, Glass.Mix(fill, Color.White, 0.28f), Color.White);
            }
            case CommandKind.Danger:
            {
                var tone = AppTheme.Critical;
                var ink = dark ? Glass.Mix(tone, Color.White, 0.55f) : tone;
                var fill = Glass.Mix(AppTheme.Surface, tone, lit ? 0.34f : 0.18f + 0.1f * _hover);
                return Enabled
                    ? new(fill, Color.FromArgb(lit ? 255 : 170 + (int)(60 * _hover), tone), ink)
                    : new(Color.FromArgb(70, fill), Color.FromArgb(70, tone), Color.FromArgb(120, ink));
            }
            case CommandKind.Chip:
            {
                var tone = _tone.IsEmpty ? AppTheme.Accent : _tone;
                var ink = dark ? Glass.Mix(tone, Color.White, 0.18f) : Glass.Mix(tone, Color.Black, 0.15f);
                var fill = Glass.Mix(AppTheme.Surface, tone, lit ? 0.12f : 0.17f + 0.09f * _hover);
                return new(fill, Color.FromArgb(120 + (int)(80 * _hover), tone), ink);
            }
            default:
            {
                var sheet = dark ? Color.FromArgb(255, 255, 255) : Color.FromArgb(10, 30, 60);
                if (!Enabled)
                    return new(Color.FromArgb(dark ? 8 : 6, sheet), Color.FromArgb(dark ? 26 : 34, sheet), Color.FromArgb(110, AppTheme.Muted));
                if (lit)
                    return new(Color.FromArgb(dark ? 150 : 255, AppTheme.AccentSoft), Color.FromArgb(200, AppTheme.Accent), AppTheme.Text);
                var fill = Color.FromArgb((dark ? 15 : 10) + (int)((dark ? 17 : 14) * _hover), sheet);
                var border = Glass.Mix(Color.FromArgb(dark ? 58 : 70, sheet), Color.FromArgb(170, AppTheme.Accent), _hover * 0.75f);
                return new(fill, border, AppTheme.Text);
            }
        }
    }

    // Line icons in a 16 x 16 box, drawn like the navigation rail's, so they need no icon font and stay sharp.
    private static void DrawGlyph(Graphics g, CommandGlyph glyph, RectangleF box, Color color)
    {
        using var pen = new Pen(color, 1.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var brush = new SolidBrush(color);
        PointF P(float x, float y) => new(box.X + x, box.Y + y);
        void Line(float x1, float y1, float x2, float y2) => g.DrawLine(pen, P(x1, y1), P(x2, y2));

        switch (glyph)
        {
            case CommandGlyph.Scan: // a frame's four corners and the line that sweeps it
                g.DrawLines(pen, [P(1.5f, 5), P(1.5f, 1.5f), P(5, 1.5f)]);
                g.DrawLines(pen, [P(11, 1.5f), P(14.5f, 1.5f), P(14.5f, 5)]);
                g.DrawLines(pen, [P(14.5f, 11), P(14.5f, 14.5f), P(11, 14.5f)]);
                g.DrawLines(pen, [P(5, 14.5f), P(1.5f, 14.5f), P(1.5f, 11)]);
                Line(4.5f, 8, 11.5f, 8);
                break;
            case CommandGlyph.Stop: // a filled square
                using (var square = Glass.RoundedPath(new RectangleF(box.X + 3.5f, box.Y + 3.5f, 9, 9), 2))
                    g.FillPath(brush, square);
                break;
            case CommandGlyph.Upgrade: // an arrow rising out of a circle
                g.DrawEllipse(pen, box.X + 1.5f, box.Y + 1.5f, 13, 13);
                Line(8, 11.5f, 8, 4.8f);
                g.DrawLines(pen, [P(5.2f, 7.4f), P(8, 4.6f), P(10.8f, 7.4f)]);
                break;
            case CommandGlyph.Drivers: // a download into a tray
                Line(8, 1.8f, 8, 9.8f);
                g.DrawLines(pen, [P(4.8f, 6.8f), P(8, 10), P(11.2f, 6.8f)]);
                g.DrawLines(pen, [P(2, 11), P(2, 14.2f), P(14, 14.2f), P(14, 11)]);
                break;
            case CommandGlyph.More: // three dots
                foreach (var x in new[] { 3f, 8f, 13f })
                    g.FillEllipse(brush, box.X + x - 1.4f, box.Y + 8 - 1.4f, 2.8f, 2.8f);
                break;
        }
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; StartHover(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressing = false; StartHover(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { _pressing = true; Invalidate(); } base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { _pressing = false; base.OnLostFocus(e); Invalidate(); }

    // Space and Enter show the pressed look while held, as a mouse press does.
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Space or Keys.Enter && !_pressing) { _pressing = true; Invalidate(); }
        // Alt+Down or Down opens a menu button's menu, as it does on any Windows drop-down.
        if (_dropDown && e.KeyCode == Keys.Down) { e.Handled = true; PerformClick(); }
        base.OnKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (_pressing) { _pressing = false; Invalidate(); }
        base.OnKeyUp(e);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (_dropDown && (keyData & Keys.KeyCode) == Keys.Down) || base.IsInputKey(keyData);

    // The underlines under access keys appear when Alt is pressed; the label has to be drawn again with them.
    protected override void OnChangeUICues(UICuesEventArgs e)
    {
        base.OnChangeUICues(e);
        if (e.ChangeKeyboard || e.ChangeFocus) Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        if (!Enabled) { _hovering = false; _pressing = false; _hover = 0; }
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        base.OnEnabledChanged(e);
        Invalidate();
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
        _hover = Motion.Approach(_hover, target, 18f, seconds);
        if (Math.Abs(_hover - target) < 0.01f) _hover = target;
        Invalidate();
        return _hover != target;
    }
}
