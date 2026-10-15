using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectWrite;
using Vortice.DXGI;
using DAlphaMode = Vortice.DCommon.AlphaMode;
using DPixelFormat = Vortice.DCommon.PixelFormat;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using InterpolationMode = Vortice.Direct2D1.InterpolationMode;

namespace Zoomifier;

/// <summary>What to draw; handed from the UI thread to the render thread as an immutable snapshot.</summary>
internal sealed record ViewState(Rectangle Region, Size ClientSize, bool KeepAspect, bool Smooth, bool ShowCursor, bool ShowFps = false);

/// <summary>
/// GPU pipeline on its own thread. Each refresh it waits on the swap chain's frame-latency object (vsync-paced,
/// at most one frame queued), takes the newest desktop frame from DXGI Desktop Duplication, scales the region with
/// Direct2D, draws the cursor and presents — frame and cursor land together, so nothing flickers.
/// When duplication isn't available (rotated monitor, secure desktop, …) the region is captured with GDI instead
/// and still scaled on the GPU.
/// </summary>
internal sealed class DxRenderer : IDisposable
{
    private const int MaxFailures = 5;
    private static readonly FeatureLevel[] Levels =
        [FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0];

    private readonly IntPtr _hwnd;
    private readonly Thread _thread;
    private readonly AutoResetEvent _wake = new(false);
    private volatile ViewState? _view;
    private volatile bool _stop;

    /// <summary>Frames presented so far, and whether the last one came from Desktop Duplication (vs GDI).</summary>
    public long FramesPresented => Interlocked.Read(ref _frames);
    public bool UsingDuplication { get; private set; }
    private long _frames;

    /// <summary>Raised (on the render thread) when the GPU path keeps failing and the thread has given up.</summary>
    public event Action? Failed;

    // Everything below is owned by the render thread.
    private IDXGIFactory2? _factory;
    private ID3D11Device? _device;
    private ID3D11DeviceContext? _d3d;
    private ID2D1Factory1? _d2dFactory;
    private ID2D1Device? _d2dDevice;
    private ID2D1DeviceContext? _ctx;
    private IDXGISwapChain2? _swapChain;
    private IntPtr _latencyHandle;
    private ID2D1Bitmap1? _target;
    private Size _swapSize;

    private Rectangle _outputBounds;
    private IDXGIOutput1? _output;
    private IDXGIOutputDuplication? _dup;
    private long _nextDupAttempt;
    private ID3D11Texture2D? _copyTex;
    private ID2D1Bitmap1? _copyBmp;

    private Dib? _gdiDib;
    private ID2D1Bitmap1? _gdiBmp;

    private IntPtr _cursorHandle;
    private Point _cursorHotspot;
    private ID2D1Bitmap1? _cursorBmp;

    private IDWriteFactory? _dwrite;
    private IDWriteTextFormat? _fpsFormat;
    private ID2D1SolidColorBrush? _fpsBack, _fpsText;
    private long _fpsStart, _fpsFrames;
    private double _fps;

    public DxRenderer(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _thread = new Thread(Run) { IsBackground = true, Name = "Zoomifier render", Priority = ThreadPriority.Highest };
        _thread.Start();
    }

    /// <summary>Sets what to draw; null pauses rendering and leaves the last frame on screen.</summary>
    public void SetView(ViewState? view)
    {
        _view = view;
        _wake.Set();
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(2000);
        _wake.Dispose();
    }

    public static Rectangle Dest(Rectangle client, Rectangle src, bool keepAspect)
    {
        if (!keepAspect || src.Width <= 0 || src.Height <= 0) return client;
        double scale = Math.Min(client.Width / (double)src.Width, client.Height / (double)src.Height);
        int w = (int)Math.Round(src.Width * scale), h = (int)Math.Round(src.Height * scale);
        return new Rectangle(client.X + (client.Width - w) / 2, client.Y + (client.Height - h) / 2, w, h);
    }

    // ---------- loop ----------

    private void Run()
    {
        // MMCSS "Games": the scheduler wakes this frame-paced thread on time even when a game loads the CPU.
        uint taskIndex = 0;
        IntPtr mmcss = Native.AvSetMmThreadCharacteristics("Games", ref taskIndex);
        try { Loop(); }
        finally { if (mmcss != IntPtr.Zero) Native.AvRevertMmThreadCharacteristics(mmcss); }
    }

