using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// One service image of the compose set, as sized before the pull.
    /// </summary>
    /// <param name="Reference">Normalised image reference, e.g. <c>docker.io/library/alpine:3.21</c>.</param>
    /// <param name="Services">Compose services that use the image.</param>
    /// <param name="IsSized">True when the registry manifest for the device platform was read.</param>
    /// <param name="LayersToFetch">Short ids of this image's layers that the pull is expected to download (empty when not sized).</param>
    public sealed record PullImageSize(
        string Reference,
        ImmutableArray<string> Services,
        bool IsSized,
        ImmutableArray<string> LayersToFetch);

    /// <summary>
    /// The download size of an update, resolved before <c>docker compose pull</c> runs (epic-106, design.md "Byte model").
    /// </summary>
    /// <remarks>
    /// Layers are keyed by their short id: the first 12 hex digits of the compressed layer digest, which is what
    /// <c>docker compose --progress json</c> reports as a layer <c>id</c>. A layer shared by several images appears once.
    /// </remarks>
    public sealed class PullSizeTable
    {
        /// <summary>
        /// Nothing known: no layer sized, no image expected. The parser then learns every size from the pull output.
        /// </summary>
        public static readonly PullSizeTable Empty = new(
            ImmutableDictionary<string, long>.Empty,
            ImmutableHashSet<string>.Empty,
            ImmutableArray<PullImageSize>.Empty);

        /// <summary>
        /// Creates a size table.
        /// </summary>
        /// <param name="layersToFetch">Compressed size of each layer the pull is expected to download, by short id; shared layers once.</param>
        /// <param name="layersPresent">Short ids of layers already local.</param>
        /// <param name="images">Every service image of the compose set, sized or not.</param>
        /// <param name="buildOnlyServices">Services without an image; compose reports each as <c>Skipped</c>.</param>
        public PullSizeTable(
            ImmutableDictionary<string, long> layersToFetch,
            ImmutableHashSet<string> layersPresent,
            ImmutableArray<PullImageSize> images,
            ImmutableArray<string> buildOnlyServices = default)
        {
            LayersToFetch = layersToFetch ?? throw new ArgumentNullException(nameof(layersToFetch));
            LayersPresent = layersPresent ?? throw new ArgumentNullException(nameof(layersPresent));
            Images = images.IsDefault ? ImmutableArray<PullImageSize>.Empty : images;
            BuildOnlyServices = buildOnlyServices.IsDefault ? ImmutableArray<string>.Empty : buildOnlyServices;
            BytesTotal = layersToFetch.Values.Sum();
        }

        /// <summary>
        /// Services without an image (build-only). Compose reports them by service name, so they are expected progress ids.
        /// </summary>
        public ImmutableArray<string> BuildOnlyServices { get; }

        /// <summary>
        /// Compressed size of every layer the pull is expected to download, by short id.
        /// </summary>
        public ImmutableDictionary<string, long> LayersToFetch { get; }

        /// <summary>
        /// Short ids of layers already present locally: nothing to download.
        /// </summary>
        public ImmutableHashSet<string> LayersPresent { get; }

        /// <summary>
        /// Every service image of the compose set, sized or not.
        /// </summary>
        public ImmutableArray<PullImageSize> Images { get; }

        /// <summary>
        /// Sum of <see cref="LayersToFetch"/>.
        /// </summary>
        public long BytesTotal { get; }

        /// <summary>
        /// Images whose manifest could not be read.
        /// </summary>
        public int UnsizedImages => Images.Count(i => !i.IsSized);

        /// <summary>
        /// Prefix of compose 5.x image progress ids.
        /// </summary>
        public const string ImageIdPrefix = "Image ";

        /// <summary>
        /// Finds the image compose refers to by a progress <c>id</c>: a service name (compose 2.x) or
        /// <c>"Image &lt;reference&gt;"</c> (compose 5.x).
        /// </summary>
        public PullImageSize? FindImage(string composeId)
        {
            foreach (var image in Images)
            {
                if (image.Services.Contains(composeId, StringComparer.Ordinal))
                {
                    return image;
                }
            }

            // Only compose 5.x's "Image <reference>" ids name an image. A bare id is a service name: a build-only service
            // called "redis" must not be taken for the image "redis" another service uses.
            if (!composeId.StartsWith(ImageIdPrefix, StringComparison.Ordinal))
            {
                return null;
            }

            var normalized = ImageReference.Normalize(composeId.Substring(ImageIdPrefix.Length));
            foreach (var image in Images)
            {
                if (string.Equals(image.Reference, normalized, StringComparison.Ordinal))
                {
                    return image;
                }
            }

            return null;
        }

        /// <summary>
        /// Short id compose uses for a layer digest (<c>sha256:0123456789ab…</c> → <c>0123456789ab</c>).
        /// </summary>
        public static string ShortId(string digest)
        {
            var colon = digest.IndexOf(':');
            var hex = colon >= 0 ? digest.Substring(colon + 1) : digest;
            return hex.Length > 12 ? hex.Substring(0, 12) : hex;
        }

        public override string ToString()
        {
            return $"{LayersToFetch.Count} layer(s) to fetch, {PullProgress.FormatBytes(BytesTotal)}, "
                   + $"{LayersPresent.Count} present, {UnsizedImages}/{Images.Length} image(s) not sized";
        }
    }

    /// <summary>
    /// Docker image reference normalisation, so compose's and the resolver's spellings of one image compare equal.
    /// </summary>
    internal static class ImageReference
    {
        private const string DefaultRegistry = "docker.io";

        /// <summary>
        /// <c>alpine</c> → <c>docker.io/library/alpine:latest</c>; <c>me/app:1</c> → <c>docker.io/me/app:1</c>;
        /// references with a registry host or a digest keep them.
        /// </summary>
        public static string Normalize(string reference)
        {
            var value = reference.Trim();
            var slash = value.IndexOf('/');
            var first = slash >= 0 ? value.Substring(0, slash) : string.Empty;
            var hasRegistry = slash >= 0 && (first.Contains('.') || first.Contains(':') || first == "localhost");
            if (!hasRegistry)
            {
                value = slash >= 0 ? $"{DefaultRegistry}/{value}" : $"{DefaultRegistry}/library/{value}";
            }
            else if (first == "index.docker.io" || first == "registry-1.docker.io")
            {
                value = DefaultRegistry + value.Substring(first.Length);
            }

            if (value.Contains('@'))
            {
                return value;
            }

            var lastSegment = value.Substring(value.LastIndexOf('/') + 1);
            return lastSegment.Contains(':') ? value : value + ":latest";
        }

        /// <summary>
        /// Repository part of a normalised reference: tag and digest removed.
        /// </summary>
        public static string Repository(string normalizedReference)
        {
            var at = normalizedReference.IndexOf('@');
            var value = at >= 0 ? normalizedReference.Substring(0, at) : normalizedReference;
            var lastSlash = value.LastIndexOf('/');
            var colon = value.LastIndexOf(':');
            return colon > lastSlash ? value.Substring(0, colon) : value;
        }
    }
}
