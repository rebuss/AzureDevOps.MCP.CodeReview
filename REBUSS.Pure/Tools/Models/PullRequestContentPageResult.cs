using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Response DTO for the <c>get_pr_content</c> tool.
/// Contains diff content for a single page of a pull request.
/// </summary>
public sealed record PullRequestContentPageResult(
    [property: JsonPropertyName("prNumber")] int PrNumber,
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("files")] IReadOnlyList<StructuredFileChange> Files,
    [property: JsonPropertyName("summary")] ContentPageSummary Summary
);

/// <summary>
/// Summary section shared by <see cref="PullRequestContentPageResult"/>
/// and <see cref="LocalContentPageResult"/>.
/// </summary>
public sealed record ContentPageSummary(
    [property: JsonPropertyName("filesOnPage")] int FilesOnPage,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("estimatedTokens")] int EstimatedTokens,
    [property: JsonPropertyName("hasMorePages")] bool HasMorePages,
    [property: JsonPropertyName("categories")] IReadOnlyDictionary<string, int> Categories
);
