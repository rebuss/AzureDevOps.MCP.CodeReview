using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Response DTO for the <c>get_local_content</c> tool.
/// Contains diff content for a single page of local changes.
/// </summary>
public sealed record LocalContentPageResult(
    [property: JsonPropertyName("repositoryRoot")] string RepositoryRoot,
    [property: JsonPropertyName("currentBranch")] string? CurrentBranch,
    [property: JsonPropertyName("scope")] string Scope,
    [property: JsonPropertyName("pageNumber")] int PageNumber,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("files")] IReadOnlyList<StructuredFileChange> Files,
    [property: JsonPropertyName("summary")] ContentPageSummary Summary
);
