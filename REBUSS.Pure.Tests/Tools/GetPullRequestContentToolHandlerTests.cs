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
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools;
using REBUSS.Pure.Services.CopilotReview;
using CopilotUnavailableException = global::REBUSS.Pure.Services.CopilotReview.CopilotUnavailableException;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestContentToolHandlerTests
{
    private readonly IPullRequestDataProvider _metadataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly IPrEnrichmentOrchestrator _orchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly IOptions<WorkflowOptions> _workflowOptions =
        Options.Create(new WorkflowOptions
        {
            MetadataInternalTimeoutMs = 28_000,
            ContentInternalTimeoutMs = 28_000,
            CopilotReviewProgressPollingIntervalMs = 50,
        });
    private readonly ICopilotAvailabilityDetector _copilotAvailability = Substitute.For<ICopilotAvailabilityDetector>();
    private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator = Substitute.For<ICopilotReviewOrchestrator>();
    private readonly IProgressReporter _progressReporter = Substitute.For<IProgressReporter>();
    private readonly CopilotReviewWaiter _copilotReviewWaiter;
    private readonly GetPullRequestContentToolHandler _handler;

    private static readonly FullPullRequestMetadata SampleMetadata = new()
    {
        PullRequestId = 42,
        Title = "Fix bug",
        Status = "active",
        LastMergeSourceCommitId = "abc123",
        LastMergeTargetCommitId = "def456",
        CommitShas = new List<string> { "abc123" },
    };

    private static PrEnrichmentResult BuildResult(int safeBudget = 140_000)
    {
        var candidates = new[]
        {
            new PackingCandidate("src/A.cs", 500, FileCategory.Source, 35),
            new PackingCandidate("src/B.cs", 500, FileCategory.Source, 25),
            new PackingCandidate("docs/README.md", 500, FileCategory.Docs, 4),
        };
        var enrichedByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/A.cs"] = "=== src/A.cs ===\n+new line",
            ["src/B.cs"] = "=== src/B.cs ===\n+another line",
            ["docs/README.md"] = "=== docs/README.md ===\n+doc line",
        };

        var slice = new PageSlice(1, 0, 3,
            Enumerable.Range(0, 3).Select(i => new PageSliceItem(i, PackingItemStatus.Included, 500)).ToArray(),
            1500, safeBudget - 1500);

        return new PrEnrichmentResult
        {
            PrNumber = 42,
            HeadSha = "abc123",
            SortedCandidates = candidates,
            EnrichedByPath = enrichedByPath,
            Allocation = new PageAllocation(new[] { slice }, 1, 3),
            SafeBudgetTokens = safeBudget,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    private static Core.Models.CopilotReview.CopilotReviewResult BuildDefaultCopilotResult() => new()
    {
        ReviewKey = "pr:42",
        PageReviews = new[]
        {
            Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "no issues found", 1),
        },
        CompletedAt = DateTimeOffset.UtcNow,
    };

    public GetPullRequestContentToolHandlerTests()
    {
        _metadataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(SampleMetadata);
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200_000, 140_000, BudgetSource.Default, Array.Empty<string>()));

        // Default: no prior snapshot — handler will trigger enrichment, then await.
        _orchestrator.TryGetSnapshot(Arg.Any<int>()).Returns((PrEnrichmentJobSnapshot?)null);
        var defaultResult = BuildResult();
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(defaultResult);

        // Default: Copilot available — the handler now requires Copilot.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);

        // Default copilot review result.
        _copilotReviewOrchestrator
            .WaitForReviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(BuildDefaultCopilotResult());

        _copilotReviewWaiter = new CopilotReviewWaiter(
            _copilotReviewOrchestrator,
            _progressReporter,
            _workflowOptions);

        _handler = new GetPullRequestContentToolHandler(
            _metadataProvider,
            _budgetResolver,
            _orchestrator,
            _workflowOptions,
            _copilotAvailability,
            _copilotReviewOrchestrator,
            _copilotReviewWaiter,
            _progressReporter,
            NullLogger<GetPullRequestContentToolHandler>.Instance);
    }

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // ─── Cold-start path: no prior snapshot ───────────────────────────────────

    [Fact]
    public async Task Execute_NoSnapshot_TriggersAndAwaits()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        await _metadataProvider.Received(1).GetMetadataAsync(42, Arg.Any<CancellationToken>());
        _orchestrator.Received(1).TriggerEnrichment(42, "abc123", 140_000);
        await _orchestrator.Received(1).WaitForEnrichmentAsync(42, Arg.Any<CancellationToken>());
        Assert.Contains("[review-mode: copilot-assisted]", text);
    }

    // ─── Copilot-assisted branch tests ──────────────────────────────────────

    [Fact]
    public async Task Execute_CopilotAvailable_ReturnsReviewSummariesWithModeHeader()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "no issues found", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "minor: fix typo in comment", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("PR #42", text);
        Assert.Contains("2 pages reviewed", text);
        Assert.Contains("2 succeeded", text);
        Assert.Contains("=== Page 1 Review ===", text);
        Assert.Contains("no issues found", text);
        Assert.Contains("=== Page 2 Review ===", text);
        Assert.DoesNotContain("[review-mode: content-only]", text);
        _copilotReviewOrchestrator.Received(1).TriggerReview("pr:42", Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_PageNumberIgnoredInCopilotMode_ReturnsAllSummaries()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "page 1 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "page 2 review", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(3, "page 3 review", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        // Caller requests pageNumber=2 — should be ignored, all pages returned in copilot mode.
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2)).ToList();
        var text = AllText(blocks);

        Assert.Contains("page 1 review", text);
        Assert.Contains("page 2 review", text);
        Assert.Contains("page 3 review", text);
    }

    // ─── Partial + all-failed tool handler tests ──

    [Fact]
    public async Task Execute_CopilotPartialFailure_ResponseIncludesFailureBlocksWithFilePaths()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "all good on page 1", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    2, new[] { "src/Failing.cs", "src/AlsoFailing.cs" }, "network timeout", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("2 pages reviewed", text);
        Assert.Contains("1 succeeded", text);
        Assert.Contains("1 failed", text);
        Assert.Contains("=== Page 1 Review ===", text);
        Assert.Contains("all good on page 1", text);
        Assert.Contains("=== Page 2 Review (FAILED) ===", text);
        Assert.Contains("src/Failing.cs", text);
        Assert.Contains("src/AlsoFailing.cs", text);
        Assert.Contains("network timeout", text);
    }

    [Fact]
    public async Task Execute_CopilotAllPagesFailed_ResponseStillReturnsCopilotAssistedHeader()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    1, new[] { "src/A.cs" }, "down", 3),
                Core.Models.CopilotReview.CopilotPageReviewResult.Failure(
                    2, new[] { "src/B.cs" }, "down", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.DoesNotContain("[review-mode: content-only]", text);
        Assert.Contains("0 succeeded", text);
        Assert.Contains("2 failed", text);
        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
    }

    // ─── Wait timeout — friendly status ───────────────────────────────────────

    [Fact]
    public async Task Execute_WaitTimesOut_ReturnsFriendlyStatusBlock()
    {
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns<Task<PrEnrichmentResult>>(_ => throw new OperationCanceledException());

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Single(blocks);
        Assert.Contains("Status: Response is still being prepared", text);
        Assert.Contains("PR #42", text);
        Assert.Contains("Retry get_pr_content", text);
    }

    // ─── Failed snapshot — friendly status, no retrigger ──────────────────────

    [Fact]
    public async Task Execute_FailedSnapshot_ReturnsFriendlyFailureBlock()
    {
        var failure = new PrEnrichmentFailure
        {
            ExceptionTypeName = "BoomException",
            SanitizedMessage = "diff fetch exploded",
            FailedAt = DateTimeOffset.UtcNow,
        };
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Failed,
            Failure = failure,
        });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Single(blocks);
        Assert.Contains("Background enrichment failed", text);
        Assert.Contains("BoomException", text);
        Assert.Contains("diff fetch exploded", text);
        // No retrigger and no metadata fetch on Failed fast-path.
        _orchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
        await _metadataProvider.DidNotReceiveWithAnyArgs().GetMetadataAsync(0, default);
    }

    // ─── Validation + error handling ──────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: null, pageNumber: 1));
        Assert.Contains("prNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PrNotFound_ThrowsMcpException()
    {
        _metadataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("Not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 999, pageNumber: 1));
        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ForwardsBudgetParams()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, modelName: "gpt-4o", maxTokens: 50_000);

        _budgetResolver.Received(1).Resolve(50_000, "gpt-4o");
    }

    // ─── Copilot not available — throws McpException ─────────────────────────

    [Fact]
    public async Task ExecuteAsync_CopilotNotAvailable_ThrowsMcpException()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(false);

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 1));

        Assert.Contains("gh copilot", ex.Message);
        Assert.Contains("CopilotReview:Enabled", ex.Message);
    }

    // ─── pageNumber ignored — copilot response returned ──────────────────────

    [Fact]
    public async Task ExecuteAsync_PageNumber5_CopilotAvailable_ReturnsCopilotResponse()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "page 1 looks good", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "page 2 looks good", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        // pageNumber=5 is accepted and ignored — all copilot review pages returned.
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 5)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Contains("page 1 looks good", text);
        Assert.Contains("page 2 looks good", text);
    }

    // ─── Progress notifications ─────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReportsProgress_AtLeast3Times()
    {
        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        // At least 3 calls: start, intermediate, completion (FR-001, SC-001)
        var calls = _progressReporter.ReceivedCalls()
            .Count(c => c.GetMethodInfo().Name == nameof(IProgressReporter.ReportAsync));
        Assert.True(calls >= 3, $"Expected at least 3 progress reports, got {calls}");
    }

    [Fact]
    public async Task ExecuteAsync_ProgressMessages_AreShortAndContainPosition()
    {
        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        // SC-002: messages ≤80 chars
        Assert.All(capturedMessages, m =>
            Assert.True(m.Length <= 80, $"Message exceeds 80 chars: \"{m}\" ({m.Length} chars)"));

        // SC-002: at least one message contains a position indicator (e.g. PR number or step context)
        Assert.Contains(capturedMessages, m =>
            m.Contains("PR #", StringComparison.Ordinal));
    }

    // ─── Copilot review progress notifications ──────────────────────────────

    [Fact]
    public async Task Execute_CopilotAvailable_SendsCopilotReviewStartedProgress()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);
        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "ok", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(copilotResult);

        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1);

        Assert.Contains(capturedMessages, m =>
            m.Contains("Copilot review started", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Execute_CopilotReviewInProgress_ReportsPageLevelProgress()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>()).Returns(true);

        var copilotResult = new Core.Models.CopilotReview.CopilotReviewResult
        {
            ReviewKey = "pr:42",
            PageReviews = new[]
            {
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(1, "review page 1", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(2, "review page 2", 1),
                Core.Models.CopilotReview.CopilotPageReviewResult.Success(3, "review page 3", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };

        // WaitForReviewAsync completes after a short delay to allow the polling loop to run.
        var completionTcs = new TaskCompletionSource<Core.Models.CopilotReview.CopilotReviewResult>();
        _copilotReviewOrchestrator
            .WaitForReviewAsync("pr:42", Arg.Any<CancellationToken>())
            .Returns(completionTcs.Task);

        // Simulate progress snapshots: pages completing over time.
        // Signal fires once the polling loop has called TryGetSnapshot at least once.
        var snapshotCallCount = 0;
        var snapshotCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _copilotReviewOrchestrator.TryGetSnapshot("pr:42")
            .Returns(_ =>
            {
                snapshotCallCount++;
                snapshotCalled.TrySetResult();
                return new Core.Models.CopilotReview.CopilotReviewSnapshot
                {
                    ReviewKey = "pr:42",
                    Status = Core.Models.CopilotReview.CopilotReviewStatus.Reviewing,
                    TotalPages = 3,
                    CompletedPages = Math.Min(snapshotCallCount, 3),
                };
            });

        var capturedMessages = new List<string>();
        _progressReporter.ReportAsync(
            Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(),
            Arg.Do<string>(m => capturedMessages.Add(m)), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        // Complete the review only after the polling loop has iterated at least once.
        // Retain the helper handle (don't discard with `_ =`): if the polling loop never
        // runs — because of a bug in ExecuteAsync — the helper would hang forever along
        // with the handler. Bounded WaitAsync turns such regressions into fail-fast
        // TimeoutExceptions, and awaiting the helper at the end surfaces any exceptions
        // that would otherwise be silently swallowed by the fire-and-forget task.
        var completionHelper = Task.Run(async () =>
        {
            await snapshotCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completionTcs.SetResult(copilotResult);
        });

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)
            .WaitAsync(TimeSpan.FromSeconds(10))).ToList();

        // Surface any helper-task exception (and guarantee it has completed).
        await completionHelper.WaitAsync(TimeSpan.FromSeconds(2));

        // Verify page-level progress was reported (at least one "K/3 pages complete" message).
        Assert.Contains(capturedMessages, m =>
            m.Contains("pages complete", StringComparison.OrdinalIgnoreCase) &&
            m.Contains("/3", StringComparison.Ordinal));
    }

    // ─── Strict-mode CopilotUnavailableException handling ───

    private static CopilotVerdict StrictModeVerdict() => new(
        IsAvailable: false,
        Reason: CopilotAuthReason.NotAuthenticated,
        TokenSource: CopilotTokenSource.LoggedInUser,
        ConfiguredModel: "claude-sonnet-4.6",
        EntitledModels: Array.Empty<string>(),
        Login: null,
        Host: null,
        Remediation: "Run 'gh auth login' with Copilot scopes.");

    [Fact]
    public async Task Execute_StrictModeCopilotUnavailable_SurfacesAsMcpErrorWithRemediation()
    {
        var verdict = StrictModeVerdict();
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new CopilotUnavailableException(verdict));

        var ex = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 1));

        Assert.True(
            ex.Message.Contains(verdict.Remediation, StringComparison.Ordinal)
                || ex.InnerException is CopilotUnavailableException { Verdict.Remediation: var r } && r == verdict.Remediation,
            $"Expected the McpException to carry the remediation string. Got: {ex.Message}");
        _copilotReviewOrchestrator.DidNotReceive().TriggerReview(Arg.Any<string>(), Arg.Any<IEnrichmentResult>());
    }

    [Fact]
    public async Task Execute_UnrelatedExceptionFromIsAvailableAsync_StillPropagates()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("unrelated failure"));

        await Assert.ThrowsAnyAsync<Exception>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 1));
    }
}
