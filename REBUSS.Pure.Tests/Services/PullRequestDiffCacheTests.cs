using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services;

namespace REBUSS.Pure.Tests.Services;

public class PullRequestDiffCacheTests
{
    private readonly IPullRequestDataProvider _inner = Substitute.For<IPullRequestDataProvider>();
    private readonly PullRequestDiffCache _cache;

    private static readonly PullRequestDiff SampleDiff = new()
    {
        Title = "Test",
        Status = "active",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "src/A.cs", ChangeType = "edit", Additions = 10, Deletions = 2,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 2, NewStart = 1, NewCount = 10,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "line" } }
                    }
                }
            }
        }
    };

    public PullRequestDiffCacheTests()
    {
        _inner.GetDiffAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        _cache = new PullRequestDiffCache(_inner, NullLogger<PullRequestDiffCache>.Instance);
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_CacheMiss_FetchesFromProvider()
    {
        var result = await _cache.GetOrFetchDiffAsync(42);

        Assert.Same(SampleDiff, result);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_CacheHit_DoesNotFetchAgain()
    {
        await _cache.GetOrFetchDiffAsync(42);
        await _cache.GetOrFetchDiffAsync(42);

        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_CacheHit_ReturnsSameInstance()
    {
        var first = await _cache.GetOrFetchDiffAsync(42);
        var second = await _cache.GetOrFetchDiffAsync(42);

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_DifferentPrNumbers_FetchesBoth()
    {
        var diff2 = new PullRequestDiff
        {
            Title = "Other",
            Status = "completed",
            SourceBranch = "feature/y",
            TargetBranch = "main",
            Files = new List<FileChange>()
        };
        _inner.GetDiffAsync(99, Arg.Any<CancellationToken>()).Returns(diff2);

        var result1 = await _cache.GetOrFetchDiffAsync(42);
        var result2 = await _cache.GetOrFetchDiffAsync(99);

        Assert.Same(SampleDiff, result1);
        Assert.Same(diff2, result2);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
        await _inner.Received(1).GetDiffAsync(99, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_ProviderThrows_PropagatesException()
    {
        _inner.GetDiffAsync(404, Arg.Any<CancellationToken>())
            .Returns<PullRequestDiff>(_ => throw new PullRequestNotFoundException("Not found"));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _cache.GetOrFetchDiffAsync(404));
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_ProviderThrows_DoesNotCacheFailure()
    {
        var callCount = 0;
        _inner.GetDiffAsync(404, Arg.Any<CancellationToken>())
            .Returns<PullRequestDiff>(_ =>
            {
                callCount++;
                if (callCount == 1)
                    throw new PullRequestNotFoundException("Not found");
                return SampleDiff;
            });

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _cache.GetOrFetchDiffAsync(404));

        // Second call should retry since failure was not cached
        var result = await _cache.GetOrFetchDiffAsync(404);
        Assert.Same(SampleDiff, result);
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_PassesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        await _cache.GetOrFetchDiffAsync(42, ct: cts.Token);

        await _inner.Received(1).GetDiffAsync(42, cts.Token);
    }

    // --- Staleness detection ---

    [Fact]
    public async Task GetOrFetchDiffAsync_KnownCommitMatchesCached_ReturnsCachedInstance()
    {
        var diff = new PullRequestDiff
        {
            Title = "Test",
            Status = "active",
            LastSourceCommitId = "abc123",
            Files = new List<FileChange>()
        };
        _inner.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(diff);

        var first = await _cache.GetOrFetchDiffAsync(42);
        var second = await _cache.GetOrFetchDiffAsync(42, knownHeadCommitId: "abc123");

        Assert.Same(first, second);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_KnownCommitDiffersFromCached_EvictsAndRefetches()
    {
        var oldDiff = new PullRequestDiff
        {
            Title = "Old",
            Status = "active",
            LastSourceCommitId = "abc123",
            Files = new List<FileChange>()
        };
        var newDiff = new PullRequestDiff
        {
            Title = "New",
            Status = "active",
            LastSourceCommitId = "def456",
            Files = new List<FileChange>()
        };

        var callCount = 0;
        _inner.GetDiffAsync(42, Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return callCount == 1 ? oldDiff : newDiff;
            });

        var first = await _cache.GetOrFetchDiffAsync(42);
        Assert.Same(oldDiff, first);

        var second = await _cache.GetOrFetchDiffAsync(42, knownHeadCommitId: "def456");
        Assert.Same(newDiff, second);
        await _inner.Received(2).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_NullKnownCommit_DoesNotEvict()
    {
        var diff = new PullRequestDiff
        {
            Title = "Test",
            Status = "active",
            LastSourceCommitId = "abc123",
            Files = new List<FileChange>()
        };
        _inner.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(diff);

        await _cache.GetOrFetchDiffAsync(42);
        var second = await _cache.GetOrFetchDiffAsync(42, knownHeadCommitId: null);

        Assert.Same(diff, second);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_CachedDiffHasNullCommit_DoesNotEvict()
    {
        var diff = new PullRequestDiff
        {
            Title = "Test",
            Status = "active",
            LastSourceCommitId = null,
            Files = new List<FileChange>()
        };
        _inner.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(diff);

        await _cache.GetOrFetchDiffAsync(42);
        var second = await _cache.GetOrFetchDiffAsync(42, knownHeadCommitId: "abc123");

        Assert.Same(diff, second);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrFetchDiffAsync_CommitComparisonIsCaseInsensitive()
    {
        var diff = new PullRequestDiff
        {
            Title = "Test",
            Status = "active",
            LastSourceCommitId = "ABC123DEF",
            Files = new List<FileChange>()
        };
        _inner.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(diff);

        await _cache.GetOrFetchDiffAsync(42);
        var second = await _cache.GetOrFetchDiffAsync(42, knownHeadCommitId: "abc123def");

        Assert.Same(diff, second);
        await _inner.Received(1).GetDiffAsync(42, Arg.Any<CancellationToken>());
    }
}
