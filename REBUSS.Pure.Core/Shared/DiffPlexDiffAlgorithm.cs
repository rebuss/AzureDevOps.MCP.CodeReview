using System.Diagnostics;
using DiffPlex;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Myers-based diff algorithm backed by <see cref="Differ"/> from the DiffPlex library.
/// Produces a minimal edit list compatible with <see cref="IDiffAlgorithm"/>.
/// </summary>
public class DiffPlexDiffAlgorithm : IDiffAlgorithm
{
    public IReadOnlyList<DiffEdit> ComputeEdits(string[] oldLines, string[] newLines)
    {
        var differ = new Differ();

        var oldText = string.Join('\n', oldLines);
        var newText = string.Join('\n', newLines);

        var diffResult = differ.CreateLineDiffs(oldText, newText, ignoreWhitespace: false);

        var edits = new List<DiffEdit>(oldLines.Length + newLines.Length);
        int oldIdx = 0;
        int newIdx = 0;

        foreach (var block in diffResult.DiffBlocks)
        {
            // Context lines before this block — gaps must be equal on both sides.
            Debug.Assert(block.DeleteStartA - oldIdx == block.InsertStartB - newIdx,
                "Context gap mismatch — diff block indices drifted.");

            int contextCount = block.DeleteStartA - oldIdx;
            for (int i = 0; i < contextCount; i++)
                edits.Add(new DiffEdit(' ', oldIdx++, newIdx++));

            // Deleted lines
            for (int i = 0; i < block.DeleteCountA; i++)
                edits.Add(new DiffEdit('-', oldIdx++, newIdx));

            // Inserted lines
            for (int i = 0; i < block.InsertCountB; i++)
                edits.Add(new DiffEdit('+', oldIdx, newIdx++));
        }

        // Trailing context lines — remaining counts must match.
        Debug.Assert(oldLines.Length - oldIdx == newLines.Length - newIdx,
            "Trailing context mismatch — remaining old/new line counts differ.");

        while (oldIdx < oldLines.Length && newIdx < newLines.Length)
            edits.Add(new DiffEdit(' ', oldIdx++, newIdx++));

        return edits;
    }
}
