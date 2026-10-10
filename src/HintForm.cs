using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace Zoomifier;

/// <summary>
/// Shortcut cheat-sheet pinned to the viewer's bottom-right corner, either a compact badge or the full panel.
/// A per-pixel-alpha layered window: click it to toggle, it never takes focus and is excluded from capture.
/// </summary>
internal sealed class HintForm : Form
{
    // Key tokens: "&" draws a "+" joiner, "|" draws a "/" between alternatives; mouse actions get an outlined cap.
    private sealed record Row(string[] Keys, string Text, Func<bool>? State = null);
    private sealed record Section(string Title, Row[] Rows);

    private static readonly HashSet<string> MouseActions = ["Wheel", "Drag", "Double-click", "Right-click"];
    private static readonly Color Accent = Color.FromArgb(255, 112, 67);
    private static readonly Color CardFill = Color.FromArgb(238, 22, 24, 30);
    private static readonly Color CardEdge = Color.FromArgb(40, 255, 255, 255);
    private static readonly Color TextColor = Color.FromArgb(226, 229, 235);
    private static readonly Color DimColor = Color.FromArgb(140, 146, 158);
    private static readonly StringFormat Fmt = StringFormat.GenericTypographic;

    private readonly Section[] _sections;
    private Bitmap? _bitmap;
    private string? _renderedState;

    public event Action? Clicked;

