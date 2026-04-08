using System.ComponentModel;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Services.ReviewSession;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools;

/// <summary>
/// Free-text searches a review session's recorded observations. Pure read.
/// MVP scoring: count of distinct query tokens appearing as case-insensitive
/// substrings, tie-broken alphabetically by file path. See feature 013 spec
/// FR-012 through FR-020.
/// </summary>
[McpServerToolType]
public class QueryReviewNotesToolHandler
{
    private readonly IReviewSessionStore _sessionStore;
    private readonly ILogger<QueryReviewNotesToolHandler> _logger;

    public QueryReviewNotesToolHandler(
        IReviewSessionStore sessionStore,
        ILogger<QueryReviewNotesToolHandler> logger)
    {
        _sessionStore = sessionStore;
        _logger = logger;
    }

    [McpServerTool(Name = "query_review_notes"), Description(
        "Searches the agent's previously-recorded review observations across the session " +
        "for free-text matches. Pure read. Use before submit_pr_review to compose a final " +
        "review from accumulated observations. Limit defaults to 5, capped at 20.")]
    public Task<IEnumerable<ContentBlock>> ExecuteAsync(
        [Description("Review session id from begin_pr_review")] string? sessionId = null,
        [Description("Free-text query — whitespace-tokenized, case-insensitive substring match")] string? query = null,
        [Description("Maximum number of results (default 5, max 20)")] int limit = 5,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(sessionId))
            throw new McpException("sessionId is required.");
        if (query is null)
            throw new McpException("query is required.");

        if (!_sessionStore.TryGet(sessionId, out var session))
            return Text(PlainTextFormatter.FormatSessionNotFoundError(sessionId));

        var result = session.QueryObservations(query, limit);
        switch (result.Kind)
        {
            case QueryKind.EmptyQuery:
                throw new McpException("query must contain at least one non-whitespace character.");
            case QueryKind.Ok:
                if (result.Entries.Count == 0)
                {
                    _logger.LogInformation("query_review_notes: session {Sid} query='{Q}' returned 0 matches", sessionId, query);
                    return Text(PlainTextFormatter.FormatNoMatchesResponse(query));
                }
                _logger.LogInformation("query_review_notes: session {Sid} query='{Q}' returned {N} of {Total} matches",
                    sessionId, query, result.Entries.Count, result.TotalMatches);
                return Text(PlainTextFormatter.FormatQueryResponse(query, result, limit));
        }
        throw new McpException("Unknown query state.");
    }

    private static Task<IEnumerable<ContentBlock>> Text(string s) =>
        Task.FromResult<IEnumerable<ContentBlock>>(new[] { new TextContentBlock { Text = s } });
}
