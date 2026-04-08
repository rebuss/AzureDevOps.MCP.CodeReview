using System.Text;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Tools.Models;

namespace REBUSS.Pure.Tools.Shared;

/// <summary>
/// Formats tool output as human-readable plain text blocks.
/// All MCP tool handlers use these methods to produce their
/// <c>TextContentBlock</c> payloads instead of serialising JSON.
/// </summary>
internal static class PlainTextFormatter
{
    private const int MaxDescriptionLength = 800;
    private const int DefaultPathWidth = 60;

    // ─── Diff ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats a single file diff as plain text with a header line and
    /// <c>-</c>/<c>+</c>/<c> </c> prefixed diff lines.
    /// </summary>
    public static string FormatFileDiff(StructuredFileChange file)
    {
        ArgumentNullException.ThrowIfNull(file);
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(file.SkipReason))
        {
            sb.AppendLine($"=== {file.Path} ({file.ChangeType}: skipped) ===");
            sb.Append($"Reason: {file.SkipReason}");
            return sb.ToString();
        }

        sb.AppendLine($"=== {file.Path} ({file.ChangeType}: +{file.Additions} -{file.Deletions}) ===");

        foreach (var hunk in file.Hunks)
            sb.Append(FormatHunk(hunk));

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a single diff hunk with a unified-diff <c>@@</c> header line.
    /// </summary>
    public static string FormatHunk(StructuredHunk hunk)
    {
        ArgumentNullException.ThrowIfNull(hunk);
        var sb = new StringBuilder();
        sb.AppendLine($"@@ -{hunk.OldStart},{hunk.OldCount} +{hunk.NewStart},{hunk.NewCount} @@");
        foreach (var line in hunk.Lines)
        {
            var prefix = line.Op switch { "+" => "+", "-" => "-", _ => " " };
            sb.AppendLine($"{prefix}{line.Text}");
        }
        return sb.ToString();
    }

    // ─── File list ────────────────────────────────────────────────────────────────

    /// <summary>Returns a single-line representation used for token-size estimation.</summary>
    public static string FormatFileEntry(PullRequestFileInfo f, int pathWidth = DefaultPathWidth)
    {
        var flags = new List<string>(3);
        if (f.IsTestFile) flags.Add("test");
        if (f.IsBinary) flags.Add("binary");
        if (f.IsGenerated) flags.Add("generated");
        var flagsStr = flags.Count > 0 ? $" [{string.Join(", ", flags)}]" : string.Empty;
        var paddedPath = f.Path.Length > pathWidth ? f.Path[..(pathWidth - 3)] + "..." : f.Path.PadRight(pathWidth);
        return $"  {paddedPath} {f.Status,-10} +{f.Additions,4} -{f.Deletions,4}  {f.ReviewPriority,-6}{flagsStr}";
    }

    /// <summary>
    /// Formats a full file list as a table. <paramref name="context"/> is placed in
    /// the header line, e.g. <c>"PR #42"</c> or <c>"working-tree"</c>.
    /// </summary>
    public static string FormatFileList(
        IReadOnlyList<PullRequestFileInfo> files,
        PullRequestFilesSummary summary,
        string context)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(summary);
        var pathWidth = Math.Max(DefaultPathWidth, files.Count > 0 ? files.Max(f => f.Path.Length) + 2 : DefaultPathWidth);
        var sb = new StringBuilder();
        sb.AppendLine($"Changed files: {context} ({files.Count} file(s))");
        sb.AppendLine($"  {"Path".PadRight(pathWidth)} {"Status",-10} {"  +Add",6} {"  -Del",6}  {"Priority",-8} Flags");
        sb.AppendLine("  " + new string('-', pathWidth + 40));

        foreach (var f in files)
            sb.AppendLine(FormatFileEntry(f, pathWidth));

