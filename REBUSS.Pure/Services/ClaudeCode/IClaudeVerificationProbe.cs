namespace REBUSS.Pure.Services.ClaudeCode;

/// <summary>
/// Diagnostic probe that verifies Claude Code CLI is installed and authenticated.
/// Used by <c>ClaudeCliSetupStep</c> during <c>rebuss-pure init</c>.
/// </summary>
public interface IClaudeVerificationProbe
{
    /// <summary>
    /// Runs a minimal <c>claude -p</c> call and returns a <see cref="ClaudeVerdict"/>
    /// describing the outcome. Never throws — all failures are encoded in the verdict.
    /// </summary>
    Task<ClaudeVerdict> ProbeAsync(CancellationToken cancellationToken);
}
