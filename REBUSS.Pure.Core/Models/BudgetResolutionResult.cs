namespace REBUSS.Pure.Core.Models;

/// <summary>
/// Result of resolving the context window budget.
/// Contains the total and safe budgets, how the budget was determined, and any warnings.
/// </summary>
/// <param name="TotalBudgetTokens">Resolved total context window in tokens (after guardrails).</param>
/// <param name="SafeBudgetTokens">Usable budget after safety margin deduction.</param>
/// <param name="Source">How the budget was determined.</param>
/// <param name="Warnings">Warnings generated during resolution (clamped, model not found, etc.).</param>
public sealed record BudgetResolutionResult(
    int TotalBudgetTokens,
    int SafeBudgetTokens,
    BudgetSource Source,
    IReadOnlyList<string> Warnings);
