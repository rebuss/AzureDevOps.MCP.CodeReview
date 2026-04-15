using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingParser"/>. Feature 021.</summary>
public class FindingParserTests
{
    [Fact]
    public void Parse_SingleWellFormedFinding_ExtractsAllFields()
    {
        var input = "**[critical]** `src/Foo.cs` (line 42): Null dereference on empty input";

        var (findings, remainder) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Equal(0, f.Index);
        Assert.Equal("src/Foo.cs", f.FilePath);
        Assert.Equal(42, f.LineNumber);
        Assert.Equal("critical", f.Severity);
        Assert.Equal("Null dereference on empty input", f.Description);
        Assert.Equal("", remainder.Trim());
    }

    [Fact]
    public void Parse_FindingWithoutLine_LineNumberIsNull()
    {
        var input = "**[major]** `src/Foo.cs`: Async method lacks await";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Null(f.LineNumber);
        Assert.Equal("major", f.Severity);
    }

    [Fact]
    public void Parse_MixedSeverities_ParsesAllInOrder()
    {
        var input = """
            **[critical]** `src/A.cs` (line 1): issue one
            **[major]** `src/B.cs` (line 2): issue two
            **[minor]** `src/C.cs` (line 3): issue three
            """;

        var (findings, _) = FindingParser.Parse(input);

        Assert.Equal(3, findings.Count);
        Assert.Equal("critical", findings[0].Severity);
        Assert.Equal("major", findings[1].Severity);
        Assert.Equal("minor", findings[2].Severity);
        Assert.Equal(0, findings[0].Index);
        Assert.Equal(1, findings[1].Index);
        Assert.Equal(2, findings[2].Index);
    }

    [Fact]
    public void Parse_UnparseableText_ReturnsEmptyFindingsAndFullRemainder()
    {
        var input = "No issues found.\nEverything looks good.";

        var (findings, remainder) = FindingParser.Parse(input);

        Assert.Empty(findings);
        Assert.Equal(input, remainder);
    }

    [Fact]
    public void Parse_PartiallyParseable_ReturnsMixedOutput()
    {
        var input = """
            ## Critical Issues

            **[critical]** `src/A.cs` (line 5): null deref
            Some trailing prose after the finding.
            """;

        var (findings, remainder) = FindingParser.Parse(input);

        Assert.Single(findings);
        Assert.Equal("src/A.cs", findings[0].FilePath);
        // Remainder contains the heading (before the finding) and trailing prose (after).
        Assert.Contains("## Critical Issues", remainder);
        Assert.Contains("trailing prose", remainder);
        // Remainder does NOT contain the finding line itself.
        Assert.DoesNotContain("null deref", remainder);
    }

    [Fact]
    public void Parse_EmptyInput_ReturnsEmpty()
    {
        var (findings, remainder) = FindingParser.Parse("");

        Assert.Empty(findings);
        Assert.Equal("", remainder);
    }

    [Fact]
    public void Parse_WhitespaceOnly_ReturnsEmpty()
    {
        var (findings, remainder) = FindingParser.Parse("   \n\t  \n");

        Assert.Empty(findings);
        Assert.Equal("   \n\t  \n", remainder);
    }

    [Fact]
    public void Parse_FindingWithBackticksInDescription_ParsesCorrectly()
    {
        var input = "**[minor]** `src/Foo.cs` (line 3): Consider using `nameof(X)` instead";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Contains("nameof", f.Description);
    }

    [Fact]
    public void Parse_SeverityCaseInsensitive_Normalized()
    {
        var input = "**[CRITICAL]** `src/Foo.cs` (line 1): upper-case severity";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Equal("critical", f.Severity); // normalized to lowercase
    }

    [Fact]
    public void Parse_OriginalTextCapturesEntireHeaderLine()
    {
        var input = "**[critical]** `src/Foo.cs` (line 42): Null dereference";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Contains("[critical]", f.OriginalText);
        Assert.Contains("src/Foo.cs", f.OriginalText);
        Assert.Contains("Null dereference", f.OriginalText);
    }

    // ─── Tolerance for Copilot output variations (safety net over the prompt) ──

    [Theory]
    [InlineData("- **[major]** `src/Foo.cs` (line 42): Leaks")]
    [InlineData("* **[major]** `src/Foo.cs` (line 42): Leaks")]
    [InlineData("1. **[major]** `src/Foo.cs` (line 42): Leaks")]
    [InlineData("2) **[major]** `src/Foo.cs` (line 42): Leaks")]
    [InlineData("  **[major]** `src/Foo.cs` (line 42): Leaks")]
    public void Parse_LeadingListMarkers_StillExtractsFinding(string input)
    {
        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Equal("src/Foo.cs", f.FilePath);
        Assert.Equal(42, f.LineNumber);
    }

    [Theory]
    [InlineData("(line ~138)", 138)]
    [InlineData("(line ≈138)", 138)]
    [InlineData("(line approx 138)", 138)]
    [InlineData("(line 138-150)", 138)]
    [InlineData("(lines 100-150)", 100)]
    [InlineData("(line 138, 142)", 138)]
    [InlineData("(lines 42)", 42)]
    public void Parse_ApproximateLineForms_ExtractsFirstInteger(string lineExpr, int expected)
    {
        var input = $"**[major]** `src/Foo.cs` {lineExpr}: issue text";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Equal(expected, f.LineNumber);
    }

    [Fact]
    public void Parse_LineUnknownLiteral_LineNumberIsNull()
    {
        // Prompt instructs Copilot to write `(line unknown)` when no specific line applies.
        var input = "**[major]** `src/Foo.cs` (line unknown): broad issue";

        var (findings, _) = FindingParser.Parse(input);

        var f = Assert.Single(findings);
        Assert.Null(f.LineNumber);
    }
}
