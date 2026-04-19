using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services;

/// <summary>
/// Session-scoped cache for <see cref="PullRequestDiff"/> keyed by PR number.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Concurrent cache misses for the same PR are deduplicated — only one fetch
/// is issued and all waiters share the result via a <see cref="Lazy{T}"/>
/// wrapper in <see cref="_inflight"/>.
/// Failed fetches are not cached — errors propagate to all waiters and the
/// in-flight entry is evicted so the next call retries.
/// Supports staleness detection: when a known head commit ID is provided
/// and differs from the cached diff's <see cref="PullRequestDiff.LastSourceCommitId"/>,
/// the stale entry is evicted and a fresh diff is fetched.
/// </summary>
public sealed class PullRequestDiffCache : IPullRequestDiffCache
{
    private readonly IPullRequestDataProvider _inner;
    private readonly ILogger<PullRequestDiffCache> _logger;
    private readonly ConcurrentDictionary<int, PullRequestDiff> _cache = new();
    private readonly ConcurrentDictionary<int, Lazy<Task<PullRequestDiff>>> _inflight = new();

    public PullRequestDiffCache(IPullRequestDataProvider inner, ILogger<PullRequestDiffCache> logger)
    {
        _inner = inner;
        _logger = logger;
    }

    public async Task<PullRequestDiff> GetOrFetchDiffAsync(int prNumber, string? knownHeadCommitId = null, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(prNumber, out var cached))
        {
            if (IsStale(cached, knownHeadCommitId, prNumber))
            {
                _cache.TryRemove(prNumber, out _);
                _inflight.TryRemove(prNumber, out _);
            }
            else
            {
                _logger.LogDebug("PR diff cache hit for PR #{PrNumber}", prNumber);
                return cached;
            }
        }

        _logger.LogInformation("PR diff cache miss for PR #{PrNumber}, fetching from provider", prNumber);

        // Use CancellationToken.None in the factory so that a single caller's
        // cancellation does not kill the shared fetch for all waiters.
        // Each caller cancels around their own await via ct.
        var lazy = _inflight.GetOrAdd(prNumber,
            _ => new Lazy<Task<PullRequestDiff>>(() => _inner.GetDiffAsync(prNumber, CancellationToken.None)));

        try
        {
            var diff = await lazy.Value.WaitAsync(ct);
            _cache.TryAdd(prNumber, diff);
            _inflight.TryRemove(KeyValuePair.Create(prNumber, lazy));
            return diff;
        }
        catch
        {
            // Evict this entry so the next caller gets a fresh attempt.
            // For caller-only cancellation (WaitAsync threw but the inner task is
            // still running), TryRemove uses the exact KeyValuePair — if another
            // caller already swapped in a new Lazy, this is a harmless no-op.
            _inflight.TryRemove(KeyValuePair.Create(prNumber, lazy));
            throw;
        }
    }

    private bool IsStale(PullRequestDiff cached, string? knownHeadCommitId, int prNumber)
    {
        if (knownHeadCommitId is null)
            return false;

        if (cached.LastSourceCommitId is null)
            return false;

        if (string.Equals(cached.LastSourceCommitId, knownHeadCommitId, StringComparison.OrdinalIgnoreCase))
            return false;

        _logger.LogInformation(
            "PR diff cache stale for PR #{PrNumber}: cached commit {CachedCommit}, known commit {KnownCommit}",
            prNumber,
            cached.LastSourceCommitId.Length > 7 ? cached.LastSourceCommitId[..7] : cached.LastSourceCommitId,
            knownHeadCommitId.Length > 7 ? knownHeadCommitId[..7] : knownHeadCommitId);
        return true;
    }
}
