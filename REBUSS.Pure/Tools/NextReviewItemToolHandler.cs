using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Returns the next file (or next chunk of an oversize file) in a review session.
/// Enforces the acknowledgment gate (FR-007/FR-008) and post-submit lockout (FR-012).
/// </summary>
[McpServerToolType]
public class NextReviewItemToolHandler
{
    private readonly IReviewSessionStore _sessionStore;
    private readonly ISingleFileChunker _chunker;
    private readonly ILogger<NextReviewItemToolHandler> _logger;

    public NextReviewItemToolHandler(
        IReviewSessionStore sessionStore,
        ISingleFileChunker chunker,
        ILogger<NextReviewItemToolHandler> logger)
    {
        _sessionStore = sessionStore;
        _chunker = chunker;
        _logger = logger;
    }

    [McpServerTool(Name = "next_review_item"), Description(
        "Returns the next file in the review session — or the next chunk of a file that is " +
        "too large for one response. Enforces the acknowledgment gate: you cannot advance past " +
        "a fully-delivered file until you have called record_review_observation for it.")]
    public Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("The review session id returned by begin_pr_review")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new McpException("sessionId is required.");

        if (!_sessionStore.TryGet(sessionId, out var session))
        {
            return Text(PlainTextFormatter.FormatSessionNotFoundError(sessionId));
        }

        var result = session.NextItem(_chunker, DateTimeOffset.UtcNow);
        switch (result.Kind)
        {
            case NextItemKind.SessionSubmitted:
                throw new McpException($"Review session {sessionId} has already been submitted. Call begin_pr_review for a fresh review.");
            case NextItemKind.AllDelivered:
                throw new McpException($"All files in review session {sessionId} have been delivered. Call submit_pr_review to finalize.");
            case NextItemKind.NeedsAcknowledgment:
                throw new McpException(PlainTextFormatter.FormatAcknowledgmentGateError(result.File!.Path, sessionId));
            case NextItemKind.Delivered:
                var totalFiles = session.Files.Count;
                var position = session.Files.ToList().IndexOf(result.File!) + 1;
                var remaining = session.Files.Count(f =>
                    f.Status is ReviewItemStatus.Pending or ReviewItemStatus.DeliveredPartial or ReviewItemStatus.DeliveredAwaitingObservation);
                // Feature 014: scan-classified files are delivered as a synthetic summary
                // computed on the fly; deep files keep the full content path unchanged.
                var body = result.File!.Classification == ReviewFileClassification.Scan
                    ? PlainTextFormatter.FormatScanSummary(result.File!)
                    : result.Content!;
                var text = PlainTextFormatter.FormatAdvanceResponse(
                    result.File!,
                    body,
                    result.ChunkIndex,
                    result.TotalChunks,
                    position,
                    totalFiles,
                    remaining,
                    sessionId);
                _logger.LogInformation("next_review_item: session {Sid} delivered {Path} chunk {C}/{T}",
                    sessionId, result.File!.Path, result.ChunkIndex, result.TotalChunks);
                return Text(text);
        }
        throw new McpException("Unknown next-item state.");
    }

    private static Task<IEnumerable<ContentBlock>> Text(string s) =>
        Task.FromResult<IEnumerable<ContentBlock>>(new[] { new TextContentBlock { Text = s } });
}
