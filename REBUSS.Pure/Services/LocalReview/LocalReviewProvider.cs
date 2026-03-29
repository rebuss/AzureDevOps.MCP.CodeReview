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
    ///   <item>Building structured diffs via <see cref="IStructuredDiffBuilder"/>.</item>
    /// </list>
    /// Reuses domain models (<see cref="FileChange"/>, <see cref="DiffHunk"/>) so tool handlers
    /// can apply the same output mapping as PR-based tools.
    /// </summary>
    public class LocalReviewProvider : ILocalReviewProvider
    {
        private readonly IWorkspaceRootProvider _workspaceRootProvider;
        private readonly ILocalGitClient _gitClient;
        private readonly IStructuredDiffBuilder _diffBuilder;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<LocalReviewProvider> _logger;

        public LocalReviewProvider(
            IWorkspaceRootProvider workspaceRootProvider,
            ILocalGitClient gitClient,
            IStructuredDiffBuilder diffBuilder,
            IFileClassifier fileClassifier,
            ILogger<LocalReviewProvider> logger)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _gitClient = gitClient;
            _diffBuilder = diffBuilder;
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

        public async Task<PullRequestDiff> GetFileDiffAsync(
            string filePath,
            LocalReviewScope scope,
            CancellationToken cancellationToken = default)
        {
            var repoRoot = ResolveRepositoryRootOrThrow();

            _logger.LogInformation(
                Resources.LogLocalReviewProviderFetchingFileDiff,
                filePath, scope, repoRoot);
            var sw = Stopwatch.StartNew();

            var statuses = await _gitClient.GetChangedFilesAsync(repoRoot, scope, cancellationToken);

            var normalizedRequest = NormalizePath(filePath);
            var match = statuses.FirstOrDefault(
                s => NormalizePath(s.Path).Equals(normalizedRequest, StringComparison.OrdinalIgnoreCase));

            if (match is null)
            {
                _logger.LogWarning(Resources.LogLocalReviewProviderFileNotFound, filePath, scope);
                throw new LocalFileNotFoundException(
                    string.Format(Resources.ErrorFileNotFoundAmongLocalChanges, filePath, scope));
            }

            var fileChange = await BuildFileChangeAsync(repoRoot, match, scope, cancellationToken);

            sw.Stop();
            _logger.LogInformation(
                Resources.LogLocalReviewProviderFileDiffCompleted,
                filePath, scope, fileChange.Hunks.Count, sw.ElapsedMilliseconds);

            return new PullRequestDiff
            {
                Title = $"Local changes ({scope})",
                Status = "local",
                SourceBranch = string.Empty,
                TargetBranch = string.Empty,
                SourceRefName = string.Empty,
                TargetRefName = string.Empty,
                Files = new List<FileChange> { fileChange }
            };
        }

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

        private async Task<FileChange> BuildFileChangeAsync(
            string repoRoot,
            LocalFileStatus status,
            LocalReviewScope scope,
            CancellationToken cancellationToken)
        {
            var fileChange = new FileChange
            {
                Path = status.Path,
                ChangeType = MapChangeType(status.Status)
            };

            var skipReason = GetSkipReason(status);
            if (skipReason is not null)
            {
                fileChange.SkipReason = skipReason;
                return fileChange;
            }

            var (baseRef, targetRef) = GetDiffRefs(scope);

            var baseContent = await _gitClient.GetFileContentAtRefAsync(
                repoRoot, status.Path, baseRef, cancellationToken);
            var targetContent = await _gitClient.GetFileContentAtRefAsync(
                repoRoot, status.Path, targetRef, cancellationToken);

            fileChange.Hunks = _diffBuilder.Build(status.Path, baseContent, targetContent);

            if (IsFullFileRewrite(baseContent, targetContent, fileChange.Hunks))
            {
                fileChange.SkipReason = "full file rewrite";
                fileChange.Hunks = new List<DiffHunk>();
            }
            else
            {
                fileChange.Additions = fileChange.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '+');
                fileChange.Deletions = fileChange.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '-');
            }

            return fileChange;
        }

        private static (string BaseRef, string TargetRef) GetDiffRefs(LocalReviewScope scope)
        {
            // For deleted files, targetRef content will be null (git show returns nothing).
            // LocalGitClient.WorkingTreeRef is a sentinel that causes a filesystem read.
            return scope.Kind switch
            {
                LocalReviewScopeKind.Staged      => ("HEAD", ":0"),
                LocalReviewScopeKind.WorkingTree => ("HEAD", LocalGitClient.WorkingTreeRef),
                LocalReviewScopeKind.BranchDiff  => (scope.BaseBranch!, "HEAD"),
                _ => ("HEAD", LocalGitClient.WorkingTreeRef)
            };
        }

        internal string? GetSkipReason(LocalFileStatus status)
        {
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

        private static bool IsFullFileRewrite(string? baseContent, string? targetContent, List<DiffHunk> hunks)
        {
            const int fullRewriteMinLineCount = 10;

            if (string.IsNullOrEmpty(baseContent) || string.IsNullOrEmpty(targetContent))
                return false;
            if (hunks.Count == 0)
                return false;

            var oldLineCount = baseContent.Replace("\r\n", "\n").Split('\n').Length;
            var newLineCount = targetContent.Replace("\r\n", "\n").Split('\n').Length;

            if (oldLineCount < fullRewriteMinLineCount && newLineCount < fullRewriteMinLineCount)
                return false;

            return !hunks.SelectMany(h => h.Lines).Any(l => l.Op == ' ');
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

        private static string MapChangeType(char code) => code switch
        {
            'A' or '?' => "add",
            'M' => "edit",
            'D' => "delete",
            'R' => "rename",
            _ => "edit"
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
