using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Pagination overview included in <c>get_pr_metadata</c> response when budget
/// parameters are provided. Describes how review content would be paginated.
/// </summary>
public sealed record ContentPagingInfo(
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("totalFiles")] int TotalFiles,
    [property: JsonPropertyName("budgetPerPageTokens")] int BudgetPerPageTokens,
    [property: JsonPropertyName("filesByPage")] IReadOnlyList<PageFileCount> FilesByPage
);

/// <summary>
/// Per-page file count in the <see cref="ContentPagingInfo"/> breakdown.
/// </summary>
public sealed record PageFileCount(
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("fileCount")] int FileCount
);
