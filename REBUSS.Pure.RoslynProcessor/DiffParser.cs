using System.Text;
using System.Text.RegularExpressions;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Parses formatted diff strings produced by <c>PlainTextFormatter</c>
/// and rebuilds them with context lines inserted.
/// </summary>
public static partial class DiffParser
{
    private static readonly Regex FilePathRegex = FilePathPattern();
    private static readonly Regex HunkHeaderRegex = HunkHeaderPattern();

    [GeneratedRegex(@"^=== (.+?) \(", RegexOptions.Multiline)]
    private static partial Regex FilePathPattern();

    [GeneratedRegex(@"^@@ -(\d+),(\d+) \+(\d+),(\d+) @@", RegexOptions.Multiline)]
    private static partial Regex HunkHeaderPattern();

    /// <summary>Extracts the file path from the diff header line.</summary>
    public static string? TryParseFilePath(string diff)
    {
        if (string.IsNullOrEmpty(diff))
            return null;

        var match = FilePathRegex.Match(diff);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>Parses all hunk headers from the diff string.</summary>
    public static List<ParsedHunk> ParseHunks(string diff)
    {
        var hunks = new List<ParsedHunk>();
        var matches = HunkHeaderRegex.Matches(diff);

        for (int i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            var endOffset = i + 1 < matches.Count ? matches[i + 1].Index : diff.Length;

            hunks.Add(new ParsedHunk
            {
                OldStart = int.Parse(m.Groups[1].Value),
                OldCount = int.Parse(m.Groups[2].Value),
                NewStart = int.Parse(m.Groups[3].Value),
                NewCount = int.Parse(m.Groups[4].Value),
                HeaderLine = m.Value,
                StartOffset = m.Index,
                EndOffset = endOffset
            });
        }

        return hunks;
    }

    /// <summary>
    /// Rebuilds the diff string with context lines inserted around each hunk
    /// based on the <paramref name="decision"/>.
    /// <para>
    /// Implementation is a three-phase pipeline (feature 011):
    /// (1) expand each hunk independently with leading/trailing context drawn from the
    ///     after-file (<paramref name="sourceLines"/>), with leading-context count clamped
    ///     to <c>min(contextLines, OldStart-1, NewStart-1)</c> so neither axis underflows
    ///     to a negative line number; (2) merge any expanded hunks whose ranges touch or
    ///     overlap on the after-axis (standard unified-diff convention) so each source
    ///     line appears at most once in the output; (3) render the merged hunks back to
    ///     a diff string.
    /// </para>
    /// </summary>
    public static string RebuildDiffWithContext(
        string diff,
        string[] sourceLines,
        List<ParsedHunk> hunks,
        ContextDecision decision)
    {
        if (decision == ContextDecision.None || hunks.Count == 0 || sourceLines.Length == 0)
            return diff;

        int contextLines = decision == ContextDecision.Full ? 10 : 3;

        // Phase 1: cluster adjacent raw hunks. Two consecutive hunks join the same cluster
        // when the after-axis gap between them is small enough that their context windows
        // would overlap (gap <= 2 * contextLines). This is the standard unified-diff
        // convention: nearby hunks merge into one block to avoid duplicating context lines.
        var clusters = new List<List<ParsedHunk>>();
        var current = new List<ParsedHunk> { hunks[0] };
        for (int i = 1; i < hunks.Count; i++)
        {
            var prev = current[^1];
            int prevAfterEnd = prev.NewStart + prev.NewCount; // exclusive
            int gap = hunks[i].NewStart - prevAfterEnd;
            if (gap <= 2 * contextLines)
            {
                current.Add(hunks[i]);
            }
            else
            {
                clusters.Add(current);
                current = new List<ParsedHunk> { hunks[i] };
            }
        }
        clusters.Add(current);

        // Phase 2: expand each cluster into a single ExpandedHunk with leading + body +
        // inter-hunk-gap-context + trailing all stitched together.
        var expanded = new List<ExpandedHunk>(clusters.Count);
        foreach (var cluster in clusters)
        {
            var firstHunk = cluster[0];
            var lastHunk = cluster[^1];

            // Shared leading-context count clamped to whichever axis runs out first, so
            // neither newOldStart nor newNewStart underflows.
            int leadingCount = Math.Min(contextLines, Math.Min(firstHunk.OldStart - 1, firstHunk.NewStart - 1));
            if (leadingCount < 0) leadingCount = 0;

            int afterPos = lastHunk.NewStart - 1 + lastHunk.NewCount;
            int trailingCount = Math.Min(contextLines, sourceLines.Length - afterPos);
            if (trailingCount < 0) trailingCount = 0;

            int newOldStart = firstHunk.OldStart - leadingCount;
            int newNewStart = firstHunk.NewStart - leadingCount;
            int newOldCount = (lastHunk.OldStart + lastHunk.OldCount + trailingCount) - newOldStart;
            int newNewCount = (lastHunk.NewStart + lastHunk.NewCount + trailingCount) - newNewStart;

            var lines = new List<string>();

            // Leading context — sliced from the AFTER file at NEW-axis start.
            int leadStartIdx = firstHunk.NewStart - 1 - leadingCount;
            for (int j = 0; j < leadingCount; j++)
                lines.Add(" " + sourceLines[leadStartIdx + j]);

            for (int hi = 0; hi < cluster.Count; hi++)
            {
                var h = cluster[hi];

                // Body of this hunk (verbatim from the raw diff).
                var bodyText = diff[(h.StartOffset + h.HeaderLine.Length)..h.EndOffset];
                if (bodyText.StartsWith("\r\n")) bodyText = bodyText[2..];
                else if (bodyText.StartsWith('\n')) bodyText = bodyText[1..];
                foreach (var rawLine in bodyText.Split('\n'))
                {
                    if (rawLine.Length == 0) continue;
                    var bodyLine = rawLine.EndsWith('\r') ? rawLine[..^1] : rawLine;
                    if (bodyLine.Length == 0) continue;
                    lines.Add(bodyLine);
                }

                // Inter-hunk gap context (when not the last hunk in the cluster):
                // unchanged lines between this hunk's after-end and the next hunk's start.
                if (hi + 1 < cluster.Count)
                {
                    var nextH = cluster[hi + 1];
                    int gapStart = h.NewStart - 1 + h.NewCount;        // first context line after this hunk
                    int gapEnd = nextH.NewStart - 1;                   // exclusive
                    for (int j = gapStart; j < gapEnd && j < sourceLines.Length; j++)
                        lines.Add(" " + sourceLines[j]);
                }
            }

            // Trailing context after the last hunk in the cluster.
            for (int j = 0; j < trailingCount; j++)
                lines.Add(" " + sourceLines[afterPos + j]);

            expanded.Add(new ExpandedHunk
            {
                OldStart = newOldStart,
                OldCount = newOldCount,
                NewStart = newNewStart,
                NewCount = newNewCount,
                Lines = lines,
            });
        }

        // Phase 3: render the expanded clusters back to a diff string.
        var sb = new StringBuilder();

        // Preserve the file header line (everything before the first raw hunk).
        if (hunks[0].StartOffset > 0)
            sb.Append(diff[..hunks[0].StartOffset]);

        foreach (var eh in expanded)
        {
            sb.AppendLine($"@@ -{eh.OldStart},{eh.OldCount} +{eh.NewStart},{eh.NewCount} @@");
            foreach (var line in eh.Lines)
                sb.AppendLine(line);
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Mutable holder used by <see cref="RebuildDiffWithContext"/> Phase 1/2.</summary>
    private sealed class ExpandedHunk
    {
        public int OldStart;
        public int OldCount;
        public int NewStart;
        public int NewCount;
        public List<string> Lines = new();
    }
}

/// <summary>Represents a parsed hunk header from a unified diff.</summary>
public class ParsedHunk
{
    public int OldStart { get; set; }
    public int OldCount { get; set; }
    public int NewStart { get; set; }
    public int NewCount { get; set; }
    public string HeaderLine { get; set; } = string.Empty;
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
}
