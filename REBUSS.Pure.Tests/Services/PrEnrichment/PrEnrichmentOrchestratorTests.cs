using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Tests.Services.PrEnrichment;

/// <summary>
/// Unit tests for <see cref="PrEnrichmentOrchestrator"/>. The load-bearing
/// semantic — caller cancellation NEVER cancels the background body — is
/// asserted by <see cref="CallerCancellation_DoesNotCancelBackgroundBody"/>.
/// </summary>
public class PrEnrichmentOrchestratorTests : IDisposable
{
    private readonly IPullRequestDiffCache _diffCache = Substitute.For<IPullRequestDiffCache>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly ICodeProcessor _codeProcessor = Substitute.For<ICodeProcessor>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly PrEnrichmentOrchestrator _orchestrator;

    public PrEnrichmentOrchestratorTests()
    {
        _lifetime.ApplicationStopping.Returns(_shutdownCts.Token);

        // Empty allocation by default — sufficient for most tests.
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(Array.Empty<PageSlice>(), 0, 0));

        _orchestrator = new PrEnrichmentOrchestrator(
            _diffCache, _tokenEstimator, _fileClassifier, _codeProcessor, _pageAllocator,
            _lifetime, NullLogger<PrEnrichmentOrchestrator>.Instance);
    }

    public void Dispose() => _shutdownCts.Dispose();

    private static PullRequestDiff EmptyDiff(string sha = "abc")
        => new() { LastSourceCommitId = sha };

    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 2000, string? description = null)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(15);
        }
        throw new Xunit.Sdk.XunitException($"Timed out waiting for: {description ?? "condition"}");
    }

    private static async Task<PrEnrichmentJobSnapshot> WaitUntilStatusAsync(
        PrEnrichmentOrchestrator orch, int prNumber, PrEnrichmentStatus expected,
        int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var snap = orch.TryGetSnapshot(prNumber);
            if (snap is not null && snap.Status == expected)
                return snap;
            await Task.Delay(15);
        }
        var final = orch.TryGetSnapshot(prNumber);
        throw new Xunit.Sdk.XunitException(
            $"Timed out waiting for PR #{prNumber} to reach {expected}; last status was {final?.Status.ToString() ?? "<null>"}");
    }

    // ──────────────────────────────────────────────────────────

    [Fact]
    public async Task TriggerThenWait_RunsToReady_AndReturnsResult()
    {
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>()).Returns(EmptyDiff("sha"));

        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        var result = await _orchestrator.WaitForEnrichmentAsync(42, CancellationToken.None);

        Assert.Equal(42, result.PrNumber);
        Assert.Equal("sha", result.HeadSha);
        Assert.Equal(5000, result.SafeBudgetTokens);
    }

    [Fact]
    public async Task WaitWithoutTrigger_Throws_InvalidOperationException()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _orchestrator.WaitForEnrichmentAsync(99, CancellationToken.None));
    }

    [Fact]
    public async Task CallerCancellation_DoesNotCancelBackgroundBody()
    {
        // Gate the diff fetch on a TCS so background body hangs until we release it.
        var diffGate = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>()).Returns(diffGate.Task);

        _orchestrator.TriggerEnrichment(42, "sha", 5000);

        // Caller starts waiting, then cancels its own ct.
        using var callerCts = new CancellationTokenSource();
        var waitTask = _orchestrator.WaitForEnrichmentAsync(42, callerCts.Token);
        callerCts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);

        // Background body is still in FetchingDiff because the gate is closed.
        Assert.Equal(PrEnrichmentStatus.FetchingDiff, _orchestrator.TryGetSnapshot(42)!.Status);

        // Release the gate — background body should now run to completion DESPITE
        // caller having cancelled.
        diffGate.SetResult(EmptyDiff("sha"));
        var snapshot = await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Ready);
        Assert.NotNull(snapshot.Result);
    }

    [Fact]
    public async Task SameShaReTrigger_IsNoOp_ReusesExistingJob()
    {
        var diffGate = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>()).Returns(diffGate.Task);

        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        _orchestrator.TriggerEnrichment(42, "sha", 5000);

        // Wait for the background body of the FIRST job to actually invoke the cache,
        // then assert no further re-triggers issued additional calls.
        await WaitUntilAsync(
            () => _diffCache.ReceivedCalls().Any(c => c.GetMethodInfo().Name == nameof(IPullRequestDiffCache.GetOrFetchDiffAsync)),
            description: "first GetOrFetchDiffAsync call");
        await _diffCache.Received(1).GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>());

        diffGate.SetResult(EmptyDiff("sha"));
        await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Ready);
    }

    [Fact]
    public async Task DifferentShaReTrigger_SupersedesOldJob()
    {
        var oldGate = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newGate = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diffCache.GetOrFetchDiffAsync(42, "old", Arg.Any<CancellationToken>()).Returns(oldGate.Task);
        _diffCache.GetOrFetchDiffAsync(42, "new", Arg.Any<CancellationToken>()).Returns(newGate.Task);

        _orchestrator.TriggerEnrichment(42, "old", 5000);
        _orchestrator.TriggerEnrichment(42, "new", 5000);

        // Wait for the new job's background body to invoke the cache for "new".
        await WaitUntilAsync(
            () => _diffCache.ReceivedCalls().Any(c =>
                c.GetMethodInfo().Name == nameof(IPullRequestDiffCache.GetOrFetchDiffAsync)
                && (string?)c.GetArguments()[1] == "new"),
            description: "GetOrFetchDiffAsync call for new SHA");
        await _diffCache.Received(1).GetOrFetchDiffAsync(42, "new", Arg.Any<CancellationToken>());

        // Old job's gate is now redundant — release it (it would propagate cancellation anyway).
        oldGate.TrySetCanceled();

        newGate.SetResult(EmptyDiff("new"));
        var snap = await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Ready);
        Assert.Equal("new", snap.HeadSha);
    }

    [Fact]
    public async Task BackgroundBodyThrows_StatusBecomesFailed_FailurePopulated()
    {
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>())
            .Returns<Task<PullRequestDiff>>(_ => throw new InvalidOperationException("boom"));

        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        var snap = await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Failed);

        Assert.NotNull(snap.Failure);
        Assert.Equal("InvalidOperationException", snap.Failure!.ExceptionTypeName);
        Assert.Contains("boom", snap.Failure.SanitizedMessage);
    }

    [Fact]
    public async Task FailedJob_IsRetryable()
    {
        var callCount = 0;
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new InvalidOperationException("first call fails");
                return Task.FromResult(EmptyDiff("sha"));
            });

        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Failed);

        // Retrigger after failure — should start a fresh job.
        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        var snap = await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Ready);
        Assert.NotNull(snap.Result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void TryGetSnapshot_UnknownPr_ReturnsNull()
    {
        Assert.Null(_orchestrator.TryGetSnapshot(12345));
    }

    [Fact]
    public async Task TryGetSnapshot_ReadyJob_ReturnsSnapshotWithResult()
    {
        _diffCache.GetOrFetchDiffAsync(42, "sha", Arg.Any<CancellationToken>()).Returns(EmptyDiff("sha"));

        _orchestrator.TriggerEnrichment(42, "sha", 5000);
        await _orchestrator.WaitForEnrichmentAsync(42, CancellationToken.None);

        var snap = _orchestrator.TryGetSnapshot(42);
        Assert.NotNull(snap);
        Assert.Equal(PrEnrichmentStatus.Ready, snap!.Status);
        Assert.NotNull(snap.Result);
        Assert.Equal("sha", snap.HeadSha);
    }

    [Fact]
    public async Task ConcurrentDistinctPrs_RunInParallel_NeitherBlocksTheOther()
    {
        // FR-009 — distinct PRs must not serialize.
        var gate1 = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate2 = new TaskCompletionSource<PullRequestDiff>(TaskCreationOptions.RunContinuationsAsynchronously);
        _diffCache.GetOrFetchDiffAsync(42, "sha-a", Arg.Any<CancellationToken>()).Returns(gate1.Task);
        _diffCache.GetOrFetchDiffAsync(43, "sha-b", Arg.Any<CancellationToken>()).Returns(gate2.Task);

        _orchestrator.TriggerEnrichment(42, "sha-a", 5000);
        _orchestrator.TriggerEnrichment(43, "sha-b", 5000);

        // Distinct dictionary entries.
        Assert.NotNull(_orchestrator.TryGetSnapshot(42));
        Assert.NotNull(_orchestrator.TryGetSnapshot(43));

        // Complete PR 42 first; PR 43 must remain in FetchingDiff.
        gate1.SetResult(EmptyDiff("sha-a"));
        await WaitUntilStatusAsync(_orchestrator, 42, PrEnrichmentStatus.Ready);
        Assert.Equal(PrEnrichmentStatus.FetchingDiff, _orchestrator.TryGetSnapshot(43)!.Status);

        // Then complete PR 43 independently.
        gate2.SetResult(EmptyDiff("sha-b"));
        await WaitUntilStatusAsync(_orchestrator, 43, PrEnrichmentStatus.Ready);
    }
}
