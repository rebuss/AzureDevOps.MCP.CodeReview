using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.Pagination;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools.Shared;

/// <summary>
/// Shared helpers for pagination, candidate building, page extraction, and manifest
/// construction used across multiple tool handlers. Eliminates duplication of
/// <c>SortCandidates</c>, <c>BuildCandidates</c>, <c>ExtractPageFiles</c>,
/// <c>BuildPageManifest</c>, and <c>TruncateHunks</c>.
/// </summary>
internal static class ToolHandlerHelpers
{
    /// <summary>
    /// Returns a copy of the candidates list sorted by packing priority
    /// (category ascending → total changes descending → path ascending).
    /// </summary>
    public static List<PackingCandidate> SortCandidates(List<PackingCandidate> candidates)
    {
        var sorted = new List<PackingCandidate>(candidates);
        sorted.Sort(PackingPriorityComparer.Instance);
        return sorted;
    }

    /// <summary>
    /// Builds a list of <see cref="PackingCandidate"/> items from any typed list.
    /// Each item is formatted via <paramref name="formatter"/> to produce the plain-text
    /// output that will be returned to the client, and that representation is used for
    /// accurate token-count estimation.
    /// </summary>
    public static List<PackingCandidate> BuildCandidates<T>(
        List<T> items,
        int safeBudgetTokens,
        ITokenEstimator tokenEstimator,
        IFileClassifier fileClassifier,
        Func<T, string> pathSelector,
        Func<T, int> changeSizeSelector,
        Func<T, string> formatter)
    {
        var candidates = new List<PackingCandidate>(items.Count);
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var plainText = formatter(item);
            var estimation = tokenEstimator.Estimate(plainText, safeBudgetTokens);
            var classification = fileClassifier.Classify(pathSelector(item));

            candidates.Add(new PackingCandidate(
                pathSelector(item),
                estimation.EstimatedTokens,
                classification.Category,
                changeSizeSelector(item)));
        }
        return candidates;
    }

    /// <summary>
    /// Extracts the files belonging to a single page slice, looking up each item by path.
    /// Used by handlers that do not perform partial truncation (e.g. file-list handlers).
    /// </summary>
    public static List<T> ExtractPageFiles<T>(
        List<T> allFiles,
        List<PackingCandidate> candidates,
        PageSlice pageSlice,
        Func<T, string> pathSelector)
    {
        var filesByPath = allFiles.ToDictionary(pathSelector);
        var pageFiles = new List<T>(pageSlice.Items.Count);
        foreach (var item in pageSlice.Items)
        {
            var candidate = candidates[item.OriginalIndex];
            if (filesByPath.TryGetValue(candidate.Path, out var fileItem))
                pageFiles.Add(fileItem);
        }
        return pageFiles;
    }

    /// <summary>
    /// Extracts the files belonging to a single page slice with support for partial
    /// truncation. Items with <see cref="PackingItemStatus.Partial"/> status are
    /// transformed via <paramref name="partialTransform"/> before inclusion.
    /// </summary>
    public static List<T> ExtractPageFiles<T>(
        List<T> allFiles,
        List<PackingCandidate> candidates,
        PageSlice pageSlice,
        Func<T, string> pathSelector,
        Func<T, int, T> partialTransform)
    {
        var filesByPath = allFiles.ToDictionary(pathSelector);
        var pageFiles = new List<T>(pageSlice.Items.Count);
        foreach (var item in pageSlice.Items)
        {
            var candidate = candidates[item.OriginalIndex];
            if (!filesByPath.TryGetValue(candidate.Path, out var fileItem))
                continue;

            if (item.Status == PackingItemStatus.Partial)
                pageFiles.Add(partialTransform(fileItem, item.BudgetForPartial ?? 0));
            else
                pageFiles.Add(fileItem);
        }
        return pageFiles;
    }

    /// <summary>
    /// Builds a <see cref="ContentManifestResult"/> for the items on a single page,
    /// including the extended summary with pagination fields.
    /// </summary>
    public static ContentManifestResult BuildPageManifest(
        List<PackingCandidate> candidates,
        PageSlice pageSlice,
        PageAllocation allocation,
        int safeBudgetTokens)
    {
        var entries = pageSlice.Items.Select(item =>
        {
            var candidate = candidates[item.OriginalIndex];
            return new ManifestEntryResult
            {
                Path = candidate.Path,
                EstimatedTokens = item.EstimatedTokens,
                Status = item.Status.ToString(),
                PriorityTier = candidate.Category.ToString()
            };
        }).ToList();

        return new ContentManifestResult
        {
            Items = entries,
            Summary = PaginationOrchestrator.BuildExtendedManifestSummary(pageSlice, allocation, safeBudgetTokens)
        };
    }

    /// <summary>
    /// Truncates a <see cref="StructuredFileChange"/> to fit within the given partial
    /// budget by including hunks greedily until the budget is exhausted.
    /// </summary>
    public static StructuredFileChange TruncateHunks(
        StructuredFileChange file,
        int budgetForPartial,
        int safeBudgetTokens,
        ITokenEstimator tokenEstimator)
    {
        var truncated = new StructuredFileChange
        {
            Path = file.Path,
            ChangeType = file.ChangeType,
            SkipReason = file.SkipReason,
            Additions = file.Additions,
            Deletions = file.Deletions,
            Hunks = new List<StructuredHunk>()
        };

        var usedTokens = 0;
        foreach (var hunk in file.Hunks)
        {
            var plainText = PlainTextFormatter.FormatHunk(hunk);
            var estimation = tokenEstimator.Estimate(plainText, safeBudgetTokens);

            if (usedTokens + estimation.EstimatedTokens > budgetForPartial)
                break;

            truncated.Hunks.Add(hunk);
            usedTokens += estimation.EstimatedTokens;
        }

        if (truncated.Hunks.Count < file.Hunks.Count)
        {
            truncated.SkipReason = string.Format(Resources.ErrorPartiallyIncludedHunks, truncated.Hunks.Count, file.Hunks.Count);
        }

        return truncated;
    }
}
