using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace Zoomifier;

/// <summary>Shows a frozen, dimmed screenshot on every monitor and lets the user drag a rectangle.</summary>
internal static class RegionSelector
{
    public static void Begin(Action<Rectangle?> done)
    {
        var overlays = new List<SelectionOverlay>();
        bool finished = false;

        void Finish(Rectangle? result)
        {
            if (finished) return;
            finished = true;
            foreach (var o in overlays) o.Close();
            done(result);
        }

        // Capture every screen first so the overlays don't appear in each other's screenshots.
        var shots = Screen.AllScreens.Select(s => (s.Bounds, Shot: Capture(s.Bounds))).ToList();
        foreach (var (bounds, shot) in shots)
            overlays.Add(new SelectionOverlay(bounds, shot, Finish));

        foreach (var o in overlays) o.Show();
        (overlays.FirstOrDefault(o => o.Bounds.Contains(Cursor.Position)) ?? overlays[0]).Activate();
    }

    private static Bitmap Capture(Rectangle bounds)
    {
        var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);
        return bmp;
    }
}

internal sealed class SelectionOverlay : Form
{
    private const int MinSize = 8;
    private readonly Rectangle _screen;
    private readonly Bitmap _shot;
    private readonly Action<Rectangle?> _done;
    private readonly SolidBrush _dim = new(Color.FromArgb(130, 0, 0, 0));
    private readonly Pen _pen = new(Color.OrangeRed, 2);
    private readonly Font _font;
    private Point? _start;
    private Rectangle _sel;

    public SelectionOverlay(Rectangle screen, Bitmap shot, Action<Rectangle?> done)
    {
        _screen = screen;
        _shot = shot;
        _done = done;
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        Cursor = Cursors.Cross;
        Bounds = screen;
        _font = new Font("Segoe UI", Math.Max(14, screen.Height / 70), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Bounds = _screen; // re-assert after any DPI adjustments during handle creation
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) { _start = e.Location; _sel = Rectangle.Empty; }
        else if (e.Button == MouseButtons.Right) _done(null);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_start is not Point s) return;
        var old = _sel;
        var p = new Point(Math.Clamp(e.X, 0, ClientSize.Width), Math.Clamp(e.Y, 0, ClientSize.Height));
        _sel = Rectangle.FromLTRB(Math.Min(s.X, p.X), Math.Min(s.Y, p.Y), Math.Max(s.X, p.X), Math.Max(s.Y, p.Y));
        var dirty = Rectangle.Union(old, _sel);
        dirty.Inflate(8, _font.Height * 2 + 16);
        Invalidate(dirty);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || _start == null) return;
        _start = null;
        if (_sel.Width >= MinSize && _sel.Height >= MinSize)
            _done(new Rectangle(_sel.X + _screen.X, _sel.Y + _screen.Y, _sel.Width, _sel.Height));
        else { _sel = Rectangle.Empty; Invalidate(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) _done(null);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.CompositingMode = CompositingMode.SourceCopy;
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.DrawImage(_shot, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
        g.CompositingMode = CompositingMode.SourceOver;

        if (_sel.IsEmpty)
        {
            g.FillRectangle(_dim, ClientRectangle);
        }
        else
        {
            using var outside = new Region(ClientRectangle);
            outside.Exclude(_sel);
            g.FillRegion(_dim, outside);
            g.DrawRectangle(_pen, _sel);
            DrawLabel(g, $"{_sel.Width} × {_sel.Height}", new Point(_sel.X, _sel.Bottom + 6));
        }

        DrawLabel(g, "Drag to select the area to zoom  •  Esc / right-click to cancel",
            new Point(ClientSize.Width / 2, 20), center: true);
    }

    private void DrawLabel(Graphics g, string text, Point at, bool center = false)
    {
        var size = TextRenderer.MeasureText(g, text, _font);
        var box = new Rectangle(center ? at.X - size.Width / 2 - 8 : at.X, at.Y, size.Width + 16, size.Height + 8);
        if (box.Bottom > ClientSize.Height) box.Y = at.Y - box.Height - _sel.Height - 12;
        using var bg = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
        g.FillRectangle(bg, box);
        TextRenderer.DrawText(g, text, _font, box, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _shot.Dispose();
        _dim.Dispose();
        _pen.Dispose();
        _font.Dispose();
    }
}
