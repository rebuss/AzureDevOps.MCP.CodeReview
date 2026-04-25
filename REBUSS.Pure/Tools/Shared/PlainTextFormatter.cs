using System.Text;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Properties;
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
            return sb.ToString().TrimEnd();
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
            // internal timeout. Tells the agent to follow up with get_pr_content.
            sb.AppendLine();
            sb.AppendLine("Content paging: not yet available — background enrichment is still running.");
            sb.AppendLine("Action: call get_pr_content with pageNumber=1 to retrieve the enriched content.");
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
    /// (e.g. <c>get_pr_content</c>, <c>get_local_content</c>).
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

    // ─── Agent review layer (feature 013) ─────────────────────────────────────────

    /// <summary>
    /// Formats the AI-agent-assisted mode indicator header. The first line MUST be the
    /// <c>[review-mode: {agent}-assisted]</c> string built from <see cref="Resources.AgentReviewModeHeader"/>
    /// — the prompt detects the prefix and the user sees which backend (claude / copilot)
    /// produced the review (FR-004, FR-014).
    /// </summary>
    /// <param name="agentName">Lowercase agent identifier — <c>"claude"</c> or <c>"copilot"</c>.</param>
    public static string FormatAgentReviewHeader(string agentName, int prNumber, int totalPages, int succeeded, int failed)
        => FormatAgentReviewHeader(agentName, $"PR #{prNumber}", totalPages, succeeded, failed);

    public static string FormatAgentReviewHeader(string agentName, string reviewSubject, int totalPages, int succeeded, int failed)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentName);
        var sb = new StringBuilder();
        sb.AppendLine(string.Format(Resources.AgentReviewModeHeader, agentName));
        sb.Append($"{reviewSubject} — Review completed ({totalPages} pages reviewed, {succeeded} succeeded, {failed} failed)");
        return sb.ToString();
    }

    /// <summary>
    /// Formats a single page's review outcome. On success renders the review text;
    /// on failure renders the list of file paths that were on the failed page plus the
    /// last-attempt reason (FR-007a, Clarification Q1).
    /// </summary>
    public static string FormatAgentPageReviewBlock(AgentPageReviewResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var sb = new StringBuilder();
        if (result.Succeeded)
        {
            sb.AppendLine($"=== Page {result.PageNumber} Review ===");
            sb.Append(result.ReviewText);
            return sb.ToString();
        }

        sb.AppendLine($"=== Page {result.PageNumber} Review (FAILED) ===");
        sb.AppendLine("Files not reviewed:");
        foreach (var path in result.FailedFilePaths)
            sb.AppendLine($"  - {path}");
        sb.Append($"Reason: {result.ErrorMessage}");
        return sb.ToString();
    }

}
