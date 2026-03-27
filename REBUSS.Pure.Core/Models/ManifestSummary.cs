namespace REBUSS.Pure.Core.Models.ResponsePacking;

/// <summary>
/// Aggregated summary of the packing manifest.
/// </summary>
/// <param name="TotalItems">Total candidate count.</param>
/// <param name="IncludedCount">Items with status Included.</param>
/// <param name="PartialCount">Items with status Partial (0 or 1).</param>
/// <param name="DeferredCount">Items with status Deferred.</param>
/// <param name="TotalBudgetTokens">Safe budget provided to the packer.</param>
/// <param name="BudgetUsed">Sum of estimated tokens for included + partial items.</param>
/// <param name="BudgetRemaining">TotalBudgetTokens minus BudgetUsed.</param>
/// <param name="UtilizationPercent">BudgetUsed / TotalBudgetTokens × 100.</param>
public sealed record ManifestSummary(
    int TotalItems,
    int IncludedCount,
    int PartialCount,
    int DeferredCount,
    int TotalBudgetTokens,
    int BudgetUsed,
    int BudgetRemaining,
    double UtilizationPercent);