    public HintForm(Form owner, Settings s, Func<bool> isFullscreen)
    {
        Owner = owner;
        Text = "Zoomifier shortcuts";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        Size = new Size(1, 1);
        Cursor = Cursors.Hand;

        _sections =
        [
            new("Region",
            [
                new(["Ctrl", "&", "Alt", "&", "Z"], "Select a new region (works from any app)"),
                new(["R"], "Select a new region"),
                new(["Esc", "|", "Right-click"], "Cancel while selecting"),
            ]),
            new("Zoom & pan",
            [
                new(["Wheel"], "Zoom in / out around the pointer"),
                new(["+", "|", "−"], "Zoom in / out around the centre"),
                new(["Drag"], "Move the zoomed area"),
                new(["←↑→↓"], "Move by 5% of the area"),
                new(["Shift", "&", "←↑→↓"], "Move by a single pixel"),
            ]),
            new("View",
            [
                new(["A"], "Keep aspect ratio", () => s.KeepAspect),
                new(["S"], "Smooth (bilinear) scaling", () => s.Smooth),
                new(["C"], "Show the mouse cursor", () => s.ShowCursor),
                new(["B"], "Red frame around the source", () => s.ShowBorder),
            ]),
            new("Window",
            [
                new(["F11", "|", "Double-click"], "Fullscreen", isFullscreen),
                new(["Esc"], "Leave fullscreen"),
                new(["M"], "Move viewer to the next monitor"),
                new(["Right-click"], "Menu: monitors, toggles, exit"),
            ]),
            new("This panel",
            [
                new(["H"], "Collapse / expand (or click it)"),
                new(["Shift", "&", "H"], "Hide / show hints completely"),
            ]),
        ];
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Native.SetWindowDisplayAffinity(Handle, Native.WDA_EXCLUDEFROMCAPTURE);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e) => e.Cancel = true;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_MOUSEACTIVATE) { m.Result = Native.MA_NOACTIVATE; return; }
        base.WndProc(ref m);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (e.Button == MouseButtons.Left) Clicked?.Invoke();
    }

    // Content comes from UpdateLayeredWindow, not WM_PAINT.
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        _bitmap?.Dispose();
    }

    /// <summary>Re-renders if anything shown has changed, and pins the hint to the bottom-right of <paramref name="anchor"/>.</summary>
    public void Present(Rectangle anchor, float scale, bool expanded)
    {
        var states = _sections.SelectMany(s => s.Rows).Select(r => r.State?.Invoke() switch { true => '1', false => '0', null => '-' });
        string state = $"{expanded}|{scale}|{anchor.Size}|{string.Concat(states)}";
        if (_bitmap == null || state != _renderedState)
        {
            _bitmap?.Dispose();
            _bitmap = expanded ? RenderPanel(scale) : RenderBadge(scale);
            // Shrink the panel to fit a small viewer; when it would get unreadable, show the badge instead.
            float fit = Math.Min(anchor.Width / (float)_bitmap.Width, anchor.Height / (float)_bitmap.Height);
            if (expanded && fit < 1)
            {
                _bitmap.Dispose();
                _bitmap = fit >= 0.6f ? RenderPanel(scale * fit * 0.97f) : RenderBadge(scale);
            }
            _renderedState = state;
        }

        // The bitmap carries its own shadow margin, so this lands the card ~16 px from the corner.
        int margin = (int)(4 * scale);
        var at = new Point(
            Math.Max(anchor.Left, anchor.Right - _bitmap.Width - margin),
            Math.Max(anchor.Top, anchor.Bottom - _bitmap.Height - margin));
        if (!Visible) Show();
        Apply(_bitmap, at);
    }

    private void Apply(Bitmap bmp, Point at)
    {
        IntPtr screenDc = Native.GetDC(IntPtr.Zero);
        IntPtr memDc = Native.CreateCompatibleDC(screenDc);
        IntPtr hBmp = bmp.GetHbitmap(Color.FromArgb(0));
        IntPtr old = Native.SelectObject(memDc, hBmp);
        try
        {
            var dst = new Native.POINT { X = at.X, Y = at.Y };
            var src = new Native.POINT();
            var size = new Native.SIZE { cx = bmp.Width, cy = bmp.Height };
            var blend = new Native.BLENDFUNCTION { SourceConstantAlpha = 255, AlphaFormat = Native.AC_SRC_ALPHA };
            Native.UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, Native.ULW_ALPHA);
        }
        finally
        {
            Native.SelectObject(memDc, old);
            Native.DeleteObject(hBmp);
            Native.DeleteDC(memDc);
            Native.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---------- rendering ----------

    private sealed class Style : IDisposable
    {
        public readonly float K;
        public readonly Font Title, Header, Body, Key, Badge;

        public Style(float k)
        {
            K = k;
            Title = new Font("Segoe UI Semibold", 15 * k, GraphicsUnit.Pixel);
            Header = new Font("Segoe UI Semibold", 11 * k, GraphicsUnit.Pixel);
            Body = new Font("Segoe UI", 13 * k, GraphicsUnit.Pixel);
            Key = new Font("Segoe UI Semibold", 12 * k, GraphicsUnit.Pixel);
            Badge = new Font("Segoe UI Semibold", 10 * k, GraphicsUnit.Pixel);
        }

        public float S(float v) => v * K;

        public void Dispose()
        {
            Title.Dispose(); Header.Dispose(); Body.Dispose(); Key.Dispose(); Badge.Dispose();
        }
    }

    private Bitmap RenderPanel(float scale)
    {
        using var st = new Style(scale);
        using var mb = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(mb);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        float pad = st.S(18), titleH = st.S(40), headerH = st.S(26), rowH = st.S(30), sectionGap = st.S(8);
        float colGap = st.S(20), badgeGap = st.S(14);
        var rows = _sections.SelectMany(s => s.Rows).ToArray();
        float keyCol = rows.Max(r => KeysWidth(mg, st, r.Keys));
        float badgeW = Measure(mg, "OFF", st.Badge) + st.S(16);
        float textCol = rows.Max(r => Measure(mg, r.Text, st.Body) + (r.State != null ? badgeGap + badgeW : 0));
        textCol = Math.Max(textCol, Measure(mg, "Keyboard shortcuts", st.Title) + st.S(120) - keyCol);

        float cardW = pad * 2 + keyCol + colGap + textCol;
        float cardH = pad + titleH + _sections.Sum(s => headerH + s.Rows.Length * rowH)
                      + sectionGap * (_sections.Length - 1) + pad * 0.6f;

        return RenderCard(st, cardW, cardH, st.S(12), g =>
        {
            // Title bar.
            float ty = pad + st.S(10);
            using (var dot = new SolidBrush(Accent))
                g.FillEllipse(dot, pad, ty - st.S(4), st.S(8), st.S(8));
            DrawText(g, "Keyboard shortcuts", st.Title, Color.White, pad + st.S(16), ty);
            float zw = Measure(g, "ZOOMIFIER", st.Header);
            DrawText(g, "ZOOMIFIER", st.Header, DimColor, cardW - pad - zw, ty);
            using (var line = new Pen(Color.FromArgb(26, 255, 255, 255), Math.Max(1, st.K)))
                g.DrawLine(line, pad, pad + titleH - st.S(10), cardW - pad, pad + titleH - st.S(10));

            float y = pad + titleH;
            foreach (var section in _sections)
            {
                DrawText(g, section.Title.ToUpperInvariant(), st.Header, Accent, pad, y + headerH / 2);
                y += headerH;
                foreach (var row in section.Rows)
                {
                    float cy = y + rowH / 2;
                    DrawKeys(g, st, row.Keys, pad, cy);
                    DrawText(g, row.Text, st.Body, TextColor, pad + keyCol + colGap, cy);
                    if (row.State is { } state)
                        DrawBadge(g, st, state(), cardW - pad - badgeW, cy, badgeW);
                    y += rowH;
                }
                y += sectionGap;
            }
        });
    }

    private static Bitmap RenderBadge(float scale)
    {
        using var st = new Style(scale);
        using var mb = new Bitmap(1, 1);
        using var mg = Graphics.FromImage(mb);
        mg.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        const string label = "Shortcuts";
        float h = st.S(36), capW = CapWidth(mg, st, "H");
        float w = st.S(7) + capW + st.S(9) + Measure(mg, label, st.Body) + st.S(16);
        return RenderCard(st, w, h, h / 2, g =>
        {
            DrawCap(g, st, "H", st.S(7), h / 2);
            DrawText(g, label, st.Body, TextColor, st.S(7) + capW + st.S(9), h / 2);
        });
    }

    /// <summary>A rounded translucent card with a soft drop shadow; <paramref name="content"/> draws in card coordinates.</summary>
    private static Bitmap RenderCard(Style st, float cardW, float cardH, float radius, Action<Graphics> content)
    {
        float shadow = st.S(12);
        var bmp = new Bitmap((int)Math.Ceiling(cardW + shadow * 2), (int)Math.Ceiling(cardH + shadow * 2), PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.TranslateTransform(shadow, shadow);

        var card = new RectangleF(0, 0, cardW, cardH);
        const int steps = 8;
        for (int i = steps; i >= 1; i--)
        {
            var r = card;
            r.Inflate(shadow * i / steps, shadow * i / steps);
            r.Offset(0, st.S(3));
            using var path = Round(r, radius + shadow * i / steps);
            using var b = new SolidBrush(Color.FromArgb(9, 0, 0, 0));
            g.FillPath(b, path);
        }

        using (var path = Round(card, radius))
        using (var fill = new SolidBrush(CardFill))
        using (var edge = new Pen(CardEdge, Math.Max(1, st.K)))
        {
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
        }

        content(g);
        return bmp;
    }

    private static float CapWidth(Graphics g, Style st, string text) =>
        Math.Max(st.S(24), Measure(g, text, st.Key) + st.S(14));

    private static bool IsJoiner(string t) => t is "&" or "|";

    private static float KeysWidth(Graphics g, Style st, string[] keys)
    {
        float w = 0;
        string? prev = null;
        foreach (var t in keys)
        {
            if (IsJoiner(t)) w += Measure(g, Joiner(t), st.Key) + st.S(10);
            else w += (prev != null && !IsJoiner(prev) ? st.S(4) : 0) + CapWidth(g, st, t);
            prev = t;
        }
        return w;
    }

    private static void DrawKeys(Graphics g, Style st, string[] keys, float x, float cy)
    {
        string? prev = null;
        foreach (var t in keys)
        {
            if (IsJoiner(t))
            {
                x += st.S(5);
                DrawText(g, Joiner(t), st.Key, DimColor, x, cy);
                x += Measure(g, Joiner(t), st.Key) + st.S(5);
            }
            else
            {
                if (prev != null && !IsJoiner(prev)) x += st.S(4);
                x += DrawCap(g, st, t, x, cy);
            }
            prev = t;
        }
    }

    private static string Joiner(string t) => t == "&" ? "+" : "/";

    /// <summary>Draws a keyboard key (raised, filled) or a mouse action (outlined, blue); returns its width.</summary>
    private static float DrawCap(Graphics g, Style st, string text, float x, float cy)
    {
        float w = CapWidth(g, st, text), h = st.S(22), radius = st.S(5);
        var r = new RectangleF(x, cy - h / 2, w, h);
        Color textColor;
        if (MouseActions.Contains(text))
        {
            using var path = Round(r, radius);
            using var fill = new SolidBrush(Color.FromArgb(22, 120, 170, 255));
            using var edge = new Pen(Color.FromArgb(170, 120, 170, 255), Math.Max(1, st.K));
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
            textColor = Color.FromArgb(180, 205, 255);
        }
        else
        {
            var lip = r;
            lip.Offset(0, st.S(2));
            using (var lipPath = Round(lip, radius))
            using (var lipFill = new SolidBrush(Color.FromArgb(255, 12, 13, 17)))
                g.FillPath(lipFill, lipPath);
            using var path = Round(r, radius);
            using var fill = new LinearGradientBrush(r, Color.FromArgb(255, 82, 87, 101), Color.FromArgb(255, 60, 64, 76), LinearGradientMode.Vertical);
            using var edge = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1, st.K));
            g.FillPath(fill, path);
            g.DrawPath(edge, path);
            textColor = Color.White;
        }
        var size = g.MeasureString(text, st.Key, PointF.Empty, Fmt);
        using var tb = new SolidBrush(textColor);
        g.DrawString(text, st.Key, tb, r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2, Fmt);
        return w;
    }

    private static void DrawBadge(Graphics g, Style st, bool on, float x, float cy, float w)
    {
        float h = st.S(18);
        var r = new RectangleF(x, cy - h / 2, w, h);
        using var path = Round(r, h / 2);
        using var fill = new SolidBrush(on ? Color.FromArgb(60, 52, 199, 89) : Color.FromArgb(22, 255, 255, 255));
        g.FillPath(fill, path);
        string text = on ? "ON" : "OFF";
        var size = g.MeasureString(text, st.Badge, PointF.Empty, Fmt);
        using var tb = new SolidBrush(on ? Color.FromArgb(120, 232, 150) : DimColor);
        g.DrawString(text, st.Badge, tb, r.X + (r.Width - size.Width) / 2, r.Y + (r.Height - size.Height) / 2, Fmt);
    }

    /// <summary>Draws text left-aligned at <paramref name="x"/>, vertically centred on <paramref name="cy"/>.</summary>
    private static void DrawText(Graphics g, string text, Font font, Color color, float x, float cy)
    {
        using var b = new SolidBrush(color);
        g.DrawString(text, font, b, x, cy - g.MeasureString(text, font, PointF.Empty, Fmt).Height / 2, Fmt);
    }

    private static float Measure(Graphics g, string text, Font font) => g.MeasureString(text, font, PointF.Empty, Fmt).Width;

    private static GraphicsPath Round(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}
