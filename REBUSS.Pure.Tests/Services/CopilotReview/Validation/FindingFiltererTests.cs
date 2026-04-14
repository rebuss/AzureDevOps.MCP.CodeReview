using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingFilterer"/>. Feature 021.</summary>
public class FindingFiltererTests
{
    private static ParsedFinding MakeFinding(int idx, string text) => new()
    {
        Index = idx,
        FilePath = "src/A.cs",
        LineNumber = 10,
        Severity = "major",
        Description = $"issue {idx}",
        OriginalText = text,
    };

    [Fact]
    public void Apply_AllValid_KeepsAllFindingsAndAppendsSummary()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "finding A"), Verdict = FindingVerdict.Valid },
            new ValidatedFinding { Finding = MakeFinding(1, "finding B"), Verdict = FindingVerdict.Valid },
        };

        var result = FindingFilterer.Apply("", validated);

        Assert.Contains("finding A", result);
        Assert.Contains("finding B", result);
        Assert.Contains("_Validation: 2 confirmed, 0 filtered, 0 uncertain_", result);
    }

    [Fact]
    public void Apply_RemovesFalsePositives()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "real bug"), Verdict = FindingVerdict.Valid },
            new ValidatedFinding { Finding = MakeFinding(1, "fake bug"), Verdict = FindingVerdict.FalsePositive },
        };

        var result = FindingFilterer.Apply("", validated);

        Assert.Contains("real bug", result);
        Assert.DoesNotContain("fake bug", result);
        Assert.Contains("1 confirmed, 1 filtered, 0 uncertain", result);
    }

    [Fact]
    public void Apply_TagsUncertainWithPrefix()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "needs review"), Verdict = FindingVerdict.Uncertain },
        };

        var result = FindingFilterer.Apply("", validated);

        Assert.Contains("[uncertain] needs review", result);
        Assert.Contains("0 confirmed, 0 filtered, 1 uncertain", result);
    }

    [Fact]
    public void Apply_AllFilteredNoRemainder_ReturnsNoIssuesFound()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "fake 1"), Verdict = FindingVerdict.FalsePositive },
            new ValidatedFinding { Finding = MakeFinding(1, "fake 2"), Verdict = FindingVerdict.FalsePositive },
        };

        var result = FindingFilterer.Apply("", validated);

        Assert.Contains("No issues found.", result);
        // Summary still shows even when everything was filtered.
        Assert.Contains("0 confirmed, 2 filtered, 0 uncertain", result);
    }

    [Fact]
    public void Apply_UnparseableRemainderPreservedVerbatim()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "real bug"), Verdict = FindingVerdict.Valid },
        };

        var result = FindingFilterer.Apply("## Critical Issues\nIntro prose.\n", validated);

        Assert.Contains("## Critical Issues", result);
        Assert.Contains("Intro prose.", result);
        Assert.Contains("real bug", result);
    }

    [Fact]
    public void Apply_NoFindings_NoFooterAppended()
    {
        var result = FindingFilterer.Apply("Some review text.", Array.Empty<ValidatedFinding>());

        // SC-005: no footer when there are zero parseable findings.
        Assert.DoesNotContain("_Validation:", result);
        Assert.Contains("Some review text.", result);
    }

    [Fact]
    public void Apply_MixedVerdicts_CountsAreCorrect()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "a"), Verdict = FindingVerdict.Valid },
            new ValidatedFinding { Finding = MakeFinding(1, "b"), Verdict = FindingVerdict.Valid },
            new ValidatedFinding { Finding = MakeFinding(2, "c"), Verdict = FindingVerdict.Valid },
            new ValidatedFinding { Finding = MakeFinding(3, "d"), Verdict = FindingVerdict.FalsePositive },
            new ValidatedFinding { Finding = MakeFinding(4, "e"), Verdict = FindingVerdict.FalsePositive },
            new ValidatedFinding { Finding = MakeFinding(5, "f"), Verdict = FindingVerdict.Uncertain },
        };

        var result = FindingFilterer.Apply("", validated);

        Assert.Contains("_Validation: 3 confirmed, 2 filtered, 1 uncertain_", result);
    }

    [Fact]
    public void Apply_FooterHasExpectedFormat()
    {
        var validated = new[]
        {
            new ValidatedFinding { Finding = MakeFinding(0, "a"), Verdict = FindingVerdict.Valid },
        };

        var result = FindingFilterer.Apply("", validated);

        // Exact format check (italic markdown underscores + separator).
        Assert.Contains("---", result);
        Assert.EndsWith("_Validation: 1 confirmed, 0 filtered, 0 uncertain_", result);
    }
}
