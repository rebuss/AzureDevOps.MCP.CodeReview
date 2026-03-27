using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core;

/// <summary>
/// Resolves the token budget for an MCP request based on explicit declaration,
/// model identifier registry lookup, or safe default fallback.
/// </summary>
public interface IContextBudgetResolver
{
    /// <summary>
    /// Resolves the token budget from the provided context window parameters.
    /// </summary>
    /// <param name="explicitTokens">Optional explicit token count from the agent.</param>
    /// <param name="modelIdentifier">Optional model name for registry lookup.</param>
    /// <returns>Resolution result with total budget, safe budget, source, and warnings.</returns>
    BudgetResolutionResult Resolve(int? explicitTokens, string? modelIdentifier);
}