    private void Loop()
    {
        int failures = 0;
        while (!_stop)
        {
            var v = _view;
            if (v == null || v.Region.Width <= 0 || v.Region.Height <= 0 || v.ClientSize.Width <= 0 || v.ClientSize.Height <= 0)
            {
                _wake.WaitOne();
                continue;
            }
            try
            {
                EnsureDevice(v.Region);
                EnsureSwapChain(v.ClientSize);
                Native.WaitForSingleObjectEx(_latencyHandle, 100, true);
                if (_view is { } latest && latest.Region == v.Region && latest.ClientSize == v.ClientSize) v = latest;
                Render(v);
                _swapChain!.Present(1, PresentFlags.None).CheckError();
                Interlocked.Increment(ref _frames);
                CountFrame();
                failures = 0;
            }
            catch (Exception) when (!_stop)
            {
                ReleaseDevice();
                if (++failures >= MaxFailures)
                {
                    Failed?.Invoke();
                    return;
                }
                Thread.Sleep(200);
            }
        }
        ReleaseDevice();
    }

    private void Render(ViewState v)
    {
        var ctx = _ctx!;
        ID2D1Bitmap1 src;
        RawRectF srcRect;
        UsingDuplication = CaptureDuplication();
        if (UsingDuplication)
        {
            src = _copyBmp!;
            srcRect = new RawRectF(v.Region.Left - _outputBounds.Left, v.Region.Top - _outputBounds.Top,
                v.Region.Right - _outputBounds.Left, v.Region.Bottom - _outputBounds.Top);
        }
        else
        {
            src = CaptureGdi(v.Region);
            srcRect = new RawRectF(0, 0, v.Region.Width, v.Region.Height);
        }

        var dst = Dest(new Rectangle(Point.Empty, v.ClientSize), v.Region, v.KeepAspect);
        var mode = v.Smooth ? InterpolationMode.Linear : InterpolationMode.NearestNeighbor;
        ctx.BeginDraw();
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        ctx.DrawBitmap(src, new RawRectF(dst.Left, dst.Top, dst.Right, dst.Bottom), 1f, mode, srcRect, null);
        if (v.ShowCursor) DrawCursor(v.Region, dst, mode);
        if (v.ShowFps) DrawFps();
        ctx.EndDraw().CheckError();
    }

    // ---------- device ----------

    private void EnsureDevice(Rectangle region)
    {
        var center = new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
        if (_ctx != null && _outputBounds.Contains(center)) return;
        ReleaseDevice();

        // Duplication needs a device on the adapter that drives the source monitor.
        _factory = DXGI.CreateDXGIFactory1<IDXGIFactory2>();
        IDXGIAdapter1? adapter = null;
        IDXGIOutput1? output = null;
        _outputBounds = Screen.FromPoint(center).Bounds;
        for (uint a = 0; output == null && _factory.EnumAdapters1(a, out var candidate).Success; a++)
        {
            for (uint o = 0; candidate.EnumOutputs(o, out var op).Success; o++)
            {
                var d = op.Description;
                var b = Rectangle.FromLTRB(d.DesktopCoordinates.Left, d.DesktopCoordinates.Top, d.DesktopCoordinates.Right, d.DesktopCoordinates.Bottom);
                bool match = d.AttachedToDesktop && b.Contains(center);
                // A rotated monitor's duplicated image is unrotated; let GDI handle those.
                if (match && d.Rotation is ModeRotation.Identity or ModeRotation.Unspecified)
                    output = op.QueryInterface<IDXGIOutput1>();
                op.Dispose();
                if (match) { _outputBounds = b; break; }
            }
            if (output != null) adapter = candidate;
            else candidate.Dispose();
        }

        var flags = DeviceCreationFlags.BgraSupport;
        if (adapter == null || D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, flags, Levels, out _device, out _d3d).Failure)
        {
            output?.Dispose();
            output = null;
            if (D3D11.D3D11CreateDevice(null, DriverType.Hardware, flags, Levels, out _device, out _d3d).Failure)
                D3D11.D3D11CreateDevice(null, DriverType.Warp, flags, Levels, out _device, out _d3d).CheckError();
        }
        adapter?.Dispose();
        _output = output;
        _nextDupAttempt = 0;

