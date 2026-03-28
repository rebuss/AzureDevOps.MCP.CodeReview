using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Pagination navigation information included in every paginated response.
/// </summary>
public sealed class PaginationMetadataResult
{
    [JsonPropertyName("currentPage")]
    public int CurrentPage { get; set; }

    [JsonPropertyName("totalPages")]
    public int TotalPages { get; set; }

    [JsonPropertyName("hasMore")]
    public bool HasMore { get; set; }

    [JsonPropertyName("currentPageReference")]
    public string CurrentPageReference { get; set; } = string.Empty;

    /// <summary>
    /// Null when hasMore == false (final page or single-page result).
    /// Omitted from JSON by WhenWritingNull.
    /// </summary>
    [JsonPropertyName("nextPageReference")]
    public string? NextPageReference { get; set; }
}
