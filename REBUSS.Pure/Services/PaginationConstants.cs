namespace REBUSS.Pure.Services.Pagination;

/// <summary>
/// Constants used by the pagination system.
/// </summary>
internal static class PaginationConstants
{
    /// <summary>
    /// Fixed token overhead for pagination metadata (pagination JSON object + page references).
    /// Includes: pagination object structure (~40 tokens), currentPageReference (~40),
    /// nextPageReference (~40), ManifestSummary extension fields (~15), safety margin (~15).
    /// </summary>
    public const int PaginationOverhead = 150;

    /// <summary>
    /// Base manifest overhead from Feature 003's ResponsePacker.
    /// </summary>
    public const int BaseManifestOverhead = 100;

    /// <summary>
    /// Per-item manifest overhead from Feature 003's ResponsePacker.
    /// </summary>
    public const int PerItemManifestOverhead = 15;

    /// <summary>
    /// Minimum budget required for pagination to function.
    /// = PaginationOverhead + BaseManifestOverhead + PerItemManifestOverhead (at least 1 item).
    /// Any budget below this cannot deliver even one manifest entry with pagination metadata.
    /// </summary>
    public const int MinimumBudgetForPagination = PaginationOverhead + BaseManifestOverhead + PerItemManifestOverhead; // 265
}
