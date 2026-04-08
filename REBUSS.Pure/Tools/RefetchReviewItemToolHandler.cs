using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Re-fetches a previously-delivered file from a review session. Pure read —
/// never mutates session state and never re-runs enrichment. Honors the
/// acknowledgment gate by refusing to return content for files in Pending or
/// DeliveredPartial state. See feature 013 spec FR-001 through FR-011.
/// </summary>
[McpServerToolType]
public class RefetchReviewItemToolHandler
{
    private readonly IReviewSessionStore _sessionStore;
    private readonly ILogger<RefetchReviewItemToolHandler> _logger;

    public RefetchReviewItemToolHandler(
        IReviewSessionStore sessionStore,
        ILogger<RefetchReviewItemToolHandler> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    [McpServerTool(Name = "refetch_review_item"), Description(
        "Re-reads any acknowledged file's exact original content from a review session. " +
        "Pure read; never re-runs enrichment and never changes session state. " +
        "For files originally delivered in chunks, pass chunkIndex (default 1) and follow " +
        "the continuation hint. Refuses Pending or partially-delivered files — use next_review_item for those.")]
    public Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("Review session id from begin_pr_review")] string? sessionId = null,
        [Description("Path of the file to refetch")] string? filePath = null,
        [Description("1-based chunk index (default 1) for files originally delivered in multiple chunks")] int chunkIndex = 1,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new McpException("sessionId is required.");
        if (string.IsNullOrEmpty(filePath))
            throw new McpException("filePath is required.");

        if (!_sessionStore.TryGet(sessionId, out var session))
            return Text(PlainTextFormatter.FormatSessionNotFoundError(sessionId));

        var result = session.Refetch(filePath, chunkIndex);
        switch (result.Kind)
        {
            case RefetchKind.Ok:
                _logger.LogInformation("refetch_review_item: session {Sid} {Path} chunk {C}/{T}",
                    sessionId, filePath, result.ChunkIndex, result.TotalChunks);
                return Text(PlainTextFormatter.FormatRefetchResponse(
                    result.File!, result.Content!, result.ChunkIndex, result.TotalChunks, sessionId));
            case RefetchKind.FileNotInSession:
                throw new McpException($"File '{filePath}' is not part of review session {sessionId}.");
            case RefetchKind.FilePending:
                throw new McpException($"File '{filePath}' has not been fetched yet. Use next_review_item to receive files in the prescribed order.");
            case RefetchKind.FilePartial:
                throw new McpException($"File '{filePath}' is still being delivered chunk-by-chunk via next_review_item. Finish that flow before using refetch_review_item.");
            case RefetchKind.ChunkOutOfRange:
                throw new McpException($"Chunk index {chunkIndex} is out of range for file '{filePath}' (valid range: 1..{result.TotalChunks}).");
            case RefetchKind.EnrichmentMissing:
                throw new McpException($"Enriched content for '{filePath}' is unavailable in this session. The session may have been corrupted; call begin_pr_review for a fresh session.");
        }
        throw new McpException("Unknown refetch state.");
    }

    private static Task<IEnumerable<ContentBlock>> Text(string s) =>
        Task.FromResult<IEnumerable<ContentBlock>>(new[] { new TextContentBlock { Text = s } });
}
