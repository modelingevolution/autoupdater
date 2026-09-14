using System;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Snapshot of a <c>docker compose pull</c> in flight.
    /// </summary>
    /// <param name="ImagesTotal">Number of images the pull covers (grows as compose announces them).</param>
    /// <param name="ImagesPulled">Number of images compose is finished with: pulled, skipped (build-only services) or failed.</param>
    /// <param name="BytesDownloaded">Bytes downloaded so far across every image of the update; never decreases.</param>
    /// <param name="BytesTotal">Bytes the update downloads: the size resolved before the pull plus sizes learned since; never decreases.</param>
    /// <param name="BytesPercent">Download completion 0-100 (<paramref name="BytesDownloaded"/> / <paramref name="BytesTotal"/>); 100 once every image is pulled; null while no layer size is known or some layer is not sized.</param>
    /// <param name="LayersExtracting">Layers of in-flight images currently being extracted (compose reports no bytes for this phase).</param>
    /// <param name="LayersKnown">Layers to download whose size is known.</param>
    /// <param name="LayersTotal">Layers to download; more than <paramref name="LayersKnown"/> when some could not be sized (then <paramref name="BytesTotal"/> is a lower bound).</param>
    public readonly record struct PullProgress(
        int ImagesTotal,
        int ImagesPulled,
        long BytesDownloaded,
        long BytesTotal,
        float? BytesPercent,
        int LayersExtracting = 0,
        int LayersKnown = 0,
        int LayersTotal = 0)
    {
        public static readonly PullProgress Empty = new(0, 0, 0, 0, null);

        /// <summary>
        /// Fraction of images pulled, 0-1. Zero when nothing is known yet.
        /// </summary>
        public float ImagesFraction => ImagesTotal > 0 ? (float)ImagesPulled / ImagesTotal : 0f;

        /// <summary>
        /// Human readable byte progress, e.g. "412.0 MB / 1.2 GB".
        /// </summary>
        public string FormatBytes()
        {
            return BytesTotal > 0
                ? $"{FormatBytes(BytesDownloaded)} / {FormatBytes(BytesTotal)}"
                : string.Empty;
        }

        /// <summary>
        /// True once compose is finished with every image (pulled, skipped or failed).
        /// </summary>
        public bool IsComplete => ImagesTotal > 0 && ImagesPulled == ImagesTotal;

        /// <summary>
        /// Layers to download whose size could not be determined.
        /// </summary>
        public int LayersNotSized => Math.Max(0, LayersTotal - LayersKnown);

        public override string ToString()
        {
            return $"{ImagesPulled}/{ImagesTotal} images, {FormatBytes()}";
        }

        internal static string FormatBytes(long bytes) => new Bytes(bytes, precision: 1).ToString();
    }
}
