using System.Text.Json;
using System.Text.Json.Serialization;

namespace Zoomifier;

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
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
        }
        catch { /* fall back to defaults */ }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* settings are best-effort */ }
    }
}
