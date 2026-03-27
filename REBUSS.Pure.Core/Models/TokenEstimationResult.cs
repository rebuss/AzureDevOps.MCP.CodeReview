namespace REBUSS.Pure.Core.Models;

/// <summary>
/// Result of estimating the token count of serialized content.
/// </summary>
/// <param name="EstimatedTokens">Approximate token count of the serialized content.</param>
/// <param name="PercentageUsed">Percentage of safe budget consumed (EstimatedTokens / SafeBudgetTokens × 100).</param>
/// <param name="FitsWithinBudget">Whether the estimated tokens fit within the safe budget.</param>
public sealed record TokenEstimationResult(
    int EstimatedTokens,
    double PercentageUsed,
    bool FitsWithinBudget);
