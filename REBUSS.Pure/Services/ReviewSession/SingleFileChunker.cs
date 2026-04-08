using REBUSS.Pure.Core;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Hunk-aware single-file chunker. NOT a reuse of <c>PageAllocator</c> — that
/// component is a multi-file bin-packer. See research.md for the rationale.
/// </summary>
internal sealed class SingleFileChunker : ISingleFileChunker
{
    public const string MidHunkSplitMarker = "[truncated mid-hunk: continued in next chunk]";

    private readonly ITokenEstimator _tokenEstimator;

    public SingleFileChunker(ITokenEstimator tokenEstimator)
    {
        _tokenEstimator = tokenEstimator;
    }

    public IReadOnlyList<string> Split(string enrichedText, int budgetTokens)
    {
        ArgumentNullException.ThrowIfNull(enrichedText);
        if (budgetTokens <= 0)
            throw new ArgumentOutOfRangeException(nameof(budgetTokens));

        // Fast path: whole file fits.
        if (_tokenEstimator.EstimateTokenCount(enrichedText) <= budgetTokens)
            return new[] { enrichedText };

        // Walk lines, accumulating into a current chunk. Prefer to break right
        // before a "@@ " hunk header so each chunk is a set of complete hunks.
        var lines = enrichedText.Split('\n');
        var chunks = new List<string>();
        var current = new System.Text.StringBuilder();
        int currentTokens = 0;

        void Flush()
        {
            if (current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd('\n'));
                current.Clear();
                currentTokens = 0;
            }
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var lineWithNl = line + (i < lines.Length - 1 ? "\n" : string.Empty);
            var lineTokens = _tokenEstimator.EstimateTokenCount(lineWithNl);

            bool isHunkHeader = line.StartsWith("@@ ", StringComparison.Ordinal);

            // Prefer to flush at a hunk boundary if we already have content and
            // adding this hunk header would exceed the budget OR we're already past it.
            if (isHunkHeader && current.Length > 0 && currentTokens + lineTokens > budgetTokens)
            {
                Flush();
            }

            // Pathological case: a single line (or accumulated single hunk) exceeds the budget.
            if (currentTokens + lineTokens > budgetTokens && current.Length > 0)
            {
                // Mid-hunk forced split with explicit warning marker.
                current.AppendLine();
                current.Append(MidHunkSplitMarker);
                Flush();
            }

            current.Append(lineWithNl);
            currentTokens += lineTokens;
        }

        Flush();
        return chunks;
    }
}
