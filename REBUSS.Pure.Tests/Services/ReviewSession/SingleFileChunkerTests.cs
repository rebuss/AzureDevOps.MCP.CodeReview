using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Services.ReviewSession;

namespace REBUSS.Pure.Tests.Services.ReviewSession;

public class SingleFileChunkerTests
{
    private static SingleFileChunker NewChunker(int tokensPerChar = 1)
    {
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length * tokensPerChar);
        return new SingleFileChunker(est);
    }

    [Fact]
    public void Split_SmallFile_ReturnsSingleChunk()
    {
        var chunker = NewChunker();
        var text = "@@ -1,3 +1,3 @@\n line1\n+line2\n line3";
        var result = chunker.Split(text, budgetTokens: 10_000);

        Assert.Single(result);
        Assert.Equal(text, result[0]);
    }

    [Fact]
    public void Split_TwoHunks_SplitsAtHunkBoundary()
    {
        var chunker = NewChunker();
        var hunk1 = "@@ -1,2 +1,2 @@\n a\n+b\n";
        var hunk2 = "@@ -10,2 +10,2 @@\n c\n+d";
        var text = hunk1 + hunk2;

        // Budget that fits one hunk but not both
        var result = chunker.Split(text, budgetTokens: hunk1.Length + 5);

        Assert.True(result.Count >= 2, $"expected ≥2 chunks, got {result.Count}");
        Assert.StartsWith("@@ -1,", result[0]);
        Assert.Contains("@@ -10,", result[1]);
    }

    [Fact]
    public void Split_PathologicalHunk_ForcesMidHunkSplitWithMarker()
    {
        var chunker = NewChunker();
        // One huge hunk far exceeding the budget
        var bigBody = string.Join("\n", Enumerable.Range(0, 50).Select(i => "+line" + i));
        var text = "@@ -1,50 +1,50 @@\n" + bigBody;

        var result = chunker.Split(text, budgetTokens: 80);

        Assert.True(result.Count >= 2);
        Assert.Contains(result, c => c.Contains(SingleFileChunker.MidHunkSplitMarker));
    }

    [Fact]
    public void Split_RoundTrip_ReproducesContentModuloMarkers()
    {
        var chunker = NewChunker();
        var text = "@@ -1,2 +1,2 @@\n a\n+b\n@@ -10,2 +10,2 @@\n c\n+d";
        var result = chunker.Split(text, budgetTokens: 30);

        var rejoined = string.Concat(result)
            .Replace(SingleFileChunker.MidHunkSplitMarker, string.Empty)
            .Replace("\n\n", "\n");
        Assert.Contains("a", rejoined);
        Assert.Contains("d", rejoined);
        Assert.Contains("@@ -10,", rejoined);
    }

    [Fact]
    public void Split_NegativeBudget_Throws()
    {
        var chunker = NewChunker();
        Assert.Throws<ArgumentOutOfRangeException>(() => chunker.Split("text", 0));
    }
}
