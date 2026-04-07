namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Owns background enrichment of pull-request diffs. Lets <c>get_pr_metadata</c>
/// kick off enrichment and time-bound the wait, while <c>get_pr_content</c>
/// reuses the in-flight or completed result with its own fresh timeout window.
///
/// The background body always runs under the host's
/// <c>IHostApplicationLifetime.ApplicationStopping</c> token — the caller's
/// <see cref="CancellationToken"/> only governs the wait, never the work.
/// This is the load-bearing semantic that makes the progressive workflow
/// possible.
/// </summary>
public interface IPrEnrichmentOrchestrator
{
    /// <summary>
    /// Start (or join) a background enrichment job for the given PR + head SHA.
    /// Returns immediately. Idempotent for the same <paramref name="prNumber"/>
    /// and <paramref name="headSha"/>; supersedes any existing job for a
    /// different SHA on the same PR.
    /// </summary>
    /// <param name="prNumber">Pull request number.</param>
    /// <param name="headSha">Head commit SHA — sourced from
    /// <c>FullPullRequestMetadata.LastMergeSourceCommitId</c>.</param>
    /// <param name="safeBudgetTokens">Page-allocator budget the result should be sized for.</param>
    void TriggerEnrichment(int prNumber, string headSha, int safeBudgetTokens);

    /// <summary>
    /// Awaits the result of an in-flight or completed enrichment job, bounded
    /// by <paramref name="ct"/>. Throws <see cref="OperationCanceledException"/>
    /// tied to <paramref name="ct"/> if the wait expires.
    ///
    /// The background body itself is <b>not</b> cancelled when
    /// <paramref name="ct"/> fires — that is the whole point of this API.
    /// </summary>
    /// <exception cref="InvalidOperationException">No job has been triggered for the given PR.</exception>
    Task<PrEnrichmentResult> WaitForEnrichmentAsync(int prNumber, CancellationToken ct);

    /// <summary>
    /// Returns the current job state for a PR without awaiting, or <c>null</c>
    /// if no job exists. Used by the content tool to discover the head SHA
    /// (and short-circuit on Ready/Failed) without re-fetching metadata.
    /// </summary>
    PrEnrichmentJobSnapshot? TryGetSnapshot(int prNumber);
}
