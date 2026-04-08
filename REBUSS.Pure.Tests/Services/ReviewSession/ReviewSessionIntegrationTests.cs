using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Services.ReviewSession;

/// <summary>
/// End-to-end lifecycle integration covering US1–US5: begin → next/record loop → submit
/// against a fake metadata provider and a stub enrichment orchestrator.
/// </summary>
public class ReviewSessionIntegrationTests
{
    private readonly IPullRequestDataProvider _meta = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budget = Substitute.For<IContextBudgetResolver>();
    private readonly IPrEnrichmentOrchestrator _orchestrator = Substitute.For<IPrEnrichmentOrchestrator>();
    private readonly ReviewSessionStore _store = new();
    private readonly SingleFileChunker _chunker;
    private readonly IOptions<WorkflowOptions> _workflow =
        Options.Create(new WorkflowOptions { MetadataInternalTimeoutMs = 5_000, ContentInternalTimeoutMs = 5_000 });

    private readonly BeginPullRequestReviewToolHandler _begin;
    private readonly NextReviewItemToolHandler _next;
    private readonly RecordReviewObservationToolHandler _record;
    private readonly SubmitPullRequestReviewToolHandler _submit;

    public ReviewSessionIntegrationTests()
    {
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        _chunker = new SingleFileChunker(est);

        _meta.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata
            {
                PullRequestId = 42,
                Title = "test",
                Status = "active",
                LastMergeSourceCommitId = "head-sha",
                LastMergeTargetCommitId = "base-sha",
                CommitShas = new List<string> { "head-sha" },
            });