        using var dxgiDevice = _device!.QueryInterface<IDXGIDevice>();
        // Jump ahead of a game's GPU work so lens/viewer frames don't queue behind it. Raising above normal
        // needs admin rights; without them the call fails and the default priority stays.
        dxgiDevice.SetGPUThreadPriority(7);
        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>();
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _ctx = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
    }

    private void EnsureSwapChain(Size size)
    {
        if (_swapChain == null)
        {
            var desc = new SwapChainDescription1
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2,
                BufferUsage = Usage.RenderTargetOutput,
                SampleDescription = new SampleDescription(1, 0),
                Scaling = Scaling.Stretch,
                SwapEffect = SwapEffect.FlipDiscard,
                AlphaMode = Vortice.DXGI.AlphaMode.Ignore,
                Flags = SwapChainFlags.FrameLatencyWaitableObject,
            };
            using (var sc = _factory!.CreateSwapChainForHwnd(_device!, _hwnd, desc))
                _swapChain = sc.QueryInterface<IDXGISwapChain2>();
            _factory.MakeWindowAssociation(_hwnd, WindowAssociationFlags.IgnoreAll);
            _swapChain.MaximumFrameLatency = 1;
            _latencyHandle = _swapChain.FrameLatencyWaitableObject;
            _swapSize = size;
        }
        else if (size != _swapSize)
        {
            _ctx!.Target = null;
            _target?.Dispose();
            _target = null;
            _swapChain.ResizeBuffers(2, (uint)size.Width, (uint)size.Height, Format.B8G8R8A8_UNorm,
                SwapChainFlags.FrameLatencyWaitableObject).CheckError();
            _swapSize = size;
        }

        if (_target == null)
        {
            using var surface = _swapChain.GetBuffer<IDXGISurface>(0);
            _target = _ctx!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
                new DPixelFormat(Format.B8G8R8A8_UNorm, DAlphaMode.Ignore), 96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
            _ctx.Target = _target;
        }
    }

    private void ReleaseDevice()
    {
        if (_ctx != null) _ctx.Target = null;
        DisposeAndNull(ref _cursorBmp);
        _cursorHandle = IntPtr.Zero;
        DisposeAndNull(ref _fpsBack);
        DisposeAndNull(ref _fpsText);
        DisposeAndNull(ref _fpsFormat);
        DisposeAndNull(ref _dwrite);
        DisposeAndNull(ref _gdiBmp);
        _gdiDib?.Dispose();
        _gdiDib = null;
        DisposeAndNull(ref _copyBmp);
        DisposeAndNull(ref _copyTex);
        DisposeAndNull(ref _dup);
        DisposeAndNull(ref _output);
        DisposeAndNull(ref _target);
        if (_latencyHandle != IntPtr.Zero) { Native.CloseHandle(_latencyHandle); _latencyHandle = IntPtr.Zero; }
        DisposeAndNull(ref _swapChain);
        DisposeAndNull(ref _ctx);
        DisposeAndNull(ref _d2dDevice);
        DisposeAndNull(ref _d2dFactory);
        _d3d?.ClearState();
        DisposeAndNull(ref _d3d);
        DisposeAndNull(ref _device);
        DisposeAndNull(ref _factory);
    }

    private static void DisposeAndNull<T>(ref T? o) where T : class, IDisposable
    {
        try { o?.Dispose(); } catch { /* already broken */ }
        o = null;
    }

    // ---------- capture ----------

    /// <summary>Copies the newest duplicated frame (if any) into <see cref="_copyBmp"/>; false means use GDI.</summary>
    private bool CaptureDuplication()
    {
        if (_output == null) return false;
        if (_dup == null)
        {
            if (Environment.TickCount64 < _nextDupAttempt) return false;
            try { _dup = _output.DuplicateOutput(_device!); }
            catch (SharpGenException)
            {
                _nextDupAttempt = Environment.TickCount64 + 1000;
                return false;
            }
        }

        var hr = _dup.AcquireNextFrame(0, out var info, out var resource);
        if (hr.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code) return _copyBmp != null;
        if (hr.Failure)
        {
            // Access lost: mode change, secure desktop, exclusive fullscreen app. Recreate on the next frame.
            DisposeAndNull(ref _dup);
            return false;
        }
        try
        {
            if (info.LastPresentTime != 0 || _copyBmp == null)
            {
                using var tex = resource.QueryInterface<ID3D11Texture2D>();
                EnsureCopyTexture(tex.Description);
                _d3d!.CopyResource(_copyTex!, tex);
            }
        }
        finally
        {
            resource.Dispose();
            _dup.ReleaseFrame();
        }
        return true;
    }

    private void EnsureCopyTexture(Texture2DDescription src)
    {
        if (_copyTex is { } t && t.Description.Width == src.Width && t.Description.Height == src.Height && t.Description.Format == src.Format)
            return;
        DisposeAndNull(ref _copyBmp);
        DisposeAndNull(ref _copyTex);
        _copyTex = _device!.CreateTexture2D(new Texture2DDescription
        {
            Width = src.Width,
            Height = src.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = src.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        using var surface = _copyTex.QueryInterface<IDXGISurface>();
        _copyBmp = _ctx!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(new DPixelFormat(src.Format, DAlphaMode.Ignore)));
    }

    private ID2D1Bitmap1 CaptureGdi(Rectangle r)
    {
        if (_gdiDib == null || _gdiDib.Width != r.Width || _gdiDib.Height != r.Height)
        {
            DisposeAndNull(ref _gdiBmp);
            _gdiDib?.Dispose();
            _gdiDib = new Dib(r.Width, r.Height);
            _gdiBmp = _ctx!.CreateBitmap(new Vortice.Mathematics.SizeI(r.Width, r.Height), IntPtr.Zero, 0,
                new BitmapProperties1(new DPixelFormat(Format.B8G8R8A8_UNorm, DAlphaMode.Ignore)));
        }
        IntPtr screen = Native.GetDC(IntPtr.Zero);
        try { Native.BitBlt(_gdiDib.Dc, 0, 0, r.Width, r.Height, screen, r.X, r.Y, Native.SRCCOPY); }
        finally { Native.ReleaseDC(IntPtr.Zero, screen); }
        Native.GdiFlush();
        _gdiBmp!.CopyFromMemory(_gdiDib.Bits, (uint)(r.Width * 4));
        return _gdiBmp;
    }

    // ---------- fps counter ----------

    /// <summary>Presents per second, averaged over half-second windows.</summary>
    private void CountFrame()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_fpsStart == 0) { _fpsStart = now; _fpsFrames = 0; return; }
        _fpsFrames++;
        double elapsed = (now - _fpsStart) / (double)System.Diagnostics.Stopwatch.Frequency;
        if (elapsed < 0.5) return;
        // A long pause (view hidden) isn't a slow frame rate: start a fresh window instead.
        if (elapsed < 2) _fps = _fpsFrames / elapsed;
        _fpsStart = now;
        _fpsFrames = 0;
    }

    private void DrawFps()
    {
        var ctx = _ctx!;
        if (_fpsFormat == null)
        {
            _dwrite = DWrite.DWriteCreateFactory<IDWriteFactory>();
            _fpsFormat = _dwrite.CreateTextFormat("Segoe UI", FontWeight.SemiBold, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, 15f);
            _fpsFormat.TextAlignment = TextAlignment.Center;
            _fpsFormat.ParagraphAlignment = ParagraphAlignment.Center;
            _fpsBack = ctx.CreateSolidColorBrush(new Vortice.Mathematics.Color4(0, 0, 0, 0.6f));
            _fpsText = ctx.CreateSolidColorBrush(new Vortice.Mathematics.Color4(0.47f, 0.91f, 0.59f, 1));
        }
        var box = new RawRectF(8, 8, 8 + 78, 8 + 26);
        ctx.FillRoundedRectangle(new RoundedRectangle(box, 5, 5), _fpsBack!);
        ctx.DrawText(_fps > 0 ? $"{_fps:0} FPS" : "– FPS", _fpsFormat, box, _fpsText!);
    }

    // ---------- cursor ----------

    private void DrawCursor(Rectangle src, Rectangle dst, InterpolationMode mode)
    {
        var ci = new Native.CURSORINFO { cbSize = Marshal.SizeOf<Native.CURSORINFO>() };
        if (!Native.GetCursorInfo(ref ci) || (ci.flags & Native.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero)
            return;
        if (ci.hCursor != _cursorHandle)
        {
            DisposeAndNull(ref _cursorBmp);
            _cursorBmp = CreateCursorBitmap(ci.hCursor, out _cursorHotspot);
            _cursorHandle = ci.hCursor;
        }
        if (_cursorBmp == null) return;

        float sx = dst.Width / (float)src.Width, sy = dst.Height / (float)src.Height;
        var size = _cursorBmp.PixelSize;
        float x = dst.X + (ci.ptScreenPos.X - _cursorHotspot.X - src.X) * sx;
        float y = dst.Y + (ci.ptScreenPos.Y - _cursorHotspot.Y - src.Y) * sy;
        var rect = new RawRectF(x, y, x + size.Width * sx, y + size.Height * sy);
        if (rect.Left > dst.Right || rect.Top > dst.Bottom || rect.Right < dst.Left || rect.Bottom < dst.Top) return;

        _ctx!.PushAxisAlignedClip(new RawRectF(dst.Left, dst.Top, dst.Right, dst.Bottom), AntialiasMode.Aliased);
        _ctx.DrawBitmap(_cursorBmp, rect, 1f, mode, null, null);
        _ctx.PopAxisAlignedClip();
    }

    /// <summary>
    /// Turns an HCURSOR into a premultiplied bitmap by drawing it on black and on white: on black the result is the
    /// premultiplied colour, and the difference between the two gives alpha. Inverting pixels (I-beam) come out black.
    /// </summary>
    private ID2D1Bitmap1? CreateCursorBitmap(IntPtr cursor, out Point hotspot)
    {
        hotspot = Point.Empty;
        if (!Native.GetIconInfo(cursor, out var ii)) return null;
        int w, h;
        try
        {
            bool mono = ii.hbmColor == IntPtr.Zero;
            if (Native.GetObject(mono ? ii.hbmMask : ii.hbmColor, Marshal.SizeOf<Native.BITMAP>(), out var bm) == 0) return null;
            w = bm.bmWidth;
            h = mono ? bm.bmHeight / 2 : bm.bmHeight;
            hotspot = new Point(ii.xHotspot, ii.yHotspot);
        }
        finally
        {
            if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
        }
        if (w <= 0 || h <= 0) return null;

        using var black = new Dib(w, h);
        using var white = new Dib(w, h);
        int n = w * h;
        var px = new int[n];
        Array.Fill(px, -1);
        Marshal.Copy(px, 0, white.Bits, n);
        Native.DrawIconEx(black.Dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, Native.DI_NORMAL);
        Native.DrawIconEx(white.Dc, 0, 0, cursor, w, h, 0, IntPtr.Zero, Native.DI_NORMAL);
        Native.GdiFlush();

        var b = new int[n];
        var wh = new int[n];
        Marshal.Copy(black.Bits, b, 0, n);
        Marshal.Copy(white.Bits, wh, 0, n);
        for (int i = 0; i < n; i++)
        {
            int bg = (b[i] >> 8) & 0xFF, wg = (wh[i] >> 8) & 0xFF;
            if (wg < bg) { px[i] = unchecked((int)0xFF000000); continue; } // inverting pixel
            int a = 255 - (wg - bg);
            int r = Math.Min((b[i] >> 16) & 0xFF, a), g = Math.Min(bg, a), bl = Math.Min(b[i] & 0xFF, a);
            px[i] = (a << 24) | (r << 16) | (g << 8) | bl;
        }

        var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
        try
        {
            return _ctx!.CreateBitmap(new Vortice.Mathematics.SizeI(w, h), handle.AddrOfPinnedObject(), (uint)(w * 4),
                new BitmapProperties1(new DPixelFormat(Format.B8G8R8A8_UNorm, DAlphaMode.Premultiplied)));
        }
        finally { handle.Free(); }
    }

    /// <summary>A top-down 32bpp DIB section selected into its own memory DC.</summary>
    private sealed class Dib : IDisposable
    {
        public readonly int Width, Height;
        public readonly IntPtr Dc, Bits;
        private readonly IntPtr _bitmap, _old;

        public Dib(int width, int height)
        {
            Width = width;
            Height = height;
            var bi = new Native.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Native.BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
            };
            Dc = Native.CreateCompatibleDC(IntPtr.Zero);
            _bitmap = Native.CreateDIBSection(Dc, ref bi, 0, out Bits, IntPtr.Zero, 0);
            _old = Native.SelectObject(Dc, _bitmap);
        }

        public void Dispose()
        {
            Native.SelectObject(Dc, _old);
            Native.DeleteObject(_bitmap);
            Native.DeleteDC(Dc);
        }
    }
}
