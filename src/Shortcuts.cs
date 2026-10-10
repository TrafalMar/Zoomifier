namespace Zoomifier;

internal enum ShortcutScope { Global, Viewer }

/// <summary>A rebindable action. Global ones are system-wide hotkeys; Viewer ones work while the viewer has focus.</summary>
internal sealed record ShortcutDef(string Id, string Group, string Label, ShortcutScope Scope, Keys Default,
    bool LensOnly = false, bool Repeat = false);

/// <summary>
/// The catalogue of rebindable shortcuts. Settings store only the bindings the user changed (by id), so new
/// actions or changed defaults in later versions apply automatically while customisations are kept.
/// </summary>
internal static class Shortcuts
{
    public const string Select = "select", SelectLocal = "selectLocal", LensToggle = "lensToggle",
        LensZoomIn = "lensZoomIn", LensZoomOut = "lensZoomOut", ZoomIn = "zoomIn", ZoomOut = "zoomOut",
        PanLeft = "panLeft", PanRight = "panRight", PanUp = "panUp", PanDown = "panDown",
        Aspect = "aspect", Smooth = "smooth", Cursor = "cursor", Border = "border",
        Fullscreen = "fullscreen", LeaveFullscreen = "leaveFullscreen", NextMonitor = "nextMonitor", LensMode = "lensMode", SwitchMode = "switchMode",
        HintToggle = "hintToggle", HintHide = "hintHide";

    public static readonly ShortcutDef[] All =
    [
        new(Select, "Global (work in any app)", "Select a new region", ShortcutScope.Global, Keys.Control | Keys.Alt | Keys.Z),
        new(SwitchMode, "Global (work in any app)", "Switch viewer ⇄ lens mode", ShortcutScope.Global, Keys.Control | Keys.Alt | Keys.L),
        new(LensToggle, "Global (work in any app)", "Lens on / off", ShortcutScope.Global, Keys.Control | Keys.Alt | Keys.X, LensOnly: true),
        new(LensZoomIn, "Global (work in any app)", "Lens zoom in", ShortcutScope.Global, Keys.Control | Keys.Alt | Keys.Oemplus, LensOnly: true, Repeat: true),
        new(LensZoomOut, "Global (work in any app)", "Lens zoom out", ShortcutScope.Global, Keys.Control | Keys.Alt | Keys.OemMinus, LensOnly: true, Repeat: true),

        new(SelectLocal, "Viewer: region, zoom & pan", "Select a new region", ShortcutScope.Viewer, Keys.R),
        new(ZoomIn, "Viewer: region, zoom & pan", "Zoom in", ShortcutScope.Viewer, Keys.Oemplus),
        new(ZoomOut, "Viewer: region, zoom & pan", "Zoom out", ShortcutScope.Viewer, Keys.OemMinus),
        new(PanLeft, "Viewer: region, zoom & pan", "Move left (hold Shift for 1 px)", ShortcutScope.Viewer, Keys.Left),
        new(PanRight, "Viewer: region, zoom & pan", "Move right", ShortcutScope.Viewer, Keys.Right),
        new(PanUp, "Viewer: region, zoom & pan", "Move up", ShortcutScope.Viewer, Keys.Up),
        new(PanDown, "Viewer: region, zoom & pan", "Move down", ShortcutScope.Viewer, Keys.Down),

        new(Aspect, "Viewer: view", "Keep aspect ratio", ShortcutScope.Viewer, Keys.A),
        new(Smooth, "Viewer: view", "Smooth scaling", ShortcutScope.Viewer, Keys.S),
        new(Cursor, "Viewer: view", "Show mouse cursor", ShortcutScope.Viewer, Keys.C),
        new(Border, "Viewer: view", "Red frame around the source", ShortcutScope.Viewer, Keys.B),

        new(Fullscreen, "Viewer: window", "Toggle fullscreen", ShortcutScope.Viewer, Keys.F11),
        new(LeaveFullscreen, "Viewer: window", "Leave fullscreen", ShortcutScope.Viewer, Keys.Escape),
        new(NextMonitor, "Viewer: window", "Move viewer to the next monitor", ShortcutScope.Viewer, Keys.M),
        new(LensMode, "Viewer: window", "Switch to lens mode (in the viewer)", ShortcutScope.Viewer, Keys.L),
        new(HintToggle, "Viewer: window", "Collapse / expand shortcut panel", ShortcutScope.Viewer, Keys.H),
        new(HintHide, "Viewer: window", "Hide / show shortcut panel", ShortcutScope.Viewer, Keys.Shift | Keys.H),
    ];

    public static readonly (int Vk, string Name)[] HoldKeys =
    [
        (0, "None"), (0x05, "Mouse back button"), (0x06, "Mouse forward button"), (0x04, "Middle mouse button"),
        (0x02, "Right mouse button"), (0x10, "Shift"), (0x11, "Ctrl"), (0x12, "Alt"), (0x14, "Caps Lock"),
    ];

    public static ShortcutDef Def(string id) => All.First(d => d.Id == id);

    public static string HoldKeyName(int vk) =>
        HoldKeys.FirstOrDefault(k => k.Vk == vk).Name ?? KeyName((Keys)vk);

    /// <summary>
    /// Mouse software (G Hub, Synapse, …) often makes the side buttons send Browser Back/Forward instead of
    /// XButton1/2, so those count as the same hold key.
    /// </summary>
    public static int HoldKeyAlias(int vk) => vk switch
    {
        0x05 => (int)Keys.BrowserBack,
        0x06 => (int)Keys.BrowserForward,
        (int)Keys.BrowserBack => 0x05,
        (int)Keys.BrowserForward => 0x06,
        _ => 0,
    };

