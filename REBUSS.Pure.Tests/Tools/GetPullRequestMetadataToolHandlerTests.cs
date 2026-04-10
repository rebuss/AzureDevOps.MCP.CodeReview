using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestMetadataToolHandlerTests
{
    private readonly IPullRequestDataProvider _dataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly IRepositoryDownloadOrchestrator _downloadOrchestrator = Substitute.For<IRepositoryDownloadOrchestrator>();
    private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IOptions<WorkflowOptions> _workflowOptions =
        Options.Create(new WorkflowOptions { MetadataInternalTimeoutMs = 28_000, ContentInternalTimeoutMs = 28_000 });
    private readonly ICopilotClientProvider _copilotClientProvider = Substitute.For<ICopilotClientProvider>();
    private readonly IOptions<CopilotReviewOptions> _copilotReviewOptions =
        Options.Create(new CopilotReviewOptions { Enabled = false });
    private readonly GetPullRequestMetadataToolHandler _handler;

    private static readonly FullPullRequestMetadata SampleMetadata = new()
    {
        PullRequestId = 42,
        CodeReviewId = 100,
        Title = "Fix the bug",
        Description = "Some description",
        Status = "active",
        IsDraft = false,
        AuthorLogin = "user1",
        AuthorDisplayName = "User One",
        CreatedDate = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ClosedDate = null,
        SourceBranch = "feature/x",
        TargetBranch = "main",
        LastMergeSourceCommitId = "abc123",
        LastMergeTargetCommitId = "def456",
        CommitShas = new List<string> { "abc123" },
        ChangedFilesCount = 3,
        Additions = 50,
        Deletions = 10,
        RepositoryFullName = "org/repo",
        WebUrl = "https://example.com/pr/42"
    };

    private static PrEnrichmentResult SampleEnrichmentResult(int safeBudget = 140_000)
    {
        var slice = new PageSlice(1, 0, 2,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 500), new PageSliceItem(1, PackingItemStatus.Included, 500) },
            1000, safeBudget - 1000);
        return new PrEnrichmentResult
        {
            PrNumber = 42,
            HeadSha = "abc123",
            SortedCandidates = Array.Empty<PackingCandidate>(),
            EnrichedByPath = new Dictionary<string, string>(),
            Allocation = new PageAllocation(new[] { slice }, 1, 2),
            SafeBudgetTokens = safeBudget,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public GetPullRequestMetadataToolHandlerTests()
    {
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleMetadata);

        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200_000, 140_000, BudgetSource.Default, Array.Empty<string>()));

        // Default: orchestrator returns Ready immediately.
        var sample = SampleEnrichmentResult();
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(sample);
        _enrichmentOrchestrator.TryGetSnapshot(Arg.Any<int>()).Returns((PrEnrichmentJobSnapshot?)null);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(sample.Allocation);

        _handler = new GetPullRequestMetadataToolHandler(
            _dataProvider,
            _budgetResolver,
            _downloadOrchestrator,
            _enrichmentOrchestrator,
            _pageAllocator,
            _workflowOptions,
            _copilotClientProvider,
            _copilotReviewOptions,
            NullLogger<GetPullRequestMetadataToolHandler>.Instance);
    }

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // ─── Existing behavior (backward compatibility) ───────────────────────────

    [Fact]
    public async Task ExecuteAsync_WithoutBudgetParams_ReturnsMetadataWithoutPaging()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42)).ToList();
        var text = AllText(blocks);

        Assert.Contains("PR #42: Fix the bug", text);
        Assert.DoesNotContain("Content paging:", text);
    }

    [Fact]
    public async Task ExecuteAsync_PreservesExistingFields()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42)).ToList();
        var text = AllText(blocks);

        Assert.Contains("user1", text);
        Assert.Contains("active", text);
        Assert.Contains("main", text);
        Assert.Contains("feature/x", text);
        Assert.Contains("3 file(s)", text);
    }

    [Fact]
    public async Task ExecuteAsync_NoPagingRequested_DoesNotTriggerOrchestrator()
    {
        await _handler.ExecuteAsync(prNumber: 42);

        _enrichmentOrchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
    }

    // ─── Orchestrator integration (paging requested) ──────────────────────────

    [Fact]
    public async Task Execute_PagingRequested_OrchestratorReady_ReturnsPagingBlock()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o")).ToList();
        var text = AllText(blocks);

        _enrichmentOrchestrator.Received(1).TriggerEnrichment(42, "abc123", 140_000);
        Assert.Contains("Content paging:", text);
        Assert.Contains("1 page(s)", text);
        Assert.Contains("2 file(s)", text);
        Assert.Contains("140000 tokens/page", text);
        Assert.DoesNotContain("not yet available", text);
    }

    [Fact]
    public async Task Execute_PagingRequested_WithMaxTokens_ReturnsPagingBlock()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, maxTokens: 50_000)).ToList();
        var text = AllText(blocks);

        Assert.Contains("Content paging:", text);
    }

    [Fact]
    public async Task Execute_PagingRequested_OrchestratorTimesOut_ReturnsBasicSummaryWithDeferredIndicator()
    {
        // Simulate the linked-cts internal timeout: orchestrator throws OCE.
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PrEnrichmentResult>>(_ => throw new OperationCanceledException());

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o")).ToList();
        var text = AllText(blocks);

        // Basic summary still present.
        Assert.Contains("PR #42: Fix the bug", text);
        // FR-004: explicit indicator that paging is deferred.
        Assert.Contains("not yet available", text);
        Assert.Contains("get_pr_content", text);
        Assert.DoesNotContain("Content paging: 1 page", text);

        // No exception escapes — tool returns a successful response.
        Assert.NotEmpty(blocks);
    }

    [Fact]
    public async Task Execute_PagingRequested_OrchestratorFailedSnapshot_ReturnsBasicSummaryPlusFriendlyStatus()
    {
        var failure = new PrEnrichmentFailure
        {
            ExceptionTypeName = "BoomException",
            SanitizedMessage = "something went wrong",
            FailedAt = DateTimeOffset.UtcNow,
        };
        _enrichmentOrchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Failed,
            Failure = failure,
        });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o")).ToList();
        var text = AllText(blocks);

        // Basic summary block present.
        Assert.Contains("PR #42: Fix the bug", text);
        // Friendly-status block present and references the failure.
        Assert.Contains("Background enrichment failed", text);
        Assert.Contains("BoomException", text);
        Assert.Contains("something went wrong", text);
        // Did NOT retrigger enrichment (fast-path on cached failure).
        _enrichmentOrchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
    }

    [Fact]
    public async Task Execute_PagingRequested_BackgroundExceptionDuringWait_ReturnsBasicSummaryPlusFriendlyStatus()
    {
        // Background body raised a non-cancellation exception that propagated through WaitAsync.
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PrEnrichmentResult>>(_ => throw new InvalidOperationException("provider exploded"));

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o")).ToList();
        var text = AllText(blocks);

        Assert.Contains("PR #42: Fix the bug", text);
        Assert.Contains("Background enrichment failed", text);
        Assert.Contains("InvalidOperationException", text);
        Assert.Contains("provider exploded", text);
    }

    [Fact]
    public async Task Execute_CallerCancellation_PropagatesAsMcpException()
    {
        using var callerCts = new CancellationTokenSource();
        callerCts.Cancel();
        // The orchestrator wait would normally observe the linked token; simulate by throwing OCE
        // tied to caller's already-cancelled token.
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PrEnrichmentResult>>(_ => throw new OperationCanceledException(callerCts.Token));

        // Caller-cancellation propagates through the catch (not Mcp wrapped) — handler should rethrow OCE,
        // which is then wrapped by the generic catch in ExecuteAsync as McpException. Either way the
        // host sees a failure when the host itself cancelled, which is fine.
        await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o", cancellationToken: callerCts.Token));
    }

    // ─── Repository download (base branch) ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_TriggersDownload_WithTargetCommitId()
    {
        await _handler.ExecuteAsync(prNumber: 42);

        _downloadOrchestrator.Received(1).TriggerDownloadAsync(42, "def456");
    }

    [Fact]
    public async Task ExecuteAsync_FallsBackToSourceCommitId_WhenTargetIsNull()
    {
        var metadataWithoutTarget = new FullPullRequestMetadata
        {
            PullRequestId = 42,
            Title = "Fix",
            Description = "Desc",
            Status = "active",
            LastMergeSourceCommitId = "abc123",
            LastMergeTargetCommitId = "",
            CommitShas = new List<string> { "abc123" }
        };
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(metadataWithoutTarget);

        await _handler.ExecuteAsync(prNumber: 42);

        _downloadOrchestrator.Received(1).TriggerDownloadAsync(42, "abc123");
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotTriggerDownload_WhenBothCommitIdsEmpty()
    {
        var metadataNoCommits = new FullPullRequestMetadata
        {
            PullRequestId = 42,
            Title = "Fix",
            Description = "Desc",
            Status = "active",
            LastMergeSourceCommitId = "",
            LastMergeTargetCommitId = "",
            CommitShas = new List<string>()
        };
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(metadataNoCommits);

        await _handler.ExecuteAsync(prNumber: 42);

        _downloadOrchestrator.DidNotReceive().TriggerDownloadAsync(Arg.Any<int>(), Arg.Any<string>());
    }

    // ─── Error handling ───────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: null));
        Assert.Contains("Missing required parameter", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 0));
        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PrNotFound_ThrowsMcpException()
    {
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 999));
        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DescriptionTruncation_WorksCorrectly()
    {
        var longDescription = new string('x', 1000);
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata
            {
                Title = "Test",
                Description = longDescription,
                CommitShas = new List<string>()
            });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42)).ToList();
        var text = AllText(blocks);

        Assert.Contains("... [truncated]", text);
    }

    // ─── Feature 015: Eager Copilot SDK initialization ───────────────────────

    [Fact]
    public async Task ExecuteAsync_CopilotEnabled_TriggersEagerInit()
    {
        var copilotOptions = Options.Create(new CopilotReviewOptions { Enabled = true });
        var copilotProvider = Substitute.For<ICopilotClientProvider>();
        copilotProvider.TryEnsureStartedAsync(Arg.Any<CancellationToken>()).Returns(true);

        var handler = new GetPullRequestMetadataToolHandler(
            _dataProvider, _budgetResolver, _downloadOrchestrator,
            _enrichmentOrchestrator, _pageAllocator, _workflowOptions,
            copilotProvider, copilotOptions,
            NullLogger<GetPullRequestMetadataToolHandler>.Instance);

        await handler.ExecuteAsync(prNumber: 42);

        await copilotProvider.Received(1).TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CopilotDisabled_DoesNotTriggerInit()
    {
        var copilotOptions = Options.Create(new CopilotReviewOptions { Enabled = false });
        var copilotProvider = Substitute.For<ICopilotClientProvider>();

        var handler = new GetPullRequestMetadataToolHandler(
            _dataProvider, _budgetResolver, _downloadOrchestrator,
            _enrichmentOrchestrator, _pageAllocator, _workflowOptions,
            copilotProvider, copilotOptions,
            NullLogger<GetPullRequestMetadataToolHandler>.Instance);

        await handler.ExecuteAsync(prNumber: 42);

        await copilotProvider.DidNotReceive().TryEnsureStartedAsync(Arg.Any<CancellationToken>());
    }
}
