using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Resolves the true download size of a compose set before it is pulled (epic-106 FR-1).
    /// </summary>
    public interface IPullSizeResolver
    {
        /// <summary>
        /// Reads the registry manifests of every service image for the device platform and subtracts the layers
        /// already present locally. Never throws: whatever cannot be read is returned as not sized.
        /// </summary>
        Task<PullSizeTable> ResolveAsync(string[] composeFiles, string workingDirectory);
    }

    /// <summary>
    /// <see cref="IPullSizeResolver"/> that runs the docker CLI on the device over <see cref="ISshService"/>,
    /// with <c>sudo</c> like the pull itself so the registry credentials are the same.
    /// </summary>
    /// <remarks>
    /// Local layers (design.md "Already local"): a target layer counts as present when some local image of a repository in
    /// the compose set has the same layer digests from the bottom up to and including that layer. That is the condition
    /// under which the classic daemon reports <c>Already exists</c> (it looks layers up by chain, not by digest alone) and
    /// the containerd store skips the blob. The local images' layer lists come from the registry manifest of their repo
    /// digest. Everything the check cannot prove present is counted as to fetch, so the total can be too large, never too small.
    /// </remarks>
    public sealed partial class DockerPullSizeResolver : IPullSizeResolver
    {
        internal const string VersionCommand = "sudo docker version --format '{{.Server.Os}}/{{.Server.Arch}}'";
        internal const string ImageListCommand = "sudo docker image ls --no-trunc --digests --format '{{json .}}'";

        /// <summary>
        /// Local repo digests inspected per repository, newest first. Skipped ones can only make the total larger.
        /// </summary>
        internal const int MaxLocalDigestsPerRepository = 3;

        internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(60);

        private readonly ISshService _ssh;
        private readonly ILogger<DockerPullSizeResolver> _logger;

        public DockerPullSizeResolver(ISshService ssh, ILogger<DockerPullSizeResolver> logger)
        {
            _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        internal static string ConfigCommand(string[] composeFiles)
        {
            var composeFileArgs = string.Join(" ", composeFiles.Select(f => $"-f \"{f}\""));
            return $"sudo docker compose {composeFileArgs} config --format json";
        }

        internal static string ManifestCommand(string reference) => $"sudo docker manifest inspect --verbose {reference}";

        public async Task<PullSizeTable> ResolveAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                return await ResolveCoreAsync(composeFiles, workingDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not size the update before pulling; progress will show layers as not sized");
                return PullSizeTable.Empty;
            }
        }

        private async Task<PullSizeTable> ResolveCoreAsync(string[] composeFiles, string workingDirectory)
        {
            var config = await RunAsync(ConfigCommand(composeFiles), workingDirectory);
            if (config == null)
            {
                return PullSizeTable.Empty;
            }

            var services = ParseComposeServices(config);
            if (services.Count == 0)
            {
                return PullSizeTable.Empty;
            }

            var devicePlatform = await RunAsync(VersionCommand, workingDirectory);
            var device = devicePlatform != null ? Platform.Parse(devicePlatform.Trim()) : null;
            if (device == null)
            {
                _logger.LogWarning("Could not read the device platform (docker version); images without a pinned platform stay not sized");
            }

            // One entry per image reference + platform; services sharing it are grouped.
            var groups = services
                .GroupBy(s => (s.Reference, s.Platform))
                .ToList();

            var imageLayers = new List<(string Reference, ImmutableArray<string> Services, Platform? Platform, IReadOnlyList<ManifestLayer>? Layers)>();
            foreach (var group in groups)
            {
                var platform = group.Key.Platform != null ? Platform.Parse(group.Key.Platform) : device;
                IReadOnlyList<ManifestLayer>? layers = null;
                if (platform == null)
                {
                    _logger.LogWarning("Image {Image}: unknown platform, not sized", group.Key.Reference);
                }
                else
                {
                    layers = await ReadLayersAsync(group.Key.Reference, platform, workingDirectory);
                }
                imageLayers.Add((group.Key.Reference, group.Select(s => s.Service).ToImmutableArray(), platform, layers));
            }

            var localChains = await ReadLocalChainsAsync(imageLayers.Where(i => i.Layers != null && i.Platform != null)
                .Select(i => (ImageReference.Repository(i.Reference), i.Platform!))
                .Distinct()
                .ToList(), workingDirectory);

            var toFetch = ImmutableDictionary.CreateBuilder<string, long>(StringComparer.Ordinal);
            var present = ImmutableHashSet.CreateBuilder<string>(StringComparer.Ordinal);
            var images = ImmutableArray.CreateBuilder<PullImageSize>();
            foreach (var image in imageLayers)
            {
                if (image.Layers == null)
                {
                    images.Add(new PullImageSize(image.Reference, image.Services, false, ImmutableArray<string>.Empty));
                    continue;
                }

                var fetch = ImmutableArray.CreateBuilder<string>();
                var chain = string.Empty;
                foreach (var layer in image.Layers)
                {
                    chain = Chain(chain, layer.Digest);
                    var id = PullSizeTable.ShortId(layer.Digest);
                    if (localChains.Contains(chain))
                    {
                        present.Add(id);
                        continue;
                    }

                    fetch.Add(id);
                    // Deduplicated by digest: a base layer shared by two images is downloaded once.
                    toFetch[id] = Math.Max(layer.Size, toFetch.TryGetValue(id, out var known) ? known : 0);
                }
                images.Add(new PullImageSize(image.Reference, image.Services, true, fetch.ToImmutable()));
            }

            // A layer both fetched for one image and present under another's chain is still downloaded: keep it in the total.
            foreach (var id in toFetch.Keys)
            {
                present.Remove(id);
            }

            var table = new PullSizeTable(toFetch.ToImmutable(), present.ToImmutable(), images.ToImmutable());
            _logger.LogInformation("Update download size resolved: {Table}", table);
            return table;
        }

        private async Task<IReadOnlyList<ManifestLayer>?> ReadLayersAsync(string reference, Platform platform, string workingDirectory)
        {
            if (!SafeReference().IsMatch(reference))
            {
                _logger.LogWarning("Image {Image}: reference not usable in a shell command, not sized", reference);
                return null;
            }

            var output = await RunAsync(ManifestCommand(reference), workingDirectory);
            if (output == null)
            {
                return null;
            }

            try
            {
                var layers = SelectPlatformLayers(output, platform);
                if (layers == null)
                {
                    _logger.LogWarning("Image {Image}: no manifest for platform {Platform}, not sized", reference, platform);
                }
                return layers;
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Image {Image}: manifest is not readable JSON, not sized", reference);
                return null;
            }
        }

        /// <summary>
        /// Chains (bottom-up layer digest sequences) of local images of the given repositories.
        /// </summary>
        private async Task<HashSet<string>> ReadLocalChainsAsync(IReadOnlyList<(string Repository, Platform Platform)> repositories, string workingDirectory)
        {
            var chains = new HashSet<string>(StringComparer.Ordinal);
            if (repositories.Count == 0)
            {
                return chains;
            }

            var listing = await RunAsync(ImageListCommand, workingDirectory);
            if (listing == null)
            {
                return chains;
            }

            var local = ParseLocalImages(listing);
            foreach (var (repository, platform) in repositories)
            {
                var digests = local
                    .Where(l => l.Repository == repository)
                    .OrderByDescending(l => l.CreatedAt, StringComparer.Ordinal)
                    .Select(l => l.RepoDigestReference)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (digests.Count > MaxLocalDigestsPerRepository)
                {
                    _logger.LogInformation("Repository {Repository}: {Count} local images, checking the newest {Max} for shared layers",
                        repository, digests.Count, MaxLocalDigestsPerRepository);
                }

                foreach (var reference in digests.Take(MaxLocalDigestsPerRepository))
                {
                    var layers = await ReadLayersAsync(reference, platform, workingDirectory);
                    if (layers == null)
                    {
                        continue;
                    }

                    var chain = string.Empty;
                    foreach (var layer in layers)
                    {
                        chain = Chain(chain, layer.Digest);
                        chains.Add(chain);
                    }
                }
            }

            return chains;
        }

        private async Task<string?> RunAsync(string command, string workingDirectory)
        {
            try
            {
                var result = await _ssh.ExecuteCommandAsync(command, CommandTimeout, workingDirectory);
                if (result.IsSuccess)
                {
                    return result.Output;
                }

                _logger.LogWarning("Sizing command failed (exit {ExitCode}): {Command}: {Reason}",
                    result.ExitCode, command, DockerComposeService.DescribeFailure(result));
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Sizing command failed: {Command}", command);
                return null;
            }
        }

        internal sealed record ComposeService(string Service, string Reference, string? Platform);

        internal sealed record ManifestLayer(string Digest, long Size);

        internal sealed record LocalImage(string Repository, string RepoDigestReference, string CreatedAt);

        /// <summary>
        /// Services of <c>docker compose config --format json</c> that have an image; build-only services are skipped.
        /// </summary>
        internal static IReadOnlyList<ComposeService> ParseComposeServices(string json)
        {
            var result = new List<ComposeService>();
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            foreach (var service in services.EnumerateObject())
            {
                if (!TryGetString(service.Value, "image", out var image) || string.IsNullOrWhiteSpace(image))
                {
                    continue;
                }

                TryGetString(service.Value, "pull_policy", out var pullPolicy);
                if (pullPolicy is "never" or "build")
                {
                    continue;
                }

                TryGetString(service.Value, "platform", out var platform);
                result.Add(new ComposeService(service.Name, ImageReference.Normalize(image), string.IsNullOrWhiteSpace(platform) ? null : platform));
            }

            return result;
        }

        /// <summary>
        /// Layers of the manifest matching <paramref name="platform"/> in <c>docker manifest inspect --verbose</c> output:
        /// an array for a multi-platform index, an object for a single manifest. Null when no entry matches unambiguously.
        /// </summary>
        internal static IReadOnlyList<ManifestLayer>? SelectPlatformLayers(string json, Platform platform)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var entries = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };

            var candidates = new List<(JsonElement Entry, Platform? Platform)>();
            foreach (var entry in entries)
            {
                Platform? entryPlatform = null;
                if (entry.TryGetProperty("Descriptor", out var descriptor)
                    && descriptor.TryGetProperty("platform", out var p)
                    && p.ValueKind == JsonValueKind.Object)
                {
                    TryGetString(p, "os", out var os);
                    TryGetString(p, "architecture", out var arch);
                    TryGetString(p, "variant", out var variant);
                    entryPlatform = new Platform(os ?? string.Empty, arch ?? string.Empty, variant);
                }
                candidates.Add((entry, entryPlatform));
            }

            JsonElement? chosen = null;
            if (root.ValueKind != JsonValueKind.Array && candidates.Count == 1)
            {
                // A single manifest is what the pull fetches whatever it claims; a wrong platform fails the pull anyway.
                var only = candidates[0];
                if (only.Platform == null || only.Platform.Matches(platform))
                {
                    chosen = only.Entry;
                }
            }
            else
            {
                var matching = candidates.Where(c => c.Platform != null && c.Platform.Matches(platform)).ToList();
                if (matching.Count > 1)
                {
                    matching = matching.Where(c => c.Platform!.VariantEquals(platform)).ToList();
                }
                if (matching.Count == 1)
                {
                    chosen = matching[0].Entry;
                }
            }

            if (chosen is not { } manifestEntry)
            {
                return null;
            }

            JsonElement manifest;
            if (!manifestEntry.TryGetProperty("OCIManifest", out manifest) && !manifestEntry.TryGetProperty("SchemaV2Manifest", out manifest))
            {
                return null;
            }

            if (!manifest.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var layers = new List<ManifestLayer>();
            foreach (var layer in layersElement.EnumerateArray())
            {
                if (!TryGetString(layer, "digest", out var digest) || digest == null
                    || !layer.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes))
                {
                    return null;
                }
                layers.Add(new ManifestLayer(digest, bytes));
            }
            return layers;
        }

        /// <summary>
        /// Rows of <c>docker image ls --no-trunc --digests --format '{{json .}}'</c> that carry a repo digest.
        /// </summary>
        internal static IReadOnlyList<LocalImage> ParseLocalImages(string output)
        {
            var result = new List<LocalImage>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (line[0] != '{')
                {
                    continue;
                }

                try
                {
                    using var document = JsonDocument.Parse(line);
                    var row = document.RootElement;
                    if (!TryGetString(row, "Repository", out var repository) || !TryGetString(row, "Digest", out var digest)
                        || string.IsNullOrEmpty(repository) || repository == "<none>"
                        || digest == null || !digest.StartsWith("sha256:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    TryGetString(row, "CreatedAt", out var createdAt);
                    var normalizedRepository = ImageReference.Repository(ImageReference.Normalize(repository + "@" + digest));
                    result.Add(new LocalImage(normalizedRepository, $"{repository}@{digest}", createdAt ?? string.Empty));
                }
                catch (JsonException)
                {
                    // Not a row.
                }
            }
            return result;
        }

        /// <summary>
        /// Identity of a layer together with every layer below it, like the daemon's chain id.
        /// </summary>
        internal static string Chain(string parent, string digest)
        {
            if (parent.Length == 0)
            {
                return digest;
            }
            return "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(parent + " " + digest)));
        }

        private static bool TryGetString(JsonElement element, string name, out string? value)
        {
            if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                value = property.GetString();
                return true;
            }
            value = null;
            return false;
        }

        [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._:/@-]*$")]
        private static partial Regex SafeReference();

        /// <summary>
        /// OS/architecture[/variant], e.g. <c>linux/arm64/v8</c>.
        /// </summary>
        internal sealed record Platform(string Os, string Architecture, string? Variant)
        {
            public static Platform? Parse(string value)
            {
                var parts = value.Trim().Trim('\'').Split('/');
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                {
                    return null;
                }
                return new Platform(parts[0], parts[1], parts.Length > 2 && parts[2].Length > 0 ? parts[2] : null);
            }

            public bool Matches(Platform device) =>
                string.Equals(Os, device.Os, StringComparison.OrdinalIgnoreCase)
                && string.Equals(Architecture, device.Architecture, StringComparison.OrdinalIgnoreCase)
                && (Variant == null || device.Variant == null || VariantEquals(device));

            /// <summary>
            /// Variant equality where arm64's implicit variant is v8.
            /// </summary>
            public bool VariantEquals(Platform device) =>
                string.Equals(NormalVariant(this), NormalVariant(device), StringComparison.OrdinalIgnoreCase);

            private static string? NormalVariant(Platform p) =>
                p.Variant ?? (string.Equals(p.Architecture, "arm64", StringComparison.OrdinalIgnoreCase) ? "v8" : null);

            public override string ToString() => Variant == null ? $"{Os}/{Architecture}" : $"{Os}/{Architecture}/{Variant}";
        }
    }
}
