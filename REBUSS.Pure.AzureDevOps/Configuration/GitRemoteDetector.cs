using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.AzureDevOps.Configuration;

/// <summary>
/// Detects Azure DevOps organization, project, and repository from the local Git remote URL.
/// Supports both HTTPS and SSH remote URL formats:
/// <list type="bullet">
///   <item><c>https://dev.azure.com/{org}/{project}/_git/{repo}</c></item>
///   <item><c>https://{org}@dev.azure.com/{org}/{project}/_git/{repo}</c></item>
///   <item><c>git@ssh.dev.azure.com:v3/{org}/{project}/{repo}</c></item>
/// </list>
/// </summary>
public partial class GitRemoteDetector : IGitRemoteDetector
{
    private readonly ILogger<GitRemoteDetector> _logger;

    public GitRemoteDetector(ILogger<GitRemoteDetector> logger)
    {
        _logger = logger;
    }

    public DetectedGitInfo? Detect()
    {
        return DetectFromDirectories(GetCandidateDirectories());
    }

    public DetectedGitInfo? Detect(string repositoryPath)
    {
        _logger.LogDebug("Detecting git remote from provided repository path: {RepositoryPath}", repositoryPath);
        return DetectFromDirectories(new List<string> { repositoryPath });
    }

    private DetectedGitInfo? DetectFromDirectories(List<string> candidateDirs)
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
                    _logger.LogDebug("Git remote URL is not an Azure DevOps URL: {RemoteUrl}", remoteUrl);
                    continue;
                }

                _logger.LogInformation(
                    "Auto-detected Azure DevOps repository: org={Organization}, project={Project}, repo={Repository}",
                    info.OrganizationName, info.ProjectName, info.RepositoryName);

                return info;
            }

            _logger.LogDebug("No Azure DevOps git remote detected in any candidate directory");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to detect git remote information");
            return null;
        }
    }

    internal static DetectedGitInfo? ParseRemoteUrl(string url)
    {
        // HTTPS: https://dev.azure.com/{org}/{project}/_git/{repo}
        // HTTPS with user: https://{org}@dev.azure.com/{org}/{project}/_git/{repo}
        var httpsMatch = HttpsRemotePattern().Match(url);
        if (httpsMatch.Success)
        {
            return new DetectedGitInfo(
                httpsMatch.Groups["org"].Value,
                httpsMatch.Groups["project"].Value,
                httpsMatch.Groups["repo"].Value);
        }

        // SSH: git@ssh.dev.azure.com:v3/{org}/{project}/{repo}
        var sshMatch = SshRemotePattern().Match(url);
        if (sshMatch.Success)
        {
            return new DetectedGitInfo(
                sshMatch.Groups["org"].Value,
                sshMatch.Groups["project"].Value,
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

    /// <summary>
    /// Returns candidate directories to search for a Git repository, in priority order:
    /// <list type="number">
    ///   <item>The current working directory.</item>
    ///   <item>The nearest ancestor of the executable directory that contains a <c>.git</c> folder.</item>
    /// </list>
    /// When the MCP server is launched by an IDE, the current working directory is often
    /// arbitrary (e.g. the user's home directory). The executable, however, lives inside
    /// the build output of the repository (e.g. <c>&lt;repo&gt;/bin/Debug/net8.0/</c>),
    /// so walking up from <see cref="AppContext.BaseDirectory"/> reliably finds the repo root.
    /// </summary>
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

    /// <summary>
    /// Walks up from <paramref name="startDirectory"/> looking for a directory that contains
    /// a <c>.git</c> subdirectory. Returns the first match, or <c>null</c>.
    /// </summary>
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

    [GeneratedRegex(@"https://(?:[^@]+@)?dev\.azure\.com/(?<org>[^/]+)/(?<project>[^/]+)/_git/(?<repo>[^/?\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsRemotePattern();

    [GeneratedRegex(@"git@ssh\.dev\.azure\.com:v3/(?<org>[^/]+)/(?<project>[^/]+)/(?<repo>[^/?\s]+)", RegexOptions.IgnoreCase)]
    private static partial Regex SshRemotePattern();
}
