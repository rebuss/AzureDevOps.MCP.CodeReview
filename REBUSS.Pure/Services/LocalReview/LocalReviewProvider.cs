using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.LocalReview
{
    /// <summary>
    /// Produces structured review data for local git changes by:
    /// <list type="number">
    ///   <item>Resolving the git repository root via <see cref="IWorkspaceRootProvider"/>.</item>
    ///   <item>Enumerating changed files via <see cref="ILocalGitClient"/>.</item>
    ///   <item>Classifying each file via <see cref="IFileClassifier"/>.</item>
    ///   <item>Parsing the unified-diff (<c>git diff -p</c>) output via <see cref="UnifiedPatchParser"/>.</item>
    /// </list>
    /// Reuses domain models (<see cref="FileChange"/>, <see cref="DiffHunk"/>) so tool handlers
    /// can apply the same output mapping as PR-based tools.
    /// </summary>
    /// <remarks>
    /// The diff is sourced from a single <c>git diff -p</c> invocation per scope. The earlier
    /// path that read base/target content via two parallel <c>git show</c> calls per file and
    /// re-diffed in C# was prone to silent failures under IDE-side git contention (see Feature
    /// 023 deferral notes). Using git's own diff output is authoritative and matches what the
    /// user sees in the CLI.
    /// </remarks>
    public class LocalReviewProvider : ILocalReviewProvider
    {
        private readonly IWorkspaceRootProvider _workspaceRootProvider;
        private readonly ILocalGitClient _gitClient;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<LocalReviewProvider> _logger;

        // Short-lived snapshot of the last full unified-diff result, keyed by repo root + scope.
        // Lets GetFileDiffAsync serve repeated single-file lookups in O(1) after a single
        // GetAllFileDiffsAsync (typical orchestrator → per-file flow) without re-running git.
        // The TTL bounds staleness because this provider is registered as a singleton and the
        // working tree can change between calls; expired snapshots fall back to a fresh git diff.
        private static readonly long CacheTtlMs = 5_000;
        private DiffCacheSnapshot? _diffCache;

        public LocalReviewProvider(
            IWorkspaceRootProvider workspaceRootProvider,
            ILocalGitClient gitClient,
            IFileClassifier fileClassifier,
            ILogger<LocalReviewProvider> logger)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _gitClient = gitClient;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public async Task<LocalReviewFiles> GetFilesAsync(
            LocalReviewScope scope,
            CancellationToken cancellationToken = default)
        {
            var repoRoot = ResolveRepositoryRootOrThrow();

            _logger.LogInformation(Resources.LogLocalReviewProviderFetchingFiles, scope, repoRoot);
            var sw = Stopwatch.StartNew();

            var currentBranch = await _gitClient.GetCurrentBranchAsync(repoRoot, cancellationToken);
            var statuses = await _gitClient.GetChangedFilesAsync(repoRoot, scope, cancellationToken);

            var classified = statuses
                .Select(s => (status: s, classification: _fileClassifier.Classify(s.Path)))
                .ToList();

            var files = classified
                .Select(x => BuildFileInfo(x.status, x.classification))
                .ToList();

            var summary = BuildSummary(
                classified.Select(x => x.classification).ToList(),
                files);

            sw.Stop();

            _logger.LogInformation(
                Resources.LogLocalReviewProviderFilesCompleted,
                scope, files.Count, sw.ElapsedMilliseconds);

            return new LocalReviewFiles
            {
                RepositoryRoot = repoRoot,
                Scope = scope.ToString(),
                CurrentBranch = currentBranch,
                Files = files,
                Summary = summary
            };
        }

        public Task<PullRequestDiff> GetAllFileDiffsAsync(
            LocalReviewScope scope,
            CancellationToken cancellationToken = default)
        {
            var repoRoot = ResolveRepositoryRootOrThrow();
            return LoadAllFileDiffsAsync(repoRoot, scope, cancellationToken);
        }

        public async Task<PullRequestDiff> GetFileDiffAsync(
            string filePath,
            LocalReviewScope scope,
            CancellationToken cancellationToken = default)
        {
            var repoRoot = ResolveRepositoryRootOrThrow();

            var allDiffs = TryReadCache(repoRoot, scope)
                           ?? await LoadAllFileDiffsAsync(repoRoot, scope, cancellationToken);

            var normalizedRequest = NormalizePath(filePath);
            var match = allDiffs.Files.FirstOrDefault(
                f => NormalizePath(f.Path).Equals(normalizedRequest, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _logger.LogWarning(Resources.LogLocalReviewProviderFileNotFound, filePath, scope);
                throw new LocalFileNotFoundException(
                    string.Format(Resources.ErrorFileNotFoundAmongLocalChanges, filePath, scope));
            }

            return BuildDiffEnvelope(scope, new List<FileChange> { match });
        }

        private async Task<PullRequestDiff> LoadAllFileDiffsAsync(
            string repoRoot,
            LocalReviewScope scope,
            CancellationToken cancellationToken)
        {
            _logger.LogInformation(Resources.LogLocalReviewProviderFetchingUnifiedDiff, scope, repoRoot);
            var sw = Stopwatch.StartNew();

            var rawDiff = await _gitClient.GetUnifiedDiffAsync(repoRoot, scope, cancellationToken);
            var fileChanges = UnifiedPatchParser.ParseMultiFile(rawDiff);

            foreach (var fc in fileChanges)
                ApplyClassifierSkipReason(fc);

            sw.Stop();

            _logger.LogInformation(
                Resources.LogLocalReviewProviderUnifiedDiffCompleted,
                scope, fileChanges.Count, sw.ElapsedMilliseconds);

            var diff = BuildDiffEnvelope(scope, fileChanges);
            StoreCache(repoRoot, scope, diff);
            return diff;
        }

        private PullRequestDiff? TryReadCache(string repoRoot, LocalReviewScope scope)
        {
            // Snapshot the field once: reference assignment is atomic so a concurrent writer
            // can only swap in a newer entry, not corrupt our local view.
            var snapshot = _diffCache;
            if (snapshot is null)
                return null;
            if (!string.Equals(snapshot.RepositoryRoot, repoRoot, StringComparison.Ordinal))
                return null;
            if (snapshot.ScopeKind != scope.Kind)
                return null;
            if (!string.Equals(snapshot.ScopeBaseBranch, scope.BaseBranch, StringComparison.Ordinal))
                return null;
            if (Environment.TickCount64 > snapshot.ExpiresAtTicks)
                return null;

            return snapshot.Diff;
        }

        private void StoreCache(string repoRoot, LocalReviewScope scope, PullRequestDiff diff)
        {
            _diffCache = new DiffCacheSnapshot(
                repoRoot,
                scope.Kind,
                scope.BaseBranch,
                diff,
                Environment.TickCount64 + CacheTtlMs);
        }

        private sealed record DiffCacheSnapshot(
            string RepositoryRoot,
            LocalReviewScopeKind ScopeKind,
            string? ScopeBaseBranch,
            PullRequestDiff Diff,
            long ExpiresAtTicks);

        // --- Private helpers ------------------------------------------------------

        private string ResolveRepositoryRootOrThrow()
        {
            var root = _workspaceRootProvider.ResolveRepositoryRoot();
            if (root is null)
            {
                throw new LocalRepositoryNotFoundException(Resources.ErrorNoGitRepositoryRoot);
            }

            return root;
        }

        private void ApplyClassifierSkipReason(FileChange fc)
        {
            // Honour skip reasons git already established (e.g. "binary file" from
            // "Binary files ... differ"). Renamed/deleted files keep their hunks
            // (when present) so the review can still see the content delta.
            if (fc.SkipReason is not null)
                return;

            var classification = _fileClassifier.Classify(fc.Path);
            if (classification.IsBinary)
            {
                ClearAsSkipped(fc, "binary file");
            }
            else if (classification.IsGenerated)
            {
                ClearAsSkipped(fc, "generated file");
            }
        }

        private static void ClearAsSkipped(FileChange fc, string reason)
        {
            fc.SkipReason = reason;
            fc.Hunks = new List<DiffHunk>();
            fc.Additions = 0;
            fc.Deletions = 0;
        }

        private static PullRequestDiff BuildDiffEnvelope(LocalReviewScope scope, List<FileChange> files) => new()
        {
            Title = $"Local changes ({scope})",
            Status = "local",
            SourceBranch = string.Empty,
            TargetBranch = string.Empty,
            SourceRefName = string.Empty,
            TargetRefName = string.Empty,
            Files = files
        };

        internal string? GetSkipReason(LocalFileStatus status)
        {
            // Used by GetFilesAsync-style callers that need a skip-reason without first
            // running the diff parser. Mirrors the classifier rules in
            // ApplyClassifierSkipReason — rename/delete are NOT skipped here because the
            // diff parser will return their actual content.
            if (status.Status == 'D')
                return "file deleted";
            if (status.Status == 'R')
                return "file renamed";

            var classification = _fileClassifier.Classify(status.Path);

            if (classification.IsBinary)
                return "binary file";
            if (classification.IsGenerated)
                return "generated file";

            return null;
        }

        private static PullRequestFileInfo BuildFileInfo(
            LocalFileStatus status,
            FileClassification classification)
        {
            return new PullRequestFileInfo
            {
                Path = status.Path.TrimStart('/'),
                Status = MapStatus(status.Status),
                Additions = 0,  // populated during diff; not available from status alone
                Deletions = 0,
                Changes = 0,
                Extension = classification.Extension,
                IsBinary = classification.IsBinary,
                IsGenerated = classification.IsGenerated,
                IsTestFile = classification.IsTestFile,
                ReviewPriority = classification.ReviewPriority
            };
        }

        private static string MapStatus(char code) => code switch
        {
            'A' or '?' => "added",
            'M' => "modified",
            'D' => "removed",
            'R' => "renamed",
            _ => code.ToString()
        };

        private static string NormalizePath(string path) =>
            path.TrimStart('/').Replace('\\', '/');

        private static PullRequestFilesSummary BuildSummary(
            List<FileClassification> classifications,
            List<PullRequestFileInfo> files)
        {
            return new PullRequestFilesSummary
            {
                SourceFiles = classifications.Count(c => c.Category == FileCategory.Source),
                TestFiles = classifications.Count(c => c.Category == FileCategory.Test),
                ConfigFiles = classifications.Count(c => c.Category == FileCategory.Config),
                DocsFiles = classifications.Count(c => c.Category == FileCategory.Docs),
                BinaryFiles = classifications.Count(c => c.Category == FileCategory.Binary),
                GeneratedFiles = classifications.Count(c => c.Category == FileCategory.Generated),
                HighPriorityFiles = files.Count(f => f.ReviewPriority == "high")
            };
        }
    }
}
