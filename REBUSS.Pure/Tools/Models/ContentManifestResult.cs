using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// JSON output DTO for the content manifest included in tool responses.
/// Wraps <see cref="ManifestEntryResult"/> items and a <see cref="ManifestSummaryResult"/>.
/// </summary>
public sealed class ContentManifestResult
{
    [JsonPropertyName("items")]
    public List<ManifestEntryResult> Items { get; set; } = new();

    [JsonPropertyName("summary")]
    public ManifestSummaryResult Summary { get; set; } = new();
}
