using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Core;

/// <summary>
/// Allocates sorted candidates across deterministic pages.
/// Uses strict sequential allocation (no skipping): items in priority order fill
/// each page; when the next item doesn't fit, the page closes.
/// </summary>
public interface IPageAllocator
{
    /// <summary>
    /// Computes deterministic page boundaries for the given sorted candidates.
    /// </summary>
    /// <param name="sortedCandidates">Candidates already sorted by Feature 003's PackingPriorityComparer.</param>
    /// <param name="safeBudgetTokens">Token budget per page.</param>
    /// <returns>Complete page allocation with all page boundaries.</returns>
    /// <exception cref="Exceptions.BudgetTooSmallException">
    /// Thrown when <paramref name="safeBudgetTokens"/> is below the minimum required for pagination.
    /// </exception>
    PageAllocation Allocate(
        IReadOnlyList<PackingCandidate> sortedCandidates,
        int safeBudgetTokens);
}
