namespace REBUSS.Pure.Cli;

/// <summary>
/// Represents an executable CLI command (e.g. <c>init</c>).
/// </summary>
public interface ICliCommand
{
    /// <summary>
    /// The command name as typed by the user (e.g. "init").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Executes the command and returns an exit code (0 = success).
    /// </summary>
    Task<int> ExecuteAsync(CancellationToken cancellationToken = default);
}
