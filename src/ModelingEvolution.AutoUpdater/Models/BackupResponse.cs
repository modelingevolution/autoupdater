using System.Text.Json.Serialization;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// JSON response model for backup.sh script
    /// </summary>
    public class BackupResponse
    {
        [JsonPropertyName("file")]
        public string File { get; set; } = string.Empty;

        [JsonPropertyName("success")]
        public bool? Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}