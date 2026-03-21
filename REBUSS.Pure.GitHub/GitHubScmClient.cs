using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Configuration;
using REBUSS.Pure.GitHub.Providers;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub;

/// <summary>
/// GitHub implementation of <see cref="IScmClient"/>.
/// Delegates to the fine-grained providers and enriches metadata
/// with provider-agnostic <see cref="FullPullRequestMetadata.WebUrl"/>
/// and <see cref="FullPullRequestMetadata.RepositoryFullName"/>.
/// </summary>
public sealed class GitHubScmClient : IScmClient
{
    private readonly GitHubDiffProvider _diffProvider;
    private readonly GitHubMetadataProvider _metadataProvider;
    private readonly GitHubFilesProvider _filesProvider;
    private readonly GitHubFileContentProvider _contentProvider;
    private readonly GitHubOptions _options;

    public string ProviderName => "GitHub";

    public GitHubScmClient(
        GitHubDiffProvider diffProvider,
        GitHubMetadataProvider metadataProvider,
        GitHubFilesProvider filesProvider,
        GitHubFileContentProvider contentProvider,
        IOptions<GitHubOptions> options)
    {
        _diffProvider = diffProvider;
        _metadataProvider = metadataProvider;
        _filesProvider = filesProvider;
        _contentProvider = contentProvider;
        _options = options.Value;
    }

    public Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken ct = default)
        => _diffProvider.GetDiffAsync(prNumber, ct);

    public Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken ct = default)
        => _diffProvider.GetFileDiffAsync(prNumber, path, ct);

    public async Task<FullPullRequestMetadata> GetMetadataAsync(int prNumber, CancellationToken ct = default)
    {
        var metadata = await _metadataProvider.GetMetadataAsync(prNumber, ct);

        metadata.RepositoryFullName = $"{_options.Owner}/{_options.RepositoryName}";

        metadata.WebUrl = $"https://github.com/{_options.Owner}/{_options.RepositoryName}/pull/{prNumber}";

        return metadata;
    }

    public Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken ct = default)
        => _filesProvider.GetFilesAsync(prNumber, ct);

    public Task<FileContent> GetFileContentAsync(string path, string gitRef, CancellationToken ct = default)
        => _contentProvider.GetFileContentAsync(path, gitRef, ct);
}