    /// <summary>The effective binding: the user's override if it parses, otherwise the default.</summary>
    public static Keys Get(Settings s, string id) =>
        s.Shortcuts.TryGetValue(id, out var text) && TryParse(text, out var k) ? k : Def(id).Default;

    /// <summary>Stores <paramref name="bindings"/> as overrides of the defaults (unchanged ones are dropped).</summary>
    public static void Store(Settings s, IReadOnlyDictionary<string, Keys> bindings)
    {
        foreach (var def in All)
        {
            if (!bindings.TryGetValue(def.Id, out var k) || Normalize(k) == Normalize(def.Default)) s.Shortcuts.Remove(def.Id);
            else s.Shortcuts[def.Id] = Serialize(k);
        }
    }

    /// <summary>Numpad +/− behave like the main-row keys.</summary>
    public static Keys Normalize(Keys k) => (k & Keys.KeyCode) switch
    {
        Keys.Add => (k & Keys.Modifiers) | Keys.Oemplus,
        Keys.Subtract => (k & Keys.Modifiers) | Keys.OemMinus,
        _ => k,
    };

    /// <summary>Global shortcuts need Ctrl or Alt (or an F-key), or they would swallow ordinary typing everywhere.</summary>
    public static bool IsValidGlobal(Keys k) =>
        (k & (Keys.Control | Keys.Alt)) != 0 || (k & Keys.KeyCode) is >= Keys.F1 and <= Keys.F24;

    // ---------- text ----------

    public static string Serialize(Keys k)
    {
        if (k == Keys.None) return "None";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        parts.Add((k & Keys.KeyCode).ToString());
        return string.Join("+", parts);
    }

    public static bool TryParse(string? text, out Keys k)
    {
        k = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        if (text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": k |= Keys.Control; break;
                case "alt": k |= Keys.Alt; break;
                case "shift": k |= Keys.Shift; break;
                default:
                    if (!Enum.TryParse<Keys>(part, true, out var code) || (code & Keys.Modifiers) != 0) return false;
                    k |= code;
                    break;
            }
        }
        return (k & Keys.KeyCode) != Keys.None;
    }

    /// <summary>Human-readable, e.g. "Ctrl+Alt+Z", "Ctrl+Alt+Plus", "+", "←"; used in menus and the dialog.</summary>
    public static string Display(Keys k, string separator = "+")
    {
        if (k == Keys.None) return "";
        var parts = new List<string>();
        if ((k & Keys.Control) != 0) parts.Add("Ctrl");
        if ((k & Keys.Alt) != 0) parts.Add("Alt");
        if ((k & Keys.Shift) != 0) parts.Add("Shift");
        string key = KeyName(k & Keys.KeyCode);
        if (parts.Count > 0 && key is "+" or "−") key = key == "+" ? "Plus" : "Minus";
        parts.Add(key);
        return string.Join(separator, parts);
    }

    /// <summary>Key-cap tokens for the hint panel: modifiers joined with "&amp;" (drawn as "+").</summary>
    public static string[] Tokens(Keys k)
    {
        if (k == Keys.None) return ["unset"];
        var t = new List<string>();
        if ((k & Keys.Control) != 0) t.AddRange(["Ctrl", "&"]);
        if ((k & Keys.Alt) != 0) t.AddRange(["Alt", "&"]);
        if ((k & Keys.Shift) != 0) t.AddRange(["Shift", "&"]);
        t.Add(KeyName(k & Keys.KeyCode));
        return [.. t];
    }

    public static string KeyName(Keys code) => code switch
    {
        Keys.Oemplus or Keys.Add => "+",
        Keys.OemMinus or Keys.Subtract => "−",
        Keys.Left => "←", Keys.Right => "→", Keys.Up => "↑", Keys.Down => "↓",
        Keys.Escape => "Esc", Keys.Return => "Enter", Keys.Back => "Backspace", Keys.Space => "Space",
        Keys.Delete => "Del", Keys.Insert => "Ins", Keys.PageUp => "PgUp", Keys.PageDown => "PgDn",
        Keys.Capital => "Caps Lock", Keys.Tab => "Tab", Keys.Home => "Home", Keys.End => "End",
        >= Keys.D0 and <= Keys.D9 => ((int)(code - Keys.D0)).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + (int)(code - Keys.NumPad0),
        Keys.Multiply => "Num *", Keys.Divide => "Num /", Keys.Decimal => "Num .",
        Keys.Oemcomma => ",", Keys.OemPeriod => ".", Keys.OemQuestion => "/", Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'", Keys.OemOpenBrackets => "[", Keys.OemCloseBrackets => "]", Keys.OemPipe => "\\",
        Keys.Oemtilde => "`", Keys.OemBackslash => "\\",
        Keys.LButton => "Left mouse button", Keys.RButton => "Right mouse button", Keys.MButton => "Middle mouse button",
        Keys.XButton1 => "Mouse back button", Keys.XButton2 => "Mouse forward button",
        Keys.BrowserBack => "Browser Back", Keys.BrowserForward => "Browser Forward",
        Keys.ShiftKey => "Shift", Keys.ControlKey => "Ctrl", Keys.Menu => "Alt", Keys.LWin or Keys.RWin => "Win",
        _ => code.ToString(),
    };
}
