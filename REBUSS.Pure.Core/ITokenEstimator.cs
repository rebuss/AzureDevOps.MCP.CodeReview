using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core;

/// <summary>
/// Estimates the token count of serialized response content using a
/// character-count heuristic. Schema-independent — operates on the
/// serialized JSON wire format.
/// </summary>
public interface ITokenEstimator
{
    /// <summary>
    /// Estimates the token count of the given serialized content
    /// and evaluates whether it fits within the specified safe budget.
    /// </summary>
    /// <param name="serializedContent">The serialized JSON string to measure.</param>
    /// <param name="safeBudgetTokens">The available safe budget in tokens.</param>
    /// <returns>Estimation result with token count, percentage, and fit signal.</returns>
    TokenEstimationResult Estimate(string serializedContent, int safeBudgetTokens);

    /// <summary>
    /// Estimates the token count for a file diff based on line-change statistics,
    /// without requiring the actual diff content. Uses a fixed per-line token factor
    /// plus a per-file overhead constant.
    /// </summary>
    /// <param name="additions">Number of added lines (clamped to 0 if negative).</param>
    /// <param name="deletions">Number of deleted lines (clamped to 0 if negative).</param>
    /// <returns>Estimated token count (always ≥ PerFileOverhead).</returns>
    int EstimateFromStats(int additions, int deletions);
}
