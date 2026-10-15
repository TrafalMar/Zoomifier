namespace Zoomifier;

/// <summary>
/// Lens mode: a click-through window lying exactly over the selected area, showing the area's centre magnified.
/// Excluded from capture, so it never zooms itself; rendered by the same GPU pipeline as the viewer.
/// </summary>
internal sealed class LensForm : Form
{
    private DxRenderer? _dx;

    public LensForm()
    {
        Text = "Zoomifier lens";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        BackColor = Color.Black;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Fully opaque, but layered + transparent so every click goes to whatever is underneath.
        Native.SetLayeredWindowAttributes(Handle, 0, 255, Native.LWA_ALPHA);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        _dx = new DxRenderer(Handle);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _dx?.Dispose();
        _dx = null;
        base.OnFormClosed(e);
    }

    /// <summary>The part of <paramref name="area"/> that, magnified by <paramref name="zoom"/>, fills the area.</summary>
    public static Rectangle Source(Rectangle area, double zoom)
    {
        int w = Math.Max(1, (int)Math.Round(area.Width / zoom)), h = Math.Max(1, (int)Math.Round(area.Height / zoom));
        return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    public void Present(Rectangle area, double zoom, bool smooth, bool showCursor, bool showFps)
    {
        if (!IsHandleCreated) CreateHandle();
        if (Bounds != area) Bounds = area;
        _dx!.SetView(new ViewState(Source(area, zoom), area.Size, true, smooth, showCursor, showFps));
        if (!Visible) Show();
    }

    public void Conceal()
    {
        _dx?.SetView(null);
        if (Visible) Hide();
    }
}

/// <summary>Polls a key or mouse button every ~2 ms on a background thread; works while games and other apps have focus.</summary>
internal sealed class HoldWatcher : IDisposable
{
    private readonly Thread _thread;
    private volatile int _vk;
    private volatile bool _stop;

    /// <summary>Raised on the watcher thread whenever the key goes down or up.</summary>
    public event Action<bool>? Changed;
    public bool IsDown { get; private set; }

    public HoldWatcher(int vk)
    {
        _vk = vk;
        _thread = new Thread(Run) { IsBackground = true, Name = "Zoomifier lens key" };
        _thread.Start();
    }

    public int Key
    {
        get => _vk;
        set => _vk = value;
    }

    private void Run()
    {
        Native.timeBeginPeriod(1);
        try
        {
            while (!_stop)
            {
                int vk = _vk, alias = Shortcuts.HoldKeyAlias(vk);
                bool down = vk != 0 && ((Native.GetAsyncKeyState(vk) & 0x8000) != 0
                                        || (alias != 0 && (Native.GetAsyncKeyState(alias) & 0x8000) != 0));
                if (down != IsDown)
                {
                    IsDown = down;
                    Changed?.Invoke(down);
                }
                Thread.Sleep(2);
            }
        }
        finally { Native.timeEndPeriod(1); }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(500);
    }
}
