namespace Zoomifier;

/// <summary>Click-through frame drawn just outside the source region so you can see what is being zoomed.</summary>
internal sealed class RegionBorderForm : Form
{
    private const int Thickness = 3;

    public RegionBorderForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.Magenta;
        TransparencyKey = Color.Magenta;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    public void SetRegion(Rectangle region)
    {
        var bounds = Rectangle.Inflate(region, Thickness, Thickness);
        if (Bounds == bounds) return;
        Bounds = bounds;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var frame = new SolidBrush(Color.OrangeRed);
        e.Graphics.FillRectangle(frame, ClientRectangle);
        e.Graphics.FillRectangle(Brushes.Magenta, Rectangle.Inflate(ClientRectangle, -Thickness, -Thickness));
    }
}
