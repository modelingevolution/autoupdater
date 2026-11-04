using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>The file one app offers, as the apps image lists it.</summary>
public sealed record AppFile(string Version, string FileName, long Size);

/// <summary>
/// One app on the Apps page, as the apps image's manifest.json (schemaVersion 2) lists it. <see cref="File"/> is null for an
/// app listed without a file yet; <see cref="Guide"/> is the name of its markdown install guide, served at
/// <c>guides/&lt;name&gt;</c>, or null for none.
/// </summary>
public sealed record AppEntry(string Id, string Title, AppFile? File, string? Guide);

/// <summary>
/// What the apps image offers. <see cref="Reachable"/> is false when it could not be read, or when it uses a manifest
/// schema this AutoUpdater does not read (<see cref="UnsupportedSchemaVersion"/>).
/// </summary>
public sealed record AppsCatalog(bool Reachable, string? Release, ImmutableArray<AppEntry> Apps)
{
    public static readonly AppsCatalog Unreachable = new(false, null, ImmutableArray<AppEntry>.Empty);

    /// <summary>The schemaVersion the image declared when it is not one this AutoUpdater reads; "?" when it declared none.</summary>
    public string? UnsupportedSchemaVersion { get; init; }

    public static AppsCatalog Unsupported(string declared) => Unreachable with { UnsupportedSchemaVersion = declared };

    public AppEntry? Find(string id) => Apps.FirstOrDefault(a => a.Id == id);

    public IEnumerable<AppFile> Files => Apps.Select(a => a.File).OfType<AppFile>();
}

/// <summary>
/// Reads the image's manifest.json (schemaVersion 2). It never throws: the apps image is optional, and nothing it answers
/// may hurt the AutoUpdater, which is the remote recovery path. An entry that does not validate is dropped, and a manifest
/// of another schema is refused as a whole. The rules match packaging/apps/publish-apps.sh, which refuses to publish what
/// this would drop.
/// </summary>
public static partial class AppsManifest
{
    public const int SchemaVersion = 2;
    public const int MaxApps = 16;
    public const int MaxTitle = 80;

    // \z, not $: $ also matches before a final line break, so "a.exe\n" would pass.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}\z")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,124}\.md\z")]
    private static partial Regex GuideNamePattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}\z")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9-]{0,31}\z")]
    private static partial Regex IdPattern();

    /// <summary>One plain file name: the rule publish-apps.sh, nginx and the AutoUpdater's file proxy all apply.</summary>
    public static bool IsSafeFileName(string? name) => name != null && FileNamePattern().IsMatch(name);

    /// <summary>A guide's name: a plain file name ending in ".md", at most 128 characters.</summary>
    public static bool IsGuideName(string? name) => name != null && GuideNamePattern().IsMatch(name);

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
            // Only the first MaxApps positions are read: the page stays bounded whatever the image says.
            foreach (var app in apps.EnumerateArray().Take(MaxApps))
            {
                if (ReadEntry(app) is not { } entry) continue;
                if (entries.Any(e => e.Id == entry.Id)) continue;
                if (entry.File != null && entries.Any(e => e.File?.FileName == entry.File.FileName)) continue;
                entries.Add(entry);
            }

            var release = ReadString(root, "release");
            return new AppsCatalog(true, release != null && VersionPattern().IsMatch(release) ? release : null, entries.ToImmutable());
        }
        catch (JsonException)
        {
            return AppsCatalog.Unreachable;
        }
    }

    private static AppEntry? ReadEntry(JsonElement app)
    {
        if (app.ValueKind != JsonValueKind.Object) return null;

        var id = ReadString(app, "id");
        var title = ReadString(app, "title");
        if (!IsAppId(id) || !IsPlainText(title, MaxTitle)) return null;

        string? guide = null;
        if (app.TryGetProperty("guide", out var guideElement) && guideElement.ValueKind != JsonValueKind.Null)
        {
            guide = guideElement.ValueKind == JsonValueKind.String ? GetString(guideElement) : null;
            if (!IsGuideName(guide)) return null;
        }

        if (!ReadFile(app, out var file)) return null;

        return new AppEntry(id!, title!, file, guide);
    }

    /// <summary>version, file and size come together or not at all; some but not all, or any invalid, drops the entry.</summary>
    private static bool ReadFile(JsonElement app, out AppFile? file)
    {
        file = null;
        string[] names = ["version", "file", "size"];
        var present = names.Count(n => app.TryGetProperty(n, out var v) && v.ValueKind != JsonValueKind.Null);
        if (present == 0) return true;
        if (present != names.Length) return false;

        var version = ReadString(app, "version");
        var name = ReadString(app, "file");
        if (version == null || name == null || !VersionPattern().IsMatch(version) || !FileNamePattern().IsMatch(name)) return false;
        if (!app.TryGetProperty("size", out var size) || size.ValueKind != JsonValueKind.Number
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
