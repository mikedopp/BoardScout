namespace BoardScout.UI;

public sealed class ContentTabControl : TabControl
{
    private const int TcmAdjustRect = 0x1328;

    public ContentTabControl()
    {
        Appearance = TabAppearance.FlatButtons;
        SizeMode = TabSizeMode.Fixed;
        ItemSize = new Size(0, 1);
        Multiline = true;
    }

    public override Rectangle DisplayRectangle => ClientRectangle;

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == TcmAdjustRect && !DesignMode)
        {
            message.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref message);
    }
}
