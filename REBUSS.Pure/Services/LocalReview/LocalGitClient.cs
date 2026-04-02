using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.LocalReview
{
    /// <summary>
    /// Executes git commands as child processes to retrieve local repository data.
    /// All output parsing is intentionally minimal; only the fields required for
    /// self-review are extracted.
    /// </summary>
    public class LocalGitClient : ILocalGitClient
    {
        private readonly ILogger<LocalGitClient> _logger;

        public LocalGitClient(ILogger<LocalGitClient> logger)
        {
            _logger = logger;
        }

        public async Task<IReadOnlyList<LocalFileStatus>> GetChangedFilesAsync(
            string repositoryRoot,
            LocalReviewScope scope,
            CancellationToken cancellationToken = default)
        {
            // Optimistic path: assume HEAD exists (true for virtually all repositories).
            // On failure we verify HEAD absence before retrying with no-HEAD fallback,
            // avoiding a redundant `rev-parse --verify HEAD` process on every call.
            var (args, parseMode) = BuildStatusArgs(scope, hasHead: true);

            _logger.LogDebug(Resources.LogLocalGitClientRunning, args, repositoryRoot);

            string output;
            try
            {
                output = await RunGitAsync(repositoryRoot, args, cancellationToken);
            }
            catch (GitCommandException)
            {
                // Primary command failed — verify whether this is a fresh repo with no commits.
                // If HEAD exists the failure has a different cause; re-throw the original error.
                if (await HasHeadAsync(repositoryRoot, cancellationToken))
                    throw;

                _logger.LogDebug(Resources.LogLocalGitClientNoHead, repositoryRoot);

                // BuildStatusArgs throws for BranchDiff when no HEAD — provides a clear error.
                (args, parseMode) = BuildStatusArgs(scope, hasHead: false);
                _logger.LogDebug(Resources.LogLocalGitClientRunning, args, repositoryRoot);
                output = await RunGitAsync(repositoryRoot, args, cancellationToken);
            }

            return parseMode == StatusParseMode.Porcelain
                ? ParsePorcelainStatus(output)
                : ParseNameStatusOutput(output);
        }

        public async Task<string?> GetFileContentAtRefAsync(
            string repositoryRoot,
            string filePath,
            string gitRef,
            CancellationToken cancellationToken = default)
        {
            var normalizedPath = filePath.TrimStart('/').Replace('\\', '/');

            // Special sentinel: read directly from the working tree on disk
            if (string.Equals(gitRef, WorkingTreeRef, StringComparison.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(repositoryRoot, normalizedPath.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(fullPath))
                    return null;

                return await File.ReadAllTextAsync(fullPath, cancellationToken);
            }

            // git show <ref>:<path> � works for HEAD, branch names, commit SHAs, and ":0" (index)
            var args = $"show {gitRef}:{normalizedPath}";

            _logger.LogDebug(Resources.LogLocalGitClientRunning, args, repositoryRoot);

            try
            {
                return await RunGitAsync(repositoryRoot, args, cancellationToken);
            }
            catch (GitCommandException ex) when (ex.ExitCode != 0)
            {
                // File does not exist at this ref (new file / deleted file)
                _logger.LogDebug(Resources.LogLocalGitClientFileNotFoundAtRef, filePath, gitRef, ex.StdErr);
                return null;
            }
        }

        /// <summary>
        /// Sentinel ref value that instructs <see cref="GetFileContentAtRefAsync"/> to read
        /// the file from the working tree on disk rather than from git.
        /// </summary>
        public const string WorkingTreeRef = "WORKING_TREE";

        public async Task<string?> GetCurrentBranchAsync(
            string repositoryRoot,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var output = await RunGitAsync(
                    repositoryRoot,
                    Resources.GitRevParseAbbrevRefHead,
                    cancellationToken);
                var branch = output.Trim();
                return string.Equals(branch, "HEAD", StringComparison.Ordinal) ? null : branch;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, Resources.LogLocalGitClientCouldNotDetermineBranch);
                return null;
            }
        }

        // --- Status parsing -------------------------------------------------------

        private enum StatusParseMode { Porcelain, NameStatus }

        private static (string Args, StatusParseMode Mode) BuildStatusArgs(LocalReviewScope scope, bool hasHead)
        {
            if (!hasHead)
            {
                return scope.Kind switch
                {
                    LocalReviewScopeKind.Staged =>
                        ("diff --name-status --cached", StatusParseMode.NameStatus),

                    LocalReviewScopeKind.WorkingTree =>
                        ("status --porcelain", StatusParseMode.Porcelain),

                    LocalReviewScopeKind.BranchDiff =>
                        throw new GitCommandException(128,
                            Resources.ErrorBranchDiffRequiresCommits),

                    _ => throw new ArgumentOutOfRangeException(nameof(scope))
                };
            }

            return scope.Kind switch
            {
                LocalReviewScopeKind.Staged =>
                    ("diff --name-status --cached HEAD", StatusParseMode.NameStatus),

                LocalReviewScopeKind.WorkingTree =>
                    ("diff --name-status HEAD", StatusParseMode.NameStatus),

                LocalReviewScopeKind.BranchDiff =>
                    ($"diff --name-status {scope.BaseBranch}...HEAD", StatusParseMode.NameStatus),

                _ => throw new ArgumentOutOfRangeException(nameof(scope))
            };
        }

        /// <summary>
        /// Parses <c>git status --porcelain</c> output.
        /// Each line is: <c>XY path</c> (where X=index, Y=worktree status).
        /// We combine both columns to determine the effective status.
        /// </summary>
        private static IReadOnlyList<LocalFileStatus> ParsePorcelainStatus(string output)
        {
            var result = new List<LocalFileStatus>();

            foreach (var line in SplitLines(output))
            {
                if (line.Length < 4) continue;

                var indexStatus = line[0];
                var workStatus = line[1];
                var path = line[2..].Trim();

                if (string.IsNullOrEmpty(path)) continue;

                // Handle renames: "old -> new" format
                if (path.Contains(" -> "))
                {
                    var parts = path.Split(" -> ", 2);
                    result.Add(new LocalFileStatus('R', parts[1].Trim(), parts[0].Trim()));
                    continue;
                }

                // Pick effective status: index takes priority; fall back to worktree
                var effectiveStatus = indexStatus != ' ' && indexStatus != '?' ? indexStatus : workStatus;
                result.Add(new LocalFileStatus(effectiveStatus, path));
            }

            return result;
        }

        /// <summary>
        /// Parses <c>git diff --name-status</c> output.
        /// Each line is: <c>STATUS[score]\tpath[\tnew-path]</c>.
        /// </summary>
        private static IReadOnlyList<LocalFileStatus> ParseNameStatusOutput(string output)
        {
            var result = new List<LocalFileStatus>();

            foreach (var line in SplitLines(output))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                var parts = line.Split('\t');
                if (parts.Length < 2) continue;

                // Status field may include a similarity score (e.g. "R95") � take first char
                var statusChar = parts[0].Length > 0 ? parts[0][0] : 'M';
                var path = parts.Length >= 3 ? parts[2] : parts[1];
                var originalPath = parts.Length >= 3 ? parts[1] : null;

                result.Add(new LocalFileStatus(statusChar, path, originalPath));
            }

            return result;
        }

        // --- HEAD detection -------------------------------------------------------

        private async Task<bool> HasHeadAsync(
            string repositoryRoot,
            CancellationToken cancellationToken)
        {
            try
            {
                await RunGitAsync(repositoryRoot, Resources.GitRevParseVerifyHead, cancellationToken);
                return true;
            }
            catch (GitCommandException)
            {
                return false;
            }
        }

        // --- Git process execution ------------------------------------------------

        private async Task<string> RunGitAsync(
            string workingDirectory,
            string arguments,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Resources.GitExecutable,
                    Arguments = arguments,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = workingDirectory
                }
            };

            process.Start();
            process.StandardInput.Close();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
                throw new GitCommandException(process.ExitCode, stderr.Trim());

            return stdout;
        }

        private static IEnumerable<string> SplitLines(string output) =>
            output.Replace("\r\n", "\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
