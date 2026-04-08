using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools;
using RSession = REBUSS.Pure.Services.ReviewSession.ReviewSession;

namespace REBUSS.Pure.Tests.Tools;

public class QueryReviewNotesToolHandlerTests
{
    private readonly ReviewSessionStore _store = new();
    private readonly QueryReviewNotesToolHandler _handler;

    public QueryReviewNotesToolHandlerTests()
    {
        _handler = new QueryReviewNotesToolHandler(_store, NullLogger<QueryReviewNotesToolHandler>.Instance);
    }

    private RSession SeedSessionWithObservations(params (string Path, string Obs)[] obs)
    {
        var paths = obs.Select(o => o.Path).Distinct().ToArray();
        var entries = paths.Select(p => new ReviewFileEntry(p, FileCategory.Source, 1)).ToList();
        var enriched = paths.ToDictionary(p => p, p => "content");
        var s = new RSession(Guid.NewGuid().ToString("N"), 1, "head", 10_000, entries, enriched, DateTimeOffset.UtcNow);
        _store.Add(s);

        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var now = DateTimeOffset.UtcNow;

        // Walk all files in input order (which matches Files list order); record real
        // observation immediately so the next iteration's NextItem can advance through the gate.
        var firstObsByPath = obs.GroupBy(o => o.Path).ToDictionary(g => g.Key, g => g.First().Obs);
        foreach (var path in paths)
        {
            var r = s.NextItem(chunker, now);
            // r should be Delivered with path == current target (Files are in input order)
            s.RecordObservation(path, firstObsByPath[path], ReviewItemStatus.ReviewedComplete, now);
        }
        return s;
    }

    private static string TextOf(IEnumerable<ContentBlock> blocks) =>
        string.Concat(blocks.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task HappyPath_ReturnsMatches()
    {
        var s = SeedSessionWithObservations(("a.cs", "validation now requires CancellationToken"));
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, query: "validation");
        var text = TextOf(blocks);
        Assert.Contains("1 match(es)", text);
        Assert.Contains("a.cs", text);
        Assert.Contains("validation", text);
    }

    [Fact]
    public async Task MissingSessionId_Throws()
    {
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: null, query: "x"));
    }

    [Fact]
    public async Task MissingQuery_Throws()
    {
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: "x", query: null));
    }

    [Fact]
    public async Task EmptyQuery_Throws()
    {
        var s = SeedSessionWithObservations(("a.cs", "x"));
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, query: ""));
        Assert.Contains("non-whitespace", ex.Message);
    }

    [Fact]
    public async Task WhitespaceOnlyQuery_Throws()
    {
        var s = SeedSessionWithObservations(("a.cs", "x"));
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, query: "   "));
    }

    [Fact]
    public async Task ZeroMatches_ReturnsNoMatchesText_NotException()
    {
        var s = SeedSessionWithObservations(("a.cs", "completely unrelated"));
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, query: "validation");
        Assert.Contains("No observations match", TextOf(blocks));
    }

    [Fact]
    public async Task UnknownSessionId_ReturnsSessionNotFoundText()
    {
        var blocks = await _handler.ExecuteAsync(sessionId: "missing", query: "x");
        Assert.Contains("not found", TextOf(blocks));
    }

    [Fact]
    public async Task LimitExceedingMax_CappedAt20()
    {
        var obs = Enumerable.Range(0, 30)
            .Select(i => ($"f{i:D3}.cs", "match here"))
            .ToArray();
        var s = SeedSessionWithObservations(obs);
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, query: "match", limit: 100);
        var text = TextOf(blocks);
        Assert.Contains("30 match(es) total", text);
        Assert.Contains("showing top 20", text);
    }

    [Fact]
    public async Task TruncationMarkerPresentForLongObservation()
    {
        var s = SeedSessionWithObservations(("a.cs", "validation " + new string('x', 5000)));
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, query: "validation");
        var text = TextOf(blocks);
        Assert.Contains("truncated", text);
    }

    [Fact]
    public async Task MultiToken_OrderedByDescendingScoreThenAlphabetical()
    {
        var s = SeedSessionWithObservations(
            ("zeta.cs", "validation here"),
            ("alpha.cs", "validation null inputs"),
            ("beta.cs", "validation here"));
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, query: "validation null");
        var text = TextOf(blocks);
        // alpha.cs should appear first (score 2)
        var alphaIdx = text.IndexOf("alpha.cs");
        var betaIdx = text.IndexOf("beta.cs");
        var zetaIdx = text.IndexOf("zeta.cs");
        Assert.True(alphaIdx > 0 && alphaIdx < betaIdx && alphaIdx < zetaIdx);
        Assert.True(betaIdx < zetaIdx); // alphabetical tie-break among score-1 entries
    }
}
