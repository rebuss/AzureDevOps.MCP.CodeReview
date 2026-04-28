using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Focused unit tests for the registry collaborator extracted from
/// <see cref="AgentReviewOrchestrator"/>. The orchestrator-level integration
/// behaviour (background body, cancellation, dispose drain) stays covered by
/// <see cref="AgentReviewOrchestratorTests"/>.
/// </summary>
public class AgentReviewJobRegistryTests
{
    private static AgentReviewJobRegistry NewRegistry(int retentionMinutes = 30)
        => new(Options.Create(new CopilotReviewOptions { JobRetentionMinutes = retentionMinutes }));

    [Fact]
    public void TryRegister_FreshKey_ReturnsNewJob_AndStoresIt()
    {
        var registry = NewRegistry();

        var job = registry.TryRegister("pr:1", _ => Task.CompletedTask);

        Assert.NotNull(job);
        Assert.Equal("pr:1", job!.ReviewKey);
        Assert.Equal(AgentReviewStatus.Reviewing, job.Status);
        Assert.True(registry.TryGet("pr:1", out var stored));
        Assert.Same(job, stored);
    }

    [Fact]
    public void TryRegister_SameKey_SecondCall_ReturnsNull_Idempotent()
    {
        var registry = NewRegistry();
        var first = registry.TryRegister("pr:1", _ => Task.CompletedTask);

        var second = registry.TryRegister("pr:1", _ => Task.CompletedTask);

        Assert.NotNull(first);
        Assert.Null(second);
        // Original job still in place.
        Assert.True(registry.TryGet("pr:1", out var stored));
        Assert.Same(first, stored);
    }

    [Fact]
    public void TryRegister_AfterRetentionExpired_EvictsTerminalJob_AndStartsNewOne()
    {
        var registry = NewRegistry(retentionMinutes: 1);
        var first = registry.TryRegister("pr:1", _ => Task.CompletedTask)!;
        // Simulate terminal completion 10 minutes ago — TTL sweep should evict it on next register.
        // Failed (not Ready) keeps the test cleanup honest with the "Ready ⇒ Result non-null"
        // invariant; the sweep evicts on CompletedAt regardless of which terminal status.
        registry.CompleteUnderLock(first, AgentReviewStatus.Failed, result: null, errorMessage: "test-cleanup");
        first.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);

        var second = registry.TryRegister("pr:1", _ => Task.CompletedTask);