        _budget.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200_000, 10_000, BudgetSource.Default, Array.Empty<string>()));

        _orchestrator.TryGetSnapshot(Arg.Any<int>()).Returns((PrEnrichmentJobSnapshot?)null);

        _begin = new BeginPullRequestReviewToolHandler(
            _meta, _budget, _orchestrator, _store, _workflow,
            NullLogger<BeginPullRequestReviewToolHandler>.Instance);
        _next = new NextReviewItemToolHandler(_store, _chunker,
            NullLogger<NextReviewItemToolHandler>.Instance);
        _record = new RecordReviewObservationToolHandler(_store,
            NullLogger<RecordReviewObservationToolHandler>.Instance);
        _submit = new SubmitPullRequestReviewToolHandler(_store,
            NullLogger<SubmitPullRequestReviewToolHandler>.Instance);
    }

    private void StubEnrichment(int n, int sizeEach = 50)
    {
        var candidates = Enumerable.Range(0, n)
            .Select(i => new PackingCandidate($"src/file_{(char)('a' + i)}.cs", sizeEach, FileCategory.Source, 5))
            .ToArray();
        var enriched = candidates.ToDictionary(
            c => c.Path,
            c => $"@@ -1,3 +1,3 @@\n+content of {c.Path}\n");
        var result = new PrEnrichmentResult
        {
            PrNumber = 42,
            HeadSha = "head-sha",
            SortedCandidates = candidates,
            EnrichedByPath = enriched,
            Allocation = new PageAllocation(Array.Empty<PageSlice>(), 0, 0),
            SafeBudgetTokens = 10_000,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        _orchestrator.WaitForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(result);
    }

    private static string TextOf(IEnumerable<ContentBlock> blocks) =>
        string.Concat(blocks.OfType<TextContentBlock>().Select(t => t.Text));

    private static string ExtractSessionId(string manifest)
    {
        var idx = manifest.IndexOf("Session id:", StringComparison.Ordinal);
        var line = manifest.Substring(idx).Split('\n')[0];
        return line.Split(':')[1].Trim();
    }

    // ─── US1: Full lifecycle happy path + SC-003 enrichment-once + FR-013 audit replay ──

    [Fact]
    public async Task FullLifecycle_FiveFiles_AllAcknowledgedAndAuditTrailReplayable()
    {
        StubEnrichment(5);

        var manifestBlocks = await _begin.ExecuteAsync(prNumber: 42);
        var manifest = TextOf(manifestBlocks);
        var sid = ExtractSessionId(manifest);
        Assert.Contains("Files:      5", manifest);

        for (int i = 0; i < 5; i++)
        {
            var nextBlocks = await _next.ExecuteAsync(sessionId: sid);
            var nextText = TextOf(nextBlocks);
            Assert.Contains("===", nextText);
            // Recover the path from the header
            var path = nextText.Split('\n')[0].Replace("===", "").Trim();
            await _record.ExecuteAsync(sessionId: sid, filePath: path,
                observations: $"observation for {path}", status: "reviewed_complete");
        }

        var subBlocks = await _submit.ExecuteAsync(sessionId: sid, reviewText: "Overall LGTM");
        var subText = TextOf(subBlocks);
        Assert.Contains("Final Review", subText);
        Assert.Contains("Audit Trail", subText);
        Assert.Contains("Overall LGTM", subText);
        for (int i = 0; i < 5; i++)
            Assert.Contains($"file_{(char)('a' + i)}.cs", subText);

        // SC-003: enrichment triggered exactly once across the whole lifecycle.
        _orchestrator.Received(1).TriggerEnrichment(42, "head-sha", Arg.Any<int>());

        // FR-013: submitted session is queryable post-submit.
        Assert.True(_store.TryGet(sid, out var session));
        Assert.True(session.IsSubmitted);
        Assert.Equal("Overall LGTM", session.FinalReviewText);
    }

    // ─── T026a: FR-022 / SC-002 transport size assertion ─────────────────────────────

    [Fact]
    public async Task FullLifecycle_EveryResponseStaysUnderSafeBudget()
    {
        StubEnrichment(3);
        var manifestBlocks = await _begin.ExecuteAsync(prNumber: 42);
        var manifest = TextOf(manifestBlocks);
        var sid = ExtractSessionId(manifest);

        Assert.True(manifest.Length <= 10_000, $"manifest length {manifest.Length} > 10000 budget");

        for (int i = 0; i < 3; i++)
        {
            var nextText = TextOf(await _next.ExecuteAsync(sessionId: sid));
            Assert.True(nextText.Length <= 10_000, $"next response length {nextText.Length} > budget");
            var path = nextText.Split('\n')[0].Replace("===", "").Trim();
            var recText = TextOf(await _record.ExecuteAsync(sessionId: sid, filePath: path,
                observations: "ok", status: "reviewed_complete"));
            Assert.True(recText.Length <= 10_000);
        }

        var subText = TextOf(await _submit.ExecuteAsync(sessionId: sid, reviewText: "ok"));
        Assert.True(subText.Length <= 10_000);
    }

    // ─── US2: Acknowledgment gate enforcement ──────────────────────────────────────

    [Fact]
    public async Task AdvancingPastUnacknowledgedFile_RaisesGateError()
    {
        StubEnrichment(2);
        var sid = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 42)));
        await _next.ExecuteAsync(sessionId: sid);
        var ex = await Assert.ThrowsAsync<McpException>(() => _next.ExecuteAsync(sessionId: sid));
        Assert.Contains("not yet acknowledged", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── US3: Force submission ─────────────────────────────────────────────────────

    [Fact]
    public async Task Submit_WithoutForce_OnIncomplete_ReturnsStructuredRejection()
    {
        StubEnrichment(3);
        var sid = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 42)));
        // Acknowledge only one file
        var nextText = TextOf(await _next.ExecuteAsync(sessionId: sid));
        var path = nextText.Split('\n')[0].Replace("===", "").Trim();
        await _record.ExecuteAsync(sessionId: sid, filePath: path,
            observations: "ok", status: "reviewed_complete");

        var rejText = TextOf(await _submit.ExecuteAsync(sessionId: sid, reviewText: "incomplete"));
        Assert.Contains("Cannot submit", rejText);
        Assert.Contains("force=true", rejText);
        Assert.False(_store.TryGet(sid, out var s) && s.IsSubmitted);
    }

    [Fact]
    public async Task Submit_WithForce_OnIncomplete_AcceptsAndAuditTrailRecordsOverride()
    {
        StubEnrichment(3);
        var sid = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 42)));
        var nextText = TextOf(await _next.ExecuteAsync(sessionId: sid));
        var path = nextText.Split('\n')[0].Replace("===", "").Trim();
        await _record.ExecuteAsync(sessionId: sid, filePath: path,
            observations: "ok", status: "reviewed_complete");

        var subText = TextOf(await _submit.ExecuteAsync(sessionId: sid, reviewText: "shipping anyway", force: true));
        Assert.Contains("Audit Trail", subText);
        Assert.Contains("Override:", subText);
        Assert.Contains("FORCE", subText);
    }

    // ─── US4: Append-only observations across the lifecycle ─────────────────────────

    [Fact]
    public async Task AppendingSecondObservation_OnSameFile_PreservesBothInOrder()
    {
        StubEnrichment(2);
        var sid = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 42)));

        var nextText = TextOf(await _next.ExecuteAsync(sessionId: sid));
        var pathA = nextText.Split('\n')[0].Replace("===", "").Trim();
        await _record.ExecuteAsync(sessionId: sid, filePath: pathA,
            observations: "first take", status: "reviewed_complete");

        var nextText2 = TextOf(await _next.ExecuteAsync(sessionId: sid));
        var pathB = nextText2.Split('\n')[0].Replace("===", "").Trim();
        await _record.ExecuteAsync(sessionId: sid, filePath: pathB,
            observations: "ok", status: "reviewed_complete");

        // Now go back and add a follow-up to file A.
        await _record.ExecuteAsync(sessionId: sid, filePath: pathA,
            observations: "second thought after seeing B", status: "reviewed_complete");

        Assert.True(_store.TryGet(sid, out var session));
        var fileA = session.Files.First(f => f.Path == pathA);
        Assert.Equal(2, fileA.Observations.Count);
        Assert.Equal(1, fileA.Observations[0].SequenceNumber);
        Assert.Equal(2, fileA.Observations[1].SequenceNumber);
        Assert.Equal("first take", fileA.Observations[0].Text);
        Assert.Equal("second thought after seeing B", fileA.Observations[1].Text);
    }

    // ─── US5: Concurrent independent sessions ──────────────────────────────────────

    [Fact]
    public async Task TwoSessions_DifferentPRs_DoNotInterfere()
    {
        StubEnrichment(2);
        var sid1 = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 42)));
        var sid2 = ExtractSessionId(TextOf(await _begin.ExecuteAsync(prNumber: 99)));
        Assert.NotEqual(sid1, sid2);
        Assert.Equal(2, _store.Count);
    }

    // ─── Session lookup error path ─────────────────────────────────────────────────

    [Fact]
    public async Task NextReviewItem_OnUnknownSession_ReturnsSessionNotFound()
    {
        var blocks = await _next.ExecuteAsync(sessionId: "no-such-session");
        var text = TextOf(blocks);
        Assert.Contains("not found", text);
        Assert.Contains("begin_pr_review", text);
    }
}
