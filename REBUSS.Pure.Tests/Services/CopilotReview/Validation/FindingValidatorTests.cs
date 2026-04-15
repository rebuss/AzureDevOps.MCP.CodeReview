using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;
using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingValidator"/>. Feature 021.</summary>
public class FindingValidatorTests
{
    private static FindingValidator CreateValidator(FakeSessionFactory factory, int batchSize = 5, ICopilotInspectionWriter? inspection = null) =>
        new(factory,
            Options.Create(new CopilotReviewOptions
            {
                Model = "claude-sonnet-4.6",
                ValidateFindings = true,
                ValidationBatchSize = batchSize,
            }),
            inspection ?? Substitute.For<ICopilotInspectionWriter>(),
            NullLogger<FindingValidator>.Instance);

    private static ParsedFinding MakeFinding(int index) => new()
    {
        Index = index,
        FilePath = "src/A.cs",
        LineNumber = 10,
        Severity = "major",
        Description = $"issue {index}",
        OriginalText = $"**[major]** `src/A.cs` (line 10): issue {index}",
    };

    private static FindingWithScope Resolved(ParsedFinding finding) => new()
    {
        Finding = finding,
        ScopeSource = "public void Bar() { }",
        ScopeName = "Foo.Bar()",
        ResolutionFailure = ScopeResolutionFailure.None,
    };

    private static FindingWithScope Unresolved(ParsedFinding finding, ScopeResolutionFailure failure) => new()
    {
        Finding = finding,
        ScopeSource = "",
        ScopeName = "",
        ResolutionFailure = failure,
    };

    [Fact]
    public async Task ValidateAsync_NotCSharp_MapsToValidWithoutCopilotCall()
    {
        var factory = new FakeSessionFactory { OnSendAsync = (_, _) => throw new Exception("must not call Copilot") };

        var validator = CreateValidator(factory);
        var input = new[] { Unresolved(MakeFinding(0), ScopeResolutionFailure.NotCSharp) };

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        var v = Assert.Single(result);
        Assert.Equal(FindingVerdict.Valid, v.Verdict);
        Assert.Equal(0, factory.CreateSessionCalls);
    }

    [Fact]
    public async Task ValidateAsync_SourceUnavailable_MapsToUncertainWithoutCopilotCall()
    {
        var factory = new FakeSessionFactory { OnSendAsync = (_, _) => throw new Exception("must not call Copilot") };

        var validator = CreateValidator(factory);
        var input = new[] { Unresolved(MakeFinding(0), ScopeResolutionFailure.SourceUnavailable) };

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        var v = Assert.Single(result);
        Assert.Equal(FindingVerdict.Uncertain, v.Verdict);
        Assert.Equal(0, factory.CreateSessionCalls);
    }

    [Fact]
    public async Task ValidateAsync_ScopeNotFound_MapsToUncertainWithoutCopilotCall()
    {
        var factory = new FakeSessionFactory { OnSendAsync = (_, _) => throw new Exception("must not call Copilot") };

        var validator = CreateValidator(factory);
        var input = new[] { Unresolved(MakeFinding(0), ScopeResolutionFailure.ScopeNotFound) };

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        var v = Assert.Single(result);
        Assert.Equal(FindingVerdict.Uncertain, v.Verdict);
        Assert.Equal(0, factory.CreateSessionCalls);
    }

