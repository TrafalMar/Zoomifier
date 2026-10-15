using System.Drawing.Drawing2D;

namespace Zoomifier;

/// <summary>Dark "Keyboard shortcuts" dialog: click a shortcut, press the new keys, Save.</summary>
internal sealed class ShortcutsForm : Form
{
    private static readonly Color Bg = Color.FromArgb(24, 26, 32);
    private static readonly Color Card = Color.FromArgb(34, 37, 46);
    private static readonly Color Edge = Color.FromArgb(58, 62, 74);
    private static readonly Color TextColor = Color.FromArgb(226, 229, 235);
    private static readonly Color Dim = Color.FromArgb(140, 146, 158);
    private static readonly Color Accent = Color.FromArgb(255, 112, 67);
    private static readonly Color Warn = Color.FromArgb(255, 125, 110);
    private static readonly Color Ok = Color.FromArgb(120, 232, 150);

    /// <summary>Saves the bindings and lens keys; returns the ids of global shortcuts that couldn't be registered.</summary>
    public delegate IReadOnlyCollection<string> ApplyHandler(IReadOnlyDictionary<string, Keys> bindings, int holdKey, int toggleKey);

    private readonly ApplyHandler _apply;
    private readonly Dictionary<string, Keys> _bindings;
    private readonly Dictionary<string, KeyBox> _boxes = new();
    private readonly Dictionary<string, Label> _notes = new();
    private readonly HoldBox _hold, _toggle;
    private readonly Label _status;

    public ShortcutsForm(Settings s, ApplyHandler apply)
    {
        _apply = apply;
        _bindings = Shortcuts.All.ToDictionary(d => d.Id, d => Shortcuts.Get(s, d.Id));

        Text = "Zoomifier – Keyboard shortcuts";
        Icon = AppIcon.Load(SystemInformation.SmallIconSize);
        AutoScaleDimensions = new SizeF(96f, 96f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = MaximizeBox = false;
        ShowInTaskbar = true;
        BackColor = Bg;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 9.75f);
        var work = Screen.FromPoint(Cursor.Position).WorkingArea;
        ClientSize = new Size(660, Math.Min(740, work.Height - 80));
        MinimumSize = new Size(560, 420);
        KeyPreview = false;

        // Header.
        var header = new Panel { Dock = DockStyle.Top, Height = 78, Padding = new Padding(24, 16, 24, 0), BackColor = Bg };
        header.Controls.Add(new Label
        {
            Text = "Click a shortcut, then press the new keys. Global shortcuts work in any app and need Ctrl or Alt.",
            ForeColor = Dim, Dock = DockStyle.Top, Height = 24, AutoEllipsis = true,
        });
        header.Controls.Add(new Label
        {
            Text = "Keyboard shortcuts", Font = new Font("Segoe UI Semibold", 15f), ForeColor = Color.White,
            Dock = DockStyle.Top, Height = 34,
        });

        // Footer.
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 100, Padding = new Padding(24, 10, 24, 20), BackColor = Bg };
        _status = new Label { Dock = DockStyle.Top, Height = 28, ForeColor = Dim, AutoEllipsis = true };
        var buttons = new Panel { Dock = DockStyle.Bottom, Height = 36 };
        var reset = MakeButton("Reset to defaults", false);
        reset.Dock = DockStyle.Left;
        reset.Click += (_, _) => ResetDefaults();
        var save = MakeButton("Save", true);
        save.Dock = DockStyle.Right;
        save.Click += (_, _) => SaveAndClose();
        var spacer = new Panel { Dock = DockStyle.Right, Width = 10 };
        var cancel = MakeButton("Cancel", false);
        cancel.Dock = DockStyle.Right;
        cancel.Click += (_, _) => Close();
        CancelButton = cancel;
        buttons.Controls.AddRange([reset, cancel, spacer, save]);
        footer.Controls.Add(buttons);
        footer.Controls.Add(_status);

