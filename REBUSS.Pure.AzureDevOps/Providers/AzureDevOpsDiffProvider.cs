using System.Diagnostics;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.AzureDevOps.Configuration;
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
    /// 4. For each file, fetching raw content at both commits (Azure DevOps does not return
    ///    a unified-diff <c>patch</c> field, so the structured diff is rebuilt locally).
    /// 5. Producing structured diff hunks via <see cref="StructuredDiffBuilder"/>.
    /// <para>
    /// When the PR has more than <see cref="AzureDevOpsDiffOptions.ZipFallbackThreshold"/>
    /// changed files, steps 4–5 switch to the ZIP path: the provider downloads the base +
    /// target repository archives once and reads file contents from disk, bounding the
    /// API request count to two regardless of file count. This avoids Azure DevOps TSTU
    /// rate-limit throttling on large refactor PRs.
    /// </para>
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
        private readonly AzureDevOpsRepositoryArchiveProvider _archiveProvider;
        private readonly IOptions<AzureDevOpsDiffOptions> _diffOptions;
        private readonly ILogger<AzureDevOpsDiffProvider> _logger;

        // Note: the archive provider is injected as the concrete type (not
        // IRepositoryArchiveProvider) to avoid a DI cycle — IRepositoryArchiveProvider
        // is forwarded to AzureDevOpsScmClient, which already depends on this provider.
        public AzureDevOpsDiffProvider(
            IAzureDevOpsApiClient apiClient,
            IPullRequestMetadataParser metadataParser,
            IIterationInfoParser iterationParser,
            IFileChangesParser changesParser,
            IStructuredDiffBuilder diffBuilder,
            IFileClassifier fileClassifier,
            AzureDevOpsRepositoryArchiveProvider archiveProvider,
            IOptions<AzureDevOpsDiffOptions> diffOptions,
            ILogger<AzureDevOpsDiffProvider> logger)
        {
            _apiClient = apiClient;
            _metadataParser = metadataParser;
            _iterationParser = iterationParser;
            _changesParser = changesParser;
            _diffBuilder = diffBuilder;
            _fileClassifier = fileClassifier;
            _archiveProvider = archiveProvider;
            _diffOptions = diffOptions;
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
                    ShortSha(baseCommit), ShortSha(targetCommit));

                await BuildFileDiffsAsync(files, baseCommit, targetCommit, cancellationToken);

                var result = BuildDiff(metadata, files, targetCommit);
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

                var result = BuildDiff(metadata, matchingFiles, targetCommit);
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

        private static string ShortSha(string sha) => sha.Length > 7 ? sha[..7] : sha;

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
            if (filesToDiff.Count == 0)
                return;

            var threshold = _diffOptions.Value.ZipFallbackThreshold;
            if (threshold > 0 && filesToDiff.Count > threshold)
            {
                _logger.LogInformation(
                    "Using ZIP-fallback path: {FileCount} file(s) > threshold {Threshold} — " +
                    "downloading base + target archives instead of {RequestCount} per-file requests",
                    filesToDiff.Count, threshold, filesToDiff.Count * 2);
                await BuildFileDiffsViaZipAsync(filesToDiff, baseCommit, targetCommit, cancellationToken);
            }
            else
            {
                await BuildFileDiffsViaApiAsync(filesToDiff, baseCommit, targetCommit, cancellationToken);
            }
        }

        private async Task BuildFileDiffsViaApiAsync(
            List<FileChange> filesToDiff,
            string baseCommit,
            string targetCommit,
            CancellationToken cancellationToken)
        {
            // Thread-safety: each FileChange is a distinct instance — the lambda
            // mutates only its own file (Hunks, SkipReason, Additions, Deletions).
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
                    BuildSingleFileDiff(file, baseContent, targetContent);

                    fileSw.Stop();

                    _logger.LogDebug(
                        "Built diff for '{FilePath}' ({ChangeType}): {HunkCount} hunk(s), {ElapsedMs}ms",
                        file.Path, file.ChangeType, file.Hunks.Count, fileSw.ElapsedMilliseconds);
                });
        }

        private async Task BuildFileDiffsViaZipAsync(
            List<FileChange> filesToDiff,
            string baseCommit,
            string targetCommit,
            CancellationToken cancellationToken)
        {
            // Place our temp under the same parent the orchestrator uses (rebuss-repo-{pid})
            // so RepositoryCleanupService can sweep it on next start if we crash mid-build.
            var instanceDir = Path.Combine(
                Path.GetTempPath(),
                $"rebuss-repo-{Environment.ProcessId}",
                $"diff-{Guid.NewGuid():N}");
            var baseZip = Path.Combine(instanceDir, "base.zip");
            var baseDir = Path.Combine(instanceDir, "base");
            var targetZip = Path.Combine(instanceDir, "target.zip");
            var targetDir = Path.Combine(instanceDir, "target");

            try
            {
                Directory.CreateDirectory(instanceDir);

                var sw = Stopwatch.StartNew();
                await Task.WhenAll(
                    _archiveProvider.DownloadRepositoryZipAsync(baseCommit, baseZip, cancellationToken),
                    _archiveProvider.DownloadRepositoryZipAsync(targetCommit, targetZip, cancellationToken));
                sw.Stop();

                _logger.LogInformation(
                    "Downloaded base + target archives in {ElapsedMs}ms (base={BaseSize}B, target={TargetSize}B)",
                    sw.ElapsedMilliseconds,
                    new FileInfo(baseZip).Length,
                    new FileInfo(targetZip).Length);

                var extractSw = Stopwatch.StartNew();
                ZipFile.ExtractToDirectory(baseZip, baseDir);
                ZipFile.ExtractToDirectory(targetZip, targetDir);
                extractSw.Stop();

                // Free disk space — extracted trees are kept, ZIPs are not needed past this point.
                TryDelete(baseZip);
                TryDelete(targetZip);

                _logger.LogDebug("Extracted archives in {ElapsedMs}ms", extractSw.ElapsedMilliseconds);

                Parallel.ForEach(
                    filesToDiff,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = MaxParallelDiffRequests,
                        CancellationToken = cancellationToken
                    },
                    file =>
                    {
                        var fileSw = Stopwatch.StartNew();

                        var basePath = TryResolveFilePath(baseDir, file.Path);
                        var targetPath = TryResolveFilePath(targetDir, file.Path);

                        var baseContent = basePath is not null ? File.ReadAllText(basePath) : null;
                        var targetContent = targetPath is not null ? File.ReadAllText(targetPath) : null;
                        BuildSingleFileDiff(file, baseContent, targetContent);

                        fileSw.Stop();

                        _logger.LogDebug(
                            "Built diff for '{FilePath}' ({ChangeType}): {HunkCount} hunk(s), {ElapsedMs}ms",
                            file.Path, file.ChangeType, file.Hunks.Count, fileSw.ElapsedMilliseconds);
                    });
            }
            finally
            {
                TryDeleteDirectory(instanceDir);
            }
        }

        private void BuildSingleFileDiff(FileChange file, string? baseContent, string? targetContent)
        {
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
        }

        /// <summary>
        /// Resolves the absolute on-disk path to the given diff file path within an extracted
        /// repository archive. Tries the path directly first and, if absent, falls back to
        /// looking under a single wrapper directory (some archives nest the repository under
        /// a top-level folder named after the commit). Returns <c>null</c> when the file is
        /// not present in any layout — e.g. for added/deleted files which are missing from
        /// one side of the diff.
        /// </summary>
        internal static string? TryResolveFilePath(string rootPath, string diffFilePath)
        {
            if (!Directory.Exists(rootPath))
                return null;

            var normalized = diffFilePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);

            var direct = Path.Combine(rootPath, normalized);
            if (File.Exists(direct) && IsWithin(rootPath, direct))
                return direct;

            // Fallback: a single wrapper directory at the archive root (Azure DevOps does this
            // for some downloads). Only attempt the fallback if the wrapper layout is present
            // and would actually contain the requested path.
            var topLevelDirs = Directory.GetDirectories(rootPath);
            var topLevelFiles = Directory.GetFiles(rootPath);
            if (topLevelDirs.Length == 1 && topLevelFiles.Length == 0)
            {
                var wrapped = Path.Combine(topLevelDirs[0], normalized);
                if (File.Exists(wrapped) && IsWithin(rootPath, wrapped))
                    return wrapped;
            }

            return null;
        }

        /// <summary>
        /// Defence-in-depth: rejects paths that, after resolving <c>..</c> segments / absolute
        /// roots, escape the extracted archive root. The diff paths originate from the Azure
        /// DevOps API (generally trusted) but we never blindly read from a path that canonicalizes
        /// outside <paramref name="rootPath"/>.
        /// </summary>
        private static bool IsWithin(string rootPath, string candidatePath)
        {
            var rootFull = Path.GetFullPath(rootPath);
            var candidateFull = Path.GetFullPath(candidatePath);

            if (!rootFull.EndsWith(Path.DirectorySeparatorChar))
                rootFull += Path.DirectorySeparatorChar;

            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            return candidateFull.StartsWith(rootFull, comparison);
        }

        private void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete temp file {Path}", path);
            }
        }

        private void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to delete temp directory {Path}", path);
            }
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
        /// Detects a full-file rewrite: both contents are non-trivial but every line
        /// in the file was changed (deletions == old line count and additions == new line count).
        /// </summary>
        internal static bool IsFullFileRewrite(string? baseContent, string? targetContent, List<DiffHunk> hunks)
        {
            if (string.IsNullOrEmpty(baseContent) || string.IsNullOrEmpty(targetContent))
                return false;

            if (hunks.Count == 0)
                return false;

            var oldLineCount = baseContent.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;
            var newLineCount = targetContent.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').Length;

            if (oldLineCount < FullRewriteMinLineCount && newLineCount < FullRewriteMinLineCount)
                return false;

            var allLines = hunks.SelectMany(h => h.Lines);
            var deletions = allLines.Count(l => l.Op == '-');
            var additions = allLines.Count(l => l.Op == '+');

            return deletions == oldLineCount && additions == newLineCount;
        }

        private static PullRequestDiff BuildDiff(
            PullRequestMetadata metadata,
            List<FileChange> files,
            string? sourceCommitId)
        {
            return new PullRequestDiff
            {
                Title         = metadata.Title,
                Status        = metadata.Status,
                SourceBranch  = metadata.SourceBranch,
                TargetBranch  = metadata.TargetBranch,
                SourceRefName = metadata.SourceRefName,
                TargetRefName = metadata.TargetRefName,
                Files         = files,
                LastSourceCommitId = sourceCommitId
            };
        }
    }
}
