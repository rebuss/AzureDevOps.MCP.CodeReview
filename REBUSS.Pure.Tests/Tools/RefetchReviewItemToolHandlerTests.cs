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

public class RefetchReviewItemToolHandlerTests
{
    private readonly ReviewSessionStore _store = new();
    private readonly RefetchReviewItemToolHandler _handler;

    public RefetchReviewItemToolHandlerTests()
    {
        _handler = new RefetchReviewItemToolHandler(_store, NullLogger<RefetchReviewItemToolHandler>.Instance);
    }

    private RSession SeedSession(string path = "a.cs", string content = "the-content", bool acknowledge = true)
    {
        var entries = new List<ReviewFileEntry> { new(path, FileCategory.Source, content.Length) };
        var enriched = new Dictionary<string, string> { [path] = content };
        var s = new RSession(Guid.NewGuid().ToString("N"), 1, "head", 10_000, entries, enriched, DateTimeOffset.UtcNow);
        _store.Add(s);
        if (acknowledge)
        {
            var est = Substitute.For<ITokenEstimator>();
            est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
            var chunker = new SingleFileChunker(est);
            s.NextItem(chunker, DateTimeOffset.UtcNow);
            s.RecordObservation(path, "ok", ReviewItemStatus.ReviewedComplete, DateTimeOffset.UtcNow);
        }
        return s;
    }

    private static string TextOf(IEnumerable<ContentBlock> blocks) =>
        string.Concat(blocks.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task HappyPath_Acknowledged_ReturnsRefetchMarkerAndContent()
    {
        var s = SeedSession();
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "a.cs");
        var text = TextOf(blocks);
        Assert.Contains("[REFETCH]", text);
        Assert.Contains("the-content", text);
    }

    [Fact]
    public async Task MissingSessionId_Throws()
    {
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: null, filePath: "a.cs"));
    }

    [Fact]
    public async Task MissingFilePath_Throws()
    {
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: "x", filePath: null));
    }

    [Fact]
    public async Task UnknownSessionId_ReturnsSessionNotFoundText()
    {
        var blocks = await _handler.ExecuteAsync(sessionId: "no-such-session", filePath: "a.cs");
        Assert.Contains("not found", TextOf(blocks));
    }

    [Fact]
    public async Task RefetchOnPending_ThrowsWithGateGuidance()
    {
        var s = SeedSession(acknowledge: false);
        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "a.cs"));
        Assert.Contains("next_review_item", ex.Message);
    }

    [Fact]
    public async Task RefetchOnPartial_Throws()
    {
        // Build a session whose only file is oversized → DeliveredPartial after 1 NextItem call
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var s = new RSession(Guid.NewGuid().ToString("N"), 1, "head", 25, entries, enriched, DateTimeOffset.UtcNow);
        _store.Add(s);
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        s.NextItem(chunker, DateTimeOffset.UtcNow); // file is now DeliveredPartial

        var ex = await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "big.cs"));
        Assert.Contains("chunk-by-chunk", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RefetchOnChunkedFile_Index2_ReturnsChunk2WithContinuationHint()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var s = new RSession(Guid.NewGuid().ToString("N"), 1, "head", 25, entries, enriched, DateTimeOffset.UtcNow);
        _store.Add(s);
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var first = s.NextItem(chunker, DateTimeOffset.UtcNow);
        var total = first.TotalChunks;
        for (int i = 2; i <= total; i++) s.NextItem(chunker, DateTimeOffset.UtcNow);

        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "big.cs", chunkIndex: 2);
        var text = TextOf(blocks);
        Assert.Contains("Chunk 2 of", text);
        if (total > 2)
            Assert.Contains("More chunks remain", text);
    }

    [Fact]
    public async Task RefetchOnChunkedFile_LastChunk_NoContinuationHint()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var s = new RSession(Guid.NewGuid().ToString("N"), 1, "head", 25, entries, enriched, DateTimeOffset.UtcNow);
        _store.Add(s);
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var first = s.NextItem(chunker, DateTimeOffset.UtcNow);
        var total = first.TotalChunks;
        for (int i = 2; i <= total; i++) s.NextItem(chunker, DateTimeOffset.UtcNow);

        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "big.cs", chunkIndex: total);
        var text = TextOf(blocks);
        Assert.Contains("End of refetched content", text);
        Assert.DoesNotContain("More chunks remain", text);
    }

    [Fact]
    public async Task RefetchUnchunkedFile_NonOneIndex_Throws()
    {
        var s = SeedSession();
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "a.cs", chunkIndex: 2));
    }

    [Fact]
    public async Task RefetchChunkIndexZero_Throws()
    {
        var s = SeedSession();
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "a.cs", chunkIndex: 0));
    }

    [Fact]
    public async Task RefetchFileNotInSession_Throws()
    {
        var s = SeedSession();
        await Assert.ThrowsAsync<McpException>(() => _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "ghost.cs"));
    }

    [Fact]
    public async Task RefetchOnSubmittedSession_StillSucceeds()
    {
        var s = SeedSession();
        s.Submit("done", false, DateTimeOffset.UtcNow);
        var blocks = await _handler.ExecuteAsync(sessionId: s.SessionId, filePath: "a.cs");
        Assert.Contains("[REFETCH]", TextOf(blocks));
    }
}
