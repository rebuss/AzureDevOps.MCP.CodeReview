using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Produces structured diff hunks for a single file given base and target content.
/// Depends on <see cref="IDiffAlgorithm"/> for the line-level edit computation (DIP).
/// </summary>
public class StructuredDiffBuilder : IStructuredDiffBuilder
{
    private const int DefaultContextLines = 3;

    private readonly IDiffAlgorithm _diffAlgorithm;
    private readonly ILogger<StructuredDiffBuilder> _logger;

    public StructuredDiffBuilder(IDiffAlgorithm diffAlgorithm, ILogger<StructuredDiffBuilder> logger)
    {
        _diffAlgorithm = diffAlgorithm;
        _logger = logger;
    }

    public List<DiffHunk> Build(string filePath, string? baseContent, string? targetContent)
    {
        if (baseContent == targetContent)
            return new List<DiffHunk>();

        var sw = Stopwatch.StartNew();

        var aPath = filePath.TrimStart('/');
        var baseLines = SplitLines(baseContent);
        var targetLines = SplitLines(targetContent);

        var hunks = ComputeHunks(baseLines, targetLines, DefaultContextLines);

        sw.Stop();

        _logger.LogDebug(
            "Diff for '{FilePath}': {HunkCount} hunk(s), old={OldLineCount} lines, new={NewLineCount} lines, {ElapsedMs}ms",
            aPath, hunks.Count, baseLines.Length, targetLines.Length, sw.ElapsedMilliseconds);

        if (hunks.Count > 50)
        {
            _logger.LogWarning(
                "Suspicious diff for '{FilePath}': {HunkCount} hunks (possible generated/binary file)",
                aPath, hunks.Count);
        }

        return hunks;
    }

    internal static string[] SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<string>();
        return content.Replace("\r\n", "\n").Split('\n');
    }

    // --- Hunk computation --------------------------------------------------------

    private List<DiffHunk> ComputeHunks(string[] oldLines, string[] newLines, int contextLines)
    {
        var edits = _diffAlgorithm.ComputeEdits(oldLines, newLines);
        var hunks = new List<DiffHunk>();
        int i = 0;

        while (i < edits.Count)
        {
            if (edits[i].Kind == ' ') { i++; continue; }

            int hunkStart = Math.Max(0, i - contextLines);
            var (hunkEdits, nextI) = CollectHunkEdits(edits, hunkStart, contextLines);
            i = nextI;

            if (hunkEdits.Count > 0)
                hunks.Add(FormatHunk(hunkEdits, oldLines, newLines));
        }

        return hunks;
    }

    private static (List<DiffEdit> HunkEdits, int NextI) CollectHunkEdits(
        IReadOnlyList<DiffEdit> edits, int hunkStart, int contextLines)
    {
        var hunkEdits = new List<DiffEdit>();
        int j = hunkStart;
        int nextI = hunkStart;

        while (j < edits.Count)
        {
            hunkEdits.Add(edits[j]);

            if (edits[j].Kind == ' ')
            {
                var (trailingLen, moreChanges) = CountTrailingContext(edits, j);

                if (!moreChanges || trailingLen > contextLines)
                {
                    int keep = Math.Min(contextLines, trailingLen);
                    for (int x = 1; x < keep && j + x < edits.Count; x++)
                        hunkEdits.Add(edits[j + x]);
                    return (hunkEdits, j + keep);
                }
            }

            j++;
            nextI = j;
        }

        return (hunkEdits, nextI);
    }

    private static (int TrailingLen, bool MoreChanges) CountTrailingContext(
        IReadOnlyList<DiffEdit> edits, int j)
    {
        int trailingEnd = j;
        while (trailingEnd + 1 < edits.Count && edits[trailingEnd + 1].Kind == ' ')
            trailingEnd++;

        return (trailingEnd - j + 1, trailingEnd + 1 < edits.Count);
    }

    // --- Hunk formatting ---------------------------------------------------------

    private static DiffHunk FormatHunk(List<DiffEdit> hunkEdits, string[] oldLines, string[] newLines)
    {
        int oldStart = hunkEdits.Where(e => e.Kind != '+').Select(e => e.OldIdx + 1).DefaultIfEmpty(1).First();
        int newStart = hunkEdits.Where(e => e.Kind != '-').Select(e => e.NewIdx + 1).DefaultIfEmpty(1).First();
        int oldCount = hunkEdits.Count(e => e.Kind != '+');
        int newCount = hunkEdits.Count(e => e.Kind != '-');

        var lines = new List<DiffLine>();
        foreach (var edit in hunkEdits)
        {
            var lineText = edit.Kind == '+' ? newLines[edit.NewIdx] : oldLines[edit.OldIdx];
            lines.Add(new DiffLine { Op = edit.Kind, Text = lineText });
        }

        return new DiffHunk
        {
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
            Lines = lines
        };
    }
}
