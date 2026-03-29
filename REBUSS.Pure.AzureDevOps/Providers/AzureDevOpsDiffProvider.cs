using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.AzureDevOps.Properties;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.AzureDevOps.Providers
{
    /// <summary>
    /// Fetches structured diff content from Azure DevOps by:
    /// 1. Reading PR details (title, status, refs).
    /// 2. Reading the last iteration to get the base and target commit SHAs.
    /// 3. Enumerating changed files from the iteration changes endpoint.
    /// 4. For each file, fetching raw content at both commits.
    /// 5. Producing structured diff hunks via <see cref="StructuredDiffBuilder"/>.
    /// </summary>
    public class AzureDevOpsDiffProvider
    {
        private const int FullRewriteMinLineCount = 10;

        private readonly IAzureDevOpsApiClient _apiClient;
        private readonly IPullRequestMetadataParser _metadataParser;
        private readonly IIterationInfoParser _iterationParser;
        private readonly IFileChangesParser _changesParser;
        private readonly IStructuredDiffBuilder _diffBuilder;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<AzureDevOpsDiffProvider> _logger;

        public AzureDevOpsDiffProvider(
            IAzureDevOpsApiClient apiClient,
            IPullRequestMetadataParser metadataParser,
            IIterationInfoParser iterationParser,
            IFileChangesParser changesParser,
            IStructuredDiffBuilder diffBuilder,
            IFileClassifier fileClassifier,
            ILogger<AzureDevOpsDiffProvider> logger)
        {
            _apiClient = apiClient;
            _metadataParser = metadataParser;
            _iterationParser = iterationParser;
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

                var (metadata, files, baseCommit, targetCommit) = await FetchPullRequestDataAsync(prNumber);

                _logger.LogInformation(
                    "PR #{PrNumber}: {FileCount} file(s) changed, building diffs (base={BaseCommit}, target={TargetCommit})",
                    prNumber, files.Count,
                    baseCommit.Length > 7 ? baseCommit[..7] : baseCommit,
                    targetCommit.Length > 7 ? targetCommit[..7] : targetCommit);

                await BuildFileDiffsAsync(files, baseCommit, targetCommit, cancellationToken);

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
                throw new PullRequestNotFoundException(string.Format(Resources.ErrorPullRequestNotFound, prNumber), ex);
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

                var (metadata, files, baseCommit, targetCommit) = await FetchPullRequestDataAsync(prNumber);

                var normalizedPath = NormalizePath(path);
                var matchingFiles = files
                    .Where(f => NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingFiles.Count == 0)
                {
                    _logger.LogWarning("File '{Path}' not found in PR #{PrNumber}", path, prNumber);
                    throw new FileNotFoundInPullRequestException(
                            string.Format(Resources.ErrorFileNotFoundInPullRequest, path, prNumber));
                }

                await BuildFileDiffsAsync(matchingFiles, baseCommit, targetCommit, cancellationToken);

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
                throw new PullRequestNotFoundException(string.Format(Resources.ErrorPullRequestNotFound, prNumber), ex);
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

        private async Task<(PullRequestMetadata metadata, List<FileChange> files, string baseCommit, string targetCommit)>
            FetchPullRequestDataAsync(int prNumber)
        {
            var metadata  = _metadataParser.Parse(await _apiClient.GetPullRequestDetailsAsync(prNumber));
            var iteration = _iterationParser.ParseLast(await _apiClient.GetPullRequestIterationsAsync(prNumber));
            var files     = await FetchFileChangesAsync(prNumber, iteration.Id);

            return (metadata, files, iteration.BaseCommit, iteration.TargetCommit);
        }

        private static string NormalizePath(string path) => path.TrimStart('/');

        private async Task<List<FileChange>> FetchFileChangesAsync(int prNumber, int iterationId)
        {
            var changesJson = iterationId > 0
                ? await _apiClient.GetPullRequestIterationChangesAsync(prNumber, iterationId)
                : "{}";

            return _changesParser.Parse(changesJson);
        }

        internal const int MaxParallelDiffRequests = 15;

        private async Task BuildFileDiffsAsync(
                List<FileChange> files,
                string baseCommit,
                string targetCommit,
                CancellationToken cancellationToken)
            {
                if (string.IsNullOrEmpty(baseCommit) || string.IsNullOrEmpty(targetCommit))
                {
                    foreach (var file in files)
                    {
                        _logger.LogDebug(
                            "Skipping diff for '{FilePath}': commit SHAs not resolved (base={BaseCommit}, target={TargetCommit})",
                            file.Path, baseCommit ?? "<null>", targetCommit ?? "<null>");
                    }
                    return;
                }

                // Pre-filter skippable files synchronously (no I/O needed)
                foreach (var file in files)
                {
                    var skipReason = GetSkipReason(file);
                    if (skipReason is not null)
                    {
                        file.SkipReason = skipReason;
                        _logger.LogDebug(
                            "Skipping diff for '{FilePath}': {SkipReason}",
                            file.Path, skipReason);
                    }
                }

                var filesToDiff = files.Where(f => f.SkipReason is null).ToList();

                await Parallel.ForEachAsync(
                    filesToDiff,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelDiffRequests,
                        CancellationToken = cancellationToken
                    },
                    async (file, ct) =>
                    {
                        var fileSw = Stopwatch.StartNew();

                        var baseContentTask = _apiClient.GetFileContentAtCommitAsync(baseCommit, file.Path);
                        var targetContentTask = _apiClient.GetFileContentAtCommitAsync(targetCommit, file.Path);
                        await Task.WhenAll(baseContentTask, targetContentTask);

                        var baseContent = await baseContentTask;
                        var targetContent = await targetContentTask;
                        file.Hunks = _diffBuilder.Build(file.Path, baseContent, targetContent);

                        if (IsFullFileRewrite(baseContent, targetContent, file.Hunks))
                        {
                            file.SkipReason = Resources.SkipReasonFullFileRewrite;
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
                    });
            }

        /// <summary>
        /// Returns a skip reason if the file should not have its diff computed,
        /// or <c>null</c> if normal diff logic should proceed.
        /// </summary>
        internal string? GetSkipReason(FileChange file)
        {
            if (string.Equals(file.ChangeType, "delete", StringComparison.OrdinalIgnoreCase))
                return Resources.SkipReasonFileDeleted;

            if (string.Equals(file.ChangeType, "rename", StringComparison.OrdinalIgnoreCase))
                return Resources.SkipReasonFileRenamed;

            var classification = _fileClassifier.Classify(file.Path);

            if (classification.IsBinary)
                return Resources.SkipReasonBinaryFile;

            if (classification.IsGenerated)
                return Resources.SkipReasonGeneratedFile;

            return null;
        }

        /// <summary>
        /// Detects a full-file rewrite: both contents are non-trivial but the diff
        /// contains zero context (unchanged) lines, indicating every line changed.
        /// </summary>
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
                Title         = metadata.Title,
                Status        = metadata.Status,
                SourceBranch  = metadata.SourceBranch,
                TargetBranch  = metadata.TargetBranch,
                SourceRefName = metadata.SourceRefName,
                TargetRefName = metadata.TargetRefName,
                Files         = files
            };
        }
    }
}
