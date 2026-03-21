using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Detects GitHub owner and repository from the local Git remote URL.
/// Supports both HTTPS and SSH remote URL formats:
/// <list type="bullet">
///   <item><c>https://github.com/{owner}/{repo}</c></item>
///   <item><c>https://github.com/{owner}/{repo}.git</c></item>
///   <item><c>git@github.com:{owner}/{repo}.git</c></item>
/// </list>
/// </summary>
public partial class GitHubRemoteDetector : IGitHubRemoteDetector
{
    private readonly ILogger<GitHubRemoteDetector> _logger;

    public GitHubRemoteDetector(ILogger<GitHubRemoteDetector> logger)
    {
        _logger = logger;
    }

    public DetectedGitHubInfo? Detect()
    {
        return DetectFromDirectories(GetCandidateDirectories());
    }

    public DetectedGitHubInfo? Detect(string repositoryPath)
    {
        _logger.LogDebug("Detecting git remote from provided repository path: {RepositoryPath}", repositoryPath);
        return DetectFromDirectories([repositoryPath]);
    }

    private DetectedGitHubInfo? DetectFromDirectories(List<string> candidateDirs)
    {
        try
        {
            foreach (var dir in candidateDirs)
            {
                _logger.LogDebug("Trying git remote detection in: {Directory}", dir);

                var remoteUrl = GetGitRemoteUrl(dir);
                if (string.IsNullOrWhiteSpace(remoteUrl))
                    continue;

                _logger.LogDebug("Detected git remote URL: {RemoteUrl} (from {Directory})", remoteUrl, dir);

                var info = ParseRemoteUrl(remoteUrl.Trim());
                if (info is null)
                {
                    _logger.LogDebug("Git remote URL is not a GitHub URL: {RemoteUrl}", remoteUrl);
                    continue;
                }

                _logger.LogInformation(
                    "Auto-detected GitHub repository: owner={Owner}, repo={Repository}",
                    info.Owner, info.RepositoryName);

                return info;
            }

            _logger.LogDebug("No GitHub git remote detected in any candidate directory");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect git remote information");
            return null;
        }
    }

    internal static DetectedGitHubInfo? ParseRemoteUrl(string url)
    {
        // HTTPS: https://github.com/{owner}/{repo} or https://github.com/{owner}/{repo}.git
        var httpsMatch = HttpsRemotePattern().Match(url);
        if (httpsMatch.Success)
        {
            return new DetectedGitHubInfo(
                httpsMatch.Groups["owner"].Value,
                httpsMatch.Groups["repo"].Value);
        }

        // SSH: git@github.com:{owner}/{repo}.git
        var sshMatch = SshRemotePattern().Match(url);
        if (sshMatch.Success)
        {
            return new DetectedGitHubInfo(
                sshMatch.Groups["owner"].Value,
                sshMatch.Groups["repo"].Value);
        }

        return null;
    }

    private static string? GetGitRemoteUrl(string workingDirectory)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "remote get-url origin",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            }
        };

        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(5));

        return process.ExitCode == 0 ? output.Trim() : null;
    }

    internal static List<string> GetCandidateDirectories()
    {
        var candidates = new List<string>();

        var cwd = Environment.CurrentDirectory;
        if (Directory.Exists(cwd))
            candidates.Add(cwd);

        var repoRoot = FindGitRepositoryRoot(AppContext.BaseDirectory);
        if (repoRoot is not null && !string.Equals(repoRoot, cwd, StringComparison.OrdinalIgnoreCase))
            candidates.Add(repoRoot);

        return candidates;
    }

    internal static string? FindGitRepositoryRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        var dir = new DirectoryInfo(startDirectory);

        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, ".git")))
                return dir.FullName;

            dir = dir.Parent;
        }

        return null;
    }

    [GeneratedRegex(@"^https?://github\.com/(?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?/?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsRemotePattern();

    [GeneratedRegex(@"^git@github\.com:(?<owner>[^/]+)/(?<repo>[^/.]+?)(?:\.git)?$", RegexOptions.IgnoreCase)]
    private static partial Regex SshRemotePattern();
}
