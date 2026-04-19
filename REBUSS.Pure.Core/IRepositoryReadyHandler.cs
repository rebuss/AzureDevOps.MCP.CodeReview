namespace REBUSS.Pure.Core;

/// <summary>
/// Invoked by <see cref="IRepositoryDownloadOrchestrator"/> after a repository
/// archive has been successfully downloaded and extracted.
/// Handlers run fire-and-forget via <c>Task.Run</c> — they must not block the orchestrator.
/// The cancellation token is tied to <c>ApplicationStopping</c>.
/// </summary>
public interface IRepositoryReadyHandler
{
    /// <summary>
    /// Called when the repository is ready for processing.
    /// </summary>
    /// <param name="repoPath">
    /// Path to the extracted repository directory.
    /// WARNING: This path may be deleted if a new PR download supersedes the current one.
    /// Implementations must handle <see cref="IOException"/> / <see cref="DirectoryNotFoundException"/> gracefully.
    /// </param>
    /// <param name="prNumber">The pull request number.</param>
    /// <param name="ct">Cancellation token linked to application shutdown.</param>
    Task OnRepositoryReadyAsync(string repoPath, int prNumber, CancellationToken ct);
}
