namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Detects GitHub repository information from the local Git remote configuration.
/// </summary>
public interface IGitHubRemoteDetector
{
    /// <summary>
    /// Attempts to detect the GitHub owner and repository name
    /// from the current working directory's git remote.
    /// Returns <c>null</c> if detection fails.
    /// </summary>
    DetectedGitHubInfo? Detect();

    /// <summary>
    /// Attempts to detect the GitHub owner and repository name
    /// from the git remote at the specified repository path.
    /// Returns <c>null</c> if detection fails.
    /// </summary>
    DetectedGitHubInfo? Detect(string repositoryPath);
}

/// <summary>
/// Repository information detected from a GitHub Git remote URL.
/// </summary>
public sealed record DetectedGitHubInfo(string Owner, string RepositoryName);
