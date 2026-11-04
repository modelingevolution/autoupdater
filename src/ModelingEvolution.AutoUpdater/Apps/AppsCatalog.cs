using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>The operating system a download is for. The manifest spells it in lower case.</summary>
public enum AppPlatform
{
    Windows,
    Linux,
    Macos,
    Android,
    Ios,
}

/// <summary>The processor architecture a download is for. The manifest spells it in lower case.</summary>
public enum AppArchitecture
{
    X64,
    Arm64,
    X86,
    Arm,
    Any,
}

/// <summary>The file one download offers, as the apps image lists it.</summary>
public sealed record AppFile(string Version, string FileName, long Size);

/// <summary>
/// One download of an app: a platform and architecture, optionally a short <see cref="Label"/> that tells two variants
/// apart, and a <see cref="Preview"/> flag. <see cref="File"/> is null for a download listed without a file yet;
/// <see cref="Guide"/> is the name of its markdown install guide, served at <c>guides/&lt;name&gt;</c>, or null.
/// </summary>
public sealed record AppDownload(AppPlatform Platform, AppArchitecture Architecture, string? Label, bool Preview, AppFile? File, string? Guide)
{
    /// <summary>Unique within its app: the manifest drops a later download with the same platform, architecture and label.</summary>
    public string Key => $"{Platform}-{Architecture}-{Label}".ToLowerInvariant();
}

/// <summary>
/// One app on the Apps page, as the apps image's manifest.json (schemaVersion 2) lists it: a name, an optional PNG
/// <see cref="Icon"/> served at <c>icons/&lt;name&gt;</c>, and its downloads in page order.
/// </summary>
public sealed record AppEntry(string Id, string Name, string? Icon, ImmutableArray<AppDownload> Downloads);

/// <summary>
/// What the apps image offers. <see cref="Reachable"/> is false when it could not be read, or when it uses a manifest
/// schema this AutoUpdater does not read (<see cref="UnsupportedSchemaVersion"/>).
/// </summary>
public sealed record AppsCatalog(bool Reachable, ImmutableArray<AppEntry> Apps)
{
    public static readonly AppsCatalog Unreachable = new(false, ImmutableArray<AppEntry>.Empty);

    /// <summary>The schemaVersion the image declared when it is not one this AutoUpdater reads; "?" when it declared none.</summary>
    public string? UnsupportedSchemaVersion { get; init; }

    public static AppsCatalog Unsupported(string declared) => Unreachable with { UnsupportedSchemaVersion = declared };

    /// <summary>Every file the manifest lists: the only names the file proxy fetches.</summary>
    public IEnumerable<AppFile> Files => Apps.SelectMany(a => a.Downloads).Select(d => d.File).OfType<AppFile>();

    /// <summary>Every icon the manifest lists: the only names the icon endpoint fetches.</summary>
    public IEnumerable<string> Icons => Apps.Select(a => a.Icon).OfType<string>();
}

/// <summary>
/// Reads the image's manifest.json (schemaVersion 2). It never throws: the apps image is optional, and nothing it answers
/// may hurt the AutoUpdater, which is the remote recovery path. An app or download that does not validate is dropped, and
/// a manifest of another schema is refused as a whole. The rules match packaging/apps/publish-apps.sh, which refuses to
/// publish what this would drop.
/// </summary>
public static partial class AppsManifest
{
    public const int SchemaVersion = 2;
    public const int MaxApps = 16;
    public const int MaxDownloads = 8;
    public const int MaxName = 80;
    public const int MaxLabel = 40;

