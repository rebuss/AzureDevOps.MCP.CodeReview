using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.RoslynProcessor.Tests;

public class DiffParserTests
{
    [Fact]
    public void TryParseFilePath_ValidDiff_ReturnsPath()
    {
        var diff = "=== src/Services/MyService.cs (edit: +5 -3) ===\n@@ -1,3 +1,5 @@\n+new line\n";
        Assert.Equal("src/Services/MyService.cs", DiffParser.TryParseFilePath(diff));
    }

    [Fact]
    public void TryParseFilePath_SkippedFile_ReturnsPath()
    {
        var diff = "=== src/File.cs (edit: skipped) ===\nReason: full file rewrite";
        Assert.Equal("src/File.cs", DiffParser.TryParseFilePath(diff));
    }

    [Fact]
    public void TryParseFilePath_InvalidDiff_ReturnsNull()
    {
        Assert.Null(DiffParser.TryParseFilePath("not a diff"));
    }

    [Fact]
    public void TryParseFilePath_EmptyString_ReturnsNull()
    {
        Assert.Null(DiffParser.TryParseFilePath(""));
    }

    [Fact]
    public void ParseHunks_SingleHunk_ReturnsCorrectRange()
    {
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -10,3 +10,3 @@\n-old\n+new\n";
        var hunks = DiffParser.ParseHunks(diff);

        Assert.Single(hunks);
        Assert.Equal(10, hunks[0].OldStart);
        Assert.Equal(3, hunks[0].OldCount);
        Assert.Equal(10, hunks[0].NewStart);
        Assert.Equal(3, hunks[0].NewCount);
    }

    [Fact]
    public void ParseHunks_MultipleHunks_ReturnsAll()
    {
        var diff = "=== src/A.cs (edit: +2 -2) ===\n@@ -5,2 +5,2 @@\n-a\n+b\n@@ -20,3 +20,3 @@\n-c\n+d\n";
        var hunks = DiffParser.ParseHunks(diff);

        Assert.Equal(2, hunks.Count);
        Assert.Equal(5, hunks[0].OldStart);
        Assert.Equal(20, hunks[1].OldStart);
    }

