namespace REBUSS.Pure.Cli;

/// <summary>
/// Parses command-line arguments to determine the application run mode
/// and extract options like <c>--repo</c>, <c>--pat</c>, <c>--org</c>,
/// <c>--project</c>, <c>--repository</c>, <c>--provider</c>, and <c>--owner</c>.
/// </summary>
public class CliArgumentParser
{
    /// <summary>
    /// Parses the command-line arguments.
    /// Returns a <see cref="CliParseResult"/> describing the intended run mode and options.
    /// </summary>
    public static CliParseResult Parse(string[] args)
    {
        if (args.Length == 0)
            return CliParseResult.ServerMode();

        var command = args[0];

        if (string.Equals(command, "init", StringComparison.OrdinalIgnoreCase))
            return CliParseResult.CliMode("init");

        string? repoPath = null;
        string? pat = null;
        string? organization = null;
        string? project = null;
        string? repository = null;
        string? provider = null;
        string? owner = null;

        for (int i = 0; i < args.Length; i++)
        {
            if (string.Equals(args[i], "--repo", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                repoPath = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--pat", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                pat = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--org", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                organization = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--project", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                project = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--repository", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                repository = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--provider", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                provider = args[i + 1];
                i++;
            }
            else if (string.Equals(args[i], "--owner", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                owner = args[i + 1];
                i++;
            }
        }

        return CliParseResult.ServerMode(repoPath, pat, organization, project, repository, provider, owner);
    }
}

/// <summary>
/// Result of parsing command-line arguments.
/// </summary>
public sealed class CliParseResult
{
    /// <summary>
    /// <c>true</c> when the application should run as an MCP server;
    /// <c>false</c> when a CLI command was requested.
    /// </summary>
    public bool IsServerMode { get; private init; }

    /// <summary>
    /// The CLI command name (e.g. "init"). <c>null</c> in server mode.
    /// </summary>
    public string? CommandName { get; private init; }

    /// <summary>
    /// The repository path provided via <c>--repo</c>. <c>null</c> if not specified.
    /// </summary>
    public string? RepoPath { get; private init; }

    /// <summary>
    /// The Personal Access Token provided via <c>--pat</c>. <c>null</c> if not specified.
    /// </summary>
    public string? Pat { get; private init; }

    /// <summary>
    /// The Azure DevOps organization name provided via <c>--org</c>. <c>null</c> if not specified.
    /// </summary>
    public string? Organization { get; private init; }

    /// <summary>
    /// The Azure DevOps project name provided via <c>--project</c>. <c>null</c> if not specified.
    /// </summary>
    public string? Project { get; private init; }

    /// <summary>
    /// The Azure DevOps repository name provided via <c>--repository</c>. <c>null</c> if not specified.
    /// </summary>
    public string? Repository { get; private init; }

    /// <summary>
    /// The SCM provider name provided via <c>--provider</c> (e.g. "AzureDevOps", "GitHub").
    /// <c>null</c> if not specified (auto-detected from git remote).
    /// </summary>
    public string? Provider { get; private init; }

    /// <summary>
    /// The GitHub repository owner provided via <c>--owner</c>. <c>null</c> if not specified.
    /// </summary>
    public string? Owner { get; private init; }

    public static CliParseResult ServerMode(
        string? repoPath = null,
        string? pat = null,
        string? organization = null,
        string? project = null,
        string? repository = null,
        string? provider = null,
        string? owner = null) => new()
    {
        IsServerMode = true,
        CommandName = null,
        RepoPath = repoPath,
        Pat = pat,
        Organization = organization,
        Project = project,
        Repository = repository,
        Provider = provider,
        Owner = owner
    };

    public static CliParseResult CliMode(string commandName) => new()
    {
        IsServerMode = false,
        CommandName = commandName,
        RepoPath = null
    };
}
