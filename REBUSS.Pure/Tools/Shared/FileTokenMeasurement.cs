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
