using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NSubstitute.ReceivedExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetLocalContentToolHandlerTests
{
    private readonly ILocalReviewProvider _localProvider = Substitute.For<ILocalReviewProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ILocalEnrichmentOrchestrator _enrichmentOrchestrator = Substitute.For<ILocalEnrichmentOrchestrator>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly ICopilotAvailabilityDetector _copilotAvailability = Substitute.For<ICopilotAvailabilityDetector>();
    private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator = Substitute.For<ICopilotReviewOrchestrator>();
    private readonly IProgressReporter _progressReporter = Substitute.For<IProgressReporter>();
    private readonly WorkflowOptions _workflowOpts = new() { ContentInternalTimeoutMs = 28_000, CopilotReviewProgressPollingIntervalMs = 100 };
    private readonly CopilotReviewWaiter _copilotReviewWaiter;
    private readonly GetLocalContentToolHandler _handler;

    private static readonly IReadOnlyList<PackingCandidate> SampleCandidates = new[]
    {
        new PackingCandidate("src/A.cs", 500, FileCategory.Source, 35),
        new PackingCandidate("src/B.cs", 500, FileCategory.Source, 20),
    };

    private static readonly IReadOnlyDictionary<string, string> SampleEnrichedByPath =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["src/A.cs"] = "=== src/A.cs ===\n+new line A",
            ["src/B.cs"] = "=== src/B.cs ===\n+new line B",
        };

    public GetLocalContentToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));

        var pageSlice = new PageSlice(1, 0, 2,
            new[]
            {
                new PageSliceItem(0, PackingItemStatus.Included, 500),
                new PageSliceItem(1, PackingItemStatus.Included, 500)
            },
            1000, 139000);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { pageSlice }, 1, 2));

        // Default: enrichment returns immediately with sample data.
        _enrichmentOrchestrator.TryGetSnapshot(Arg.Any<string>()).Returns((LocalEnrichmentJobSnapshot?)null);
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new LocalEnrichmentResult
            {
                RepositoryRoot = "C:\\Projects\\MyRepo",
                CurrentBranch = "feature/my-branch",
                Scope = "working-tree",
                SortedCandidates = SampleCandidates,
                EnrichedByPath = SampleEnrichedByPath,
                Allocation = new PageAllocation(new[] { pageSlice }, 1, 2),
                SafeBudgetTokens = 140000,
                CompletedAt = DateTimeOffset.UtcNow,
            });

        // Default: Copilot not available (content-only path).
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        _progressReporter.ReportAsync(Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        _copilotReviewWaiter = new CopilotReviewWaiter(
            _copilotReviewOrchestrator,
            _progressReporter,
            Options.Create(_workflowOpts));

        _handler = new GetLocalContentToolHandler(
            _localProvider,
            _budgetResolver,
            _enrichmentOrchestrator,
            _pageAllocator,
            Options.Create(_workflowOpts),
            _copilotAvailability,
            _copilotReviewOrchestrator,
            _copilotReviewWaiter,
            _progressReporter,
            NullLogger<GetLocalContentToolHandler>.Instance);
    }

    private static string AllText(IEnumerable<ContentBlock> blocks) =>
        string.Join("\n", blocks.Cast<TextContentBlock>().Select(b => b.Text));

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_SinglePage_ReturnsAllFiles()
    {
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);

        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;
        Assert.Contains("Page 1 of 1", lastBlock);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesRepositoryRoot()
    {
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var headerText = AllText(blocks);

        Assert.Contains("C:\\Projects\\MyRepo", headerText);
    }

    [Fact]
    public async Task ExecuteAsync_IncludesCurrentBranch()
    {
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var headerText = AllText(blocks);

        Assert.Contains("feature/my-branch", headerText);
    }

    [Fact]
    public async Task ExecuteAsync_DefaultScope_IsWorkingTree()
    {
        await _handler.ExecuteAsync(pageNumber: 1);

        _enrichmentOrchestrator.Received(1).TriggerEnrichment(
            Arg.Is<string>(s => s.Contains("working-tree", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<int>());
    }

    [Fact]
    public async Task ExecuteAsync_StagedScope_Forwarded()
    {
        await _handler.ExecuteAsync(pageNumber: 1, scope: "staged");

        _enrichmentOrchestrator.Received(1).TriggerEnrichment(
            Arg.Is<string>(s => s.Contains("staged", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<int>());
    }

    [Fact]
    public async Task ExecuteAsync_BranchScope_ForwardedAsBaseBranch()
    {
        await _handler.ExecuteAsync(pageNumber: 1, scope: "main");

        _enrichmentOrchestrator.Received(1).TriggerEnrichment(
            Arg.Is<string>(s => s.Contains("main", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<int>());
    }

    [Fact]
    public async Task ExecuteAsync_Summary_HasCorrectValues()
    {
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Page 1 of 1", lastBlock);
        Assert.Contains("hasMore: false", lastBlock);
        Assert.Contains("2/2 files", lastBlock);
        Assert.Contains("~1000 tokens", lastBlock);
    }

    [Fact]
    public async Task ExecuteAsync_Summary_HasCategories()
    {
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
    }

    [Fact]
    public async Task ExecuteAsync_TriggersAndWaitsForEnrichment()
    {
        await _handler.ExecuteAsync(pageNumber: 1);

        _enrichmentOrchestrator.Received(1).TriggerEnrichment(Arg.Any<string>(), Arg.Any<int>());
        await _enrichmentOrchestrator.Received(1).WaitForEnrichmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_NullPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: null));
        Assert.Contains("pageNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: 0));
        Assert.Contains("pageNumber must be >= 1", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PageExceedsTotalPages_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: 99));
        Assert.Contains("exceeds total pages", ex.Message);
    }

    // --- Budget forwarding ---

    [Fact]
    public async Task ExecuteAsync_ForwardsBudgetParams()
    {
        await _handler.ExecuteAsync(pageNumber: 1, modelName: "gpt-4o", maxTokens: 50000);

        _budgetResolver.Received(1).Resolve(50000, "gpt-4o");
    }

    // --- T026: Copilot available -> copilot-assisted header ---

    [Fact]
    public async Task ExecuteAsync_CopilotAvailable_ResponseHasCopilotAssistedHeader()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var copilotResult = new CopilotReviewResult
        {
            ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
            PageReviews = new[]
            {
                CopilotPageReviewResult.Success(1, "Review for page 1: LGTM", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator.WaitForReviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(copilotResult);
        _copilotReviewOrchestrator.TryGetSnapshot(Arg.Any<string>())
            .Returns(new CopilotReviewSnapshot
            {
                ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
                Status = CopilotReviewStatus.Ready,
                TotalPages = 1,
                CompletedPages = 1,
                Result = copilotResult,
            });

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
    }

    // --- T027: Partial Copilot failure -> response includes succeeded and failed blocks ---

    [Fact]
    public async Task ExecuteAsync_PartialCopilotFailure_ResponseIncludesSucceededAndFailedBlocks()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var copilotResult = new CopilotReviewResult
        {
            ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
            PageReviews = new[]
            {
                CopilotPageReviewResult.Success(1, "Page 1 is good", 1),
                CopilotPageReviewResult.Failure(2, new[] { "src/C.cs" }, "timeout", 3),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator.WaitForReviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(copilotResult);
        _copilotReviewOrchestrator.TryGetSnapshot(Arg.Any<string>())
            .Returns(new CopilotReviewSnapshot
            {
                ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
                Status = CopilotReviewStatus.Ready,
                TotalPages = 2,
                CompletedPages = 2,
                Result = copilotResult,
            });

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("1 succeeded", text);
        Assert.Contains("1 failed", text);
        Assert.Contains("Page 1 is good", text);
        Assert.Contains("FAILED", text);
        Assert.Contains("src/C.cs", text);
    }

    // --- T028: pageNumber=3 in copilot mode -> all summaries returned (pageNumber ignored) ---

    [Fact]
    public async Task ExecuteAsync_CopilotMode_PageNumber3_AllSummariesReturned()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var copilotResult = new CopilotReviewResult
        {
            ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
            PageReviews = new[]
            {
                CopilotPageReviewResult.Success(1, "Review page 1", 1),
                CopilotPageReviewResult.Success(2, "Review page 2", 1),
            },
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator.WaitForReviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(copilotResult);
        _copilotReviewOrchestrator.TryGetSnapshot(Arg.Any<string>())
            .Returns(new CopilotReviewSnapshot
            {
                ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
                Status = CopilotReviewStatus.Ready,
                TotalPages = 2,
                CompletedPages = 2,
                Result = copilotResult,
            });

        var blocks = (await _handler.ExecuteAsync(pageNumber: 3)).ToList();
        var text = AllText(blocks);

        // In copilot mode, all pages are returned regardless of pageNumber.
        Assert.Contains("Review page 1", text);
        Assert.Contains("Review page 2", text);
        Assert.Contains("[review-mode: copilot-assisted]", text);
    }

    // --- T031: Progress notifications emitted at each stage ---

    [Fact]
    public async Task ExecuteAsync_ContentOnly_ProgressNotificationsEmitted()
    {
        var progress = Substitute.For<IProgress<ProgressNotificationValue>>();

        await _handler.ExecuteAsync(pageNumber: 1, progress: progress);

        // The handler calls _progressReporter.ReportAsync multiple times.
        await _progressReporter.Received(Quantity.Within(3, 10))
            .ReportAsync(Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- T032: Copilot reviewing 5 pages -> incremental notifications ---

    [Fact]
    public async Task ExecuteAsync_CopilotReview5Pages_IncrementalNotifications()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(true);

        var pageReviews = Enumerable.Range(1, 5)
            .Select(i => CopilotPageReviewResult.Success(i, $"Review page {i}", 1))
            .ToArray();

        var copilotResult = new CopilotReviewResult
        {
            ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
            PageReviews = pageReviews,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _copilotReviewOrchestrator.WaitForReviewAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(copilotResult);
        _copilotReviewOrchestrator.TryGetSnapshot(Arg.Any<string>())
            .Returns(new CopilotReviewSnapshot
            {
                ReviewKey = "local:working-tree:C:\\Projects\\MyRepo",
                Status = CopilotReviewStatus.Ready,
                TotalPages = 5,
                CompletedPages = 5,
                Result = copilotResult,
            });

        var progress = Substitute.For<IProgress<ProgressNotificationValue>>();

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1, progress: progress)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: copilot-assisted]", text);
        Assert.Equal(5, copilotResult.TotalPages);

        // Progress reporter was called multiple times (handler stages + waiter polling).
        await _progressReporter.Received(Quantity.Within(3, 20))
            .ReportAsync(Arg.Any<object?>(), Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // --- T033: No progress token -> completes normally ---

    [Fact]
    public async Task ExecuteAsync_NoProgressToken_CompletesNormally()
    {
        // progress=null is the default, handler should not throw.
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1, progress: null)).ToList();

        Assert.NotEmpty(blocks);
        var text = AllText(blocks);
        Assert.Contains("src/A.cs", text);
    }

    // --- T034: Content-only path verified (content-only header present) ---

    [Fact]
    public async Task ExecuteAsync_ContentOnly_HasContentOnlyModeHeader()
    {
        // Default setup: Copilot not available -> content-only path.
        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var firstBlockText = blocks.Cast<TextContentBlock>().First().Text;

        Assert.Contains("[review-mode: content-only]", firstBlockText);
    }

    // --- T035: Copilot not available -> content-only mode ---

    [Fact]
    public async Task ExecuteAsync_CopilotNotAvailable_FallsBackToContentOnly()
    {
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: content-only]", text);
        Assert.DoesNotContain("[review-mode: copilot-assisted]", text);
    }

    // --- T036: CopilotReview:Enabled = false -> content-only ---

    [Fact]
    public async Task ExecuteAsync_CopilotDisabledByConfig_ContentOnlyMode()
    {
        // When CopilotReview is disabled, IsAvailableAsync returns false.
        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .Returns(false);

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("[review-mode: content-only]", text);
        Assert.DoesNotContain("copilot-assisted", text);
    }

    // --- T037: Timeout -> friendly "still preparing" ---

    [Fact]
    public async Task ExecuteAsync_EnrichmentTimeout_ReturnsFriendlyStillPreparing()
    {
        _enrichmentOrchestrator.WaitForEnrichmentAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var ct = ci.Arg<CancellationToken>();
                throw new OperationCanceledException(ct);
#pragma warning disable CS0162
                return default(LocalEnrichmentResult)!;
#pragma warning restore CS0162
            });

        // Use a very short timeout so the handler's linked CTS fires.
        var shortTimeoutOpts = new WorkflowOptions { ContentInternalTimeoutMs = 1, CopilotReviewProgressPollingIntervalMs = 100 };
        var waiter = new CopilotReviewWaiter(_copilotReviewOrchestrator, _progressReporter, Options.Create(shortTimeoutOpts));
        var handler = new GetLocalContentToolHandler(
            _localProvider,
            _budgetResolver,
            _enrichmentOrchestrator,
            _pageAllocator,
            Options.Create(shortTimeoutOpts),
            _copilotAvailability,
            _copilotReviewOrchestrator,
            waiter,
            _progressReporter,
            NullLogger<GetLocalContentToolHandler>.Instance);

        var blocks = (await handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("still being prepared", text);
    }

    // --- T038: Failure -> friendly failure status ---

    [Fact]
    public async Task ExecuteAsync_FailedSnapshot_ReturnsFriendlyFailureStatus()
    {
        _enrichmentOrchestrator.TryGetSnapshot(Arg.Any<string>())
            .Returns(new LocalEnrichmentJobSnapshot
            {
                Scope = "working-tree",
                Status = LocalEnrichmentStatus.Failed,
                Failure = new LocalEnrichmentFailure
                {
                    ExceptionTypeName = "IOException",
                    SanitizedMessage = "Disk read error",
                },
            });

        var blocks = (await _handler.ExecuteAsync(pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("enrichment failed", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("IOException", text);
        Assert.Contains("Disk read error", text);
    }

    // --- T039: Strict mode -> CopilotUnavailableException propagated ---

    [Fact]
    public async Task ExecuteAsync_StrictMode_CopilotUnavailableExceptionPropagated()
    {
        var verdict = new CopilotVerdict(
            IsAvailable: false,
            Reason: CopilotAuthReason.NotAuthenticated,
            TokenSource: CopilotTokenSource.None,
            ConfiguredModel: "gpt-4o",
            EntitledModels: Array.Empty<string>(),
            Login: null,
            Host: null,
            Remediation: "Run 'gh auth login' to authenticate");

        _copilotAvailability.IsAvailableAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new CopilotUnavailableException(verdict));

        // The handler's inner catch re-throws CopilotUnavailableException, but the
        // outer catch (Exception ex) wraps it in McpException. The remediation message
        // flows through to the McpException message.
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(pageNumber: 1));

        Assert.Contains("gh auth login", ex.Message);
    }
}
