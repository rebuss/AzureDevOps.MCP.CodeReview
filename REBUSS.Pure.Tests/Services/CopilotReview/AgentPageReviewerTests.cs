using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;
using REBUSS.Pure.Tests.Services.AgentInvocation;

namespace REBUSS.Pure.Tests.Services.CopilotReview;

/// <summary>
/// Unit tests for <see cref="AgentPageReviewer"/> after the
/// <c>IAgentInvoker</c> refactor. The reviewer's prior event-driven SDK session
/// collapses into a single <c>InvokeAsync(prompt, model, ct)</c> call, so tests
/// script responses (or exceptions) on a <see cref="FakeAgentInvoker"/> instead
/// of emitting <c>AssistantMessageEvent</c>/<c>SessionIdleEvent</c>/<c>SessionErrorEvent</c>
/// sequences.
/// </summary>
public class AgentPageReviewerTests
{
    private static AgentPageReviewer CreateReviewer(
        FakeAgentInvoker invoker,
        IAgentInspectionWriter? inspection = null) =>
        new(invoker,
            Options.Create(new CopilotReviewOptions { Model = "claude-sonnet-4.6" }),
            NullLogger<AgentPageReviewer>.Instance,
            inspection ?? new NoOpAgentInspectionWriter());

    [Fact]
    public async Task ReviewPage_InvokerReturnsText_ReturnsSuccessWithReviewText()
    {
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue("looks good");

        var reviewer = CreateReviewer(invoker);
        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.PageNumber);
        Assert.Equal("looks good", result.ReviewText);
        Assert.Empty(result.FailedFilePaths);
    }

    [Fact]
    public async Task ReviewPage_InvokerReturnsMultiLineText_PreservesContentVerbatim()
    {
        // Pre-refactor this exercised phased-output accumulation across multiple
        // AssistantMessageEvents. Post-refactor the invoker returns the already-accumulated
        // string, but the guarantee still matters: the reviewer must not mangle the content.
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue("part A\npart B");

        var reviewer = CreateReviewer(invoker);
        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal("part A\npart B", result.ReviewText);
    }

    [Fact]
    public async Task ReviewPage_InvokerThrows_ReturnsFailureNeverPropagates()
    {
        var invoker = new FakeAgentInvoker();
        invoker.EnqueueException(new InvalidOperationException("model unavailable"));

        var reviewer = CreateReviewer(invoker);
        var result = await reviewer.ReviewPageAsync("pr:42", 2, "diff");

        Assert.False(result.Succeeded);
        Assert.Contains("model unavailable", result.ErrorMessage);
    }

    [Fact]
    public async Task ReviewPage_InvokerThrowsEmptyResponseError_SurfacesThatMessage()
    {
        // Post-refactor the "idle without assistant message content" case is owned by
        // the invoker implementation (CopilotAgentInvoker still throws that message
        // on empty SDK responses). The reviewer simply surfaces it as ErrorMessage.
        var invoker = new FakeAgentInvoker();
        invoker.EnqueueException(new InvalidOperationException(
            "session idle without assistant message content"));

        var reviewer = CreateReviewer(invoker);
        var result = await reviewer.ReviewPageAsync("pr:42", 3, "diff");

        Assert.False(result.Succeeded);
        Assert.Contains("idle without assistant message", result.ErrorMessage);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \n")]
    public async Task ReviewPage_EmptyEnrichedContent_ReturnsFailureWithoutDispatch(string emptyContent)
    {
        // Without this guard an empty diff would ship to the agent SDK with only the
        // review-instruction template; the LLM then hallucinates a refusal (commonly
        // "MCP tool not available"). The reviewer must short-circuit instead.
        var invoker = new FakeAgentInvoker();

        var reviewer = CreateReviewer(invoker);
        var result = await reviewer.ReviewPageAsync("pr:42", 7, emptyContent);

        Assert.False(result.Succeeded);
        Assert.Equal(7, result.PageNumber);
        Assert.Contains("Empty enriched page content", result.ErrorMessage);
        Assert.Null(invoker.LastPrompt); // invoker never called
    }

    [Fact]
    public async Task ReviewPage_PromptAssembly_ContainsTemplateAndContent()
    {
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue("ok");

        var reviewer = CreateReviewer(invoker);
        _ = await reviewer.ReviewPageAsync("pr:42", 1, "MY_DIFF_MARKER_xyz");

        Assert.NotNull(invoker.LastPrompt);
        Assert.Contains("Code Review Task", invoker.LastPrompt!); // from template
        Assert.Contains("MY_DIFF_MARKER_xyz", invoker.LastPrompt!); // substituted content
        Assert.DoesNotContain("{enrichedPageContent}", invoker.LastPrompt!); // placeholder replaced
    }

    [Fact]
    public async Task ReviewPage_ForwardsConfiguredModelToInvoker()
    {
        // Ensures the model from CopilotReviewOptions actually reaches the invoker —
        // previously this was an implicit guarantee because CopilotSessionFactory read it;
        // post-refactor the reviewer passes it explicitly and tests must pin that.
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue("ok");

        var reviewer = CreateReviewer(invoker);
        _ = await reviewer.ReviewPageAsync("pr:42", 1, "diff");

        Assert.Equal("claude-sonnet-4.6", invoker.LastModel);
    }

    [Fact]
    public async Task ReviewPage_CancellationRespected_PropagatesOce()
    {
        var invoker = new FakeAgentInvoker
        {
            OnInvoke = async (_, _, ct) =>
            {
                // Mimic an invoker that honors cancellation mid-flight.
                await Task.Delay(-1, ct);
                return string.Empty;
            }
        };

        var reviewer = CreateReviewer(invoker);
        using var cts = new CancellationTokenSource();
        var task = reviewer.ReviewPageAsync("pr:42", 1, "diff", cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    public async Task ReviewPage_CancellationTokenAlreadyCancelled_PropagatesOCE()
    {
        // Regression: previously FakeSessionHandle.SendAsync ignored ct. Post-refactor
        // the invoker receives ct directly — FakeAgentInvoker.OnInvoke honors it here.
        var invoker = new FakeAgentInvoker
        {
            OnInvoke = (_, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult("never reached");
            }
        };

        var reviewer = CreateReviewer(invoker, new NoOpAgentInspectionWriter());

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reviewer.ReviewPageAsync("pr:42", 1, "diff content", cts.Token));
    }

    // ─── Feature 022 — inspection writer integration ─────────────────────────────

    [Fact]
    public async Task ReviewPage_SuccessfulPath_WritesPromptAndResponseToInspection()
    {
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue("review body");
        var inspection = Substitute.For<IAgentInspectionWriter>();
        var reviewer = CreateReviewer(invoker, inspection);

        var result = await reviewer.ReviewPageAsync("pr:42", 1, "MY_DIFF_MARKER");

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
        // Feature 022 US4 (T022): the NoOp inspection writer must not mutate review
        // text. The reviewer's output must match what the invoker returned byte-for-byte.
        const string agentResponse = "**[critical]** `src/Foo.cs` (line 5): null deref";
        var invoker = new FakeAgentInvoker();
        invoker.Enqueue(agentResponse);
        var reviewer = CreateReviewer(invoker, new NoOpAgentInspectionWriter());

        var result = await reviewer.ReviewPageAsync("pr:42", 1, "diff content");

        Assert.True(result.Succeeded);
        Assert.Equal(agentResponse, result.ReviewText);
    }

    [Fact]
    public async Task ReviewPage_FailurePath_OnlyPromptIsCaptured()
    {
        // When the invoker throws (agent session error, transport failure, etc.), the
        // response capture must NOT fire. Prompt capture still happens (it's written
        // before the invoker call).
        var invoker = new FakeAgentInvoker();
        invoker.EnqueueException(new InvalidOperationException("boom"));
        var inspection = Substitute.For<IAgentInspectionWriter>();
        var reviewer = CreateReviewer(invoker, inspection);

        var result = await reviewer.ReviewPageAsync("pr:42", 1, "content");

        Assert.False(result.Succeeded);
        await inspection.Received(1).WritePromptAsync(
            "pr:42", "page-1-review", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await inspection.DidNotReceive().WriteResponseAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
