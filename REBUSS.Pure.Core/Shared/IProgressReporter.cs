namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Abstracts MCP progress notification sending. Tool handlers call this to report
/// step-level progress during multi-step operations. Supports two token types:
/// <list type="bullet">
/// <item><c>IProgress&lt;ProgressNotificationValue&gt;</c> — SDK-injected; preferred path.
/// The SDK handles token extraction and notification dispatch automatically.</item>
/// <item><c>ProgressToken</c> — legacy path; resolves <c>McpServer</c> and sends manually.</item>
/// </list>
/// Each call is also logged at Information level regardless of token presence.
/// </summary>
public interface IProgressReporter
{
    /// <summary>
    /// Reports a progress step. Dispatches via the SDK's <c>IProgress&lt;T&gt;</c> when
    /// available, otherwise falls back to manual <c>ProgressToken</c> + <c>McpServer</c>.
    /// Always logs the message at Information level.
    /// </summary>
    /// <param name="progressToken">
    /// Pass the SDK-injected <c>IProgress&lt;ProgressNotificationValue&gt;</c> instance
    /// (preferred), a <c>ProgressToken</c>, or <c>null</c>. When <c>null</c>, no
    /// notification is sent but logging still occurs.
    /// </param>
    /// <param name="progress">Current step number (0-based start, strictly increasing).</param>
    /// <param name="total">Total number of steps, or <c>null</c> if unknown.</param>
    /// <param name="message">Short status message (max 80 chars), pattern: [action] [object] (N/M).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ReportAsync(object? progressToken, int progress, int? total, string message, CancellationToken cancellationToken = default);
}
