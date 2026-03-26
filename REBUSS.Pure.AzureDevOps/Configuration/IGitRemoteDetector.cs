namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Detects Azure DevOps repository information from the local Git remote configuration.
/// </summary>
public interface IGitRemoteDetector
{
    /// <summary>
    /// Attempts to detect the Azure DevOps organization, project, and repository
    /// from the current working directory's git remote.
    /// Returns <c>null</c> if detection fails.
    /// </summary>
    DetectedGitInfo? Detect();

    /// <summary>
    /// Attempts to detect the Azure DevOps organization, project, and repository
    /// from the git remote at the specified repository path.
    /// Returns <c>null</c> if detection fails.
    /// </summary>
    DetectedGitInfo? Detect(string repositoryPath);
}

/// <summary>
/// Repository information detected from the local Git remote URL.
/// </summary>
public sealed record DetectedGitInfo(string OrganizationName, string ProjectName, string RepositoryName);
