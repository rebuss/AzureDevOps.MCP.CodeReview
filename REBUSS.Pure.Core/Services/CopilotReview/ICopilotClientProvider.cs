namespace REBUSS.Pure.Core.Services.CopilotReview;

/// <summary>
/// Owns the singleton <c>GitHub.Copilot.SDK.CopilotClient</c> lifecycle for the MCP
/// server process. Lazy-started on first use, gracefully shut down at process exit
/// (implementation is also registered as <c>IHostedService</c>). Per research.md Decision 11.
/// </summary>
public interface ICopilotClientProvider
{
    /// <summary>
    /// Ensures the client has been started. On first call, attempts <c>StartAsync</c>.
    /// On subsequent calls, returns the cached outcome. Never throws for start failures —
    /// returns <c>false</c> if the start attempt failed. Cancellation still propagates
    /// as <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<bool> TryEnsureStartedAsync(CancellationToken ct = default);

    /// <summary>
    /// Returns the started client as an opaque <see cref="object"/> so that
    /// <c>REBUSS.Pure.Core</c> does not leak the <c>GitHub.Copilot.SDK</c> type.
    /// Throws <see cref="InvalidOperationException"/> if <see cref="TryEnsureStartedAsync"/>
    /// has not yet returned <c>true</c>. Callers in <c>REBUSS.Pure</c> cast to
    /// <c>CopilotClient</c> — safe because this interface is never implemented anywhere
    /// but in the SDK-referencing project.
    /// </summary>
    object Client { get; }

    /// <summary>
    /// The <see cref="CopilotVerdict"/> produced by the first verification run
    /// (successful or failed). <c>null</c> until
    /// <see cref="TryEnsureStartedAsync"/> has completed at least once without
    /// cancellation. Read by <c>CopilotAvailabilityDetector</c> to cache the
    /// process-lifetime availability decision. Feature 018 (FR-007, T018).
    /// </summary>
    CopilotVerdict? StartupVerdict { get; }
}
