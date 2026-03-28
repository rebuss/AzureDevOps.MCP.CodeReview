using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Services.Pagination;

/// <summary>
/// Strict sequential page allocator. Iterates sorted candidates in order,
/// fills pages without skipping, closes page when next item doesn't fit.
/// Oversized items get their own page with Partial status.
/// Pure function: no state retained between calls (FR-013).
/// </summary>
public sealed class PageAllocator : IPageAllocator
{
    private readonly ILogger<PageAllocator> _logger;

    public PageAllocator(ILogger<PageAllocator> logger)
    {
        _logger = logger;
    }

    public PageAllocation Allocate(IReadOnlyList<PackingCandidate> sortedCandidates, int safeBudgetTokens)
    {
        // Empty candidates (per Q21): return 1 page with 0 items — consistent shape
        if (sortedCandidates.Count == 0)
        {
            _logger.LogDebug("[PageAllocator] Empty candidates, returning single empty page");
            return BuildEmptyAllocation(safeBudgetTokens);
        }

        if (safeBudgetTokens < PaginationConstants.MinimumBudgetForPagination)
        {
            _logger.LogWarning(
                "[PageAllocator] Budget {Budget} below minimum {Minimum}",
                safeBudgetTokens, PaginationConstants.MinimumBudgetForPagination);
            throw new InvalidOperationException(
                $"Token budget ({safeBudgetTokens}) is too small for pagination. " +
                $"Minimum required: {PaginationConstants.MinimumBudgetForPagination} tokens " +
                "(pagination overhead + manifest overhead).");
        }

        var pages = new List<PageSlice>();
        var candidateIndex = 0;

        while (candidateIndex < sortedCandidates.Count)
        {
            var pageNumber = pages.Count + 1;
            var pageItems = new List<PageSliceItem>();
            var startIndex = candidateIndex;
            var pageBudgetUsed = 0;

            // Per-page overhead: we start with a base and add per-item as we go.
            // Available budget = safeBudget - PaginationOverhead - BaseManifestOverhead
            // Then for each item we also deduct PerItemManifestOverhead.
            var baseOverhead = PaginationConstants.PaginationOverhead + PaginationConstants.BaseManifestOverhead;
            var availableBudget = safeBudgetTokens - baseOverhead;

            while (candidateIndex < sortedCandidates.Count)
            {
                var candidate = sortedCandidates[candidateIndex];
                var itemOverhead = PaginationConstants.PerItemManifestOverhead;
                var costWithOverhead = candidate.EstimatedTokens + itemOverhead;
                var remaining = availableBudget - pageBudgetUsed;

                if (costWithOverhead <= remaining)
                {
                    // Item fits on this page
                    pageItems.Add(new PageSliceItem(
                        candidateIndex,
                        PackingItemStatus.Included,
                        candidate.EstimatedTokens));
                    pageBudgetUsed += costWithOverhead;
                    candidateIndex++;
                }
                else if (pageItems.Count == 0)
                {
                    // Oversized item — gets its own page with Partial status
                    // Available budget for content (minus the one per-item overhead)
                    var budgetForPartial = Math.Max(0, availableBudget - itemOverhead);
                    pageItems.Add(new PageSliceItem(
                        candidateIndex,
                        PackingItemStatus.Partial,
                        candidate.EstimatedTokens,
                        budgetForPartial));
                    pageBudgetUsed = budgetForPartial + itemOverhead;
                    candidateIndex++;
                    break; // Close this page — oversized item gets its own page
                }
                else
                {
                    // Item doesn't fit and page already has items — close the page
                    break;
                }
            }

            var totalPageOverhead = baseOverhead + (pageItems.Count * PaginationConstants.PerItemManifestOverhead);
            var budgetUsedContent = pageItems.Sum(i => i.EstimatedTokens);

            pages.Add(new PageSlice(
                PageNumber: pageNumber,
                StartIndex: startIndex,
                EndIndex: candidateIndex,
                Items: pageItems,
                BudgetUsed: budgetUsedContent,
                BudgetRemaining: Math.Max(0, safeBudgetTokens - totalPageOverhead - budgetUsedContent)));
        }

        var allocation = new PageAllocation(
            Pages: pages,
            TotalPages: pages.Count,
            TotalItems: sortedCandidates.Count);

        _logger.LogInformation(
            "[PageAllocator] Allocated {TotalItems} items across {TotalPages} pages, budget={Budget}",
            allocation.TotalItems, allocation.TotalPages, safeBudgetTokens);

        return allocation;
    }

    private static PageAllocation BuildEmptyAllocation(int safeBudgetTokens)
    {
        var emptyPage = new PageSlice(
            PageNumber: 1,
            StartIndex: 0,
            EndIndex: 0,
            Items: Array.Empty<PageSliceItem>(),
            BudgetUsed: 0,
            BudgetRemaining: safeBudgetTokens);

        return new PageAllocation(
            Pages: new[] { emptyPage },
            TotalPages: 1,
            TotalItems: 0);
    }
}
