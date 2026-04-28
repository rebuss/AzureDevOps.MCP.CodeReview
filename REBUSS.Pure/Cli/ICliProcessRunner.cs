namespace REBUSS.Pure.Cli;

/// <summary>
/// Single seam for spawning external CLI processes from <see cref="InitCommand"/> and the
/// auth/setup flows it composes (<c>az</c>, <c>gh</c>, <c>winget</c>, <c>npm</c>, <c>claude</c>, …).
/// Two flavours:
/// <list type="bullet">
///   <item><see cref="RunAsync"/> — captures stdout/stderr, returns the exit code; used for
///         probes and version checks where the parent needs to inspect output.</item>
///   <item><see cref="RunInteractiveAsync"/> — inherits the parent's stdin/stdout/stderr so the
///         child can open a browser, display prompts, and interact with the user; used for
///         <c>az login</c>, <c>gh auth login --web</c>, <c>claude /login</c>, etc. Returns
///         only the exit code.</item>
/// </list>
/// Production code uses <see cref="CliProcessRunner"/>; tests typically inject a stub
/// <c>processRunner</c> <c>Func</c> on the auth-flow / setup-step constructors instead of
/// replacing this seam.
/// </summary>
internal interface ICliProcessRunner
{
    Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName, string arguments, CancellationToken cancellationToken);

    Task<int> RunInteractiveAsync(
        string fileName, string arguments, CancellationToken cancellationToken,
        IDictionary<string, string>? environmentOverrides = null);
}
