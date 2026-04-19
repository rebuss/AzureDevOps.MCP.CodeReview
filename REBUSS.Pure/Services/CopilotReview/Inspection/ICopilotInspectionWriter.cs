namespace REBUSS.Pure.Services.CopilotReview.Inspection;

/// <summary>
/// Internal diagnostic hook for capturing the prompts sent to and responses received from
/// the Copilot review agent. Feature 022.
/// </summary>
/// <remarks>
/// Contract:
/// <list type="bullet">
/// <item>Both methods MUST NOT throw on IO failure — errors are logged at Warning and swallowed.
/// The feature is diagnostic; capture failures must never propagate to the review pipeline.</item>
/// <item>Both methods MAY throw <see cref="OperationCanceledException"/> when
/// <paramref name="ct"/> is canceled.</item>
/// <item>Implementations MUST be safe for concurrent invocation across threads (parallel page
/// reviews dispatch concurrent writes with the same <paramref name="reviewKey"/>).</item>
/// <item>Implementations MUST sanitize <paramref name="reviewKey"/> before using it in any
/// filesystem path — the key is opaque caller-supplied input and may contain characters that
/// are illegal in path components.</item>
/// <item>Implementations MUST NOT write authentication credentials, session tokens, or any
/// security artifact into captured content. Content originates from review prompts/responses
/// which are credential-free by upstream contract; implementations must not attempt to enrich
/// it with any auth material.</item>
/// </list>
/// </remarks>
internal interface ICopilotInspectionWriter
{
    /// <summary>Capture a prompt that was sent to the Copilot review agent.</summary>
    /// <param name="reviewKey">Opaque review identifier from the orchestrator (e.g., <c>pr:42</c>).</param>
    /// <param name="kind">Short step descriptor (e.g., <c>page-1-review</c>). Sanitized before use.</param>
    /// <param name="content">Verbatim prompt text.</param>
    Task WritePromptAsync(string reviewKey, string kind, string content, CancellationToken ct);

    /// <summary>Capture a response received from the Copilot review agent.</summary>
    /// <param name="reviewKey">Opaque review identifier from the orchestrator.</param>
    /// <param name="kind">Short step descriptor — paired with the matching prompt's <paramref name="kind"/>.</param>
    /// <param name="content">Verbatim response text.</param>
    Task WriteResponseAsync(string reviewKey, string kind, string content, CancellationToken ct);
}
