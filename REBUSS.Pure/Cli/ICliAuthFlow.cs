namespace REBUSS.Pure.Cli;

/// <summary>
/// Defines the authentication flow executed during <c>rebuss-pure init</c>.
/// Each SCM provider implements its own flow (e.g. Azure CLI, GitHub CLI).
/// </summary>
internal interface ICliAuthFlow
{
    /// <summary>
    /// Runs the interactive authentication flow, which may install the CLI tool,
    /// open a browser for login, and cache the acquired token.
    /// </summary>
    Task RunAsync(CancellationToken cancellationToken = default);
}
