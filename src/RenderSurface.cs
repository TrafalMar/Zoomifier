namespace Zoomifier;

/// <summary>
/// Child window that hosts the GPU swap chain. It is transparent to the mouse, so clicks, drags, wheel and
/// the context menu all reach the viewer form underneath.
/// </summary>
internal sealed class RenderSurface : Control
{
    private const int WM_NCHITTEST = 0x84, HTTRANSPARENT = -1;

    public RenderSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
        TabStop = false;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST) { m.Result = HTTRANSPARENT; return; }
        base.WndProc(ref m);
    }

    // The swap chain owns the pixels.
    protected override void OnPaint(PaintEventArgs e) { }
}
