using REBUSS.Pure.GitHub.Parsers;

namespace REBUSS.Pure.GitHub.Tests.Parsers;

public class GitHubPatchHunkParserTests
{
    [Fact]
    public void Parse_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Empty(GitHubPatchHunkParser.Parse(null));
        Assert.Empty(GitHubPatchHunkParser.Parse(""));
    }

    [Fact]
    public void Parse_SingleHunk_ReturnsCorrectHunkAndLines()
    {
        const string patch =
            "@@ -1,3 +1,4 @@\n" +
            " context line\n" +
            "-old line\n" +
            "+new line\n" +
            "+another new line\n" +
            " trailing context";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        var hunk = hunks[0];
        Assert.Equal(1, hunk.OldStart);
        Assert.Equal(3, hunk.OldCount);
        Assert.Equal(1, hunk.NewStart);
        Assert.Equal(4, hunk.NewCount);
        Assert.Equal(5, hunk.Lines.Count);
        Assert.Equal(' ', hunk.Lines[0].Op);
        Assert.Equal("context line", hunk.Lines[0].Text);
        Assert.Equal('-', hunk.Lines[1].Op);
        Assert.Equal("old line", hunk.Lines[1].Text);
        Assert.Equal('+', hunk.Lines[2].Op);
        Assert.Equal("new line", hunk.Lines[2].Text);
        Assert.Equal('+', hunk.Lines[3].Op);
        Assert.Equal("another new line", hunk.Lines[3].Text);
        Assert.Equal(' ', hunk.Lines[4].Op);
        Assert.Equal("trailing context", hunk.Lines[4].Text);
    }

    [Fact]
    public void Parse_MultipleHunks_ReturnsAll()
    {
        const string patch =
            "@@ -1,2 +1,2 @@\n" +
            "-old1\n" +
            "+new1\n" +
            "@@ -10,2 +10,2 @@\n" +
            "-old2\n" +
            "+new2";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Equal(2, hunks.Count);
        Assert.Equal(1, hunks[0].OldStart);
        Assert.Equal(10, hunks[1].OldStart);
        Assert.Equal('-', hunks[0].Lines[0].Op);
        Assert.Equal("old1", hunks[0].Lines[0].Text);
        Assert.Equal('+', hunks[1].Lines[1].Op);
        Assert.Equal("new2", hunks[1].Lines[1].Text);
    }

    [Fact]
    public void Parse_HeaderWithoutCounts_DefaultsToOne()
    {
        const string patch =
            "@@ -5 +5 @@\n" +
            "-old\n" +
            "+new";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(5, hunks[0].OldStart);
        Assert.Equal(1, hunks[0].OldCount);
        Assert.Equal(5, hunks[0].NewStart);
        Assert.Equal(1, hunks[0].NewCount);
    }

    [Fact]
    public void Parse_IgnoresNoNewlineMarker()
    {
        const string patch =
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "\\ No newline at end of file\n" +
            "+new";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(2, hunks[0].Lines.Count);
        Assert.Equal('-', hunks[0].Lines[0].Op);
        Assert.Equal('+', hunks[0].Lines[1].Op);
    }

    [Fact]
    public void Parse_HandlesCrLfLineEndings()
    {
        const string patch =
            "@@ -1,1 +1,1 @@\r\n" +
            "-old\r\n" +
            "+new";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(2, hunks[0].Lines.Count);
    }

    [Fact]
    public void Parse_TrueBlankContextLine_EncodedAsSpacePrefix_ProducesBlankContext()
    {
        // Unified-diff encoding: a blank context line is `" "` (a single-space prefix).
        const string patch =
            "@@ -1,3 +1,3 @@\n" +
            " before\n" +
            " \n" +
            " after";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(3, hunks[0].Lines.Count);
        Assert.Equal(' ', hunks[0].Lines[1].Op);
        Assert.Equal(string.Empty, hunks[0].Lines[1].Text);
    }

    [Fact]
    public void Parse_EmptyStringFromSplit_NotSyntheticContext()
    {
        // Regression: Split('\n') yields an empty element wherever two newlines meet (or at
        // trailing-newline). These are not real blank context lines (`""` != `" "`) and must
        // be skipped — otherwise the hunk gains a spurious context line and misaligns.
        const string patch =
            "@@ -1,2 +1,2 @@\n" +
            " before\n" +
            "\n" +
            " after";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(2, hunks[0].Lines.Count);
        Assert.Equal(' ', hunks[0].Lines[0].Op);
        Assert.Equal("before", hunks[0].Lines[0].Text);
        Assert.Equal(' ', hunks[0].Lines[1].Op);
        Assert.Equal("after", hunks[0].Lines[1].Text);
    }

    [Fact]
    public void Parse_TrailingNewline_DoesNotAppendSpuriousContextLine()
    {
        // Classic Split('\n') trap: patch ends with '\n' → last element is "". Previously
        // this emitted an extra blank context line on every hunk with a trailing newline.
        const string patch = "@@ -1,1 +1,1 @@\n-old\n+new\n";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        var hunk = Assert.Single(hunks);
        Assert.Equal(2, hunk.Lines.Count);
        Assert.Equal('-', hunk.Lines[0].Op);
        Assert.Equal('+', hunk.Lines[1].Op);
    }

    [Fact]
    public void Parse_PreHeaderNoise_IsIgnored()
    {
        const string patch =
            "diff --git a/F b/F\n" +
            "index 1234..5678 100644\n" +
            "--- a/F\n" +
            "+++ b/F\n" +
            "@@ -1,1 +1,1 @@\n" +
            "-old\n" +
            "+new";

        var hunks = GitHubPatchHunkParser.Parse(patch);

        Assert.Single(hunks);
        Assert.Equal(2, hunks[0].Lines.Count);
    }
}
