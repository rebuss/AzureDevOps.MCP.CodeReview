namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Reason code carried on a <see cref="CopilotVerdict"/> to explain why the Copilot
/// review layer is available or unavailable for the current process. Feature 018.
/// </summary>
public enum CopilotAuthReason
{
    /// <summary>Happy path — session is authenticated and the configured model is entitled.</summary>
    Ok = 0,

    /// <summary><c>CopilotReview:Enabled = false</c>. Short-circuit before any SDK call; FR-016.</summary>
    DisabledByConfig,

    /// <summary><c>CopilotClient.StartAsync</c> threw before any auth/model check could run.</summary>
    StartFailure,

    /// <summary><c>GetAuthStatusAsync</c> returned <c>IsAuthenticated = false</c>.</summary>
    NotAuthenticated,

    /// <summary>Auth succeeded but the configured model is not in <c>ListModelsAsync</c>. FR-004.</summary>
    ModelNotEntitled,

    /// <summary>Verification exceeded an internal or operator-supplied deadline.</summary>
    VerificationTimeout,

    /// <summary>Verification was cancelled by the caller. FR-020: never cached.</summary>
    Cancelled,
}
