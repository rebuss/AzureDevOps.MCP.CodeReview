using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Services.LocalReview;

/// <summary>
/// Successful payload of a completed local enrichment job. Implements
/// <see cref="IEnrichmentResult"/> so the Copilot review orchestrator
/// can consume it identically to <c>PrEnrichmentResult</c>.
/// </summary>
public sealed record LocalEnrichmentResult : IEnrichmentResult
{
    public required string RepositoryRoot { get; init; }
    public required string CurrentBranch { get; init; }
    public required string Scope { get; init; }

    /// <inheritdoc />
    public required IReadOnlyList<PackingCandidate> SortedCandidates { get; init; }

    /// <inheritdoc />
    public required IReadOnlyDictionary<string, string> EnrichedByPath { get; init; }

    public required PageAllocation Allocation { get; init; }
    public required int SafeBudgetTokens { get; init; }
    public required DateTimeOffset CompletedAt { get; init; }
}
