namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Abstracts MCP progress notification sending. Tool handlers call this to report
/// step-level progress during multi-step operations. When a progress token is
/// available from the client request, the implementation sends
/// <c>notifications/progress</c> via the MCP transport. Each call is also logged
/// at Information level regardless of token presence.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Reports a progress step. If <paramref name="progressToken"/> is non-null and
    /// the MCP server is available, sends a <c>notifications/progress</c> notification.
    /// Always logs the message at Information level.
    /// </summary>
    /// <param name="progressToken">
    /// Opaque token from the client's request <c>_meta</c>. Pass <c>null</c> when
    /// the client did not provide a token — no notification is sent, but logging still occurs.
    /// </param>
    /// <param name="progress">Current step number (0-based start, strictly increasing).</param>
    /// <param name="total">Total number of steps, or <c>null</c> if unknown.</param>
    /// <param name="message">Short status message (max 80 chars), pattern: [action] [object] (N/M).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReportAsync(object? progressToken, int progress, int? total, string message, CancellationToken cancellationToken = default);
}
