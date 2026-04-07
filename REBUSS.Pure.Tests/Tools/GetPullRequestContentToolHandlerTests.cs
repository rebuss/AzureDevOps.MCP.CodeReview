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
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestContentToolHandlerTests
{
    private readonly IPullRequestDataProvider _metadataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly IPrEnrichmentOrchestrator _orchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly IOptions<WorkflowOptions> _workflowOptions =
        Options.Create(new WorkflowOptions { MetadataInternalTimeoutMs = 28_000, ContentInternalTimeoutMs = 28_000 });
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

    private static PrEnrichmentResult BuildResult(
        int safeBudget = 140_000,
        int totalPages = 1,
        int totalItems = 3)
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

        PageAllocation allocation;
        if (totalPages == 1)
        {
            var slice = new PageSlice(1, 0, totalItems,
                Enumerable.Range(0, totalItems).Select(i => new PageSliceItem(i, PackingItemStatus.Included, 500)).ToArray(),
                500 * totalItems, safeBudget - 500 * totalItems);
            allocation = new PageAllocation(new[] { slice }, 1, totalItems);
        }
        else
        {
            var slice1 = new PageSlice(1, 0, 2,
                new[] { new PageSliceItem(0, PackingItemStatus.Included, 500), new PageSliceItem(1, PackingItemStatus.Included, 500) },
                1000, safeBudget - 1000);
            var slice2 = new PageSlice(2, 2, 3,
                new[] { new PageSliceItem(2, PackingItemStatus.Included, 500) },
                500, safeBudget - 500);
            allocation = new PageAllocation(new[] { slice1, slice2 }, 2, 3);
        }

        return new PrEnrichmentResult
        {
            PrNumber = 42,
            HeadSha = "abc123",
            SortedCandidates = candidates,
            EnrichedByPath = enrichedByPath,
            Allocation = allocation,
            SafeBudgetTokens = safeBudget,
            CompletedAt = DateTimeOffset.UtcNow,
        };
    }

    public GetPullRequestContentToolHandlerTests()
    {
        _metadataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(SampleMetadata);
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200_000, 140_000, BudgetSource.Default, Array.Empty<string>()));

        // Default: no prior snapshot — handler will trigger enrichment, then await.
        _orchestrator.TryGetSnapshot(Arg.Any<int>()).Returns((PrEnrichmentJobSnapshot?)null);
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(BuildResult());

        _handler = new GetPullRequestContentToolHandler(
            _metadataProvider,
            _budgetResolver,
            _orchestrator,
            _workflowOptions,
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
        Assert.Contains("src/A.cs", text);
    }

    [Fact]
    public async Task Execute_NoSnapshot_ReturnsAllFilesOnSinglePage()
    {
        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        Assert.Contains("src/A.cs", text);
        Assert.Contains("src/B.cs", text);
        Assert.Contains("docs/README.md", text);
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;
        Assert.Contains("Page 1 of 1", lastBlock);
    }

    // ─── Warm path: snapshot already Ready ────────────────────────────────────

    [Fact]
    public async Task Execute_SnapshotReady_ServesPageWithoutNewMetadataLookup()
    {
        var result = BuildResult();
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Ready,
            Result = result,
        });
        // WaitForEnrichmentAsync still has to return the same Ready result.
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(result);

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var text = AllText(blocks);

        // Cold-start metadata lookup should NOT happen on warm path.
        await _metadataProvider.DidNotReceiveWithAnyArgs().GetMetadataAsync(0, default);
        // No retrigger when budget matches.
        _orchestrator.DidNotReceiveWithAnyArgs().TriggerEnrichment(0, null!, 0);
        Assert.Contains("src/A.cs", text);
    }

    [Fact]
    public async Task Execute_BudgetMismatch_RetriggersEnrichment()
    {
        // Cached result was built for safeBudget = 70_000 but current call resolves to 140_000.
        var staleResult = BuildResult(safeBudget: 70_000);
        _orchestrator.TryGetSnapshot(42).Returns(new PrEnrichmentJobSnapshot
        {
            PrNumber = 42,
            HeadSha = "abc123",
            Status = PrEnrichmentStatus.Ready,
            Result = staleResult,
        });
        // The retrigger happens, and the next wait returns a fresh result.
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(BuildResult(safeBudget: 140_000));

        await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1, modelName: "gpt-4o");

        _orchestrator.Received(1).TriggerEnrichment(42, "abc123", 140_000);
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
        Assert.Contains("Retry get_pr_content with pageNumber=1", text);
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

    // ─── Multipage paging ─────────────────────────────────────────────────────

    [Fact]
    public async Task Execute_MultiPage_ReturnsRequestedPage()
    {
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(BuildResult(totalPages: 2, totalItems: 3));

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 2)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("Page 2 of 2", lastBlock);
        Assert.Contains("hasMore: false", lastBlock);
    }

    [Fact]
    public async Task Execute_MultiPage_HasMoreOnFirstPage()
    {
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(BuildResult(totalPages: 2, totalItems: 3));

        var blocks = (await _handler.ExecuteAsync(prNumber: 42, pageNumber: 1)).ToList();
        var lastBlock = blocks.Cast<TextContentBlock>().Last().Text;

        Assert.Contains("hasMore: true", lastBlock);
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
    public async Task ExecuteAsync_NullPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: null));
        Assert.Contains("pageNumber", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPageNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 0));
        Assert.Contains("pageNumber must be >= 1", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PageExceedsTotalPages_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 42, pageNumber: 99));
        Assert.Contains("exceeds total pages", ex.Message);
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
}