        // Rows.
        var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 4, 18, 8), BackColor = Bg };
        scroll.HandleCreated += (_, _) => Native.SetWindowTheme(scroll.Handle, "DarkMode_Explorer", null);
        scroll.Click += (_, _) => ActiveControl = null;
        var table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 4, BackColor = Bg,
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
        table.Click += (_, _) => ActiveControl = null;

        _hold = new HoldBox { Vk = s.HoldKey, Margin = new Padding(0, 3, 4, 3) };
        _toggle = new HoldBox { Vk = s.ToggleKey, Margin = new Padding(0, 3, 4, 3) };
        _hold.Recorded += vk => OnLensKeyRecorded(_hold, _toggle, vk, "Hold key", "lens toggle");
        _toggle.Recorded += vk => OnLensKeyRecorded(_toggle, _hold, vk, "Lens toggle", "hold key");

        string? group = null;
        foreach (var def in Shortcuts.All)
        {
            if (def.Group != group)
            {
                group = def.Group;
                var head = new Label
                {
                    Text = group.ToUpperInvariant(), UseMnemonic = false, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 8.5f),
                    AutoSize = true, Margin = new Padding(0, table.RowCount == 0 ? 6 : 18, 0, 6),
                };
                table.Controls.Add(head, 0, table.RowCount);
                table.SetColumnSpan(head, 4);
                table.RowCount++;
            }

            AddRow(table, def.Label + (def.LensOnly ? "  (lens mode)" : ""), out var note,
                _boxes[def.Id] = new KeyBox { Tag = def, Margin = new Padding(0, 3, 4, 3) },
                MakeClear(def.Id));
            _notes[def.Id] = note;
            _boxes[def.Id].Recorded += OnRecorded;

            if (def.Id == Shortcuts.LensZoomOut)
            {
                AddRow(table, "Show lens while held  (lens mode)", out _, _hold, MakeHoldClear(_hold, "Hold key removed."));
                AddRow(table, "Lens on / off with a key or mouse button  (lens mode)", out _, _toggle,
                    MakeHoldClear(_toggle, "Lens toggle key removed."));
            }
        }
        scroll.Controls.Add(table);

        Controls.Add(scroll);
        Controls.Add(footer);
        Controls.Add(header);
        RefreshBoxes();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int dark = 1;
        Native.DwmSetWindowAttribute(Handle, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ActiveControl = null; // nothing records until a shortcut is clicked
    }

    private static void AddRow(TableLayoutPanel table, string text, out Label note, Control editor, Control? clear)
    {
        int row = table.RowCount++;
        table.Controls.Add(new Label
        {
            Text = text, UseMnemonic = false, ForeColor = TextColor, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 3, 8, 3),
        }, 0, row);
        note = new Label { ForeColor = Warn, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 3, 8, 3), Font = new Font("Segoe UI", 8.5f) };
        table.Controls.Add(note, 1, row);
        table.Controls.Add(editor, 2, row);
        if (clear != null) table.Controls.Add(clear, 3, row);
    }

    private Control MakeClear(string id)
    {
        // id "" = a bare ✕ button; the caller wires up what it clears.
        var b = new Button
        {
            Text = "✕", FlatStyle = FlatStyle.Flat, ForeColor = Dim, BackColor = Bg, Size = new Size(30, 30),
            Margin = new Padding(0, 3, 0, 3), TabStop = false, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 8.5f),
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Card;
        new ToolTip().SetToolTip(b, "Remove this shortcut");
        if (id.Length > 0) b.Click += (_, _) =>
        {
            _bindings[id] = Keys.None;
            RefreshBoxes();
            SetStatus($"“{Shortcuts.Def(id).Label}” has no shortcut now.", Dim);
            ActiveControl = null;
        };
        return b;
    }

    private Control MakeHoldClear(HoldBox box, string message)
    {
        var b = (Button)MakeClear("");
        b.Click += (_, _) =>
        {
            box.Vk = 0;
            box.Invalidate();
            SetStatus(message, Dim);
        };
        return b;
    }

    /// <summary>One key can't both hold and toggle the lens, so recording it on one clears the other.</summary>
    private void OnLensKeyRecorded(HoldBox box, HoldBox other, int vk, string what, string otherWhat)
    {
        if (other.Vk == vk)
        {
            other.Vk = 0;
            other.Invalidate();
            SetStatus($"{Shortcuts.HoldKeyName(vk)} was moved here from the {otherWhat}, which is now unset.", Accent);
        }
        else SetStatus($"{what}: {Shortcuts.HoldKeyName(vk)}", Ok);
        ActiveControl = null;
    }

    private static Button MakeButton(string text, bool primary)
    {
        var b = new Button
        {
            Text = text, FlatStyle = FlatStyle.Flat, Width = primary ? 110 : (text.Length > 10 ? 150 : 96), Cursor = Cursors.Hand,
            BackColor = primary ? Accent : Card, ForeColor = primary ? Color.FromArgb(24, 18, 14) : TextColor,
            Font = new Font(primary ? "Segoe UI Semibold" : "Segoe UI", 9.75f),
        };
        b.FlatAppearance.BorderColor = primary ? Accent : Edge;
        b.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(255, 138, 100) : Color.FromArgb(44, 48, 59);
        return b;
    }

    private void OnRecorded(KeyBox box, Keys keys)
    {
        var def = (ShortcutDef)box.Tag!;
        keys = Shortcuts.Normalize(keys);
        if (def.Scope == ShortcutScope.Global && !Shortcuts.IsValidGlobal(keys))
        {
            SetStatus("Global shortcuts need Ctrl or Alt (or an F-key), so they don't block normal typing. Try again.", Warn);
            return;
        }

        var taken = Shortcuts.All.Where(d => d.Id != def.Id && Shortcuts.Normalize(_bindings[d.Id]) == keys).ToList();
        foreach (var other in taken) _bindings[other.Id] = Keys.None;
        _bindings[def.Id] = keys;
        RefreshBoxes();
        SetStatus(taken.Count > 0
            ? $"{Shortcuts.Display(keys)} was moved here from “{taken[0].Label}”, which now has no shortcut."
            : $"{def.Label}: {Shortcuts.Display(keys)}", taken.Count > 0 ? Accent : Ok);
        ActiveControl = null;
    }

    private void ResetDefaults()
    {
        foreach (var def in Shortcuts.All) _bindings[def.Id] = def.Default;
        _hold.Vk = 0x05;
        _hold.Invalidate();
        _toggle.Vk = 0x06;
        _toggle.Invalidate();
        RefreshBoxes();
        SetStatus("Defaults restored. Press Save to keep them.", Dim);
    }

    private void SaveAndClose()
    {
        var failed = _apply(_bindings, _hold.Vk, _toggle.Vk);
        RefreshBoxes(failed);
        if (failed.Count == 0)
        {
            DialogResult = DialogResult.OK;
            Close();
            return;
        }
        SetStatus("Saved, but some global shortcuts are already used by another app. Pick different keys for them.", Warn);
    }

    private void RefreshBoxes(IReadOnlyCollection<string>? failed = null)
    {
        foreach (var (id, box) in _boxes)
        {
            box.Value = _bindings[id];
            box.Invalid = failed?.Contains(id) == true;
            box.Invalidate();
            _notes[id].Text = box.Invalid ? "used by another app" : "";
        }
    }

    private void SetStatus(string text, Color color)
    {
        _status.Text = text;
        _status.ForeColor = color;
    }

    /// <summary>
    /// Hold-key picker: when focused it records the next key or mouse button that goes down, by polling the
    /// physical state, so side buttons (or whatever mouse software makes them send) are captured as-is.
    /// </summary>
    private sealed class HoldBox : Control
    {
        private readonly System.Windows.Forms.Timer _poll = new() { Interval = 10 };
        private readonly bool[] _was = new bool[256];

        public int Vk { get; set; }
        public event Action<int>? Recorded;

        public HoldBox()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(196, 30);
            Font = new Font("Segoe UI Semibold", 9.75f);
            _poll.Tick += (_, _) => Poll();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            // Snapshot what's already down (e.g. the click that focused us) so only a new press counts.
            for (int vk = 1; vk < 256; vk++) _was[vk] = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
            _poll.Start();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            _poll.Stop();
            Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _poll.Dispose();
            base.Dispose(disposing);
        }

        // While recording, swallow keys so Esc/Enter don't close the dialog; the poll records them.
        protected override bool IsInputKey(Keys keyData) => true;
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData) => Focused || base.ProcessCmdKey(ref msg, keyData);

        private void Poll()
        {
            for (int vk = 2; vk < 256; vk++) // 1 = left button: clicking elsewhere cancels instead
            {
                if (vk is >= 0xA0 and <= 0xA5) continue; // left/right Shift/Ctrl/Alt: use the generic code
                bool down = (Native.GetAsyncKeyState(vk) & 0x8000) != 0;
                if (down && !_was[vk])
                {
                    Vk = vk;
                    _poll.Stop();
                    Invalidate();
                    Recorded?.Invoke(vk);
                    return;
                }
                _was[vk] = down;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            string text = Focused ? "Press a key or mouse button…" : Vk == 0 ? "Not set" : Shortcuts.HoldKeyName(Vk);
            KeyBox.PaintBox(e.Graphics, this, Focused, false, text, Focused ? Accent : Vk == 0 ? Dim : Color.White);
        }
    }

    /// <summary>Shows a binding as text; when focused it records the next key combination.</summary>
    private sealed class KeyBox : Control
    {
        private const int WM_SYSKEYUP = 0x105, WM_SYSCHAR = 0x106;
        private Keys _pending;

        public Keys Value { get; set; }
        public bool Invalid { get; set; }
        public event Action<KeyBox, Keys>? Recorded;

        public KeyBox()
        {
            SetStyle(ControlStyles.Selectable | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            Size = new Size(196, 30);
            Font = new Font("Segoe UI Semibold", 9.75f);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); _pending = Keys.None; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); _pending = Keys.None; Invalidate(); }

        protected override bool IsInputKey(Keys keyData) => true;

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!Focused) return base.ProcessCmdKey(ref msg, keyData);
            var code = keyData & Keys.KeyCode;
            if (code is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin or Keys.LControlKey
                or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu)
            {
                _pending = keyData & Keys.Modifiers;
                Invalidate();
                return true;
            }
            Recorded?.Invoke(this, keyData);
            return true;
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            _pending = ModifierKeys;
            Invalidate();
        }

        protected override void WndProc(ref Message m)
        {
            // Don't let a lone Alt press open the window menu while recording.
            if (Focused && m.Msg is WM_SYSKEYUP or WM_SYSCHAR) { _pending = ModifierKeys; Invalidate(); return; }
            base.WndProc(ref m);
        }

        public static void PaintBox(Graphics g, Control c, bool focused, bool invalid, string text, Color color)
        {
            g.Clear(c.Parent?.BackColor ?? Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new RectangleF(0.5f, 0.5f, c.Width - 1.5f, c.Height - 1.5f);
            using var path = Round(r, 6);
            using (var fill = new SolidBrush(focused ? Color.FromArgb(52, 40, 36) : Card)) g.FillPath(fill, path);
            using (var pen = new Pen(focused ? Accent : invalid ? Warn : Edge, focused ? 1.6f : 1f)) g.DrawPath(pen, path);
            TextRenderer.DrawText(g, text, c.Font, Rectangle.Round(r), color,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            string text;
            Color color;
            if (Focused)
            {
                text = _pending == Keys.None ? "Press keys…" : ModText(_pending) + " + …";
                color = Accent;
            }
            else if (Value == Keys.None) { text = "Not set"; color = Dim; }
            else { text = Shortcuts.Display(Value, "  +  "); color = Color.White; }
            PaintBox(e.Graphics, this, Focused, Invalid, text, color);
        }

        private static string ModText(Keys mods)
        {
            var parts = new List<string>();
            if ((mods & Keys.Control) != 0) parts.Add("Ctrl");
            if ((mods & Keys.Alt) != 0) parts.Add("Alt");
            if ((mods & Keys.Shift) != 0) parts.Add("Shift");
            return string.Join(" + ", parts);
        }

        private static GraphicsPath Round(RectangleF r, float radius)
        {
            float d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
