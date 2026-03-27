namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// Per-item inclusion decision within a <see cref="PackingDecision"/>.
/// </summary>
/// <param name="OriginalIndex">Index in the original candidates list.</param>
/// <param name="Status">Inclusion status: Included, Partial, or Deferred.</param>
/// <param name="EstimatedTokens">Token estimate for this item.</param>
/// <param name="BudgetForPartial">Remaining budget for hunk-level truncation; set only when <paramref name="Status"/> is <see cref="PackingItemStatus.Partial"/>.</param>
public sealed record PackingDecisionItem(
    int OriginalIndex,
    PackingItemStatus Status,
    int EstimatedTokens,
    int? BudgetForPartial = null);
