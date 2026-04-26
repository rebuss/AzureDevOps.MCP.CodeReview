using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.AgentInvocation;
using REBUSS.Pure.Services.CopilotReview;

namespace REBUSS.Pure.Tests.Services.AgentInvocation;

public class CopilotAgentInvokerTests
{
    [Fact]
    public async Task InvokeAsync_WhenModelIsNull_FallsBackToOptionsModel()
    {
        // IAgentInvoker declares `model` as an optional hint. CopilotAgentInvoker must
        // honour that contract by substituting CopilotReviewOptions.Model when the
        // caller passes null, instead of throwing.
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ICopilotSessionHandle>>(_ => throw new OperationCanceledException("stop"));

        var options = Options.Create(new CopilotReviewOptions { Model = "fallback-model" });
        var invoker = new CopilotAgentInvoker(factory, options);

        // The session factory aborts after we observe the model — we only care that the
        // fallback string reached CreateSessionAsync, not that a session ran to completion.
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => invoker.InvokeAsync("prompt", model: null, CancellationToken.None));

        await factory.Received(1).CreateSessionAsync("fallback-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenModelIsWhitespace_FallsBackToOptionsModel()
    {
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ICopilotSessionHandle>>(_ => throw new OperationCanceledException("stop"));

        var options = Options.Create(new CopilotReviewOptions { Model = "fallback-model" });
        var invoker = new CopilotAgentInvoker(factory, options);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => invoker.InvokeAsync("prompt", model: "   ", CancellationToken.None));

        await factory.Received(1).CreateSessionAsync("fallback-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenModelIsExplicit_OverridesOptionsModel()
    {
        // Explicit hints from the caller still win over the configured default — this is
        // how AgentPageReviewer and FindingValidator pass opts.Model today.
        var factory = Substitute.For<ICopilotSessionFactory>();
        factory.CreateSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<ICopilotSessionHandle>>(_ => throw new OperationCanceledException("stop"));

        var options = Options.Create(new CopilotReviewOptions { Model = "fallback-model" });
        var invoker = new CopilotAgentInvoker(factory, options);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => invoker.InvokeAsync("prompt", model: "explicit-model", CancellationToken.None));

        await factory.Received(1).CreateSessionAsync("explicit-model", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvokeAsync_WhenModelAndOptionsBothEmpty_ThrowsInvalidOperationException()
    {
        // Misconfiguration path: operator explicitly cleared CopilotReviewOptions.Model
        // AND no caller-supplied model. Distinct from a contract violation — surfaces as
        // InvalidOperationException so callers don't mistake it for an argument error.
        var factory = Substitute.For<ICopilotSessionFactory>();
        var options = Options.Create(new CopilotReviewOptions { Model = string.Empty });
        var invoker = new CopilotAgentInvoker(factory, options);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => invoker.InvokeAsync("prompt", model: null, CancellationToken.None));

        await factory.DidNotReceive().CreateSessionAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
