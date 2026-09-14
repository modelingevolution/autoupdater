using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
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
    /// <para>
    /// Bounded (design.md "Budget and breaker"): the whole resolution gets <see cref="Budget"/>; a command still running when it
    /// runs out is abandoned. The first failed or timed-out registry read (<c>docker manifest inspect</c>) stops all further
    /// registry reads, so a slow, rate-limiting or unreachable registry costs one command timeout at most, not one per image.
    /// </para>
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

        /// <summary>
        /// Default <see cref="Budget"/>.
        /// </summary>
        internal static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(90);

        private const string ManifestCommandPrefix = "sudo docker manifest inspect ";

        private readonly ISshService _ssh;
        private readonly ILogger<DockerPullSizeResolver> _logger;

        /// <summary>
        /// Upper bound on one whole resolution, all commands included. What is not read by then stays not sized.
        /// </summary>
        internal TimeSpan Budget { get; init; } = DefaultBudget;

        /// <summary>
        /// Creates a resolver that runs its docker commands through <paramref name="ssh"/>.
        /// </summary>
        /// <param name="ssh">Command channel to the device; the same one the pull uses.</param>
        /// <param name="logger">Receives one Information summary per resolution and a Warning when registry reads are cut short.</param>
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

        /// <summary>
        /// Plain (not <c>--verbose</c>) inspect: one registry request. <c>--verbose</c> fetches the manifest of every platform in
        /// an index (17 requests for alpine), which trips registry rate limits; Docker Hub counts manifest GETs as pulls.
        /// </summary>
        internal static string ManifestCommand(string reference) => ManifestCommandPrefix + reference;

        public async Task<PullSizeTable> ResolveAsync(string[] composeFiles, string workingDirectory)
        {
            try
            {
                return await new Run(this, workingDirectory).ResolveAsync(composeFiles);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not size the update before pulling; progress will show layers as not sized");
                return PullSizeTable.Empty;
            }
        }

        /// <summary>
        /// One resolution: the budget clock, the registry breaker, and cached command outputs (a platform manifest shared by a
        /// target and a local image is read once).
        /// </summary>
        private sealed class Run
        {
            private readonly DockerPullSizeResolver _owner;
            private readonly string _workingDirectory;
            private readonly Dictionary<string, string?> _outputs = new(StringComparer.Ordinal);
            private readonly Stopwatch _clock = Stopwatch.StartNew();

            public Run(DockerPullSizeResolver owner, string workingDirectory)
            {
                _owner = owner;
                _workingDirectory = workingDirectory;
            }

            public int Commands { get; private set; }
            public int SkippedRegistryReads { get; private set; }
            public bool RegistryTripped { get; private set; }
            public bool BudgetExhausted { get; private set; }
            public TimeSpan Elapsed => _clock.Elapsed;

            public Task<PullSizeTable> ResolveAsync(string[] composeFiles) => _owner.ResolveCoreAsync(composeFiles, this);

            public async Task<string?> OutputAsync(string command)
            {
                if (_outputs.TryGetValue(command, out var cached))
                {
                    return cached;
                }

                var registry = command.StartsWith(ManifestCommandPrefix, StringComparison.Ordinal);
                if (BudgetExhausted || (registry && RegistryTripped))
                {
                    if (registry)
                    {
                        SkippedRegistryReads++;
                    }
                    return null;
                }

                var remaining = _owner.Budget - _clock.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    Exhaust(command);
                    return registry ? Skip() : null;
                }

                Commands++;
                var (output, failure) = await _owner.RunAsync(command, _workingDirectory, remaining);
                if (failure == Failure.BudgetExhausted)
                {
                    Exhaust(command);
                }
                else if (failure != Failure.None && registry && !RegistryTripped)
                {
                    RegistryTripped = true;
                    _owner._logger.LogWarning(
                        "Registry read failed after {Elapsed} ms ({Command}); remaining image sizes are not read and show as not sized",
                        (long)_clock.Elapsed.TotalMilliseconds, command);
                }

                // A result is cached only when it came back, so an abandoned command is not remembered as a real answer.
                if (failure != Failure.BudgetExhausted)
                {
                    _outputs[command] = output;
                }
                return output;
            }

            private string? Skip()
            {
                SkippedRegistryReads++;
                return null;
            }

            private void Exhaust(string command)
            {
                if (BudgetExhausted)
                {
                    return;
                }
                BudgetExhausted = true;
                _owner._logger.LogWarning(
                    "Sizing the update ran out of its {Budget} s budget at {Command}; what is not read shows as not sized",
                    _owner.Budget.TotalSeconds, command);
            }
        }

        private enum Failure
        {
            None,
            Failed,
            BudgetExhausted,
        }

        private async Task<PullSizeTable> ResolveCoreAsync(string[] composeFiles, Run run)
        {
            var config = await run.OutputAsync(ConfigCommand(composeFiles));
            if (config == null)
            {
                LogSummary(run, PullSizeTable.Empty);
                return PullSizeTable.Empty;
            }

            var services = ParseComposeServices(config);
            if (services.Count == 0)
            {
                LogSummary(run, PullSizeTable.Empty);
                return PullSizeTable.Empty;
            }

            var devicePlatform = await run.OutputAsync(VersionCommand);
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
                    layers = await ReadLayersAsync(group.Key.Reference, platform, run);
                }
                imageLayers.Add((group.Key.Reference, group.Select(s => s.Service).ToImmutableArray(), platform, layers));
            }

            var localChains = await ReadLocalChainsAsync(imageLayers.Where(i => i.Layers != null && i.Platform != null)
                .Select(i => (ImageReference.Repository(i.Reference), i.Platform!))
                .Distinct()
                .ToList(), run);

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

            var table = new PullSizeTable(toFetch.ToImmutable(), present.ToImmutable(), images.ToImmutable(), BuildOnlyServices(config));
            LogSummary(run, table);
            return table;
        }

        private void LogSummary(Run run, PullSizeTable table)
        {
            _logger.LogInformation(
                "Update download size resolved in {Elapsed} ms with {Commands} command(s), {Skipped} registry read(s) skipped: {Table}",
                (long)run.Elapsed.TotalMilliseconds, run.Commands, run.SkippedRegistryReads, table);
        }

        /// <summary>
        /// Layers of <paramref name="reference"/> for <paramref name="platform"/>: the index (or single manifest), then the
        /// platform manifest by digest. Null when either cannot be read or no index entry matches unambiguously.
        /// </summary>
        private async Task<IReadOnlyList<ManifestLayer>?> ReadLayersAsync(string reference, Platform platform, Run run)
        {
            if (!SafeReference().IsMatch(reference))
            {
                _logger.LogWarning("Image {Image}: reference not usable in a shell command, not sized", reference);
                return null;
            }

            try
            {
                var output = await run.OutputAsync(ManifestCommand(reference));
                if (output == null)
                {
                    return null;
                }

                var manifest = ParseManifest(output);
                if (manifest.Layers != null)
                {
                    // A single manifest is what the pull fetches, whatever platform it was built for.
                    return manifest.Layers;
                }

                var digest = SelectPlatformDigest(manifest.Platforms, platform);
                if (digest == null)
                {
                    _logger.LogWarning("Image {Image}: no single manifest for platform {Platform}, not sized", reference, platform);
                    return null;
                }

                var platformReference = $"{ImageReference.Repository(ImageReference.Normalize(reference))}@{digest}";
                var platformOutput = await run.OutputAsync(ManifestCommand(platformReference));
                if (platformOutput == null)
                {
                    return null;
                }

                var layers = ParseManifest(platformOutput).Layers;
                if (layers == null)
                {
                    _logger.LogWarning("Image {Image}: platform manifest {Digest} has no layer list, not sized", reference, digest);
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
        private async Task<HashSet<string>> ReadLocalChainsAsync(IReadOnlyList<(string Repository, Platform Platform)> repositories, Run run)
        {
            var chains = new HashSet<string>(StringComparer.Ordinal);
            if (repositories.Count == 0)
            {
                return chains;
            }

            var listing = await run.OutputAsync(ImageListCommand);
            if (listing == null)
            {
                return chains;
            }

            var local = ParseLocalImages(listing, _logger);
            foreach (var (repository, platform) in repositories)
            {
                var digests = local
                    .Where(l => l.Repository == repository)
                    .OrderByDescending(l => l.CreatedAt)
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
                    var layers = await ReadLayersAsync(reference, platform, run);
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

        /// <summary>
        /// Runs one command, abandoning it when <paramref name="remaining"/> budget runs out. Failures are logged at Debug only:
        /// the SSH service already logs a Warning for every failed command.
        /// </summary>
        private async Task<(string? Output, Failure Failure)> RunAsync(string command, string workingDirectory, TimeSpan remaining)
        {
            var timeout = remaining < CommandTimeout ? remaining : CommandTimeout;
            Task<SshCommandResult> execution;
            try
            {
                execution = _ssh.ExecuteCommandAsync(command, timeout, workingDirectory);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sizing command failed: {Command}", command);
                return (null, Failure.Failed);
            }

            var budget = Task.Delay(remaining);
            if (await Task.WhenAny(execution, budget) != execution)
            {
                _ = execution.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return (null, Failure.BudgetExhausted);
            }

            try
            {
                var result = await execution;
                if (result.IsSuccess)
                {
                    return (result.Output, Failure.None);
                }

                _logger.LogDebug("Sizing command failed (exit {ExitCode}): {Command}: {Reason}",
                    result.ExitCode, command, DockerComposeService.DescribeFailure(result));
                return (null, Failure.Failed);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Sizing command failed: {Command}", command);
                return (null, Failure.Failed);
            }
        }

        internal sealed record ComposeService(string Service, string Reference, string? Platform);

        internal sealed record ManifestLayer(string Digest, long Size);

        internal sealed record LocalImage(string Repository, string RepoDigestReference, DateTimeOffset CreatedAt);

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
        /// Services of <c>docker compose config --format json</c> without an image: compose reports them as <c>Skipped</c>.
        /// </summary>
        internal static ImmutableArray<string> BuildOnlyServices(string json)
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("services", out var services) || services.ValueKind != JsonValueKind.Object)
            {
                return ImmutableArray<string>.Empty;
            }
            return services.EnumerateObject()
                .Where(service => !TryGetString(service.Value, "image", out var image) || string.IsNullOrWhiteSpace(image))
                .Select(service => service.Name)
                .ToImmutableArray();
        }

        /// <summary>
        /// Output of <c>docker manifest inspect</c>: an index / manifest list (<see cref="Platforms"/>) or an image manifest (<see cref="Layers"/>).
        /// </summary>
        internal sealed record ParsedManifest(IReadOnlyList<(string Digest, Platform? Platform)> Platforms, IReadOnlyList<ManifestLayer>? Layers);

        internal static ParsedManifest ParseManifest(string json)
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var platforms = new List<(string, Platform?)>();

            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("manifests", out var manifests) && manifests.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in manifests.EnumerateArray())
                {
                    if (!TryGetString(entry, "digest", out var digest) || digest == null)
                    {
                        continue;
                    }
                    Platform? entryPlatform = null;
                    if (entry.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.Object)
                    {
                        TryGetString(p, "os", out var os);
                        TryGetString(p, "architecture", out var arch);
                        TryGetString(p, "variant", out var variant);
                        entryPlatform = new Platform(os ?? string.Empty, arch ?? string.Empty, variant);
                    }
                    platforms.Add((digest, entryPlatform));
                }
                return new ParsedManifest(platforms, null);
            }

            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("layers", out var layersElement) || layersElement.ValueKind != JsonValueKind.Array)
            {
                // Schema 1 ("fsLayers") and anything else unknown: no sizes.
                return new ParsedManifest(platforms, null);
            }

            var layers = new List<ManifestLayer>();
            foreach (var layer in layersElement.EnumerateArray())
            {
                if (!TryGetString(layer, "digest", out var digest) || digest == null
                    || !layer.TryGetProperty("size", out var size) || !size.TryGetInt64(out var bytes))
                {
                    return new ParsedManifest(platforms, null);
                }
                layers.Add(new ManifestLayer(digest, bytes));
            }
            return new ParsedManifest(platforms, layers);
        }

        /// <summary>
        /// Digest of the index entry for <paramref name="platform"/>; null when none or several match (e.g. linux/arm v6 and v7
        /// for a device that reports no variant). Attestation entries (<c>unknown/unknown</c>) never match.
        /// </summary>
        internal static string? SelectPlatformDigest(IReadOnlyList<(string Digest, Platform? Platform)> entries, Platform platform)
        {
            var matching = entries.Where(e => e.Platform != null && e.Platform.Matches(platform)).ToList();
            if (matching.Count > 1)
            {
                matching = matching.Where(e => e.Platform!.VariantEquals(platform)).ToList();
            }
            return matching.Count == 1 ? matching[0].Digest : null;
        }

        /// <summary>
        /// Rows of <c>docker image ls --no-trunc --digests --format '{{json .}}'</c> that carry a repo digest.
        /// </summary>
        internal static IReadOnlyList<LocalImage> ParseLocalImages(string output, ILogger? logger = null)
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
                    result.Add(new LocalImage(normalizedRepository, $"{repository}@{digest}", ParseCreatedAt(createdAt)));
                }
                catch (JsonException ex)
                {
                    logger?.LogDebug(ex, "Ignoring a docker image ls line that is not a JSON row: {Line}", line);
                }
            }
            return result;
        }

        /// <summary>
        /// <c>CreatedAt</c> of <c>docker image ls</c>, e.g. <c>2025-04-16 14:50:31 +0000 UTC</c>; <see cref="DateTimeOffset.MinValue"/> when unreadable.
        /// </summary>
        internal static DateTimeOffset ParseCreatedAt(string? value)
        {
            var match = value == null ? null : CreatedAtPattern().Match(value);
            if (match is not { Success: true })
            {
                return DateTimeOffset.MinValue;
            }
            var text = $"{match.Groups[1].Value} {match.Groups[2].Value}:{match.Groups[3].Value}";
            return DateTimeOffset.TryParseExact(text, "yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;
        }

        [GeneratedRegex(@"^(\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}) ([+-]\d{2})(\d{2})")]
        private static partial Regex CreatedAtPattern();

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
