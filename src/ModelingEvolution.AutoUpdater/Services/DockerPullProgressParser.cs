using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ModelingEvolution.AutoUpdater.Services
{
    /// <summary>
    /// Turns the line stream of <c>docker compose --progress json pull</c> into <see cref="PullProgress"/> snapshots.
    /// </summary>
    /// <remarks>
    /// Compose emits one JSON object per line. Objects without <c>parent_id</c> describe an image
    /// (<c>"text":"Pulling"</c> … <c>"text":"Pulled"</c>); objects with <c>parent_id</c> describe a layer of that image
    /// and, while downloading, carry <c>current</c>/<c>total</c> byte counts.
    /// Byte figures cover only images that are still in flight: once compose reports an image as <c>Pulled</c>
    /// its layers leave the sum, so the byte bar restarts for the remaining images while the image counter steps.
    /// Layers are announced lazily, so the in-flight total can grow mid-way and the ratio can dip; that is honest.
    /// Lines that are not JSON objects are ignored, which keeps the parser safe on legacy plain output.
    /// </remarks>
    public sealed class DockerPullProgressParser
    {
        private const string TextPulled = "Pulled";
        private const string TextDownloading = "Downloading";
        private const string TextDownloadComplete = "Download complete";
        private const string TextPullComplete = "Pull complete";
        private const string TextAlreadyExists = "Already exists";

        private readonly HashSet<string> _images = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pulled = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Image, long Current, long Total)> _layers = new(StringComparer.Ordinal);

        /// <summary>
        /// Latest snapshot.
        /// </summary>
        public PullProgress Current { get; private set; } = PullProgress.Empty;

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
                    ErrorMessage = GetString(root, "message") ?? "docker compose pull reported an error";
                    return false;
                }

                var id = GetString(root, "id");
                if (id == null)
                {
                    return false;
                }

                var parentId = GetString(root, "parent_id");
                var text = GetString(root, "text") ?? string.Empty;

                if (parentId == null)
                {
                    ApplyImage(id, text);
                }
                else
                {
                    ApplyLayer(id, parentId, text, GetInt64(root, "current"), GetInt64(root, "total"));
                }
            }

            return Recompute();
        }

        private void ApplyImage(string id, string text)
        {
            _images.Add(id);
            if (text == TextPulled)
            {
                _pulled.Add(id);
            }
        }

        private void ApplyLayer(string id, string parentId, string text, long current, long total)
        {
            _images.Add(parentId);

            switch (text)
            {
                case TextDownloading:
                    if (total > 0)
                    {
                        _layers[id] = (parentId, Math.Min(current, total), total);
                    }
                    break;

                case TextDownloadComplete:
                case TextPullComplete:
                case TextAlreadyExists:
                    if (_layers.TryGetValue(id, out var layer))
                    {
                        _layers[id] = (layer.Image, layer.Total, layer.Total);
                    }
                    break;
            }
        }

        private bool Recompute()
        {
            long downloaded = 0;
            long total = 0;
            foreach (var layer in _layers.Values)
            {
                if (_pulled.Contains(layer.Image))
                {
                    continue;
                }
                downloaded += layer.Current;
                total += layer.Total;
            }

            float? percent = null;
            if (_images.Count > 0 && _pulled.Count == _images.Count)
            {
                percent = 100f;
            }
            else if (total > 0)
            {
                percent = Math.Min(100f, 100f * downloaded / total);
            }

            var next = new PullProgress(_images.Count, _pulled.Count, downloaded, total, percent);
            if (next == Current)
            {
                return false;
            }

            Current = next;
            return true;
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
