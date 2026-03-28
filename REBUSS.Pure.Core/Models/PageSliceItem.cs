using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Core.Models.Pagination;

/// <summary>
/// Per-item allocation decision within a page.
/// Reuses <see cref="PackingItemStatus"/> from Feature 003.
/// In pagination context, only <see cref="PackingItemStatus.Included"/> and
/// <see cref="PackingItemStatus.Partial"/> appear on a page.
/// </summary>
/// <param name="OriginalIndex">Index in the original (pre-sorted) candidate list.</param>
/// <param name="Status">Included or Partial (never Deferred — deferred items are on other pages).</param>
/// <param name="EstimatedTokens">Token estimate for this item.</param>
/// <param name="BudgetForPartial">Only set when Status == Partial: available budget for hunk truncation.</param>
public sealed record PageSliceItem(
    int OriginalIndex,
    PackingItemStatus Status,
    int EstimatedTokens,
    int? BudgetForPartial = null);
