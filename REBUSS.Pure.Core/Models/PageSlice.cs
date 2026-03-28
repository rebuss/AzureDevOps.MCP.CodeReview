namespace REBUSS.Pure.Core.Models.Pagination;

/// <summary>
/// One page's allocation boundaries within the global candidate list.
/// </summary>
/// <param name="PageNumber">1-based page number.</param>
/// <param name="StartIndex">Inclusive index in sorted candidate list.</param>
/// <param name="EndIndex">Exclusive index in sorted candidate list.</param>
/// <param name="Items">Items on this page with status.</param>
/// <param name="BudgetUsed">Tokens consumed on this page (including partial).</param>
/// <param name="BudgetRemaining">safeBudgetTokens - overhead - BudgetUsed.</param>
public sealed record PageSlice(
    int PageNumber,
    int StartIndex,
    int EndIndex,
    IReadOnlyList<PageSliceItem> Items,
    int BudgetUsed,
    int BudgetRemaining);