    [Fact]
    public async Task ValidateAsync_ResolvedFindings_CallsCopilotAndParsesVerdicts()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new AssistantMessageEvent
            {
                Data = new AssistantMessageData
                {
                    MessageId = "m",
                    Content =
                        "**Finding 1: VALID** — this is a real bug\n" +
                        "**Finding 2: FALSE_POSITIVE** — misinterpreted context\n" +
                        "**Finding 3: UNCERTAIN** — needs cross-file check",
                }
            });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var validator = CreateValidator(factory);
        var input = new[]
        {
            Resolved(MakeFinding(0)),
            Resolved(MakeFinding(1)),
            Resolved(MakeFinding(2)),
        };

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        Assert.Equal(3, result.Count);
        Assert.Equal(FindingVerdict.Valid, result[0].Verdict);
        Assert.Equal(FindingVerdict.FalsePositive, result[1].Verdict);
        Assert.Equal(FindingVerdict.Uncertain, result[2].Verdict);
        Assert.Equal(1, factory.CreateSessionCalls); // one batch
    }

    [Fact]
    public async Task ValidateAsync_TwelveFindings_BatchSizeFive_MakesThreeCalls()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            // Response template: declare every finding in the batch VALID.
            var content = string.Join('\n',
                Enumerable.Range(1, 10).Select(i => $"**Finding {i}: VALID** — ok"));
            h.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m", Content = content } });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var validator = CreateValidator(factory, batchSize: 5);
        var input = Enumerable.Range(0, 12).Select(i => Resolved(MakeFinding(i))).ToArray();

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        Assert.Equal(12, result.Count);
        // 12 / 5 = 3 batches (5, 5, 2).
        Assert.Equal(3, factory.CreateSessionCalls);
    }

    [Fact]
    public async Task ValidateAsync_SessionFailure_FindingsPassThroughAsValid()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new SessionErrorEvent { Data = new SessionErrorData { ErrorType = "model", Message = "boom" } });
        };

        var validator = CreateValidator(factory);
        var input = new[] { Resolved(MakeFinding(0)), Resolved(MakeFinding(1)) };

        var result = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        // Graceful degradation: both findings kept as Valid.
        Assert.All(result, r => Assert.Equal(FindingVerdict.Valid, r.Verdict));
    }

    [Fact]
    public async Task ValidateAsync_EmptyInput_NoCopilotCall()
    {
        var factory = new FakeSessionFactory();

        var validator = CreateValidator(factory);
        var result = await validator.ValidateAsync(Array.Empty<FindingWithScope>(), "test:1", CancellationToken.None);

        Assert.Empty(result);
        Assert.Equal(0, factory.CreateSessionCalls);
    }

    [Fact]
    public async Task ValidateAsync_MixedResolvedAndUnresolved_OnlyResolvedSentToCopilot()
    {
        var factory = new FakeSessionFactory();
        int? capturedFindingsInPrompt = null;
        factory.OnSendAsync = (h, prompt) =>
        {
            // Count how many "## Finding " headers are in the prompt — should equal
            // the number of RESOLVED findings in this batch.
            capturedFindingsInPrompt =
                System.Text.RegularExpressions.Regex.Matches(prompt, @"^## Finding ", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
            h.PushEvent(new AssistantMessageEvent { Data = new AssistantMessageData { MessageId = "m", Content = "**Finding 1: VALID** ok" } });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var validator = CreateValidator(factory);
        var input = new[]
        {
            Unresolved(MakeFinding(0), ScopeResolutionFailure.NotCSharp),
            Resolved(MakeFinding(1)),
            Unresolved(MakeFinding(2), ScopeResolutionFailure.ScopeNotFound),
        };

        _ = await validator.ValidateAsync(input, "test:1", CancellationToken.None);

        Assert.Equal(1, capturedFindingsInPrompt);
    }

    [Fact]
    public void CopilotReviewOptions_ValidateFindings_DefaultsToTrue()
    {
        var options = new CopilotReviewOptions();
        Assert.True(options.ValidateFindings);
    }

    [Fact]
    public async Task ValidateAsync_ResolvedFindings_WritesPromptAndResponseToInspection()
    {
        var factory = new FakeSessionFactory();
        factory.OnSendAsync = (h, _) =>
        {
            h.PushEvent(new AssistantMessageEvent
            {
                Data = new AssistantMessageData { MessageId = "m", Content = "**Finding 1: VALID** — ok" }
            });
            h.PushEvent(new SessionIdleEvent { Data = new SessionIdleData() });
        };

        var inspection = Substitute.For<ICopilotInspectionWriter>();
        inspection.WritePromptAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        inspection.WriteResponseAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var validator = CreateValidator(factory, inspection: inspection);
        var input = new[] { Resolved(MakeFinding(0)) };

        await validator.ValidateAsync(input, "pr:42", CancellationToken.None);

        await inspection.Received(1).WritePromptAsync(
            "pr:42", "validation-batch-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await inspection.Received(1).WriteResponseAsync(
            "pr:42", "validation-batch-1", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ─── Fakes (mirror CopilotPageReviewerTests pattern) ────────────────────────

    private sealed class FakeSessionFactory : ICopilotSessionFactory
    {
        public Action<FakeSessionHandle, string>? OnSendAsync { get; set; }
        public int CreateSessionCalls { get; private set; }

        public Task<ICopilotSessionHandle> CreateSessionAsync(string model, CancellationToken ct)
        {
            CreateSessionCalls++;
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
