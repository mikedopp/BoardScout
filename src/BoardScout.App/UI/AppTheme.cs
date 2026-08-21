namespace BoardScout.UI;

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

internal static class AppTheme
{
    // BoardScout should feel like a dependable Windows instrument, not a neon web dashboard.
    public static readonly Color Background = Color.FromArgb(244, 246, 248);
    public static readonly Color Surface = Color.White;
    public static readonly Color SurfaceRaised = Color.FromArgb(248, 250, 252);
    public static readonly Color Border = Color.FromArgb(218, 224, 231);
    public static readonly Color Text = Color.FromArgb(27, 38, 49);
    public static readonly Color Muted = Color.FromArgb(98, 113, 128);
    public static readonly Color Accent = Color.FromArgb(25, 105, 132);
    public static readonly Color AccentSoft = Color.FromArgb(228, 241, 245);
    public static readonly Color Good = Color.FromArgb(35, 123, 87);
    public static readonly Color Warning = Color.FromArgb(174, 99, 20);
    public static readonly Color Critical = Color.FromArgb(184, 55, 67);
    public static readonly Color Purple = Color.FromArgb(103, 82, 157);

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

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(19, 89, 113) : SurfaceRaised;
        button.FlatAppearance.MouseDownBackColor = primary ? Color.FromArgb(14, 75, 96) : AccentSoft;
        button.BackColor = primary ? Accent : Surface;
        button.ForeColor = primary ? Color.White : Text;
        button.Padding = new Padding(12, 2, 12, 2);
        button.Height = 36;
        button.AutoSize = true;
        button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI Semibold", 9);
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
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(250, 251, 252);
        grid.RowHeadersVisible = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
    }
}
