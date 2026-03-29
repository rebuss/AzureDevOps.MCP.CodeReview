using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Services.LocalReview
{
    /// <summary>
    /// Thrown when a local git repository root cannot be resolved.
    /// </summary>
    public sealed class LocalRepositoryNotFoundException : Exception
    {
        public LocalRepositoryNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a requested file is not found among the locally changed files.
    /// </summary>
    public sealed class LocalFileNotFoundException : Exception
    {
        public LocalFileNotFoundException(string message) : base(message) { }
    }

    /// <summary>
    /// Thrown when a git command exits with a non-zero exit code.
    /// </summary>
    public sealed class GitCommandException : Exception
    {
        public int ExitCode { get; }
        public string StdErr { get; }

        public GitCommandException(int exitCode, string stdErr)
            : base(string.Format(Resources.ErrorGitExitedWithCode, exitCode, stdErr))
        {
            ExitCode = exitCode;
            StdErr = stdErr;
        }
    }
}
