namespace BoardScout.UI;

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

internal static class AppTheme
{
    public static bool IsDark { get; private set; }

    public static Color Background => IsDark ? Color.FromArgb(6, 11, 16) : Color.FromArgb(240, 243, 247);
    public static Color Surface => IsDark ? Color.FromArgb(12, 22, 32) : Color.White;
    public static Color SurfaceRaised => IsDark ? Color.FromArgb(17, 30, 44) : Color.FromArgb(246, 248, 251);
    public static Color Border => IsDark ? Color.FromArgb(22, 32, 48) : Color.FromArgb(216, 224, 234);
    public static Color Text => IsDark ? Color.FromArgb(232, 237, 244) : Color.FromArgb(17, 26, 40);
    public static Color Muted => IsDark ? Color.FromArgb(138, 155, 178) : Color.FromArgb(74, 90, 114);
    public static Color Accent => IsDark ? Color.FromArgb(61, 158, 255) : Color.FromArgb(26, 111, 212);
    public static Color AccentSoft => IsDark ? Color.FromArgb(16, 40, 65) : Color.FromArgb(228, 239, 251);
    public static Color Good => IsDark ? Color.FromArgb(16, 185, 129) : Color.FromArgb(13, 148, 101);
    public static Color Warning => IsDark ? Color.FromArgb(245, 158, 11) : Color.FromArgb(194, 120, 10);
    public static Color Critical => IsDark ? Color.FromArgb(239, 68, 68) : Color.FromArgb(212, 32, 32);
    public static Color Purple => IsDark ? Color.FromArgb(167, 139, 250) : Color.FromArgb(109, 40, 217);
    public static Color Teal => IsDark ? Color.FromArgb(45, 212, 191) : Color.FromArgb(13, 126, 114);
    public static Color Orange => IsDark ? Color.FromArgb(251, 146, 60) : Color.FromArgb(194, 80, 16);

    public static void SetDarkMode(bool enabled) => IsDark = enabled;

    public static void ApplyWindowTheme(Form form)
    {
        if (!form.IsHandleCreated) return;
        var enabled = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(form.Handle, 20, ref enabled, sizeof(int)) != 0)
            DwmSetWindowAttribute(form.Handle, 19, ref enabled, sizeof(int));
    }

    public static Icon CreateAppIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var tile = new SolidBrush(Accent);
        graphics.FillRoundedRectangle(tile, new Rectangle(1, 1, 30, 30), 7);
        using var line = new Pen(Color.White, 2);
        graphics.DrawRoundedRectangle(line, new Rectangle(9, 9, 14, 14), 2);
        graphics.DrawLine(line, 4, 12, 9, 12);
        graphics.DrawLine(line, 4, 19, 9, 19);
        graphics.DrawLine(line, 23, 12, 28, 12);
        graphics.DrawLine(line, 23, 19, 28, 19);
        graphics.DrawLine(line, 12, 4, 12, 9);
        graphics.DrawLine(line, 19, 23, 19, 28);

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary
            ? (IsDark ? Color.FromArgb(48, 144, 174) : Color.FromArgb(19, 89, 113))
            : SurfaceRaised;
        button.FlatAppearance.MouseDownBackColor = primary
            ? (IsDark ? Color.FromArgb(37, 120, 146) : Color.FromArgb(14, 75, 96))
            : AccentSoft;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : Text;
        button.Padding = new Padding(12, 2, 12, 2);
        button.Height = 36;
        button.AutoSize = true;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 9);
        ApplyRoundedRegion(button, 10);
    }

    public static void StyleFeatureButton(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Good;
        button.FlatAppearance.MouseOverBackColor = IsDark
            ? Color.FromArgb(24, 58, 46) : Color.FromArgb(229, 244, 236);
        button.FlatAppearance.MouseDownBackColor = IsDark
            ? Color.FromArgb(14, 44, 34) : Color.FromArgb(210, 236, 222);
        button.BackColor = IsDark
            ? Color.FromArgb(14, 38, 30) : Color.FromArgb(240, 250, 245);
        button.ForeColor = Good;
        button.Padding = new Padding(12, 2, 12, 2);
        button.Height = 36;
        button.AutoSize = true;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 9);
        ApplyRoundedRegion(button, 10);
    }

    private static void ApplyRoundedRegion(Control control, int radius)
    {
        void Update()
        {
            if (control.Width <= 0 || control.Height <= 0) return;
            var path = new GraphicsPath();
            var d = radius * 2;
            var r = new Rectangle(0, 0, control.Width, control.Height);
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            control.Region?.Dispose();
            control.Region = new Region(path);
            path.Dispose();
        }
        Update();
        control.Resize += (_, _) => Update();
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.FixedSingle;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border;
        grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
        grid.ColumnHeadersHeight = 38;
        grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        grid.RowTemplate.Height = 36;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceRaised,
            ForeColor = Text,
            SelectionBackColor = SurfaceRaised,
            Font = new Font("Segoe UI Semibold", 9),
            Padding = new Padding(8, 4, 8, 4)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Text,
            SelectionBackColor = AccentSoft,
            SelectionForeColor = Text,
            Padding = new Padding(8, 4, 8, 4)
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = IsDark
            ? Color.FromArgb(18, 26, 34)
            : Color.FromArgb(250, 251, 252);
        grid.RowHeadersVisible = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
    }
}
