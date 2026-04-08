using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.ReviewSession;
using RSession = REBUSS.Pure.Services.ReviewSession.ReviewSession;

namespace REBUSS.Pure.Tests.Services.ReviewSession;

public class ReviewSessionStoreTests
{
    private static RSession NewSession(string id, int prNumber = 1)
    {
        var files = new List<ReviewFileEntry>
        {
            new("a.cs", FileCategory.Source, 100),
        };
        var enriched = new Dictionary<string, string> { ["a.cs"] = "@@ -1,1 +1,1 @@\n+x" };
        return new RSession(id, prNumber, "head-sha", 1000, files, enriched, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Add_TryGet_RoundTrip()
    {
        var store = new ReviewSessionStore();
        var s = NewSession("abc");
        store.Add(s);

        Assert.True(store.TryGet("abc", out var got));
        Assert.Same(s, got);
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void TryGet_MissingId_ReturnsFalse()
    {
        var store = new ReviewSessionStore();
        Assert.False(store.TryGet("nope", out _));
    }

    [Fact]
    public void Add_DuplicateId_Throws()
    {
        var store = new ReviewSessionStore();
        store.Add(NewSession("dup"));
        Assert.Throws<InvalidOperationException>(() => store.Add(NewSession("dup")));
    }

    [Fact]
    public async Task Add_Concurrent_DoesNotCollide()
    {
        var store = new ReviewSessionStore();
        var tasks = Enumerable.Range(0, 50)
            .Select(i => Task.Run(() => store.Add(NewSession("s-" + i, prNumber: i))))
            .ToArray();
        await Task.WhenAll(tasks);
        Assert.Equal(50, store.Count);
    }

    [Fact]
    public void SimulatedRestart_FreshStoreInstance_DoesNotSeeOldSessions()
    {
        // Lock in FR-018 / SC-007: in-memory state lifetime equals process lifetime.
        // A "restart" is modelled by instantiating a fresh store; the old id must be unknown.
        var storeA = new ReviewSessionStore();
        storeA.Add(NewSession("survives-restart?"));

        var storeB = new ReviewSessionStore();
        Assert.False(storeB.TryGet("survives-restart?", out _));
    }
}
