namespace Zoomifier;

internal static class AppIcon
{
    /// <summary>Loads the embedded app.ico at the requested size (Windows picks the closest frame).</summary>
    public static Icon Load(Size size)
    {
        using var stream = typeof(AppIcon).Assembly.GetManifestResourceStream("Zoomifier.app.ico")!;
        return new Icon(stream, size);
    }
}
