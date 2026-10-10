using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Zoomifier;

/// <summary>Displays the selected source region, magnified, on the monitor of your choice.</summary>
internal sealed class ViewerForm : Form
{
    private const int HotkeyId = 1;

    private readonly Settings _s;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 15 };
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu = new();
    private readonly Font _helpFont = new("Segoe UI", 18f, GraphicsUnit.Pixel);
    private readonly Font _emptyFont = new("Segoe UI Semibold", 30f, GraphicsUnit.Pixel);
    private RegionBorderForm? _border;
    private HintForm? _hint;
    private readonly RenderSurface _surface = new() { Dock = DockStyle.Fill, Visible = false };
    private DxRenderer? _dx;
    private Rectangle _windowedBounds;
    private bool _isFullscreen;
    private bool _selecting;
    private Point? _dragStart;
    private Rectangle _dragRegion;
    private Bitmap? _frame;

    private IntPtr _cursorHandle;
    private Point _cursorHotspot;
    private Size _cursorSize;

    public ViewerForm(Settings settings)
    {
        _s = settings;
        Text = "Zoomifier";
        Icon = AppIcon.Load(SystemInformation.IconSize);
        AutoScaleMode = AutoScaleMode.None;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        KeyPreview = true;
        MinimumSize = new Size(200, 150);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.Opaque, true);

        _menu.Opening += (_, _) => PopulateMenu();
        PopulateMenu();
        ContextMenuStrip = _menu;

        _tray = new NotifyIcon { Icon = AppIcon.Load(SystemInformation.SmallIconSize), Text = "Zoomifier", ContextMenuStrip = _menu, Visible = true };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) Activate(); };
        _tray.DoubleClick += (_, _) => BeginSelect();

        _timer.Tick += (_, _) => { if (_dx == null) RenderFrame(); };
        Controls.Add(_surface);
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        var screen = PickInitialScreen();
        // Every launch starts with a fresh selection rather than the region from last time.
        _s.Region = Rectangle.Empty;
        _windowedBounds = CenteredOn(screen);
        Bounds = _windowedBounds;
        if (_s.Fullscreen) EnterFullscreen(screen);
    }

    // ---------- lifecycle ----------

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Keep the viewer out of its own capture, so it works even when it overlaps the source.
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
        if (!Native.RegisterHotKey(Handle, HotkeyId, Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT, (uint)Keys.Z))
            _tray?.ShowBalloonTip(3000, "Zoomifier", "Ctrl+Alt+Z is used by another app; press R in the viewer or use the tray menu.", ToolTipIcon.Warning);
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        base.OnHandleDestroyed(e);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _dx = new DxRenderer(_surface.Handle);
        _dx.Failed += () => BeginInvoke(FallBackToGdi);
        _hint = new HintForm(this, _s, () => _isFullscreen);
        _hint.Clicked += () => { _s.HintExpanded ^= true; _s.Save(); UpdateHint(); };
        if (HasValidRegion()) { UpdateBorder(); _timer.Start(); }
        else BeginSelect();
        UpdateHint();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY && (int)m.WParam == HotkeyId) BeginSelect();
        base.WndProc(ref m);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        // We render in physical pixels; don't let WinForms resize the window when it crosses monitors.
        e.Cancel = true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _timer.Stop();
        _dx?.Dispose();
        _dx = null;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _s.Save();
        _tray.Visible = false;
        _tray.Dispose();
        _border?.Close();
        _hint?.Close();
        _frame?.Dispose();
        _helpFont.Dispose();
        _emptyFont.Dispose();
        base.OnFormClosing(e);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            if (_isFullscreen) Bounds = CurrentScreen().Bounds;
            if (!_s.Region.IsEmpty && !HasValidRegion()) _s.Region = Rectangle.Empty;
            UpdateBorder();
            UpdateHint();
            ViewChanged();
        });
    }

    // ---------- region selection ----------

    private void BeginSelect()
    {
        if (_selecting) return;
        _selecting = true;
        _timer.Stop();
        _border?.Hide();
        UpdateHint();
        ViewChanged();
        RegionSelector.Begin(result =>
        {
            _selecting = false;
            if (result is Rectangle r)
            {
                _s.Region = r;
                MoveOffSourceMonitor(r);
                _s.Save();
            }
            if (HasValidRegion()) _timer.Start();
            UpdateBorder();
            UpdateHint();
            ViewChanged();
        });
    }

    /// <summary>Pushes the current view to the GPU renderer (or repaints the GDI fallback / empty state).</summary>
    private void ViewChanged()
    {
        bool live = !_selecting && HasValidRegion();
        if (_dx != null)
        {
            // While selecting, keep the last frame up; the renderer is paused so it won't zoom the selection overlay.
            if (live) _surface.Visible = true;
            else if (!_selecting) _surface.Visible = false;
            _dx.SetView(live ? new ViewState(_s.Region, ClientSize, _s.KeepAspect, _s.Smooth, _s.ShowCursor) : null);
        }
        Invalidate();
    }

    private void FallBackToGdi()
    {
        _dx?.Dispose();
        _dx = null;
        _surface.Visible = false;
        Invalidate();
    }

    private bool HasValidRegion() =>
        _s.Region.Width > 0 && _s.Region.Height > 0 && SystemInformation.VirtualScreen.Contains(_s.Region);

    /// <summary>A fullscreen viewer on top of its own source is useless, so hop to another monitor.</summary>
    private void MoveOffSourceMonitor(Rectangle region)
    {
        if (!_isFullscreen) return;
        var source = Screen.FromRectangle(region);
        if (CurrentScreen().DeviceName != source.DeviceName) return;
        var other = Screen.AllScreens.FirstOrDefault(s => s.DeviceName != source.DeviceName);
        if (other != null) ShowOn(other);
    }

    private Rectangle SourceScreenBounds() => Screen.FromRectangle(_s.Region).Bounds;

    private void SetRegion(Rectangle r)
    {
        var scr = SourceScreenBounds();
        r.Width = Math.Min(r.Width, scr.Width);
        r.Height = Math.Min(r.Height, scr.Height);
        r.X = Math.Clamp(r.X, scr.Left, scr.Right - r.Width);
        r.Y = Math.Clamp(r.Y, scr.Top, scr.Bottom - r.Height);
        if (r == _s.Region) return;
        _s.Region = r;
        UpdateBorder();
        ViewChanged();
    }

    private void Zoom(double factor, Point? anchorClient = null)
    {
        var r = _s.Region;
        if (r.IsEmpty) return;
        int w = (int)Math.Round(r.Width * factor), h = (int)Math.Round(r.Height * factor);
        if (factor < 1 && (w < 8 || h < 8)) return;
        var scr = SourceScreenBounds();
        if (factor > 1 && (w > scr.Width || h > scr.Height)) return;

        // Keep the source point under the anchor fixed (mouse position, or the centre).
        var dst = Dest(r);
        PointF a = anchorClient is Point p && dst.Contains(p)
            ? ToSource(p, r, dst)
            : new PointF(r.X + r.Width / 2f, r.Y + r.Height / 2f);
        int x = (int)Math.Round(a.X - (a.X - r.X) * factor);
        int y = (int)Math.Round(a.Y - (a.Y - r.Y) * factor);
        SetRegion(new Rectangle(x, y, w, h));
    }

    private void Pan(int dx, int dy)
    {
        var r = _s.Region;
        r.Offset(dx, dy);
        SetRegion(r);
    }

    private void UpdateBorder()
    {
        if (!_s.ShowBorder || _selecting || !HasValidRegion())
        {
            _border?.Hide();
            return;
        }
        _border ??= new RegionBorderForm();
        _border.SetRegion(_s.Region);
        if (!_border.Visible) _border.Show();
    }

    private void UpdateHint()
    {
        if (_hint == null) return;
        if (!_s.ShowHint || _selecting || !Visible || WindowState == FormWindowState.Minimized)
        {
            _hint.Hide();
            return;
        }
        _hint.Present(RectangleToScreen(ClientRectangle), Native.GetDpiForWindow(Handle) / 96f, _s.HintExpanded);
    }

    // ---------- window placement ----------

    private Screen CurrentScreen() => Screen.FromRectangle(Bounds);

    private Screen PickInitialScreen()
    {
        var screens = Screen.AllScreens;
        var saved = screens.FirstOrDefault(s => s.DeviceName == _s.ViewerScreen);
        if (saved != null) return saved;
        if (!_s.Region.IsEmpty)
        {
            var source = Screen.FromRectangle(_s.Region);
            var other = screens.FirstOrDefault(s => s.DeviceName != source.DeviceName);
            if (other != null) return other;
        }
        return Screen.PrimaryScreen ?? screens[0];
    }

    private static Rectangle CenteredOn(Screen s)
    {
        var wa = s.WorkingArea;
        return new Rectangle(wa.X + wa.Width / 4, wa.Y + wa.Height / 4, wa.Width / 2, wa.Height / 2);
    }

    private void EnterFullscreen(Screen screen)
    {
        if (!_isFullscreen) _windowedBounds = Bounds;
        _isFullscreen = _s.Fullscreen = true;
        WindowState = FormWindowState.Normal;
        FormBorderStyle = FormBorderStyle.None;
        Bounds = screen.Bounds;
    }

    private void ExitFullscreen()
    {
        var screen = CurrentScreen();
        _isFullscreen = _s.Fullscreen = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        Bounds = screen.Bounds.IntersectsWith(_windowedBounds) ? _windowedBounds : CenteredOn(screen);
    }

    private void ToggleFullscreen()
    {
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen(CurrentScreen());
        _s.Save();
    }

    private void ShowOn(Screen screen)
    {
        _s.ViewerScreen = screen.DeviceName;
        if (_isFullscreen) EnterFullscreen(screen);
        else Bounds = CenteredOn(screen);
        _s.Save();
    }

    private void NextMonitor()
    {
        var screens = Screen.AllScreens;
        var current = CurrentScreen().DeviceName;
        int i = Array.FindIndex(screens, s => s.DeviceName == current);
        ShowOn(screens[(i + 1) % screens.Length]);
    }

    // ---------- rendering ----------

    private Rectangle Dest(Rectangle src) => DxRenderer.Dest(ClientRectangle, src, _s.KeepAspect);

    private static PointF ToSource(Point p, Rectangle src, Rectangle dst) => new(
        src.X + (p.X - dst.X) * src.Width / (float)dst.Width,
        src.Y + (p.Y - dst.Y) * src.Height / (float)dst.Height);

    private void RenderFrame()
    {
        var src = _s.Region;
        var client = ClientRectangle;
        if (_selecting || src.Width <= 0 || src.Height <= 0 || client.Width <= 0 || client.Height <= 0
            || WindowState == FormWindowState.Minimized)
            return;

        var dst = Dest(src);
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr dc = Native.GetDC(Handle);
        try
        {
            // Letterbox bars.
            if (dst.Top > 0) Native.PatBlt(dc, 0, 0, client.Width, dst.Top, Native.BLACKNESS);
            if (dst.Bottom < client.Height) Native.PatBlt(dc, 0, dst.Bottom, client.Width, client.Height - dst.Bottom, Native.BLACKNESS);
            if (dst.Left > 0) Native.PatBlt(dc, 0, dst.Top, dst.Left, dst.Height, Native.BLACKNESS);
            if (dst.Right < client.Width) Native.PatBlt(dc, dst.Right, dst.Top, client.Width - dst.Right, dst.Height, Native.BLACKNESS);

            if (_s.Smooth) DrawSmooth(dc, screenDc, src, dst);
            else
            {
                Native.SetStretchBltMode(dc, Native.COLORONCOLOR);
                Native.StretchBlt(dc, dst.X, dst.Y, dst.Width, dst.Height, screenDc, src.X, src.Y, src.Width, src.Height, Native.SRCCOPY);
            }

            if (_s.ShowCursor)
            {
                Native.IntersectClipRect(dc, dst.Left, dst.Top, dst.Right, dst.Bottom);
                DrawCursor(dc, src, dst);
            }
        }
        finally
        {
            Native.ReleaseDC(Handle, dc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Bilinear scaling via GDI+ (softer, more CPU) instead of nearest-neighbour StretchBlt.</summary>
    private void DrawSmooth(IntPtr dc, IntPtr screenDc, Rectangle src, Rectangle dst)
    {
        if (_frame == null || _frame.Size != src.Size)
        {
            _frame?.Dispose();
            _frame = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppRgb);
        }
        using (var g = Graphics.FromImage(_frame))
        {
            IntPtr fdc = g.GetHdc();
            Native.BitBlt(fdc, 0, 0, src.Width, src.Height, screenDc, src.X, src.Y, Native.SRCCOPY);
            g.ReleaseHdc(fdc);
        }
        using var wg = Graphics.FromHdc(dc);
        wg.CompositingMode = CompositingMode.SourceCopy;
        wg.InterpolationMode = InterpolationMode.Bilinear;
        wg.PixelOffsetMode = PixelOffsetMode.Half;
        wg.DrawImage(_frame, dst);
    }

    private void DrawCursor(IntPtr dc, Rectangle src, Rectangle dst)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
            return;

        if (ci.hCursor != _cursorHandle)
        {
            if (!Native.GetIconInfo(ci.hCursor, out var ii)) return;
            try
            {
                bool monochrome = ii.hbmColor == IntPtr.Zero;
                if (Native.GetObject(monochrome ? ii.hbmMask : ii.hbmColor, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0)
                    return;
                _cursorSize = new Size(bm.bmWidth, monochrome ? bm.bmHeight / 2 : bm.bmHeight);
                _cursorHotspot = new Point(ii.xHotspot, ii.yHotspot);
                _cursorHandle = ci.hCursor;
            }
            finally
            {
                if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
            }
        }

        double sx = dst.Width / (double)src.Width, sy = dst.Height / (double)src.Height;
        int x = dst.X + (int)Math.Round((ci.ptScreenPos.X - _cursorHotspot.X - src.X) * sx);
        int y = dst.Y + (int)Math.Round((ci.ptScreenPos.Y - _cursorHotspot.Y - src.Y) * sy);
        int w = (int)Math.Round(_cursorSize.Width * sx), h = (int)Math.Round(_cursorSize.Height * sy);
        if (x > dst.Right || y > dst.Bottom || x + w < dst.Left || y + h < dst.Top) return;
        Native.DrawIconEx(dc, x, y, ci.hCursor, w, h, 0, IntPtr.Zero, Native.DI_NORMAL);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (!_selecting && HasValidRegion())
        {
            if (_dx == null) RenderFrame();
            return;
        }
        e.Graphics.Clear(Color.Black);
        var c = ClientRectangle;
        int mid = c.Height / 2;
        TextRenderer.DrawText(e.Graphics, "No region selected", _emptyFont, new Rectangle(0, 0, c.Width, mid - 6),
            Color.Gainsboro, TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom);
        TextRenderer.DrawText(e.Graphics, "Press R or Ctrl+Alt+Z to pick an area to zoom", _helpFont,
            new Rectangle(0, mid + 6, c.Width, c.Height - mid - 6), Color.Gray, TextFormatFlags.HorizontalCenter | TextFormatFlags.Top);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateHint();
        ViewChanged();
    }

    protected override void OnMove(EventArgs e)
    {
        base.OnMove(e);
        UpdateHint();
    }

    // ---------- input ----------

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var r = _s.Region;
        int stepX = e.Shift ? 1 : Math.Max(1, r.Width / 20);
        int stepY = e.Shift ? 1 : Math.Max(1, r.Height / 20);
        switch (e.KeyCode)
        {
            case Keys.F11: ToggleFullscreen(); break;
            case Keys.Escape: if (_isFullscreen) ToggleFullscreen(); break;
            case Keys.R: BeginSelect(); break;
            case Keys.M: NextMonitor(); break;
            case Keys.A: _s.KeepAspect ^= true; ViewChanged(); break;
            case Keys.S: _s.Smooth ^= true; ViewChanged(); break;
            case Keys.C: _s.ShowCursor ^= true; ViewChanged(); break;
            case Keys.B: _s.ShowBorder ^= true; UpdateBorder(); break;
            case Keys.H when e.Shift: _s.ShowHint ^= true; break;
            case Keys.H: _s.HintExpanded ^= true; _s.ShowHint = true; break;
            case Keys.Oemplus or Keys.Add: Zoom(0.8); break;
            case Keys.OemMinus or Keys.Subtract: Zoom(1.25); break;
            case Keys.Left: Pan(-stepX, 0); break;
            case Keys.Right: Pan(stepX, 0); break;
            case Keys.Up: Pan(0, -stepY); break;
            case Keys.Down: Pan(0, stepY); break;
            default: return;
        }
        e.Handled = true;
        _s.Save();
        UpdateHint();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Zoom(e.Delta > 0 ? 0.9 : 1 / 0.9, e.Location);
        _s.Save();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HasValidRegion())
        {
            _dragStart = e.Location;
            _dragRegion = _s.Region;
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragStart is not Point start) return;
        var dst = Dest(_dragRegion);
        if (dst.Width <= 0 || dst.Height <= 0) return;
        int dx = (int)Math.Round((e.X - start.X) * _dragRegion.Width / (double)dst.Width);
        int dy = (int)Math.Round((e.Y - start.Y) * _dragRegion.Height / (double)dst.Height);
        var r = _dragRegion;
        r.Offset(-dx, -dy);
        SetRegion(r);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_dragStart == null) return;
        _dragStart = null;
        Cursor = Cursors.Default;
        _s.Save();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button == MouseButtons.Left) ToggleFullscreen();
    }

    // ---------- menu ----------

    private void PopulateMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add(new ToolStripMenuItem("Select region", null, (_, _) => BeginSelect()) { ShortcutKeyDisplayString = "Ctrl+Alt+Z" });

        var monitors = new ToolStripMenuItem("Show on monitor");
        var current = IsHandleCreated ? CurrentScreen().DeviceName : _s.ViewerScreen;
        var screens = Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            monitors.DropDownItems.Add(new ToolStripMenuItem(
                $"{i + 1}: {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (primary)" : "")}", null, (_, _) => ShowOn(s))
            { Checked = s.DeviceName == current });
        }
        _menu.Items.Add(monitors);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(Toggle("Fullscreen", "F11", _isFullscreen, ToggleFullscreen));
        _menu.Items.Add(Toggle("Keep aspect ratio", "A", _s.KeepAspect, () => { _s.KeepAspect ^= true; ViewChanged(); }));
        _menu.Items.Add(Toggle("Smooth scaling", "S", _s.Smooth, () => { _s.Smooth ^= true; ViewChanged(); }));
        _menu.Items.Add(Toggle("Show mouse cursor", "C", _s.ShowCursor, () => { _s.ShowCursor ^= true; ViewChanged(); }));
        _menu.Items.Add(Toggle("Show region border", "B", _s.ShowBorder, () => { _s.ShowBorder ^= true; UpdateBorder(); }));
        _menu.Items.Add(Toggle("Show shortcut hints", "Shift+H", _s.ShowHint, () => _s.ShowHint ^= true));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => Close());
    }

    private ToolStripMenuItem Toggle(string text, string key, bool on, Action action) =>
        new(text, null, (_, _) => { action(); _s.Save(); UpdateHint(); }) { Checked = on, ShortcutKeyDisplayString = key };
}
