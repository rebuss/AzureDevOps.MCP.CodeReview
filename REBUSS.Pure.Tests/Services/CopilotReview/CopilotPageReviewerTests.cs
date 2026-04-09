using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;

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
    private static CopilotPageReviewer CreateReviewer(FakeSessionFactory factory) =>
        new(factory,
            Options.Create(new CopilotReviewOptions { Model = "claude-sonnet-4.6" }),
            NullLogger<CopilotPageReviewer>.Instance);

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
        var result = await reviewer.ReviewPageAsync(1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal("looks good", result.ReviewText);
        Assert.Empty(result.FailedFilePaths);
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
        var result = await reviewer.ReviewPageAsync(2, "diff");

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
        var result = await reviewer.ReviewPageAsync(3, "diff");

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
        _ = await reviewer.ReviewPageAsync(1, "MY_DIFF_MARKER_xyz");

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
        var task = reviewer.ReviewPageAsync(1, "diff", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
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
