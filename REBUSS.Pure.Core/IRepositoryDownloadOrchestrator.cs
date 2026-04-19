using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core;

/// <summary>
/// Manages the repository download lifecycle: deduplication, state tracking,
/// and background execution. Triggered by the metadata tool handler.
/// </summary>
public interface IRepositoryDownloadOrchestrator
{
    /// <summary>
    /// Triggers a background repository download. Returns immediately (fire-and-forget).
    /// No-op if a download for the same PR/commit is already in progress or completed.
    /// If a different PR is requested, cancels the old download and starts a new one.
    /// </summary>
    void TriggerDownload(int prNumber, string commitRef);

    /// <summary>Returns the current download state.</summary>
    RepositoryDownloadState GetState();

    /// <summary>
    /// Waits for the download to complete and returns the extracted repository path,
    /// or <c>null</c> if the download failed.
    /// </summary>
    Task<string?> GetExtractedPathAsync(CancellationToken ct = default);
}
