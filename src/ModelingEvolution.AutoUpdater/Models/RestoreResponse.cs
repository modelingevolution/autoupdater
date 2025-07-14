using System.Text.Json.Serialization;

namespace ModelingEvolution.AutoUpdater.Models
{
    /// <summary>
    /// JSON response model for restore.sh script
    /// </summary>
    public class RestoreResponse
    {
        [JsonPropertyName("success")]
        public bool Success { get; set; }

        [JsonPropertyName("error")]
        public string? Error { get; set; }
    }
}