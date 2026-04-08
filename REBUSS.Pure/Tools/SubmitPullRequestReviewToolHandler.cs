using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Submits a completed review. Rejects with a structured tool result (NOT an
/// exception) listing unacknowledged files unless force=true (FR-009/FR-010/FR-011).
/// </summary>
[McpServerToolType]
public class SubmitPullRequestReviewToolHandler
{
    private readonly IReviewSessionStore _sessionStore;
    private readonly ILogger<SubmitPullRequestReviewToolHandler> _logger;

    public SubmitPullRequestReviewToolHandler(
        IReviewSessionStore sessionStore,
        ILogger<SubmitPullRequestReviewToolHandler> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    [McpServerTool(Name = "submit_pr_review"), Description(
        "Submits the final review for a session. Rejects (with a structured error result) " +
        "if any file is unacknowledged, unless force=true. The audit trail records every file's " +
        "final status, delivery order, timestamps, and any forced override.")]
    public Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("Review session id from begin_pr_review")] string? sessionId = null,
        [Description("The final review text")] string? reviewText = null,
        [Description("Force-accept submission even if files are unacknowledged (records override in audit trail)")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new McpException("sessionId is required.");
        if (reviewText is null)
            throw new McpException("reviewText is required.");

        if (!_sessionStore.TryGet(sessionId, out var session))
            return Text(PlainTextFormatter.FormatSessionNotFoundError(sessionId));

        var result = session.Submit(reviewText, force, DateTimeOffset.UtcNow);
        switch (result.Kind)
        {
            case SubmitKind.Accepted:
            case SubmitKind.AcceptedWithForce:
                var audit = PlainTextFormatter.FormatAuditTrail(session, reviewText);
                _logger.LogInformation("submit_pr_review: session {Sid} accepted (force={Force})",
                    sessionId, session.SubmissionUsedForce);
                return Text(audit);
            case SubmitKind.RejectedIncomplete:
                // Returned as a structured tool result, NOT an exception (FR-010).
                return Text(PlainTextFormatter.FormatUnacknowledgedFilesError(sessionId, result.UnacknowledgedFiles));
            case SubmitKind.AlreadySubmitted:
                throw new McpException($"Review session {sessionId} has already been submitted.");
        }
        throw new McpException("Unknown submit state.");
    }

    private static Task<IEnumerable<ContentBlock>> Text(string s) =>
        Task.FromResult<IEnumerable<ContentBlock>>(new[] { new TextContentBlock { Text = s } });
}
