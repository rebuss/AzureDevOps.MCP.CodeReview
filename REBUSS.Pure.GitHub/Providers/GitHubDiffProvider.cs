using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.GitHub.Api;
using REBUSS.Pure.GitHub.Parsers;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.GitHub.Providers;

/// <summary>
/// Fetches structured diff content from GitHub by:
/// 1. Reading PR details (title, state, head/base SHAs).
/// 2. Enumerating changed files from the PR files endpoint.
/// 3. For each file, fetching raw content at both commits.
/// 4. Producing structured diff hunks via <see cref="IStructuredDiffBuilder"/>.
/// </summary>
public class GitHubDiffProvider
{
    private const int FullRewriteMinLineCount = 10;

    private readonly IGitHubApiClient _apiClient;
    private readonly IGitHubPullRequestParser _prParser;
    private readonly IGitHubFileChangesParser _changesParser;
    private readonly IStructuredDiffBuilder _diffBuilder;
    private readonly IFileClassifier _fileClassifier;
    private readonly ILogger<GitHubDiffProvider> _logger;

    public GitHubDiffProvider(
        IGitHubApiClient apiClient,
        IGitHubPullRequestParser prParser,
        IGitHubFileChangesParser changesParser,
        IStructuredDiffBuilder diffBuilder,
        IFileClassifier fileClassifier,
        ILogger<GitHubDiffProvider> logger)
    {
        _apiClient = apiClient;
        _prParser = prParser;
        _changesParser = changesParser;
        _diffBuilder = diffBuilder;
        _fileClassifier = fileClassifier;
        _logger = logger;
    }

    public virtual async Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching diff for PR #{PrNumber}", prNumber);
            var sw = Stopwatch.StartNew();

            var (metadata, files, baseCommit, headCommit) = await FetchPullRequestDataAsync(prNumber);

            _logger.LogInformation(
                "PR #{PrNumber}: {FileCount} file(s) changed, building diffs (base={BaseCommit}, head={HeadCommit})",
                prNumber, files.Count,
                baseCommit.Length > 7 ? baseCommit[..7] : baseCommit,
                headCommit.Length > 7 ? headCommit[..7] : headCommit);

            await BuildFileDiffsAsync(files, baseCommit, headCommit, cancellationToken);

            var result = BuildDiff(metadata, files);
            sw.Stop();

