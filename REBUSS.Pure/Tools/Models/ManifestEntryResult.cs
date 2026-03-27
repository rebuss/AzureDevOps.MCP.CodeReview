using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// JSON output DTO for a single manifest entry describing a candidate's inclusion status.
/// </summary>
public sealed class ManifestEntryResult
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("estimatedTokens")]
    public int EstimatedTokens { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("priorityTier")]
    public string PriorityTier { get; set; } = string.Empty;
}
