using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Properties;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Produces structured diff hunks for a single file given base and target content.
/// Depends on <see cref="IDiffAlgorithm"/> for the line-level edit computation (DIP).
/// </summary>
public class StructuredDiffBuilder : IStructuredDiffBuilder
{
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

        var hunks = ComputeHunks(baseLines, targetLines);

        sw.Stop();

        _logger.LogDebug(
            Resources.LogStructuredDiffBuilderDiffCompleted,
            aPath, hunks.Count, baseLines.Length, targetLines.Length, sw.ElapsedMilliseconds);

        if (hunks.Count > 50)
        {
            _logger.LogWarning(
                Resources.LogStructuredDiffBuilderSuspiciousDiff,
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

    private List<DiffHunk> ComputeHunks(string[] oldLines, string[] newLines)
    {
        var edits = _diffAlgorithm.ComputeEdits(oldLines, newLines);
        var hunks = new List<DiffHunk>();
        var currentHunkEdits = new List<DiffEdit>();

        foreach (var edit in edits)
        {
            if (edit.Kind == ' ')
            {
                if (currentHunkEdits.Count > 0)
                {
                    hunks.Add(FormatHunk(currentHunkEdits, oldLines, newLines));
                    currentHunkEdits = new List<DiffEdit>();
                }
                continue;
            }
            currentHunkEdits.Add(edit);
        }

        if (currentHunkEdits.Count > 0)
            hunks.Add(FormatHunk(currentHunkEdits, oldLines, newLines));

        return hunks;
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