    [Fact]
    public void RebuildDiffWithContext_NoneDecision_NoChange()
    {
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -5,1 +5,1 @@\n-old\n+new";
        var sourceLines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.None);
        Assert.Equal(diff, result);
    }

    [Fact]
    public void RebuildDiffWithContext_MinimalDecision_Adds3Lines()
    {
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -10,1 +10,1 @@\n-old\n+new";
        var sourceLines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Minimal);

        // Should contain enricher-added context lines (feature 021: "[ctx] " prefix).
        Assert.Contains("[ctx] line", result);
        // Should contain the original change
        Assert.Contains("-old", result);
        Assert.Contains("+new", result);
    }

    [Fact]
    public void RebuildDiffWithContext_EnricherAddedContext_UsesCtxPrefix()
    {
        // Feature 021: enricher-added context lines must be prefixed with "[ctx] "
        // so the Copilot reviewer can distinguish them from original unified-diff
        // context lines (space-prefixed). This reduces false positives from
        // cross-referencing context against changed lines.
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -10,1 +10,1 @@\n-old\n+new";
        var sourceLines = Enumerable.Range(1, 20).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Minimal);

        // Leading context present, [ctx]-prefixed.
        Assert.Contains("[ctx] line7", result);
        Assert.Contains("[ctx] line8", result);
        Assert.Contains("[ctx] line9", result);
        // Trailing context present, [ctx]-prefixed.
        Assert.Contains("[ctx] line11", result);
    }

    [Fact]
    public void RebuildDiffWithContext_InterHunkGapContext_UsesCtxPrefix()
    {
        // Adjacent hunks merge — the gap between them is filled with inter-hunk
        // context, which must also carry the "[ctx] " prefix.
        var diff = "=== src/A.cs (edit: +2 -2) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -15,1 +15,1 @@\n-old2\n+new2";
        var sourceLines = Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        // line12 sits in the gap between hunks and must be [ctx]-prefixed.
        Assert.Contains("[ctx] line12", result);
    }

    // ─── Feature 011 — fix duplicated/overlapping hunks + negative line numbers ──────────────

    private static List<(int oldStart, int oldCount, int newStart, int newCount)> ParseHeaders(string diff)
    {
        var headers = new List<(int, int, int, int)>();
        foreach (var line in diff.Split('\n'))
        {
            var trimmed = line.TrimEnd('\r');
            if (!trimmed.StartsWith("@@ ")) continue;
            // Format: @@ -a,b +c,d @@
            var parts = trimmed.Split(' ');
            // parts[0] = "@@", parts[1] = "-a,b", parts[2] = "+c,d", parts[3] = "@@"
            var minus = parts[1].TrimStart('-').Split(',');
            var plus = parts[2].TrimStart('+').Split(',');
            headers.Add((int.Parse(minus[0]), int.Parse(minus[1]), int.Parse(plus[0]), int.Parse(plus[1])));
        }
        return headers;
    }

    [Fact]
    public void RebuildDiffWithContext_NewStartSmallerThanOldStart_ProducesNoNegativeLineNumbers()
    {
        // Reproduces the @@ -1,20 +-8,19 @@ symptom: NewStart < contextLines means
        // a naive `NewStart - contextLines` underflows.
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -12,1 +2,1 @@\n-old\n+new";
        var sourceLines = Enumerable.Range(1, 30).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        var headers = ParseHeaders(result);
        Assert.NotEmpty(headers);
        foreach (var h in headers)
        {
            Assert.True(h.oldStart >= 1, $"oldStart was {h.oldStart}");
            Assert.True(h.oldCount >= 0, $"oldCount was {h.oldCount}");
            Assert.True(h.newStart >= 1, $"newStart was {h.newStart}");
            Assert.True(h.newCount >= 0, $"newCount was {h.newCount}");
        }
    }

    [Fact]
    public void RebuildDiffWithContext_AdjacentHunks_MergesIntoSingleBlock()
    {
        // Two raw hunks 5 lines apart on the after-axis with Full (10) context → must merge.
        var diff = "=== src/A.cs (edit: +2 -2) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -15,1 +15,1 @@\n-old2\n+new2";
        var sourceLines = Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        var headers = ParseHeaders(result);
        Assert.Single(headers); // merged into one

        // Source line "line12" sits between the two hunks. It must appear EXACTLY ONCE in output
        // (used to appear twice — once as trailing context of hunk 1, once as leading context of hunk 2).
        // Feature 021: enricher-added context uses "[ctx] " prefix.
        var line12Count = result.Split('\n').Count(l => l.TrimEnd('\r') == "[ctx] line12");
        Assert.Equal(1, line12Count);
    }

    [Fact]
    public void RebuildDiffWithContext_DistantHunks_EmitsBothBlocks()
    {
        // Two hunks 50 lines apart — context windows do NOT overlap → two separate blocks.
        var diff = "=== src/A.cs (edit: +2 -2) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -60,1 +60,1 @@\n-old2\n+new2";
        var sourceLines = Enumerable.Range(1, 100).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        var headers = ParseHeaders(result);
        Assert.Equal(2, headers.Count);
    }

    [Fact]
    public void RebuildDiffWithContext_ThreeClusteredHunks_MergesAllThree()
    {
        var diff = "=== src/A.cs (edit: +3 -3) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -15,1 +15,1 @@\n-old2\n+new2\n" +
                   "@@ -20,1 +20,1 @@\n-old3\n+new3";
        var sourceLines = Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        var headers = ParseHeaders(result);
        Assert.Single(headers);

        // All three change markers must be present once.
        Assert.Equal(1, result.Split('\n').Count(l => l.TrimEnd('\r') == "+new1"));
        Assert.Equal(1, result.Split('\n').Count(l => l.TrimEnd('\r') == "+new2"));
        Assert.Equal(1, result.Split('\n').Count(l => l.TrimEnd('\r') == "+new3"));
    }

    [Fact]
    public void RebuildDiffWithContext_OutputRoundTripsThroughParseHunks()
    {
        var diff = "=== src/A.cs (edit: +2 -2) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -25,1 +25,1 @@\n-old2\n+new2";
        var sourceLines = Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);
        var reparsed = DiffParser.ParseHunks(result);

        Assert.NotEmpty(reparsed);
        foreach (var h in reparsed)
        {
            Assert.True(h.OldStart >= 1);
            Assert.True(h.OldCount >= 0);
            Assert.True(h.NewStart >= 1);
            Assert.True(h.NewCount >= 0);
        }
    }

    [Fact]
    public void RebuildDiffWithContext_ThreeClusteredHunks_MergedCountsAreExact()
    {
        // Three hunks at after-lines 10, 15, 20 (each 1 line). With Full=10 context they merge.
        // Merged hunk should span from min leading context to max trailing context.
        var diff = "=== src/A.cs (edit: +3 -3) ===\n" +
                   "@@ -10,1 +10,1 @@\n-old1\n+new1\n" +
                   "@@ -15,1 +15,1 @@\n-old2\n+new2\n" +
                   "@@ -20,1 +20,1 @@\n-old3\n+new3";
        var sourceLines = Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        var headers = ParseHeaders(result);
        Assert.Single(headers);
        var h = headers[0];
        // Leading context: 10 lines before line 10 → starts at line 0+1 = 1 (clamped to NewStart-1=9 lines).
        // Hunk 1 starts at NewStart=10, after clamping leadingCount = min(10, 9) = 9.
        // So newNewStart = 10 - 9 = 1.
        Assert.Equal(1, h.newStart);
        Assert.Equal(1, h.oldStart);
        // Trailing context: hunk 3 ends at line 21 (NewStart=20, NewCount=1, after-pos = 21).
        // Trailing 10 lines → up to line 30. Total span = lines 1..30 = 30 lines.
        Assert.Equal(30, h.newCount);
        Assert.Equal(30, h.oldCount);
    }

    [Fact]
    public void RebuildDiffWithContext_SourceLinesShorterThanContextRange_DoesNotThrow()
    {
        // Hunk claims NewStart=10 but sourceLines has only 5 lines.
        // Before the fix, leading context indexing would throw IndexOutOfRangeException.
        var diff = "=== src/A.cs (edit: +1 -1) ===\n@@ -10,1 +10,1 @@\n-old\n+new";
        var sourceLines = Enumerable.Range(1, 5).Select(i => $"line{i}").ToArray();
        var hunks = DiffParser.ParseHunks(diff);

        var result = DiffParser.RebuildDiffWithContext(diff, sourceLines, hunks, ContextDecision.Full);

        // Should not throw; should still contain the change.
        Assert.Contains("-old", result);
        Assert.Contains("+new", result);
    }
}
