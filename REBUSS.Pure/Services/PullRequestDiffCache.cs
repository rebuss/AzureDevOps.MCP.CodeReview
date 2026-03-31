using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services;

/// <summary>
/// Session-scoped cache for <see cref="PullRequestDiff"/> keyed by PR number.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Failed fetches are not cached — errors propagate to the caller.
/// Supports staleness detection: when a known head commit ID is provided
/// and differs from the cached diff's <see cref="PullRequestDiff.LastSourceCommitId"/>,
/// the stale entry is evicted and a fresh diff is fetched.
/// </summary>
public sealed class PullRequestDiffCache : IPullRequestDiffCache
{
    private readonly IPullRequestDataProvider _inner;
    private readonly ILogger<PullRequestDiffCache> _logger;
    private readonly ConcurrentDictionary<int, PullRequestDiff> _cache = new();

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
            }
            else
            {
                _logger.LogDebug("PR diff cache hit for PR #{PrNumber}", prNumber);
                return cached;
            }
        }

        _logger.LogInformation("PR diff cache miss for PR #{PrNumber}, fetching from provider", prNumber);
        var diff = await _inner.GetDiffAsync(prNumber, ct);
        _cache.TryAdd(prNumber, diff);
        return diff;
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
