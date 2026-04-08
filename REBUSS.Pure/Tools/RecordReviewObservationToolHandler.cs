using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Records an observation against a delivered file. Append-only — multiple
/// observations on the same file are preserved (FR-014/FR-015/FR-016).
/// </summary>
[McpServerToolType]
public class RecordReviewObservationToolHandler
{
    private readonly IReviewSessionStore _sessionStore;
    private readonly ILogger<RecordReviewObservationToolHandler> _logger;

    public RecordReviewObservationToolHandler(
        IReviewSessionStore sessionStore,
        ILogger<RecordReviewObservationToolHandler> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    [McpServerTool(Name = "record_review_observation"), Description(
        "Records the agent's observation against a previously-delivered file in a review session. " +
        "status must be 'reviewed_complete' or 'skipped_with_reason'. Observations are append-only: " +
        "you can record multiple observations against the same file and the history is preserved.")]
    public Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("Review session id from begin_pr_review")] string? sessionId = null,
        [Description("Path of the file the observation is for")] string? filePath = null,
        [Description("Free-form observation text")] string? observations = null,
        [Description("'reviewed_complete' or 'skipped_with_reason'")] string? status = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new McpException("sessionId is required.");
        if (string.IsNullOrEmpty(filePath))
            throw new McpException("filePath is required.");
        if (observations is null)
            throw new McpException("observations is required.");
        if (string.IsNullOrEmpty(status))
            throw new McpException("status is required ('reviewed_complete' or 'skipped_with_reason').");

        var declared = status switch
        {
            "reviewed_complete" => ReviewItemStatus.ReviewedComplete,
            "skipped_with_reason" => ReviewItemStatus.SkippedWithReason,
            _ => throw new McpException("status must be 'reviewed_complete' or 'skipped_with_reason'."),
        };

        if (!_sessionStore.TryGet(sessionId, out var session))
            return Text(PlainTextFormatter.FormatSessionNotFoundError(sessionId));

        var result = session.RecordObservation(filePath, observations, declared, DateTimeOffset.UtcNow);
        switch (result.Kind)
        {
            case RecordKind.Ok:
                var text = PlainTextFormatter.FormatObservationConfirmation(
                    filePath, result.AcknowledgedCount, result.TotalCount, sessionId);
                _logger.LogInformation("record_review_observation: session {Sid} {Path} -> {Status}",
                    sessionId, filePath, declared);
                return Text(text);
            case RecordKind.SessionSubmitted:
                throw new McpException($"Review session {sessionId} has already been submitted.");
            case RecordKind.FileNotFound:
                throw new McpException($"File '{filePath}' is not part of review session {sessionId}.");
            case RecordKind.RejectedFileNotDelivered:
                throw new McpException($"File '{filePath}' has not been delivered yet. Call next_review_item first.");
            case RecordKind.RejectedFilePartial:
                throw new McpException($"File '{filePath}' is only partially delivered. Call next_review_item until the final chunk is received before recording an observation.");
            case RecordKind.InvalidStatus:
                throw new McpException("Invalid status declared.");
        }
        throw new McpException("Unknown record-observation state.");
    }

    private static Task<IEnumerable<ContentBlock>> Text(string s) =>
        Task.FromResult<IEnumerable<ContentBlock>>(new[] { new TextContentBlock { Text = s } });
}
