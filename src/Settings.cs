using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zoomifier;

internal enum ZoomMode { Monitor, Lens }

internal sealed class Settings
{
    // Source region in physical virtual-screen pixels.
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; }
    public int H { get; set; }

    public string? ViewerScreen { get; set; }
    public bool Fullscreen { get; set; } = true;
    public bool KeepAspect { get; set; } = true;
    public bool Smooth { get; set; }
    public bool ShowCursor { get; set; } = true;
    public bool ShowBorder { get; set; } = true;
    public bool ShowHint { get; set; } = true;
    public bool HintExpanded { get; set; } = true;

    /// <summary>Monitor: magnified view on another monitor. Lens: magnified in place over the area.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter<ZoomMode>))]
    public ZoomMode Mode { get; set; } = ZoomMode.Monitor;
    public double LensZoom { get; set; } = 2.0;
    /// <summary>Virtual-key code that shows the lens while held; 0 = none. Default: mouse back button.</summary>
    public int HoldKey { get; set; } = 0x05;

    /// <summary>Shortcut overrides by action id (see <see cref="Zoomifier.Shortcuts"/>); defaults are not stored.</summary>
    public Dictionary<string, string> Shortcuts { get; set; } = new();

    [JsonIgnore]
    public Rectangle Region
    {
        get => new(X, Y, W, H);
        set { X = value.X; Y = value.Y; W = value.Width; H = value.Height; }
    }

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Zoomifier", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
                s.Shortcuts ??= new();
                return s;
            }
        }
        catch
        {
            // Unreadable (e.g. written by a different version): keep a copy so the user's settings aren't lost.
            try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } catch { /* best effort */ }
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep "Ctrl+Alt+Z" readable
            }));
        }
        catch { /* settings are best-effort */ }
    }
}