        sb.AppendLine();
        sb.Append(FormatFilesSummaryLine(summary));
        return sb.ToString();
    }

    private static string FormatFilesSummaryLine(PullRequestFilesSummary summary)
    {
        var parts = new List<string>(7);
        if (summary.SourceFiles > 0) parts.Add($"{summary.SourceFiles} source");
        if (summary.TestFiles > 0) parts.Add($"{summary.TestFiles} test");
        if (summary.ConfigFiles > 0) parts.Add($"{summary.ConfigFiles} config");
        if (summary.DocsFiles > 0) parts.Add($"{summary.DocsFiles} docs");
        if (summary.BinaryFiles > 0) parts.Add($"{summary.BinaryFiles} binary");
        if (summary.GeneratedFiles > 0) parts.Add($"{summary.GeneratedFiles} generated");
        var categoriesText = parts.Count > 0 ? string.Join(", ", parts) : "no files";
        return $"Summary: {categoriesText} | High priority: {summary.HighPriorityFiles}";
    }

    // ─── PR Metadata ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Formats PR metadata. Optionally appends content-paging info when
    /// <paramref name="paging"/> is provided (i.e. when the caller requested
    /// budget parameters).
    /// </summary>
    public static string FormatMetadata(
        FullPullRequestMetadata m,
        int prNumber,
        (int TotalPages, int TotalFiles, int BudgetPerPage, IReadOnlyList<(int Page, int Count)> ByPage)? paging = null,
        bool pagingDeferred = false)
    {
        ArgumentNullException.ThrowIfNull(m);
        var sb = new StringBuilder();
        sb.AppendLine($"PR #{prNumber}: {m.Title}");
        sb.AppendLine($"State:    {m.Status}{(m.IsDraft ? " [draft]" : string.Empty)}");

        var displayName = !string.IsNullOrEmpty(m.AuthorDisplayName) && m.AuthorDisplayName != m.AuthorLogin
            ? $" ({m.AuthorDisplayName})"
            : string.Empty;
        sb.AppendLine($"Author:   {m.AuthorLogin}{displayName}");
        sb.AppendLine($"Branch:   {m.SourceBranch} \u2192 {m.TargetBranch}");
        sb.AppendLine($"Stats:    {m.ChangedFilesCount} file(s) | +{m.Additions} -{m.Deletions} | {m.CommitShas.Count} commit(s)");

        if (!string.IsNullOrEmpty(m.WebUrl))
            sb.AppendLine($"URL:      {m.WebUrl}");
        if (!string.IsNullOrEmpty(m.LastMergeSourceCommitId))
            sb.AppendLine($"Head SHA: {m.LastMergeSourceCommitId}");
        if (!string.IsNullOrEmpty(m.LastMergeTargetCommitId))
            sb.AppendLine($"Base SHA: {m.LastMergeTargetCommitId}");

        if (!string.IsNullOrEmpty(m.Description))
        {
            var desc = m.Description.Length > MaxDescriptionLength
                ? m.Description[..MaxDescriptionLength] + "... [truncated]"
                : m.Description;
            sb.AppendLine("Description:");
            foreach (var line in desc.Split('\n'))
                sb.AppendLine($"  {line}");
        }

        if (paging.HasValue)
        {
            var p = paging.Value;
            sb.AppendLine();
            sb.AppendLine($"Content paging: {p.TotalPages} page(s) | {p.TotalFiles} file(s) | {p.BudgetPerPage} tokens/page");
            var pageSummary = string.Join(", ", p.ByPage.Select(x => $"p{x.Page}:{x.Count}f"));
            sb.AppendLine($"Files per page: {pageSummary}");
        }
        else if (pagingDeferred)
        {
            // FR-004: explicit indicator that paging is not yet available because
            // background enrichment did not complete within the metadata tool's
            // internal timeout. Tells the agent to follow up with begin_pr_review.
            sb.AppendLine();
            sb.AppendLine("Content paging: not yet available — background enrichment is still running.");
            sb.AppendLine("Action: call begin_pr_review to start a stateful review session that walks the PR file by file.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Formats a "friendly status" plain-text block for cases where the tool
    /// cannot produce its real result yet (e.g. background enrichment is still
    /// running, or has failed). The MCP tool response remains a successful
    /// payload — never an exception or error tool-call.
    ///
    /// Used by progressive PR metadata pipeline (FR-014, FR-015, FR-016, FR-017).
    /// </summary>
    public static string FormatFriendlyStatus(string headline, string explanation, string suggestedNextAction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(headline);
        ArgumentException.ThrowIfNullOrWhiteSpace(explanation);
        ArgumentException.ThrowIfNullOrWhiteSpace(suggestedNextAction);

        var sb = new StringBuilder();
        sb.AppendLine($"Status: {headline}");
        sb.AppendLine($"Detail: {explanation}");
        sb.AppendLine($"Suggested next: {suggestedNextAction}");
        return sb.ToString().TrimEnd();
    }

    // ─── Pagination block ─────────────────────────────────────────────────────────

    /// <summary>
    /// Formats the pagination footer line appended as the last content block
    /// in paginated tool responses.
    /// </summary>
    public static string FormatPaginationBlock(
        PaginationMetadataResult pag,
        StalenessWarningResult? staleness = null)
    {
        ArgumentNullException.ThrowIfNull(pag);
        var parts = new List<string>
        {
            $"Page {pag.CurrentPage} of {pag.TotalPages}",
            $"hasMore: {pag.HasMore.ToString().ToLowerInvariant()}"
        };

        if (pag.NextPageReference != null)
            parts.Add($"next: {pag.NextPageReference}");

        if (staleness != null)
        {
            var orig = staleness.OriginalFingerprint is { } origFp
                ? origFp[..Math.Min(8, origFp.Length)]
                : "?";
            var curr = staleness.CurrentFingerprint is { } currFp
                ? currFp[..Math.Min(8, currFp.Length)]
                : "?";
            parts.Add($"STALE: head changed ({orig}\u2026 \u2192 {curr}\u2026)");
        }

        return $"--- {string.Join(" | ", parts)} ---";
    }

    /// <summary>
    /// Formats the context header block prepended to every <c>get_local_content</c>
    /// response. Carries repository root, current branch, and review scope so
    /// consumers do not need a separate metadata call.
    /// </summary>
    public static string FormatLocalContentHeader(string repositoryRoot, string? currentBranch, string scope)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Repository: {repositoryRoot}");
        if (!string.IsNullOrEmpty(currentBranch))
            sb.AppendLine($"Branch:     {currentBranch}");
        sb.Append($"Scope:      {scope}");
        return sb.ToString();
    }

    /// <summary>
    /// Simple pagination footer for tools that do not use page-reference encoding
    /// (e.g. <c>get_local_content</c>).
    /// When <paramref name="categories"/> is provided, a category breakdown is appended.
    /// </summary>
    public static string FormatSimplePaginationBlock(
        int currentPage,
        int totalPages,
        int filesOnPage,
        int totalFiles,
        int estimatedTokens,
        Dictionary<string, int>? categories = null)
    {
        var hasMore = currentPage < totalPages;
        var parts = new List<string>
        {
            $"Page {currentPage} of {totalPages}",
            $"hasMore: {hasMore.ToString().ToLowerInvariant()}",
            $"{filesOnPage}/{totalFiles} files",
            $"~{estimatedTokens} tokens"
        };

        if (categories is { Count: > 0 })
        {
            var catParts = categories
                .Where(kv => kv.Value > 0)
                .Select(kv => $"{kv.Value} {kv.Key}");
            parts.Add(string.Join(", ", catParts));
        }

        return $"--- {string.Join(" | ", parts)} ---";
    }

    // ─── Manifest block ───────────────────────────────────────────────────────────

    // ─── Review session (feature 012) ─────────────────────────────────────────────

    /// <summary>
    /// Formats the manifest returned by <c>begin_pr_review</c>. Truncates the file
    /// list with an "... and N more" marker if it would exceed <paramref name="maxFilesShown"/>.
    /// </summary>
    public static string FormatSessionManifest(
        string sessionId,
        int prNumber,
        int safeBudgetTokens,
        IReadOnlyList<Services.ReviewSession.ReviewFileEntry> files,
        int maxFilesShown = 200)
    {
        var sb = new StringBuilder();
        var deepCount = files.Count(f => f.Classification == Services.ReviewSession.ReviewFileClassification.Deep);
        var scanCount = files.Count - deepCount;
        var deepTokens = files.Where(f => f.Classification == Services.ReviewSession.ReviewFileClassification.Deep).Sum(f => f.EstimatedTokens);
        var scanTokens = files.Where(f => f.Classification == Services.ReviewSession.ReviewFileClassification.Scan).Sum(f => f.EstimatedTokens);

        sb.AppendLine($"Review session ready");
        sb.AppendLine($"Session id: {sessionId}");
        sb.AppendLine($"PR:         #{prNumber}");
        sb.AppendLine($"Files:      {files.Count}");
        sb.AppendLine($"Budget:     {safeBudgetTokens} tokens per response");
        sb.AppendLine($"Deep:       {deepCount} (~{deepTokens} tokens)");
        sb.AppendLine($"Scan:       {scanCount} (~{scanTokens} tokens of summaries)");
        sb.AppendLine();
        sb.AppendLine("Files to review (alphabetical):");
        var shown = Math.Min(files.Count, maxFilesShown);
        for (int i = 0; i < shown; i++)
        {
            var f = files[i];
            var classTag = f.Classification == Services.ReviewSession.ReviewFileClassification.Scan
                ? $"[scan: {f.MatchedPattern}]"
                : "[deep]";
            sb.AppendLine($"  {i + 1,4}. {f.Path}  [{f.Category}, ~{f.EstimatedTokens} tokens]  {classTag}");
        }
        if (files.Count > shown)
            sb.AppendLine($"  ... and {files.Count - shown} more (walk via next_review_item)");
        sb.AppendLine();
        sb.Append($"Next: call next_review_item with sessionId={sessionId}");
        return sb.ToString();
    }

    /// <summary>
    /// Feature 014: synthetic summary block returned by next_review_item for
    /// scan-classified files. Bounded to ~1 KB regardless of underlying file size.
    /// </summary>
    public static string FormatScanSummary(Services.ReviewSession.ReviewFileEntry file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {file.Path} ===");
        sb.AppendLine("Classification: scan-only");
        sb.AppendLine($"Reason: matches pattern '{file.MatchedPattern}' (auto-generated/mechanical file)");
        sb.AppendLine();
        sb.AppendLine("Stats:");
        sb.AppendLine($"  - Lines added:   {file.LinesAdded}");
        sb.AppendLine($"  - Lines removed: {file.LinesRemoved}");
        sb.AppendLine($"  - Total lines:   {file.LinesAdded + file.LinesRemoved}");
        sb.AppendLine();
        sb.AppendLine("This file was classified as mechanical and is presented as a summary");
        sb.AppendLine("rather than full content to preserve the review context budget.");
        sb.AppendLine();
        sb.Append("If you need the full content, call refetch_review_item with this file ");
        sb.Append("path explicitly. Otherwise, acknowledge it via record_review_observation ");
        sb.Append("to advance the session.");
        return sb.ToString();
    }

    public static string FormatAdvanceResponse(
        Services.ReviewSession.ReviewFileEntry file,
        string content,
        int chunkIndex,
        int totalChunks,
        int filePosition,
        int totalFiles,
        int filesRemaining,
        string sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"=== {file.Path} ===");
        var chunkInfo = totalChunks > 1 ? $", chunk {chunkIndex} of {totalChunks}" : string.Empty;
        sb.AppendLine($"File {filePosition} of {totalFiles}{chunkInfo} | {filesRemaining} file(s) remaining");
        sb.AppendLine();
        sb.AppendLine(content);
        sb.AppendLine();
        if (totalChunks > 1 && chunkIndex < totalChunks)
            sb.Append($"--- More chunks of this file remain. Call next_review_item with sessionId={sessionId} to receive chunk {chunkIndex + 1} of {totalChunks}.");
        else
            sb.Append($"--- After reviewing this file, call record_review_observation with sessionId={sessionId}, filePath=\"{file.Path}\", and your observations before requesting the next item.");
        return sb.ToString();
    }

    public static string FormatObservationConfirmation(
        string filePath,
        int acknowledgedCount,
        int totalCount,
        string sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Observation recorded for {filePath}.");
        sb.AppendLine($"Progress: {acknowledgedCount} of {totalCount} files acknowledged.");
        sb.Append(acknowledgedCount < totalCount
            ? $"Next: call next_review_item with sessionId={sessionId}"
            : $"Next: call submit_pr_review with sessionId={sessionId} to finalize the review.");
        return sb.ToString();
    }

    public static string FormatAcknowledgmentGateError(string filePath, string sessionId)
    {
        return $"Cannot advance: file '{filePath}' has been delivered but not yet acknowledged. " +
               $"Call record_review_observation with sessionId={sessionId}, filePath=\"{filePath}\" " +
               $"before requesting the next item.";
    }

    public static string FormatSessionNotFoundError(string sessionId)
    {
        return $"Review session '{sessionId}' was not found. " +
               "Sessions are in-memory and do not persist across server restarts. " +
               "Call begin_pr_review to start a fresh session.";
    }

    public static string FormatUnacknowledgedFilesError(
        string sessionId,
        IReadOnlyList<Services.ReviewSession.ReviewFileEntry> unacknowledged)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Cannot submit: the following files have not been acknowledged:");
        foreach (var f in unacknowledged)
            sb.AppendLine($"  - {f.Path}  [state: {f.Status}]");
        sb.AppendLine();
        sb.AppendLine("Either:");
        sb.AppendLine($"  1. Call next_review_item with sessionId={sessionId} to walk through them, then record observations; or");
        sb.AppendLine($"  2. Re-call submit_pr_review with force=true to override (the override will be recorded in the audit trail).");
        return sb.ToString().TrimEnd();
    }

    // ─── Feature 013: Refetch + Query ─────────────────────────────────────────────

    public static string FormatRefetchResponse(
        Services.ReviewSession.ReviewFileEntry file,
        string content,
        int chunkIndex,
        int totalChunks,
        string sessionId)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[REFETCH]");
        sb.AppendLine($"=== {file.Path} ===");
        if (totalChunks > 1)
            sb.AppendLine($"Chunk {chunkIndex} of {totalChunks}  |  state: {file.Status}");
        else
            sb.AppendLine($"State: {file.Status}");
        sb.AppendLine();
        sb.AppendLine(content);
        sb.AppendLine();
        if (totalChunks > 1 && chunkIndex < totalChunks)
            sb.Append($"--- More chunks remain. Call refetch_review_item with sessionId={sessionId}, filePath=\"{file.Path}\", chunkIndex={chunkIndex + 1} to continue.");
        else
            sb.Append("--- End of refetched content. This call did not change session state.");
        return sb.ToString();
    }

    public static string FormatQueryResponse(
        string query,
        Services.ReviewSession.QueryResult result,
        int requestedLimit)
    {
        var sb = new StringBuilder();
        var shown = result.Entries.Count;
        sb.AppendLine($"Query: '{query}'  |  {result.TotalMatches} match(es) total  |  showing top {shown}");
        sb.AppendLine();

        for (int i = 0; i < result.Entries.Count; i++)
        {
            var e = result.Entries[i];
            sb.AppendLine($"  [{i + 1}] {e.FilePath}  ({e.Status}, observation #{e.SequenceNumber})");
            foreach (var line in e.Text.Split('\n'))
                sb.AppendLine($"      {line}");
            if (e.IsTruncated)
                sb.AppendLine($"      ... [truncated at {Services.ReviewSession.ReviewSession.MaxObservationCharsInResult} chars]");
            sb.AppendLine();
        }

        if (result.TotalMatches > shown)
            sb.Append($"--- {result.TotalMatches - shown} additional match(es) not shown. Increase 'limit' (max 20) or refine the query.");
        else
            sb.Append("--- End of query results. This call did not change session state.");
        return sb.ToString();
    }

    public static string FormatNoMatchesResponse(string query)
    {
        return $"Query: '{query}'\nNo observations match this query in the current session.";
    }

    public static string FormatAuditTrail(
        Services.ReviewSession.ReviewSession session,
        string reviewText)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Final Review ===");
        sb.AppendLine(reviewText);
        sb.AppendLine();
        sb.AppendLine("=== Audit Trail ===");
        sb.AppendLine($"Session id: {session.SessionId}");
        sb.AppendLine($"PR:         #{session.PrNumber}");
        sb.AppendLine($"Head SHA:   {session.HeadSha}");
        sb.AppendLine($"Created:    {session.CreatedAt:O}");
        sb.AppendLine($"Submitted:  {session.SubmittedAt:O}");
        if (session.SubmissionUsedForce)
            sb.AppendLine("Override:   FORCE — submission accepted with unacknowledged files");
        sb.AppendLine();
        sb.AppendLine($"Files ({session.Files.Count}):");
        for (int i = 0; i < session.Files.Count; i++)
        {
            var f = session.Files[i];
            var lastObs = f.Observations.Count > 0 ? f.Observations[^1].Text : "(no observation)";
            var truncated = lastObs.Length > 120 ? lastObs[..117] + "..." : lastObs;
            sb.AppendLine($"  {i + 1,4}. [{f.Status}] {f.Path}");
            sb.AppendLine($"        delivered: {f.DeliveredAt:O} | acknowledged: {f.LastAcknowledgedAt:O}");
            sb.AppendLine($"        last observation: {truncated}");
        }
        return sb.ToString().TrimEnd();
    }

    public static string FormatManifestBlock(
        ContentManifestResult manifest,
        string? pageContext = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var pathWidth = Math.Max(DefaultPathWidth, manifest.Items.Count > 0 ? manifest.Items.Max(i => i.Path.Length) + 2 : DefaultPathWidth);
        var sb = new StringBuilder();
        var header = pageContext != null ? $"Manifest ({pageContext})" : "Manifest";
        sb.AppendLine($"{header}:");

        foreach (var item in manifest.Items)
        {
            var paddedPath = item.Path.Length > pathWidth ? item.Path[..(pathWidth - 3)] + "..." : item.Path.PadRight(pathWidth);
            sb.AppendLine($"  {paddedPath} ~{item.EstimatedTokens,5} tokens  {item.Status,-10} {item.PriorityTier}");
        }

        sb.Append($"Budget: {manifest.Summary.BudgetUsed}/{manifest.Summary.TotalBudgetTokens} tokens ({manifest.Summary.UtilizationPercent:F0}%)");
        return sb.ToString();
    }
}
