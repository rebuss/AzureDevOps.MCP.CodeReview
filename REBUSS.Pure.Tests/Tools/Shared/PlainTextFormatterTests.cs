using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
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
        Assert.Contains("get_pr_content", text);
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

    // ─── Feature 013 Copilot review formatter tests (T030) ───────────────────────

    [Fact]
    public void FormatCopilotReviewHeader_ContainsModeAndCounts()
    {
        var text = PlainTextFormatter.FormatCopilotReviewHeader(
            prNumber: 42, totalPages: 5, succeeded: 4, failed: 1);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("PR #42", text);
        Assert.Contains("5 pages", text);
        Assert.Contains("4 succeeded", text);
        Assert.Contains("1 failed", text);
    }

    [Fact]
    public void FormatCopilotPageReviewBlock_Success_ContainsReviewText()
    {
        var result = CopilotPageReviewResult.Success(
            pageNumber: 3, reviewText: "Found a null ref bug", attemptsMade: 1);

        var text = PlainTextFormatter.FormatCopilotPageReviewBlock(result);

        Assert.Contains("=== Page 3 Review ===", text);
        Assert.Contains("Found a null ref bug", text);
        Assert.DoesNotContain("FAILED", text);
    }

    [Fact]
    public void FormatCopilotPageReviewBlock_Failure_ContainsFailedFilePathsAndReason()
    {
        var result = CopilotPageReviewResult.Failure(
            pageNumber: 4,
            failedFilePaths: new[] { "src/A.cs", "src/B.cs" },
            errorMessage: "network timeout",
            attemptsMade: 3);

        var text = PlainTextFormatter.FormatCopilotPageReviewBlock(result);

        Assert.Contains("=== Page 4 Review (FAILED) ===", text);
        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
        Assert.Contains("Reason: network timeout", text);
    }

    [Fact]
    public void FormatContentOnlyModeHeader_ContainsContentOnlyLabel()
    {
        var text = PlainTextFormatter.FormatContentOnlyModeHeader();
        Assert.Equal("[review-mode: content-only]", text);
    }

    // ─── T029: FormatCopilotReviewHeader string overload tests ──────────────────

    [Fact]
    public void FormatCopilotReviewHeader_StringOverload_ContainsModeAndCounts()
    {
        var text = PlainTextFormatter.FormatCopilotReviewHeader(
            reviewSubject: "Local changes (working-tree)",
            totalPages: 3, succeeded: 2, failed: 1);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("Local changes (working-tree)", text);
        Assert.Contains("3 pages", text);
        Assert.Contains("2 succeeded", text);
        Assert.Contains("1 failed", text);
    }

    [Fact]
    public void FormatCopilotReviewHeader_StringOverload_AllSucceeded_ZeroFailed()
    {
        var text = PlainTextFormatter.FormatCopilotReviewHeader(
            reviewSubject: "Local changes (staged)",
            totalPages: 2, succeeded: 2, failed: 0);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("Local changes (staged)", text);
        Assert.Contains("2 pages", text);
        Assert.Contains("0 failed", text);
    }

    [Fact]
    public void FormatCopilotReviewHeader_StringOverload_DifferentSubjectThanPrNumber()
    {
        var text = PlainTextFormatter.FormatCopilotReviewHeader(
            reviewSubject: "Local changes (main)",
            totalPages: 1, succeeded: 1, failed: 0);

        // Should NOT contain "PR #" since we used a string subject.
        Assert.DoesNotContain("PR #", text);
        Assert.Contains("Local changes (main)", text);
    }

    [Fact]
    public void FormatCopilotReviewHeader_IntOverload_ContainsPrPrefix()
    {
        var text = PlainTextFormatter.FormatCopilotReviewHeader(
            prNumber: 99, totalPages: 1, succeeded: 1, failed: 0);

        Assert.Contains("PR #99", text);
    }
}
