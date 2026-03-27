using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;

namespace REBUSS.Pure.Services.ResponsePacking;

/// <summary>
/// Greedy priority-based response packer. Sorts candidates by file category,
/// then iterates and includes items until the budget is exhausted.
/// The first oversized item that would not fit is marked Partial (budget hint
/// for hunk-level truncation in the handler); all subsequent items are Deferred.
/// </summary>
public sealed class ResponsePacker : IResponsePacker
{
    private const int BaseManifestOverhead = 100;
    private const int PerItemManifestOverhead = 15;

    private readonly ILogger<ResponsePacker> _logger;

    public ResponsePacker(ILogger<ResponsePacker> logger)
    {
        _logger = logger;
    }

    public PackingDecision Pack(IReadOnlyList<PackingCandidate> candidates, int safeBudgetTokens)
    {
        if (candidates.Count == 0)
        {
            return BuildEmptyDecision(safeBudgetTokens);
        }

        var manifestOverhead = BaseManifestOverhead + candidates.Count * PerItemManifestOverhead;
        var effectiveBudget = Math.Max(0, safeBudgetTokens - manifestOverhead);

        _logger.LogDebug(
            "[ResponsePacker] Budget: safe={Safe}, manifestOverhead={Overhead}, effective={Effective}, candidates={Count}",
            safeBudgetTokens, manifestOverhead, effectiveBudget, candidates.Count);

        // Build sorted index (sort by priority but track original positions)
        var sortedIndices = CreatePrioritySortedIndices(candidates);

        var decisions = new PackingDecisionItem[candidates.Count];
        var budgetUsed = 0;
        var partialAssigned = false;

        foreach (var sortedIdx in sortedIndices)
        {
            var candidate = candidates[sortedIdx];
            var remaining = effectiveBudget - budgetUsed;

            if (candidate.EstimatedTokens <= remaining)
            {
                // Fits — include in full
                decisions[sortedIdx] = new PackingDecisionItem(
                    sortedIdx, PackingItemStatus.Included, candidate.EstimatedTokens);
                budgetUsed += candidate.EstimatedTokens;
            }
            else if (!partialAssigned && remaining > 0)
            {
                // First oversized item — mark partial with remaining budget
                decisions[sortedIdx] = new PackingDecisionItem(
                    sortedIdx, PackingItemStatus.Partial, candidate.EstimatedTokens, remaining);
                partialAssigned = true;
                // Budget for partial is advisory — actual tokens used depend on handler truncation
            }
            else
            {
                // Does not fit — defer
                decisions[sortedIdx] = new PackingDecisionItem(
                    sortedIdx, PackingItemStatus.Deferred, candidate.EstimatedTokens);
            }
        }

        var manifest = BuildManifest(candidates, decisions, safeBudgetTokens, budgetUsed);

        _logger.LogInformation(
            "[ResponsePacker] Packed: {Included} included, {Partial} partial, {Deferred} deferred, " +
            "budget used {Used}/{Safe} ({Pct:F1}%)",
            manifest.Summary.IncludedCount, manifest.Summary.PartialCount,
            manifest.Summary.DeferredCount, budgetUsed, safeBudgetTokens,
            manifest.Summary.UtilizationPercent);

        return new PackingDecision(decisions, manifest);
    }

    private static int[] CreatePrioritySortedIndices(IReadOnlyList<PackingCandidate> candidates)
    {
        var indices = new int[candidates.Count];
        for (var i = 0; i < indices.Length; i++) indices[i] = i;

        Array.Sort(indices, (a, b) =>
            PackingPriorityComparer.Instance.Compare(candidates[a], candidates[b]));

        return indices;
    }

    private static ContentManifest BuildManifest(
        IReadOnlyList<PackingCandidate> candidates,
        PackingDecisionItem[] decisions,
        int safeBudgetTokens,
        int budgetUsed)
    {
        var entries = new ManifestEntry[candidates.Count];
        var includedCount = 0;
        var partialCount = 0;
        var deferredCount = 0;

        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var d = decisions[i];
            entries[i] = new ManifestEntry(c.Path, d.EstimatedTokens, d.Status, c.Category.ToString());

            switch (d.Status)
            {
                case PackingItemStatus.Included: includedCount++; break;
                case PackingItemStatus.Partial: partialCount++; break;
                case PackingItemStatus.Deferred: deferredCount++; break;
            }
        }

        var budgetRemaining = safeBudgetTokens - budgetUsed;
        var utilization = safeBudgetTokens > 0
            ? (double)budgetUsed / safeBudgetTokens * 100.0
            : 0.0;

        var summary = new ManifestSummary(
            TotalItems: candidates.Count,
            IncludedCount: includedCount,
            PartialCount: partialCount,
            DeferredCount: deferredCount,
            TotalBudgetTokens: safeBudgetTokens,
            BudgetUsed: budgetUsed,
            BudgetRemaining: budgetRemaining,
            UtilizationPercent: Math.Round(utilization, 1));

        return new ContentManifest(entries, summary);
    }

    private static PackingDecision BuildEmptyDecision(int safeBudgetTokens)
    {
        var summary = new ManifestSummary(
            TotalItems: 0,
            IncludedCount: 0,
            PartialCount: 0,
            DeferredCount: 0,
            TotalBudgetTokens: safeBudgetTokens,
            BudgetUsed: 0,
            BudgetRemaining: safeBudgetTokens,
            UtilizationPercent: 0.0);

        return new PackingDecision(
            Array.Empty<PackingDecisionItem>(),
            new ContentManifest(Array.Empty<ManifestEntry>(), summary));
    }
}
