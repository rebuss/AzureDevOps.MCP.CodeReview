using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.AzureDevOps.Properties;
using REBUSS.Pure.AzureDevOps.Providers;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.AzureDevOps;

/// <summary>
/// Azure DevOps implementation of <see cref="IScmClient"/>.
/// Delegates to the existing fine-grained providers and enriches metadata
/// with provider-agnostic <see cref="FullPullRequestMetadata.WebUrl"/>
/// and <see cref="FullPullRequestMetadata.RepositoryFullName"/>.
/// </summary>
public sealed class AzureDevOpsScmClient : IScmClient
{
    private readonly AzureDevOpsDiffProvider _diffProvider;
    private readonly AzureDevOpsMetadataProvider _metadataProvider;
    private readonly AzureDevOpsFilesProvider _filesProvider;
    private readonly AzureDevOpsRepositoryArchiveProvider _archiveProvider;
    private readonly AzureDevOpsOptions _options;

    public string ProviderName => Resources.ProviderDisplayName;

    public AzureDevOpsScmClient(
        AzureDevOpsDiffProvider diffProvider,
        AzureDevOpsMetadataProvider metadataProvider,
        AzureDevOpsFilesProvider filesProvider,
        AzureDevOpsRepositoryArchiveProvider archiveProvider,
        IOptions<AzureDevOpsOptions> options)
    {
        _diffProvider = diffProvider;
        _metadataProvider = metadataProvider;
        _filesProvider = filesProvider;
        _archiveProvider = archiveProvider;
        _options = options.Value;
    }

    public Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken ct = default)
        => _diffProvider.GetDiffAsync(prNumber, ct);

    public Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken ct = default)
        => _diffProvider.GetFileDiffAsync(prNumber, path, ct);

    public async Task<FullPullRequestMetadata> GetMetadataAsync(int prNumber, CancellationToken ct = default)
    {
        var metadata = await _metadataProvider.GetMetadataAsync(prNumber, ct);

        metadata.RepositoryFullName =
            $"{_options.OrganizationName}/{_options.ProjectName}/{_options.RepositoryName}";

        metadata.WebUrl =
            $"https://{Names.Domain}/{_options.OrganizationName}/{_options.ProjectName}" +
            $"/_git/{_options.RepositoryName}/pullrequest/{prNumber}";

        return metadata;
    }

    public Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken ct = default)
        => _filesProvider.GetFilesAsync(prNumber, ct);

    public Task DownloadRepositoryZipAsync(string commitRef, string destinationPath, CancellationToken ct = default)
        => _archiveProvider.DownloadRepositoryZipAsync(commitRef, destinationPath, ct);
}
