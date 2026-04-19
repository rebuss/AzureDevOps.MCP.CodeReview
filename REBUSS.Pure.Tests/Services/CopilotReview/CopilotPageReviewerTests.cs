using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="CopilotPageReviewer"/> — feature 013 Phase 3 US1 (T027).
/// Uses a hand-rolled <see cref="FakeSessionFactory"/> + <see cref="FakeSessionHandle"/> that
/// drives the event stream synchronously so each test can inject scripted
/// <see cref="AssistantMessageEvent"/>/<see cref="SessionIdleEvent"/>/<see cref="SessionErrorEvent"/>
/// sequences.
/// <para>
/// SDK event types (<see cref="AssistantMessageEvent"/>, <see cref="AssistantMessageData"/>, etc.)
/// are POCOs with init-only properties — constructable directly from tests.
/// </para>
/// </summary>
public class CopilotPageReviewerTests
{
    private static CopilotPageReviewer CreateReviewer(
        FakeSessionFactory factory,
        ICopilotInspectionWriter? inspection = null) =>
        new(factory,
            Options.Create(new CopilotReviewOptions { Model = "claude-sonnet-4.6" }),
            NullLogger<CopilotPageReviewer>.Instance,
            inspection ?? new NoOpCopilotInspectionWriter());

    [Fact]
    public async Task ReviewPage_AssistantMessageThenIdle_ReturnsSuccessWithReviewText()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (handle, prompt) =>
        {
            handle.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "looks good" } });
            handle.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var reviewer = CreateReviewer(factory);
        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal("looks good", result.ReviewText);
        Assert.Empty(result.FailedFilePaths);
    }

    [Fact]
    public async Task ReviewPage_MultipleAssistantMessageEvents_AccumulatesContent()
    {
        // Phased-output models (thinking + response) emit multiple AssistantMessageEvents
        // per session. All non-empty Content fragments must be accumulated — previous
        // "last one wins" behavior silently truncated responses.
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (handle, prompt) =>
        {
            handle.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "part A\n" } });
            handle.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "part B" } });
            handle.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var reviewer = CreateReviewer(factory);
        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal("part A\npart B", result.ReviewText);
    }

    [Fact]
    public async Task ReviewPage_SessionErrorEvent_ReturnsFailureNeverPropagates()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (handle, prompt) =>
        {
            handle.PushEvent(new SessionErrorEvent { Data = new SessionErrorData { ErrorType = "model", Message = "model unavailable" } });
        };

        var reviewer = CreateReviewer(factory);
        var result = await reviewer.ReviewPageAsync("pr:42", 2, "diff");

        Assert.False(result.Succeeded);
        Assert.Contains("model unavailable", result.ErrorMessage);
    }

    [Fact]
    public async Task ReviewPage_IdleWithoutAssistantMessage_ReturnsFailureEmptyResponse()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (handle, prompt) =>
        {
            // Idle fires immediately with no assistant message captured.
            handle.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var reviewer = CreateReviewer(factory);
        var result = await reviewer.ReviewPageAsync("pr:42", 3, "diff");

        Assert.False(result.Succeeded);
        Assert.Contains("idle without assistant message", result.ErrorMessage);
    }

    [Fact]
    public async Task ReviewPage_PromptAssembly_ContainsTemplateAndContent()
    {
        var factory = new FakeSessionFactory();
        string? capturedPrompt = null;
        factory.OnSendAsync = (handle, prompt) =>
        {
            capturedPrompt = prompt;
            handle.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "ok" } });
            handle.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var reviewer = CreateReviewer(factory);
        _ = await reviewer.ReviewPageAsync("pr:42", 1, "MY_DIFF_MARKER_xyz");

        Assert.NotNull(capturedPrompt);
        Assert.Contains("Code Review Task", capturedPrompt); // from template
        Assert.Contains("MY_DIFF_MARKER_xyz", capturedPrompt); // substituted content
        Assert.DoesNotContain("{enrichedPageContent}", capturedPrompt); // placeholder replaced
    }

    [Fact]
    public async Task ReviewPage_CancellationRespected_PropagatesOce()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (handle, prompt) =>
        {
            // Never fire any events — caller will cancel.
        };

        var reviewer = CreateReviewer(factory);
        using var cts = new CancellationTokenSource();
        var task = reviewer.ReviewPageAsync("pr:42", 1, "diff", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    // ─── Feature 022 — inspection writer integration ─────────────────────────────

    [Fact]
    public async Task ReviewPage_SuccessfulPath_WritesPromptAndResponseToInspection()
    {
        // Arrange: reviewer with a Substitute inspection writer; successful review.
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m1", Content = "review body" } });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };
        var inspection = Substitute.For<ICopilotInspectionWriter>();
        var reviewer = CreateReviewer(factory, inspection);

        // Act
        var result = await reviewer.ReviewPageAsync("pr:42", 1, "MY_DIFF_MARKER");

        // Assert: review succeeded, and both prompt and response were captured with the
        // correct review key and step descriptor.
        Assert.True(result.Succeeded);
        await inspection.Received(1).WritePromptAsync(
            "pr:42", "page-1-review",
            Arg.Is<string>(s => s.Contains("MY_DIFF_MARKER")),
            Arg.Any<CancellationToken>());
        await inspection.Received(1).WriteResponseAsync(
            "pr:42", "page-1-review", "review body",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewPage_NoOpInspection_ReviewTextUnchanged()
    {
        // Feature 022 US4 (T022): verify the NoOp inspection writer doesn't mutate the
        // review text in any way. The reviewer's output must match what Copilot returned
        // byte-for-byte regardless of which inspection writer is wired in.
        const string copilotResponse = "**[critical]** `src/Foo.cs` (line 5): null deref";
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m", Content = copilotResponse } });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };
        var reviewer = CreateReviewer(factory, new NoOpCopilotInspectionWriter());

        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal(copilotResponse, result.ReviewText);
    }

    [Fact]
    public async Task ReviewPage_FailurePath_OnlyPromptIsCaptured()
    {
        // When the SDK session errors, the response was never produced, so the response
        // capture must NOT fire. Prompt capture still happens (it's sent before the SDK call).
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new SessionErrorEvent { Data = new SessionErrorData { ErrorType = "model", Message = "boom" } });
        };
        var inspection = Substitute.For<ICopilotInspectionWriter>();
        var reviewer = CreateReviewer(factory, inspection);

        var result = await reviewer.ReviewPageAsync("pr:42", 1, "content");

        Assert.False(result.Succeeded);
        await inspection.Received(1).WritePromptAsync(
            "pr:42", "page-1-review", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await inspection.DidNotReceive().WriteResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReviewPage_CancellationTokenAlreadyCancelled_PropagatesOCE()
    {
        // Regression: previously FakeSessionHandle.SendAsync ignored ct, so pre-cancelled
        // tokens never reached the production reviewer's catch(OperationCanceledException)
        // → throw branch. With the fake now honouring ct (like the real CopilotSession),
        // this test exercises that path end-to-end.
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (_, _) => { /* no events — cancellation should preempt */ };
        var reviewer = CreateReviewer(factory, new NoOpCopilotInspectionWriter());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reviewer.ReviewPageAsync("pr:42", 1, "diff content", cts.Token));
    }

    // ─── Fakes ────────────────────────────────────────────────────────────────────

    private sealed class FakeSessionFactory : ICopilotSessionFactory
    {
        public Action<FakeSessionHandle, string>? OnSendAsync { get; set; }

        public Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct)
        {
            FakeSessionHandle? handle = null;
            handle = new FakeSessionHandle(prompt => OnSendAsync?.Invoke(handle!, prompt));
            return Task.FromResult<ICopilotSessionHandle>(handle);
        }
    }

    private sealed class FakeSessionHandle : ICopilotSessionHandle
    {
        private readonly List<Action<object>> _handlers = new();
        private readonly Action<string> _onSend;
        public FakeSessionHandle(Action<string> onSend) { _onSend = onSend; }

        public Task<string> SendAsync(string prompt, CancellationToken ct)
        {
            // Honor the cancellation token the way the real CopilotSession.SendAsync does —
            // otherwise tests using a pre-cancelled ct never exercise the OCE-in-SendAsync
            // path (the reviewer's catch(OCE) → rethrow would stay untested).
            ct.ThrowIfCancellationRequested();
            _onSend(prompt);
            return Task.FromResult("msg-id-1");
        }

        public IDisposable On(Action<object> handler)
        {
            _handlers.Add(handler);
            return new Subscription(() => _handlers.Remove(handler));
        }

        public void PushEvent(object evt)
        {
            foreach (var h in _handlers.ToList()) h(evt);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private sealed class Subscription : IDisposable
        {
            private readonly Action _onDispose;
            public Subscription(Action onDispose) { _onDispose = onDispose; }
            public void Dispose() => _onDispose();
        }
    }
}
