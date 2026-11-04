namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>
/// The rules for an app's icon. PNG only: an SVG can carry script, and the browser would run it if it were ever opened
/// directly, so anything that does not start with the PNG signature is refused, whatever its name says.
/// </summary>
public static class AppIcon
{
    /// <summary>The largest icon the AutoUpdater reads and publish-apps.sh accepts.</summary>
    public const int MaxBytes = 256 * 1024;

    public const string ContentType = "image/png";

    private static ReadOnlySpan<byte> Signature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Starts with the 8-byte PNG signature and is within <see cref="MaxBytes"/>.</summary>
    public static bool IsPng(ReadOnlySpan<byte> bytes) => bytes.Length <= MaxBytes && bytes.StartsWith(Signature);

    /// <summary>The page's link to an icon, through the AutoUpdater's own endpoint, relative to the page.</summary>
    public static string Path(string iconName) => "apps/icons/" + Uri.EscapeDataString(iconName);
}
