namespace REBUSS.Pure.Core;

/// <summary>
/// Provides the resolved workspace repository root path.
/// The repository root is determined lazily from the CLI <c>--repo</c> argument (highest priority),
/// MCP roots, or the <c>localRepoPath</c> configuration fallback.
/// </summary>
public interface IWorkspaceRootProvider
{
    /// <summary>
    /// Stores the repository path provided via the <c>--repo</c> CLI argument.
    /// This takes highest priority during resolution.
    /// </summary>
    void SetCliRepositoryPath(string path);

    /// <summary>
    /// Stores the root URIs sent by the MCP client during initialization.
    /// </summary>
    void SetRoots(IReadOnlyList<string> rootUris);

    /// <summary>
    /// Returns the list of root URIs provided by the MCP client,
    /// or an empty list if none were provided.
    /// </summary>
    IReadOnlyList<string> GetRootUris();

    /// <summary>
    /// Resolves and returns the workspace repository root path.
    /// Priority: CLI <c>--repo</c> argument → MCP roots → <c>localRepoPath</c> configuration.
    /// Returns <c>null</c> if no valid repository root could be found.
    /// </summary>
    string? ResolveRepositoryRoot();
}