        Assert.NotNull(second);
        Assert.NotSame(first, second);
    }

    [Fact]
    public void TryRegister_RetentionDisabled_NeverEvicts_StaysIdempotent()
    {
        // retention <= 0 disables the sweep entirely.
        var registry = NewRegistry(retentionMinutes: 0);
        var first = registry.TryRegister("pr:1", _ => Task.CompletedTask)!;
        // Use Failed to comply with the "Ready ⇒ Result non-null" invariant — the sweep
        // dispatches on CompletedAt only, status is irrelevant for this scenario.
        registry.CompleteUnderLock(first, AgentReviewStatus.Failed, result: null, errorMessage: "test-cleanup");
        first.CompletedAt = DateTimeOffset.UtcNow.AddDays(-30);

        var second = registry.TryRegister("pr:1", _ => Task.CompletedTask);

        // Even with a 30-day-old CompletedAt, the existing job is preserved → idempotent skip.
        Assert.Null(second);
    }

    [Fact]
    public void Snapshot_AfterCompleteUnderLock_ReflectsNewStatus_AndIncludesProgressFields()
    {
        var registry = NewRegistry();
        var job = registry.TryRegister("pr:1", _ => Task.CompletedTask)!;
        registry.SetTotalPagesUnderLock(job, totalPages: 4);
        Interlocked.Increment(ref job.CompletedPages);
        Interlocked.Increment(ref job.CompletedPages);
        job.CurrentActivity = "two of four";

        var result = new AgentReviewResult
        {
            ReviewKey = "pr:1",
            PageReviews = Array.Empty<AgentPageReviewResult>(),
            CompletedAt = DateTimeOffset.UtcNow,
        };
        registry.CompleteUnderLock(job, AgentReviewStatus.Ready, result, errorMessage: null);

        var snapshot = registry.Snapshot("pr:1");

        Assert.NotNull(snapshot);
        Assert.Equal(AgentReviewStatus.Ready, snapshot!.Status);
        Assert.Same(result, snapshot.Result);
        Assert.Null(snapshot.ErrorMessage);
        Assert.Equal(4, snapshot.TotalPages);
        Assert.Equal(2, snapshot.CompletedPages);
        Assert.Equal("two of four", snapshot.CurrentActivity);
    }

    [Fact]
    public void Snapshot_UnknownKey_ReturnsNull()
    {
        var registry = NewRegistry();

        var snapshot = registry.Snapshot("does-not-exist");

        Assert.Null(snapshot);
    }

    [Fact]
    public void All_ReturnsLiveJobs_AfterMultipleRegistrations()
    {
        var registry = NewRegistry();
        registry.TryRegister("pr:1", _ => Task.CompletedTask);
        registry.TryRegister("pr:2", _ => Task.CompletedTask);
        registry.TryRegister("pr:3", _ => Task.CompletedTask);

        Assert.Equal(3, registry.All.Count);
    }

    [Fact]
    public void TryRegister_AssignsBackgroundTaskBeforeReturning()
    {
        // Pin contract: BackgroundTask must be set atomically inside the registry lock
        // so a concurrent DisposeAsync iterating `All` cannot observe a freshly-registered
        // job with a null handle (which would make the shutdown drain skip the in-flight
        // body and let it outlive graceful cancellation).
        var registry = NewRegistry();
        var bodyStarted = new TaskCompletionSource();

        var job = registry.TryRegister("pr:1", _ =>
        {
            bodyStarted.SetResult();
            return Task.Delay(Timeout.Infinite);
        });

        Assert.NotNull(job);
        Assert.NotNull(job.BackgroundTask);
        Assert.True(bodyStarted.Task.IsCompletedSuccessfully,
            "Body factory should have been invoked synchronously inside the registry lock");
    }

    [Fact]
    public void TryRegister_IdempotentSecondCall_DoesNotInvokeBodyFactory()
    {
        // Second TryRegister for the same key returns null without invoking the factory —
        // critical because callers cannot infer "factory was/wasn't invoked" from the
        // null return alone, so the registry must guarantee one-fire semantics.
        var registry = NewRegistry();
        registry.TryRegister("pr:1", _ => Task.CompletedTask);

        var factoryCalled = false;
        var second = registry.TryRegister("pr:1", _ =>
        {
            factoryCalled = true;
            return Task.CompletedTask;
        });

        Assert.Null(second);
        Assert.False(factoryCalled);
    }

    [Fact]
    public void CompleteUnderLock_ReadyWithNullResult_Throws()
    {
        // Pin invariant: every production caller transitioning a job to Ready produces an
        // AgentReviewResult; consumers reading a Ready snapshot expect Result to be present.
        // The precondition catches future drift where someone could mistakenly finalize
        // Ready with a null result.
        var registry = NewRegistry();
        var job = registry.TryRegister("pr:1", _ => Task.CompletedTask)!;

        Assert.Throws<ArgumentNullException>(() =>
            registry.CompleteUnderLock(job, AgentReviewStatus.Ready, result: null, errorMessage: null));
    }

    [Fact]
    public void CompleteUnderLock_FailedWithNullResult_Allowed()
    {
        // Asymmetry: Failed transitions accept a null result — the OCE / general-catch
        // branches in AgentReviewOrchestrator finalize without producing a result. The
        // Snapshot reader sees `Result == null` plus `ErrorMessage`, which is the
        // canonical failure shape.
        var registry = NewRegistry();
        var job = registry.TryRegister("pr:1", _ => Task.CompletedTask)!;

        registry.CompleteUnderLock(job, AgentReviewStatus.Failed, result: null, errorMessage: "boom");

        var snapshot = registry.Snapshot("pr:1");
        Assert.NotNull(snapshot);
        Assert.Equal(AgentReviewStatus.Failed, snapshot!.Status);
        Assert.Null(snapshot.Result);
        Assert.Equal("boom", snapshot.ErrorMessage);
    }
}
