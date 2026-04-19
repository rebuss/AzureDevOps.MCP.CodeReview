namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Immutable, process-lifetime result of Copilot review-layer verification.
/// Built once by <c>CopilotVerificationRunner</c>, cached by
/// <see cref="ICopilotAvailabilityDetector"/>, and read by every subsequent
/// review request. Feature 018.
///
/// <para>
/// <b>Privacy invariant (FR-013a)</b>: the token VALUE MUST NEVER be a field on
/// this record, nor on any type that references it. Only the
/// <see cref="CopilotTokenSource"/> label identifies which channel supplied the
/// token — never the token itself.
/// </para>
/// </summary>
/// <param name="IsAvailable">
/// <c>true</c> iff the Copilot review layer can be used for review requests this
/// process. When <c>false</c>, the orchestrator falls back to the content-only path
/// (graceful mode) or throws <c>CopilotUnavailableException</c> (strict mode).
/// </param>
/// <param name="Reason">Reason code; <see cref="CopilotAuthReason.Ok"/> iff available.</param>
/// <param name="TokenSource">Which channel supplied the (absent or present) token.</param>
/// <param name="ConfiguredModel">
/// Value of <c>CopilotReview:Model</c> at the time of verification. May be null if
/// the verdict was built from a disabled-by-config short-circuit.
/// </param>
/// <param name="EntitledModels">
/// Model IDs returned by the SDK's <c>ListModelsAsync</c>. Never null — empty list
/// when the list could not be retrieved (e.g. auth failed before the call).
/// </param>
/// <param name="Login">GitHub login from the auth-status response, if any. FR-013b.</param>
/// <param name="Host">GitHub host from the auth-status response, if any. FR-013b.</param>
/// <param name="Remediation">
/// Operator-facing remediation string. Empty on the happy path; non-empty and
/// actionable on any failure path (names the next step to take).
/// </param>
public sealed record CopilotVerdict(
    bool IsAvailable,
    CopilotAuthReason Reason,
    CopilotTokenSource TokenSource,
    string? ConfiguredModel,
    IReadOnlyList<string> EntitledModels,
    string? Login,
    string? Host,
    string Remediation);