    // \z, not $: $ also matches before a final line break, so "a.exe\n" would pass.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\z")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,124}\.md\z")]
    private static partial Regex GuideNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,123}\.png\z")]
    private static partial Regex IconNamePattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex IdPattern();

    private static readonly ImmutableDictionary<string, AppPlatform> Platforms =
        Enum.GetValues<AppPlatform>().ToImmutableDictionary(p => p.ToString().ToLowerInvariant());

    private static readonly ImmutableDictionary<string, AppArchitecture> Architectures =
        Enum.GetValues<AppArchitecture>().ToImmutableDictionary(a => a.ToString().ToLowerInvariant());

    /// <summary>One plain file name: the rule publish-apps.sh, nginx and the AutoUpdater's file proxy all apply.</summary>
    public static bool IsSafeFileName(string? name) => name != null && FileNamePattern().IsMatch(name);

    /// <summary>A guide's name: a plain file name ending in ".md", at most 128 characters.</summary>
    public static bool IsGuideName(string? name) => name != null && GuideNamePattern().IsMatch(name);

    /// <summary>An icon's name: a plain file name ending in ".png", at most 128 characters.</summary>
    public static bool IsIconName(string? name) => name != null && IconNamePattern().IsMatch(name);

    /// <summary>An app id: lower-case letters, digits and '-', starting with a letter or digit, at most 32.</summary>
    public static bool IsAppId(string? id) => id != null && IdPattern().IsMatch(id);

    /// <summary>
    /// Plain text for one line of the page: not blank, at most <paramref name="max"/> UTF-16 characters, no control
    /// characters (line breaks included), no line/paragraph separators, bidirectional embeddings, overrides or isolates,
    /// no lone surrogates. Blazor encodes it; this keeps it honest and on one line.
    /// </summary>
    public static bool IsPlainText(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > max) return false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (char.IsControl(c) || IsLayoutControl(c)) return false;
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { i++; continue; }
            if (char.IsSurrogate(c)) return false;
        }

        return true;
    }

    /// <summary>U+2028/U+2029 (line/paragraph separator), U+202A-U+202E (embeddings, overrides), U+2066-U+2069 (isolates).</summary>
    private static bool IsLayoutControl(char c) => (int)c is 0x2028 or 0x2029 or (>= 0x202A and <= 0x202E) or (>= 0x2066 and <= 0x2069);

    public static AppsCatalog Parse(string? manifestJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return AppsCatalog.Unreachable;

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson, new JsonDocumentOptions { MaxDepth = 16 });
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return AppsCatalog.Unreachable;

            if (!root.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number)
                return AppsCatalog.Unsupported("?");
            if (!schema.TryGetInt32(out var declared) || declared != SchemaVersion)
                return AppsCatalog.Unsupported(schema.GetRawText().Length <= 12 ? schema.GetRawText() : "?");

            if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return AppsCatalog.Unreachable;

            var entries = ImmutableArray.CreateBuilder<AppEntry>();
            var files = new HashSet<string>(StringComparer.Ordinal);
            // Only the first MaxApps positions are read: the page stays bounded whatever the image says.
            foreach (var app in apps.EnumerateArray().Take(MaxApps))
            {
                if (ReadApp(app, files) is not { } entry) continue;
                if (entries.Any(e => e.Id == entry.Id)) continue;
                foreach (var file in entry.Downloads.Select(d => d.File).OfType<AppFile>()) files.Add(file.FileName);
                entries.Add(entry);
            }

            return new AppsCatalog(true, entries.ToImmutable());
        }
        catch (JsonException)
        {
            return AppsCatalog.Unreachable;
        }
    }

    /// <param name="app">One element of "apps".</param>
    /// <param name="taken">File names earlier apps already use; a download repeating one is dropped.</param>
    private static AppEntry? ReadApp(JsonElement app, HashSet<string> taken)
    {
        if (app.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(app, "id");
        var name = ReadString(app, "name");
        if (!IsAppId(id) || !IsPlainText(name, MaxName)) return null;

        // An icon that is not a plain .png name is dropped, not the app: the page shows the neutral icon instead.
        var icon = ReadString(app, "icon");
        if (!IsIconName(icon)) icon = null;

        if (!app.TryGetProperty("downloads", out var list) || list.ValueKind != JsonValueKind.Array) return null;

        var downloads = ImmutableArray.CreateBuilder<AppDownload>();
        foreach (var element in list.EnumerateArray().Take(MaxDownloads))
        {
            if (ReadDownload(element) is not { } download) continue;
            if (downloads.Any(d => d.Key == download.Key)) continue;
            if (download.File is { } file && (taken.Contains(file.FileName) || downloads.Any(d => d.File?.FileName == file.FileName))) continue;
            downloads.Add(download);
        }

        return new AppEntry(id!, name!, icon, downloads.ToImmutable());
    }

    private static AppDownload? ReadDownload(JsonElement download)
    {
        if (download.ValueKind != JsonValueKind.Object) return null;

        // Closed sets: a platform or architecture this AutoUpdater does not know drops that download only.
        if (ReadString(download, "platform") is not { } platformName || !Platforms.TryGetValue(platformName, out var platform)) return null;
        if (ReadString(download, "architecture") is not { } archName || !Architectures.TryGetValue(archName, out var architecture)) return null;

        string? label = null;
        if (download.TryGetProperty("label", out var labelElement) && labelElement.ValueKind != JsonValueKind.Null)
        {
            label = labelElement.ValueKind == JsonValueKind.String ? GetString(labelElement) : null;
            if (!IsPlainText(label, MaxLabel)) return null;
        }

        var preview = false;
        if (download.TryGetProperty("preview", out var previewElement))
        {
            if (previewElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            preview = previewElement.GetBoolean();
        }

        string? guide = null;
        if (download.TryGetProperty("guide", out var guideElement) && guideElement.ValueKind != JsonValueKind.Null)
        {
            guide = guideElement.ValueKind == JsonValueKind.String ? GetString(guideElement) : null;
            if (!IsGuideName(guide)) return null;
        }

        if (!ReadFile(download, out var file)) return null;

        return new AppDownload(platform, architecture, label?.Trim(), preview, file, guide);
    }

    /// <summary>version, file and size come together or not at all; some but not all, or any invalid, drops the download.</summary>
    private static bool ReadFile(JsonElement download, out AppFile? file)
    {
        file = null;
        string[] names = ["version", "file", "size"];
        var present = names.Count(n => download.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null);
        if (present == 0) return true;
        if (present != names.Length) return false;

        var version = ReadString(download, "version");
        var name = ReadString(download, "file");
        if (version == null || name == null || !VersionPattern().IsMatch(version) || !FileNamePattern().IsMatch(name)) return false;
        if (!download.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
            || !size.TryGetInt64(out var bytes) || bytes <= 0) return false;

        file = new AppFile(version, name, bytes);
        return true;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? GetString(value) : null;

    /// <summary>GetString throws for an escaped lone surrogate it cannot transcode; that is just an invalid value here.</summary>
    private static string? GetString(JsonElement value)
    {
        try
        {
            return value.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
