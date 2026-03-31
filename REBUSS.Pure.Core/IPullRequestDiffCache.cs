using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core;

/// <summary>
/// Caches <see cref="PullRequestDiff"/> per PR number within a session,
/// eliminating duplicate API calls between metadata and content tool handlers.
/// When <paramref name="knownHeadCommitId"/> is supplied, the cache performs
/// staleness detection — if the cached diff was built from a different source
/// commit, the entry is evicted and re-fetched.
/// </summary>
public interface IPullRequestDiffCache
{
    /// <summary>
    /// Returns the cached diff for <paramref name="prNumber"/>, or fetches it
    /// from the underlying provider on a cache miss.
    /// </summary>
    /// <param name="prNumber">The pull request number.</param>
    /// <param name="knownHeadCommitId">
    /// Optional. The latest source-branch commit SHA known to the caller
    /// (typically from <see cref="Models.FullPullRequestMetadata.LastMergeSourceCommitId"/>).
    /// When provided and different from the cached diff's
    /// <see cref="Models.PullRequestDiff.LastSourceCommitId"/>, the cached entry
    /// is evicted and a fresh diff is fetched.
    /// When <c>null</c>, staleness detection is skipped (cache-hit returned as-is).
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="Exceptions.PullRequestNotFoundException">
    /// Propagated from the underlying provider when the PR does not exist.
    /// </exception>
    Task<PullRequestDiff> GetOrFetchDiffAsync(int prNumber, string? knownHeadCommitId = null, CancellationToken ct = default);
}
