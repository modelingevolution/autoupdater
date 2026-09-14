using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Turns the line stream of <c>docker compose --progress json pull</c> into <see cref="PullProgress"/> snapshots.
    /// </summary>
    /// <remarks>
    /// Compose emits one JSON object per line. Objects without <c>parent_id</c> describe an image (<c>"text":"Pulling"</c> …
    /// <c>"text":"Pulled"</c>; the id is the service name in compose 2.x and <c>"Image &lt;reference&gt;"</c> in 5.x);
    /// objects with <c>parent_id</c> describe a layer of that image and, while downloading, carry <c>current</c>/<c>total</c>.
    /// <para>
    /// Byte model (epic-106 design.md): the total starts at the <see cref="PullSizeTable"/> resolved before the pull and never
    /// shrinks; layers the table did not size are added when their size first appears. "Downloaded" accumulates across every
    /// image for the whole update. Layers are keyed by id alone, so a base layer shared by two images counts once.
    /// A sized layer is credited in full once the daemon is done with it (download complete, already exists, or its image pulled).
    /// </para>
    /// Lines that are not JSON objects are ignored, which keeps the parser safe on legacy plain output.
    /// </remarks>
    public sealed class DockerPullProgressParser
    {
        private const string TextPulled = "Pulled";
        private const string TextDownloading = "Downloading";
        private const string TextVerifyingChecksum = "Verifying Checksum";
        private const string TextDownloadComplete = "Download complete";
        private const string TextPullComplete = "Pull complete";
        private const string TextExtracting = "Extracting";
        private const string TextAlreadyExists = "Already exists";
        private const string TextPullingFsLayer = "Pulling fs layer";
        private const string TextWaiting = "Waiting";
        private const string TextError = "Error";
        private const string TextSkippedPrefix = "Skipped";

        private sealed class Layer
        {
            public long Size;
            public long Downloaded;
            /// <summary>Size known (from the table or a Downloading line).</summary>
            public bool Sized;
        }

        private readonly PullSizeTable _table;
        private readonly HashSet<string> _images = new(StringComparer.Ordinal);
        private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
        private readonly HashSet<(string Image, string Layer)> _extracting = new();
        private readonly Dictionary<string, Layer> _layers = new(StringComparer.Ordinal);
        // Unsized table images that have not shown a layer yet: each stands for at least one layer not sized.
        private readonly HashSet<PullImageSize> _unsizedPending = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, PullImageSize?> _imageLookup = new(StringComparer.Ordinal);

        public DockerPullProgressParser() : this(null)
        {
        }

        /// <param name="sizes">Download size resolved before the pull; null or empty when unknown.</param>
        public DockerPullProgressParser(PullSizeTable? sizes)
        {
            _table = sizes ?? PullSizeTable.Empty;
            foreach (var (id, size) in _table.LayersToFetch)
            {
                _layers[id] = new Layer { Size = size, Sized = true };
            }
            foreach (var image in _table.Images)
            {
                if (!image.IsSized)
                {
                    _unsizedPending.Add(image);
                }
            }
            Current = Snapshot();
        }

        /// <summary>
        /// Latest snapshot. Before any line it already carries the resolved total.
        /// </summary>
        public PullProgress Current { get; private set; }

        /// <summary>
        /// Message of the last <c>{"error":true,"message":…}</c> line, if compose reported one.
        /// </summary>
        public string? ErrorMessage { get; private set; }

        /// <summary>
        /// Feeds one output line.
        /// </summary>
        /// <returns>True when <see cref="Current"/> changed.</returns>
        public bool Feed(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            var trimmed = line.Trim();
            if (trimmed[0] != '{')
            {
                return false;
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(trimmed);
            }
            catch (JsonException)
            {
                return false;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return false;
                }

                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.True)
                {
                    // The daemon message is the most specific one and arrives last: let it win.
                    ErrorMessage = GetString(root, "message") ?? ErrorMessage ?? "docker compose pull reported an error";
                    return false;
                }

                var id = GetString(root, "id");
                if (id == null)
                {
                    return false;
                }

                var parentId = GetString(root, "parent_id");
                var text = GetString(root, "text") ?? string.Empty;

                if (text == TextError)
                {
                    // Image-level failure; the daemon message usually follows in a final {"error":true} line.
                    ErrorMessage ??= GetString(root, "details") ?? GetString(root, "status") ?? $"pull of {id} failed";
                }

                if (parentId == null)
                {
                    ApplyImage(id, text);
                }
                else
                {
                    ApplyLayer(id, parentId, text, GetInt64(root, "current"), GetInt64(root, "total"));
                }
            }

            var next = Snapshot();
            if (next == Current)
            {
                return false;
            }

            Current = next;
            return true;
        }

        private PullImageSize? FindImage(string composeId)
        {
            if (!_imageLookup.TryGetValue(composeId, out var image))
            {
                image = _table.FindImage(composeId);
                _imageLookup[composeId] = image;
            }
            return image;
        }

        private void ApplyImage(string id, string text)
        {
            _images.Add(id);
            if (!IsTerminal(text))
            {
                return;
            }

            _finished.Add(id);
            var image = FindImage(id);
            if (image == null)
            {
                return;
            }

            _unsizedPending.Remove(image);
            if (text == TextError)
            {
                return;
            }

            // Pulled (or skipped because present / pulled by another service): every layer of the image is local now.
            foreach (var layerId in image.LayersToFetch)
            {
                if (_layers.TryGetValue(layerId, out var layer))
                {
                    layer.Downloaded = layer.Size;
                }
            }
        }

        /// <summary>
        /// Image events after which compose will not touch the image again: pulled, skipped
        /// ("Skipped - No image to be pulled" for build-only services) or failed.
        /// </summary>
        private static bool IsTerminal(string text)
        {
            return text == TextPulled
                   || text == TextError
                   || text.StartsWith(TextSkippedPrefix, StringComparison.Ordinal);
        }

        private void ApplyLayer(string id, string parentId, string text, long current, long total)
        {
            _images.Add(parentId);
            _layers.TryGetValue(id, out var layer);

            if (layer == null && IsLayerOnlyText(text) && text != TextAlreadyExists)
            {
                if (text != TextDownloading && _table.LayersPresent.Contains(id))
                {
                    // The resolver found it local; the classic daemon still announces it before "Already exists".
                    return;
                }

                // Not in the table (not sized, or the resolver was wrong): it will be downloaded, size to follow.
                layer = new Layer();
                _layers[id] = layer;
                var image = FindImage(parentId);
                if (image != null)
                {
                    _unsizedPending.Remove(image);
                }
            }

            switch (text)
            {
                case TextDownloading:
                    if (layer != null && total > 0)
                    {
                        if (!layer.Sized || total > layer.Size)
                        {
                            // Growth only: a size learned late adds to the total, it never replaces a larger one.
                            layer.Size = total;
                            layer.Sized = true;
                        }
                        layer.Downloaded = Math.Max(layer.Downloaded, Math.Min(current, layer.Size));
                    }
                    break;

                case TextVerifyingChecksum:
                case TextDownloadComplete:
                    Complete(layer);
                    break;

                case TextAlreadyExists:
                    if (layer is { Sized: false })
                    {
                        // Announced, then found local: nothing to fetch, and it held no bytes in the total.
                        _layers.Remove(id);
                    }
                    else
                    {
                        // Sized as "to fetch" but local after all: credited, so the total it is part of still completes.
                        Complete(layer);
                    }
                    break;

                case TextExtracting:
                    // Compose reports elapsed seconds here, not bytes; only the fact that extraction is running is usable.
                    Complete(layer);
                    _extracting.Add((parentId, id));
                    break;

                case TextPullComplete:
                    _extracting.Remove((parentId, id));
                    Complete(layer);
                    break;
            }
        }

        /// <summary>
        /// Statuses that only ever describe a filesystem layer. "Download complete" is not one of them: the containerd
        /// image store also reports config and manifest blobs with it.
        /// </summary>
        private static bool IsLayerOnlyText(string text)
        {
            return text is TextPullingFsLayer or TextWaiting or TextDownloading or TextVerifyingChecksum
                or TextAlreadyExists or TextExtracting;
        }

        private static void Complete(Layer? layer)
        {
            if (layer is { Sized: true })
            {
                layer.Downloaded = layer.Size;
            }
        }

        private PullProgress Snapshot()
        {
            long downloaded = 0;
            long total = 0;
            var known = 0;
            foreach (var layer in _layers.Values)
            {
                downloaded += layer.Downloaded;
                total += layer.Size;
                if (layer.Sized)
                {
                    known++;
                }
            }
            var layersTotal = _layers.Count + _unsizedPending.Count;

            var extracting = 0;
            foreach (var key in _extracting)
            {
                if (!_finished.Contains(key.Image))
                {
                    extracting++;
                }
            }

            float? percent = null;
            if (_images.Count > 0 && _finished.Count == _images.Count)
            {
                percent = 100f;
            }
            else if (total > 0)
            {
                percent = Math.Min(100f, 100f * downloaded / total);
            }

            return new PullProgress(_images.Count, _finished.Count, downloaded, total, percent, extracting, known, layersTotal);
        }

        private static string? GetString(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        }

        private static long GetInt64(JsonElement element, string name)
        {
            return element.TryGetProperty(name, out var property)
                   && property.ValueKind == JsonValueKind.Number
                   && property.TryGetInt64(out var value)
                ? value
                : 0;
        }
    }
}