            var totalHunks = result.Files.Sum(f => f.Hunks.Count);
            _logger.LogInformation(
                "Diff for PR #{PrNumber} completed: {FileCount} file(s), {TotalHunks} hunk(s), {ElapsedMs}ms",
                prNumber, files.Count, totalHunks, sw.ElapsedMilliseconds);

            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Pull Request #{PrNumber} not found", prNumber);
            throw new PullRequestNotFoundException($"Pull Request #{prNumber} not found", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching diff for PR #{PrNumber}", prNumber);
            throw;
        }
    }

    public virtual async Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken cancellationToken = default)
    {
        try
        {
            _logger.LogInformation("Fetching diff for file '{Path}' in PR #{PrNumber}", path, prNumber);
            var sw = Stopwatch.StartNew();

            var (metadata, files, baseCommit, headCommit) = await FetchPullRequestDataAsync(prNumber);

            var normalizedPath = NormalizePath(path);
            var matchingFiles = files
                .Where(f => NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matchingFiles.Count == 0)
            {
                _logger.LogWarning("File '{Path}' not found in PR #{PrNumber}", path, prNumber);
                throw new FileNotFoundInPullRequestException(
                    $"File '{path}' not found in Pull Request #{prNumber}");
            }

            await BuildFileDiffsAsync(matchingFiles, baseCommit, headCommit, cancellationToken);

            var result = BuildDiff(metadata, matchingFiles);
            sw.Stop();

            var totalHunks = result.Files.Sum(f => f.Hunks.Count);
            _logger.LogInformation(
                "File diff for '{Path}' in PR #{PrNumber} completed: {TotalHunks} hunk(s), {ElapsedMs}ms",
                path, prNumber, totalHunks, sw.ElapsedMilliseconds);

            return result;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Pull Request #{PrNumber} not found", prNumber);
            throw new PullRequestNotFoundException($"Pull Request #{prNumber} not found", ex);
        }
        catch (FileNotFoundInPullRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error fetching diff for file '{Path}' in PR #{PrNumber}", path, prNumber);
            throw;
        }
    }

    private async Task<(PullRequestMetadata metadata, List<FileChange> files, string baseCommit, string headCommit)>
        FetchPullRequestDataAsync(int prNumber)
    {
        var prJson = await _apiClient.GetPullRequestDetailsAsync(prNumber);
        var metadata = _prParser.Parse(prJson);
        var baseCommit = _prParser.ParseBaseCommit(prJson);
        var headCommit = _prParser.ParseHeadCommit(prJson);

        var filesJson = await _apiClient.GetPullRequestFilesAsync(prNumber);
        var files = _changesParser.Parse(filesJson);

        return (metadata, files, baseCommit, headCommit);
    }

    private static string NormalizePath(string path) => path.TrimStart('/');

    private async Task BuildFileDiffsAsync(
        List<FileChange> files,
        string baseCommit,
        string headCommit,
        CancellationToken cancellationToken)
    {
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(baseCommit) || string.IsNullOrEmpty(headCommit))
            {
                _logger.LogDebug(
                    "Skipping diff for '{FilePath}': commit SHAs not resolved (base={BaseCommit}, head={HeadCommit})",
                    file.Path, baseCommit ?? "<null>", headCommit ?? "<null>");
                continue;
            }

            var skipReason = GetSkipReason(file);
            if (skipReason is not null)
            {
                file.SkipReason = skipReason;
                _logger.LogDebug(
                    "Skipping diff for '{FilePath}': {SkipReason}",
                    file.Path, skipReason);
                continue;
            }

            var fileSw = Stopwatch.StartNew();

            var baseContent = await _apiClient.GetFileContentAtRefAsync(baseCommit, file.Path);
            var headContent = await _apiClient.GetFileContentAtRefAsync(headCommit, file.Path);
            file.Hunks = _diffBuilder.Build(file.Path, baseContent, headContent);

            if (IsFullFileRewrite(baseContent, headContent, file.Hunks))
            {
                file.SkipReason = "full file rewrite";
                file.Hunks = new List<DiffHunk>();
                _logger.LogDebug(
                    "Replaced diff for '{FilePath}': detected full file rewrite",
                    file.Path);
            }
            else
            {
                file.Additions = file.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '+');
                file.Deletions = file.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '-');
            }

            fileSw.Stop();

            _logger.LogDebug(
                "Built diff for '{FilePath}' ({ChangeType}): {HunkCount} hunk(s), {ElapsedMs}ms",
                file.Path, file.ChangeType, file.Hunks.Count, fileSw.ElapsedMilliseconds);
        }
    }

    internal string? GetSkipReason(FileChange file)
    {
        if (string.Equals(file.ChangeType, "delete", StringComparison.OrdinalIgnoreCase))
            return "file deleted";

        if (string.Equals(file.ChangeType, "rename", StringComparison.OrdinalIgnoreCase))
            return "file renamed";

        var classification = _fileClassifier.Classify(file.Path);

        if (classification.IsBinary)
            return "binary file";

        if (classification.IsGenerated)
            return "generated file";

        return null;
    }

    internal static bool IsFullFileRewrite(string? baseContent, string? targetContent, List<DiffHunk> hunks)
    {
        if (string.IsNullOrEmpty(baseContent) || string.IsNullOrEmpty(targetContent))
            return false;

        if (hunks.Count == 0)
            return false;

        var oldLineCount = baseContent.Replace("\r\n", "\n").Split('\n').Length;
        var newLineCount = targetContent.Replace("\r\n", "\n").Split('\n').Length;

        if (oldLineCount < FullRewriteMinLineCount && newLineCount < FullRewriteMinLineCount)
            return false;

        return !hunks.SelectMany(h => h.Lines).Any(l => l.Op == ' ');
    }

    private static PullRequestDiff BuildDiff(
        PullRequestMetadata metadata,
        List<FileChange> files)
    {
        return new PullRequestDiff
        {
            Title = metadata.Title,
            Status = metadata.Status,
            SourceBranch = metadata.SourceBranch,
            TargetBranch = metadata.TargetBranch,
            SourceRefName = metadata.SourceRefName,
            TargetRefName = metadata.TargetRefName,
            Files = files
        };
    }
}
