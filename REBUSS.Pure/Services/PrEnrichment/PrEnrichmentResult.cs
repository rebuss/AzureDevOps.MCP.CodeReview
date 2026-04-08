using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Successful payload of a completed enrichment job. Cached so both
/// <c>get_pr_metadata</c> and <c>begin_pr_review</c> can reuse it without
/// recomputation.
/// </summary>
public sealed record PrEnrichmentResult
{
    public required int PrNumber { get; init; }

    /// <summary>
    /// Sourced from <c>FullPullRequestMetadata.LastMergeSourceCommitId</c>.
    /// The cleaner internal name <c>HeadSha</c> is used inside the orchestrator
    /// because every consumer treats it as the "head commit of the PR".
    /// </summary>
    public required string HeadSha { get; init; }

    /// <summary>Already sorted by <c>PackingPriorityComparer</c>.</summary>
    public required IReadOnlyList<PackingCandidate> SortedCandidates { get; init; }

    /// <summary>Per-file enriched plain-text, keyed by file path.</summary>
    public required IReadOnlyDictionary<string, string> EnrichedByPath { get; init; }

    /// <summary>Cached page allocation derived from <see cref="SortedCandidates"/>.</summary>
    public required PageAllocation Allocation { get; init; }

    /// <summary>
    /// Captured at allocation time so the content handler can detect a budget
    /// mismatch and retrigger if a different model/maxTokens is later requested.
    /// </summary>
    public required int SafeBudgetTokens { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }
}
