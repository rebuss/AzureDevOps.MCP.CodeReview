using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tests.Tools.Shared;

public class PlainTextFormatterTests
{
    private static FullPullRequestMetadata Sample() => new()
    {
        PullRequestId = 42,
        Title = "Fix the bug",
        Status = "active",
        AuthorLogin = "user1",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        ChangedFilesCount = 3,
        Additions = 50,
        Deletions = 10,
        CommitShas = new List<string> { "abc123" },
        LastMergeSourceCommitId = "abc123",
        LastMergeTargetCommitId = "def456",
        Description = "desc",
    };

    [Fact]
    public void FormatMetadata_NoPaging_NoDeferredFlag_OmitsContentPagingBlock()
    {
        var text = PlainTextFormatter.FormatMetadata(Sample(), 42);
        Assert.DoesNotContain("Content paging:", text);
        Assert.DoesNotContain("not yet available", text);
    }

    [Fact]
    public void FormatMetadata_PagingDeferred_AppendsExplicitIndicator()
    {
        // FR-004 / T020a — basic-summary fallback path must explicitly tell the
        // agent that paging is not yet available and what to do next.
        var text = PlainTextFormatter.FormatMetadata(Sample(), 42, paging: null, pagingDeferred: true);

        Assert.Contains("PR #42: Fix the bug", text);
        Assert.Contains("Content paging: not yet available", text);
        Assert.Contains("background enrichment is still running", text);
        Assert.Contains("begin_pr_review", text);
    }

    [Fact]
    public void FormatMetadata_PagingPresent_IgnoresDeferredFlag()
    {
        var paging = (1, 2, 140_000, (IReadOnlyList<(int Page, int Count)>)new[] { (1, 2) });
        var text = PlainTextFormatter.FormatMetadata(Sample(), 42, paging, pagingDeferred: true);

        Assert.Contains("Content paging: 1 page(s)", text);
        Assert.DoesNotContain("not yet available", text);
    }

    [Fact]
    public void FormatFriendlyStatus_ProducesExpectedShape()
    {
        var text = PlainTextFormatter.FormatFriendlyStatus(
            headline: "Response is still being prepared",
            explanation: "Background enrichment for PR #42 is still running.",
            suggestedNextAction: "Continue with page 1");

        Assert.Contains("Status: Response is still being prepared", text);
        Assert.Contains("Detail: Background enrichment for PR #42 is still running.", text);
        Assert.Contains("Suggested next: Continue with page 1", text);
    }

    [Theory]
    [InlineData(null, "x", "y")]
    [InlineData("x", null, "y")]
    [InlineData("x", "y", null)]
    [InlineData("", "x", "y")]
    [InlineData("   ", "x", "y")]
    public void FormatFriendlyStatus_RejectsEmptyArgs(string? headline, string? explanation, string? suggested)
    {
        Assert.ThrowsAny<ArgumentException>(() =>
            PlainTextFormatter.FormatFriendlyStatus(headline!, explanation!, suggested!));
    }
}
