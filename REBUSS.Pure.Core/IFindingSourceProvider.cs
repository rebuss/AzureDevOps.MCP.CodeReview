namespace REBUSS.Pure.Core;

/// <summary>
/// Resolves the post-change ("after") source of a file referenced by a finding.
/// Mode-specific: PR reviews read from the downloaded archive; local reviews read
/// from git (index, working tree, or branch HEAD depending on the local mode).
/// Selected per review by review-key prefix via <see cref="IFindingSourceProviderSelector"/>.
/// </summary>
public interface IFindingSourceProvider
{
    /// <summary>
    /// Returns the after-state source of <paramref name="filePath"/> (relative to the
    /// repository root), or <c>null</c> if the file cannot be resolved (missing, over
    /// the size limit, or source root unavailable). Implementations MUST NOT throw for
    /// routine "not found" cases — return <c>null</c> instead. Cancellation propagates.
    /// </summary>
    Task<string?> GetAfterCodeAsync(string filePath, CancellationToken cancellationToken);
}
