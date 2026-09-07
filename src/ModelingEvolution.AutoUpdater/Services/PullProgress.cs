using System;
using System.Globalization;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Snapshot of a <c>docker compose pull</c> in flight.
    /// </summary>
    /// <param name="ImagesTotal">Number of images the pull covers (grows as compose announces them).</param>
    /// <param name="ImagesPulled">Number of images compose is finished with: pulled, skipped (build-only services) or failed.</param>
    /// <param name="BytesDownloaded">Bytes downloaded across known-size layers of images still in flight.</param>
    /// <param name="BytesTotal">Total bytes across known-size layers of images still in flight.</param>
    /// <param name="BytesPercent">In-flight download completion 0-100; 100 once every image is pulled; null when no layer size is known yet.</param>
    public readonly record struct PullProgress(
        int ImagesTotal,
        int ImagesPulled,
        long BytesDownloaded,
        long BytesTotal,
        float? BytesPercent)
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

        public override string ToString()
        {
            return $"{ImagesPulled}/{ImagesTotal} images, {FormatBytes()}";
        }

        internal static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            var unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }

            return unit == 0
                ? $"{bytes} B"
                : string.Create(CultureInfo.InvariantCulture, $"{value:0.0} {units[unit]}");
        }
    }
}
