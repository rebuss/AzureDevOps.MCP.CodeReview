using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Core.Models;

/// <summary>
/// Source-agnostic enrichment result consumed by the Copilot review orchestrator.
/// Contains only the fields needed for page allocation and review — no source-specific
/// metadata (PR number, SHA, repository root, etc.).
/// </summary>
/// <remarks>
/// Implemented by <c>PrEnrichmentResult</c> (PR flow) and <c>LocalEnrichmentResult</c>
/// (local self-review flow). The Copilot review orchestrator depends on this interface
/// instead of the concrete enrichment result types, enabling it to serve both flows
/// without unsafe casts.
/// </remarks>
public interface IEnrichmentResult
{
    /// <summary>Already sorted by <c>PackingPriorityComparer</c>.</summary>
    IReadOnlyList<PackingCandidate> SortedCandidates { get; }

    /// <summary>Per-file enriched plain-text, keyed by file path.</summary>
    IReadOnlyDictionary<string, string> EnrichedByPath { get; }
}
