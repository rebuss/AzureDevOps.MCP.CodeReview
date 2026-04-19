namespace REBUSS.Pure.Services.CopilotReview.Inspection;

/// <summary>
/// Default <see cref="ICopilotInspectionWriter"/> used when the <c>REBUSS_COPILOT_INSPECT</c>
/// environment variable is unset. Both methods return a completed task synchronously —
/// zero runtime cost. Feature 022.
/// </summary>
internal sealed class NoOpCopilotInspectionWriter : ICopilotInspectionWriter
{
    public Task WritePromptAsync(string reviewKey, string kind, string content, CancellationToken ct)
        => Task.CompletedTask;

    public Task WriteResponseAsync(string reviewKey, string kind, string content, CancellationToken ct)
        => Task.CompletedTask;
}
