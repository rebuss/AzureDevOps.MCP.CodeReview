namespace REBUSS.Pure.Services.ClaudeCode;

/// <summary>
/// Immutable result of a Claude Code CLI availability + authentication check.
/// Built by <see cref="ClaudeVerificationRunner"/> and consumed by the init
/// setup step to decide whether to print a success line or a remediation banner.
/// </summary>
/// <param name="IsAvailable">
/// <c>true</c> iff <c>claude -p</c> returned a usable response. When <c>false</c>,
/// the setup step prints a "NOT CONFIGURED" banner but the init exit code is
/// unaffected (soft-exit policy).
/// </param>
/// <param name="Reason">
/// Machine-readable reason: <c>ok</c>, <c>not-installed</c>, <c>not-authenticated</c>,
/// <c>timeout</c>, <c>invalid-response</c>, or <c>error</c>.
/// </param>
/// <param name="Remediation">
/// Operator-facing next-step hint. Empty when <paramref name="IsAvailable"/> is true.
/// </param>
public sealed record ClaudeVerdict(
    bool IsAvailable,
    string Reason,
    string Remediation);
