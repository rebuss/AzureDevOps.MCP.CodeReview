using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core;

/// <summary>
/// Unified source-control management client. Each provider (Azure DevOps, GitHub, GitLab, local Git)
/// implements this interface to supply data for code review tools and analysis.
///
/// Composed of fine-grained capability interfaces so that providers can indicate
/// which capabilities they support and tool handlers can depend on narrow contracts.
/// </summary>
public interface IScmClient : IPullRequestDataProvider, IFileContentDataProvider
{
    /// <summary>
    /// Human-readable provider name (e.g. "Azure DevOps", "GitHub", "Local Git").
    /// Used in tool descriptions and logging.
    /// </summary>
    string ProviderName { get; }
}

/// <summary>
/// Provides pull-request-level data: metadata, diff, and file list.
/// </summary>
public interface IPullRequestDataProvider
{
    /// <exception cref="Exceptions.PullRequestNotFoundException">Thrown when PR is not found.</exception>
    Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken ct = default);

    /// <exception cref="Exceptions.PullRequestNotFoundException">Thrown when PR is not found.</exception>
    /// <exception cref="Exceptions.FileNotFoundInPullRequestException">Thrown when the file does not exist in the PR.</exception>
    Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken ct = default);

    /// <exception cref="Exceptions.PullRequestNotFoundException">Thrown when PR is not found.</exception>
    Task<FullPullRequestMetadata> GetMetadataAsync(int prNumber, CancellationToken ct = default);

    /// <exception cref="Exceptions.PullRequestNotFoundException">Thrown when PR is not found.</exception>
    Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken ct = default);
}

/// <summary>
/// Provides raw file content at a specific Git ref.
/// </summary>
public interface IFileContentDataProvider
{
    /// <exception cref="Exceptions.FileContentNotFoundException">
    /// Thrown when the file does not exist at the given ref, or the ref is invalid.
    /// </exception>
    Task<FileContent> GetFileContentAsync(string path, string gitRef, CancellationToken ct = default);
}
