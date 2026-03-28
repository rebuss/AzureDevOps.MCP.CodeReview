using System.Text.Json;

namespace REBUSS.Pure.Core.Models.Pagination;

/// <summary>
/// Data encoded inside a page reference token. Used by <see cref="IPageReferenceCodec"/>
/// for encode/decode. Contains the actual request parameters (not a hash) per Q16.
/// </summary>
/// <param name="ToolName">Tool that produced the paginated result (e.g., "get_pr_diff").</param>
/// <param name="RequestParams">Actual request parameters as a JSON element (e.g., {"prNumber":42}). NOT a hash — per Q16.</param>
/// <param name="SafeBudgetTokens">Token budget used for page allocation.</param>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="DataFingerprint">Head commit SHA for PR tools; null for local tools.</param>
public sealed record PageReferenceData(
    string ToolName,
    JsonElement RequestParams,
    int SafeBudgetTokens,
    int PageNumber,
    string? DataFingerprint);
