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
}
