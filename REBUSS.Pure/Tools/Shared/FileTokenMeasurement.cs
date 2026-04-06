using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools.Shared;

/// <summary>
/// Shared helper that builds <see cref="PackingCandidate"/> items from a
/// <see cref="PullRequestDiff"/> by formatting each file as plain text
/// and measuring the actual token cost of that output format.
/// </summary>
internal static class FileTokenMeasurement
{
    /// <summary>
    /// Builds a list of packing candidates from the diff, each with a measured
    /// token count derived from the plain-text representation of the file diff.
    /// </summary>
    public static List<PackingCandidate> BuildCandidatesFromDiff(
        PullRequestDiff diff,
        ITokenEstimator tokenEstimator,
        IFileClassifier fileClassifier)
    {
        var candidates = new List<PackingCandidate>(diff.Files.Count);

        foreach (var file in diff.Files)
        {
            var structured = MapToStructured(file);
            var plainText = PlainTextFormatter.FormatFileDiff(structured);
            var actualTokens = tokenEstimator.EstimateTokenCount(plainText);
            var classification = fileClassifier.Classify(file.Path);

            candidates.Add(new PackingCandidate(
                file.Path,
                actualTokens,
                classification.Category,
                file.Additions + file.Deletions));
        }

        return candidates;
    }

    /// <summary>
    /// Builds packing candidates whose token counts reflect the diff text *after* it has been
    /// run through <paramref name="codeProcessor"/>'s enrichment pipeline. Returns both the
    /// candidates and a cache of enriched plain-text per file path so callers can emit the
    /// enriched output without re-running the (expensive) pipeline.
    /// </summary>
    /// <remarks>
    /// This is the correct measurement entry point whenever an <see cref="ICodeProcessor"/> is
    /// active: pagination computed against raw diff sizes will under-count tokens and let pages
    /// blow past the budget once enrichers inject scope/structural/call-site annotations and
    /// before/after context windows.
    /// </remarks>
    public static async Task<(List<PackingCandidate> Candidates, Dictionary<string, string> EnrichedByPath)>
        BuildEnrichedCandidatesAsync(
            PullRequestDiff diff,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            ICodeProcessor codeProcessor,
            CancellationToken ct = default)
    {
        var candidates = new List<PackingCandidate>(diff.Files.Count);
        var enrichedByPath = new Dictionary<string, string>(diff.Files.Count, StringComparer.OrdinalIgnoreCase);

        foreach (var file in diff.Files)
        {
            ct.ThrowIfCancellationRequested();
            var structured = MapToStructured(file);
            var plainText = PlainTextFormatter.FormatFileDiff(structured);
            var enriched = await codeProcessor.AddBeforeAfterContext(plainText, ct);
            var actualTokens = tokenEstimator.EstimateTokenCount(enriched);
            var classification = fileClassifier.Classify(file.Path);

            candidates.Add(new PackingCandidate(
                file.Path,
                actualTokens,
                classification.Category,
                file.Additions + file.Deletions));
            enrichedByPath[file.Path] = enriched;
        }

        return (candidates, enrichedByPath);
    }

    /// <summary>
    /// Maps a domain <see cref="FileChange"/> to the structured output model
    /// used for JSON serialization. Identical to the mapping previously inline
    /// in handler code — extracted here for single-source-of-truth reuse.
    /// </summary>
    public static StructuredFileChange MapToStructured(FileChange file)
    {
        return new StructuredFileChange
        {
            Path = file.Path,
            ChangeType = file.ChangeType,
            SkipReason = file.SkipReason,
            Additions = file.Additions,
            Deletions = file.Deletions,
            Hunks = file.Hunks.Select(h => new StructuredHunk
            {
                OldStart = h.OldStart,
                OldCount = h.OldCount,
                NewStart = h.NewStart,
                NewCount = h.NewCount,
                Lines = h.Lines.Select(l => new StructuredLine
                {
                    Op = l.Op.ToString(),
                    Text = l.Text
                }).ToList()
            }).ToList()
        };
    }
}
