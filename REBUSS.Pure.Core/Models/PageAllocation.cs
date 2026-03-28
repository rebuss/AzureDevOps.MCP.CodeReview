namespace REBUSS.Pure.Core.Models.Pagination;

/// <summary>
/// Result of allocating all candidates across pages. Computed once per request.
/// Deterministic: same input → same output.
/// </summary>
/// <param name="Pages">Ordered list of page boundaries.</param>
/// <param name="TotalPages">Pages.Count.</param>
/// <param name="TotalItems">Total candidate count across all pages.</param>
public sealed record PageAllocation(
    IReadOnlyList<PageSlice> Pages,
    int TotalPages,
    int TotalItems);
