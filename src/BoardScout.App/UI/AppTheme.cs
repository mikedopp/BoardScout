namespace BoardScout.UI;

internal static class AppTheme
{
    public static readonly Color Background = Color.FromArgb(7, 13, 19);
    public static readonly Color Surface = Color.FromArgb(13, 24, 35);
    public static readonly Color SurfaceRaised = Color.FromArgb(18, 34, 48);
    public static readonly Color Border = Color.FromArgb(31, 51, 72);
    public static readonly Color Text = Color.FromArgb(232, 237, 244);
    public static readonly Color Muted = Color.FromArgb(138, 155, 178);
    public static readonly Color Accent = Color.FromArgb(61, 158, 255);
    public static readonly Color Good = Color.FromArgb(16, 185, 129);
    public static readonly Color Warning = Color.FromArgb(245, 158, 11);
    public static readonly Color Critical = Color.FromArgb(239, 68, 68);
    public static readonly Color Purple = Color.FromArgb(167, 139, 250);

    public static void StyleButton(Button button, bool primary = false)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = primary ? Accent : Border;
        button.BackColor = primary ? Color.FromArgb(26, 111, 212) : SurfaceRaised;
        button.ForeColor = Text;
        button.Padding = new Padding(10, 2, 10, 2);
        button.Height = 34;
        button.AutoSize = true;
        button.Cursor = Cursors.Hand;
    }

    public static void StyleGrid(DataGridView grid)
    {
        grid.BackgroundColor = Surface;
        grid.BorderStyle = BorderStyle.None;
        grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        grid.GridColor = Border;
        grid.EnableHeadersVisualStyles = false;
        grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = SurfaceRaised,
            ForeColor = Muted,
            SelectionBackColor = SurfaceRaised,
            Font = new Font("Segoe UI Semibold", 9),
            Padding = new Padding(4)
        };
        grid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = Surface,
            ForeColor = Text,
            SelectionBackColor = Color.FromArgb(27, 72, 108),
            SelectionForeColor = Text,
            Padding = new Padding(4)
        };
        grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(11, 22, 32);
        grid.RowHeadersVisible = false;
        grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
    }
}
