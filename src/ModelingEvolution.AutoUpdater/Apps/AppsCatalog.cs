using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModelingEvolution.AutoUpdater.Apps;

/// <summary>One file the apps container offers.</summary>
public sealed record AppFile(string Platform, string Version, string FileName, long Size, string Sha256, bool Verified);

/// <summary>
/// What the apps container offers now. <see cref="Reachable"/> is false when it could not be read.
/// <see cref="VerificationKnown"/> is false when the container gave no usable verification result: then nothing is verified,
/// but nothing is known to be wrong either.
/// </summary>
public sealed record AppsCatalog(bool Reachable, string? Release, ImmutableArray<AppFile> Files, bool VerificationKnown = true)
{
    public static readonly AppsCatalog Unreachable = new(false, null, ImmutableArray<AppFile>.Empty);

    public AppFile? Find(string platform) => Files.FirstOrDefault(f => f.Platform == platform);
}

/// <summary>
/// Reads the container's manifest.json and verification.json. It never throws: the apps container is optional, and
/// nothing it answers may hurt the AutoUpdater, which is the remote recovery path. An entry that does not validate is
/// dropped, and a file counts as verified only when the container positively says so.
/// </summary>
public static partial class AppsManifest
{
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._+~-]{0,127}$")]
    private static partial Regex FileNamePattern();

    [GeneratedRegex(@"^[0-9A-Za-z][0-9A-Za-z.+~_-]{0,63}$")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    public static AppsCatalog Parse(string? manifestJson, string? verificationJson)
    {
        if (string.IsNullOrWhiteSpace(manifestJson)) return AppsCatalog.Unreachable;

        try
        {
            using var manifest = JsonDocument.Parse(manifestJson);
            var root = manifest.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return AppsCatalog.Unreachable;
            if (!root.TryGetProperty("apps", out var apps) || apps.ValueKind != JsonValueKind.Array) return AppsCatalog.Unreachable;

            var (verificationKnown, verified) = ReadVerification(verificationJson);
            var files = ImmutableArray.CreateBuilder<AppFile>();
            foreach (var app in apps.EnumerateArray())
            {
                if (ReadEntry(app, verified) is { } file && files.All(f => f.Platform != file.Platform))
                {
                    files.Add(file);
                }
            }

            var release = ReadString(root, "release");
            return new AppsCatalog(true, release != null && VersionPattern().IsMatch(release) ? release : null, files.ToImmutable(), verificationKnown);
        }
        catch (JsonException)
        {
            return AppsCatalog.Unreachable;
        }
    }

    private static AppFile? ReadEntry(JsonElement app, Func<string, bool> verified)
    {
        if (app.ValueKind != JsonValueKind.Object) return null;

        var platform = ReadString(app, "platform");
        var version = ReadString(app, "version");
        var file = ReadString(app, "file");
        var sha = ReadString(app, "sha256");
        if (platform == null || version == null || file == null || sha == null) return null;
        if (!AppGuides.IsKnownPlatform(platform)) return null;
        if (!VersionPattern().IsMatch(version) || !FileNamePattern().IsMatch(file) || !Sha256Pattern().IsMatch(sha)) return null;
        if (!app.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0) return null;

        return new AppFile(platform, version, file, size, sha, verified(file));
    }

    /// <summary>Fails closed: no verification, an unreadable one, or anything but "the check ran" verifies nothing.</summary>
    private static (bool Known, Func<string, bool> Verified) ReadVerification(string? verificationJson)
    {
        (bool, Func<string, bool>) unknown = (false, _ => false);
        if (string.IsNullOrWhiteSpace(verificationJson)) return unknown;

        try
        {
            using var document = JsonDocument.Parse(verificationJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return unknown;

            var state = ReadString(root, "state");
            if (state is not ("verified" or "failed")) return unknown;
            if (!root.TryGetProperty("blocked", out var blocked) || blocked.ValueKind != JsonValueKind.Array) return unknown;

            var names = blocked.EnumerateArray()
                .Where(b => b.ValueKind == JsonValueKind.String)
                .Select(b => b.GetString()!)
                .ToImmutableHashSet();
            return (true, name => !names.Contains(name));
        }
        catch (JsonException)
        {
            return unknown;
        }
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
