using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Properties;
using REBUSS.Pure.Services.PrEnrichment;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_content MCP tool. Serves a single
    /// page of enriched PR diff content from the
    /// <see cref="IPrEnrichmentOrchestrator"/>'s cached result, kicking off
    /// background enrichment as a fallback when no prior <c>get_pr_metadata</c>
    /// call has primed it. Runs with its own fresh internal timeout window;
    /// on timeout returns a friendly "still preparing" status block instead of
    /// surfacing a tool-call failure.
    /// </summary>
    [McpServerToolType]
    public class GetPullRequestContentToolHandler
    {
        private readonly IPullRequestDataProvider _metadataProvider;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly IPrEnrichmentOrchestrator _enrichmentOrchestrator;
        private readonly IPageAllocator _pageAllocator;
        private readonly IOptions<WorkflowOptions> _workflowOptions;
        private readonly ICopilotAvailabilityDetector _copilotAvailability;
        private readonly ICopilotReviewOrchestrator _copilotReviewOrchestrator;
        private readonly IProgressReporter _progressReporter;
        private readonly ILogger<GetPullRequestContentToolHandler> _logger;

        public GetPullRequestContentToolHandler(
            IPullRequestDataProvider metadataProvider,
            IContextBudgetResolver budgetResolver,
            IPrEnrichmentOrchestrator enrichmentOrchestrator,
            IPageAllocator pageAllocator,
            IOptions<WorkflowOptions> workflowOptions,
            ICopilotAvailabilityDetector copilotAvailability,
            ICopilotReviewOrchestrator copilotReviewOrchestrator,
            IProgressReporter progressReporter,
            ILogger<GetPullRequestContentToolHandler> logger)
        {
            _metadataProvider = metadataProvider;
            _budgetResolver = budgetResolver;
            _enrichmentOrchestrator = enrichmentOrchestrator;
            _pageAllocator = pageAllocator;
            _workflowOptions = workflowOptions;
            _copilotAvailability = copilotAvailability;
            _copilotReviewOrchestrator = copilotReviewOrchestrator;
            _progressReporter = progressReporter;
            _logger = logger;
        }

        [McpServerTool(Name = "get_pr_content"), Description(
            "Returns plain-text diff content for a specific page of a pull request review. " +
            "One content block per file with -/+/space prefixed lines, plus a pagination footer. " +
            "Reuses background enrichment from a prior get_pr_metadata call when available. " +
            "If enrichment is still running and cannot complete within the internal timeout, " +
            "returns a friendly 'still preparing' status block — never a raw timeout error.")]
        public async Task<IEnumerable<ContentBlock>> ExecuteAsync(
            [Description("The Pull Request number/ID")] int? prNumber = null,
            [Description("Page number to retrieve (1-based)")] int? pageNumber = null,
            [Description("Model name for context budget resolution")] string? modelName = null,
            [Description("Explicit token budget override")] int? maxTokens = null,
            IProgress<ProgressNotificationValue>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (prNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPrNumber);
            if (prNumber <= 0)
                throw new McpException(Resources.ErrorPrNumberMustBePositive);
            if (pageNumber == null)
                throw new McpException(Resources.ErrorMissingRequiredPageNumber);
            if (pageNumber < 1)
                throw new McpException(Resources.ErrorPageNumberMustBePositive);

            try
            {
                _logger.LogInformation(Resources.LogGetPrContentEntry, prNumber, pageNumber);
                var sw = Stopwatch.StartNew();

                // SDK injects IProgress<T> automatically from _meta.progressToken;
                // it no-ops when the client omits the token.
                await _progressReporter.ReportAsync(progress, 0, 4,
                    $"Starting content retrieval for PR #{prNumber}", cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);
                var safeBudget = budget.SafeBudgetTokens;

                // Fast-path: a Failed snapshot means the prior background body raised.
                // Surface it via friendly-status without retriggering.
                var snapshot = _enrichmentOrchestrator.TryGetSnapshot(prNumber.Value);
                if (snapshot is { Status: PrEnrichmentStatus.Failed, Failure: not null })
                {
                    _logger.LogInformation("PR {Pr} content request hit Failed snapshot; returning friendly status", prNumber);
                    return BuildFriendlyFailureBlocks(prNumber.Value, pageNumber.Value, snapshot.Failure!);
                }

                // SHA discovery: if there is no existing job, ask the metadata provider for the head SHA
                // and start one. A budget change does NOT retrigger enrichment — pagination is recomputed
                // per-call below from the cached enriched candidates, so the expensive Roslyn/diff work
                // runs at most once per (prNumber, headSha).
                if (snapshot is null)
                {
                    var meta = await _metadataProvider.GetMetadataAsync(prNumber.Value, cancellationToken);
                    var headSha = meta.LastMergeSourceCommitId ?? string.Empty;
                    _enrichmentOrchestrator.TriggerEnrichment(prNumber.Value, headSha, safeBudget);
                }

                // Wait with our own internal timeout. The background body keeps running
                // even if this wait expires.
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                linkedCts.CancelAfter(_workflowOptions.Value.ContentInternalTimeoutMs);

                await _progressReporter.ReportAsync(progress, 1, 4,
                    $"Waiting for enrichment — PR #{prNumber}", cancellationToken);

                PrEnrichmentResult result;
                try
                {
                    result = await _enrichmentOrchestrator.WaitForEnrichmentAsync(prNumber.Value, linkedCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogInformation(
                        "PR {Pr} content wait expired after {TimeoutMs}ms; returning friendly status",
                        prNumber, _workflowOptions.Value.ContentInternalTimeoutMs);
                    return BuildFriendlyStillPreparingBlocks(prNumber.Value, pageNumber.Value);
                }

                // Feature 013 branch: if Copilot is available + enabled, the server performs
                // the review itself and returns compact page summaries in a single response.
                // Otherwise, fall through to the existing content-only path.
                await _progressReporter.ReportAsync(progress, 2, null,
                    "Enrichment complete — checking review mode", cancellationToken);

                bool copilotAvailable;
                try
                {
                    copilotAvailable = await _copilotAvailability.IsAvailableAsync(cancellationToken);
                }
                catch (Services.CopilotReview.CopilotUnavailableException ex)
                {
                    // Feature 018 (FR-015 strict mode): when CopilotReview:StrictMode = true
                    // and the cached verdict is unavailable for a real verification failure,
                    // IsAvailableAsync throws instead of returning false. Surface the
                    // remediation string as an MCP tool error so the operator sees a loud,
                    // unambiguous signal on this exact request.
                    sw.Stop();
                    _logger.LogWarning(
                        "PR {Pr} Copilot review layer unavailable (strict mode): {Reason}. Remediation: {Remediation}",
                        prNumber, ex.Verdict.Reason, ex.Verdict.Remediation);
                    throw;
                }
                if (copilotAvailable)
                {
                    await _progressReporter.ReportAsync(progress, 3, null,
                        $"Copilot review started for PR #{prNumber}", cancellationToken);

                    _copilotReviewOrchestrator.TriggerReview(prNumber.Value, result);

                    var copilotResult = await WaitForReviewWithProgressAsync(
                        prNumber.Value, progress, cancellationToken);

                    var copilotBlocks = BuildCopilotAssistedBlocks(prNumber.Value, copilotResult);

                    sw.Stop();
                    _logger.LogInformation(
                        "PR {Pr} copilot-assisted content returned in {Ms}ms ({Pages} pages, {Succeeded} ok, {Failed} failed)",
                        prNumber, sw.ElapsedMilliseconds,
                        copilotResult.TotalPages, copilotResult.SucceededPages, copilotResult.FailedPages);

                    return copilotBlocks;
                }

                // Repaginate per-call against the caller's resolved safe budget. The enriched
                // candidates and per-file enriched text are reused from the cache; only the
                // bin-packing (PageAllocator.Allocate) re-runs. This is what makes maxTokens /
                // modelName overrides cheap and effective on every call, fixing the bug where
                // a stale cached page was returned byte-for-byte.
                var allocation = _pageAllocator.Allocate(result.SortedCandidates, safeBudget);

                if (pageNumber > allocation.TotalPages)
                    throw new McpException(
                        string.Format(Resources.ErrorPageNumberExceedsTotalPages, pageNumber, allocation.TotalPages));

                var pageSlice = allocation.Pages[pageNumber.Value - 1];
                var blocks = BuildPageBlocks(pageSlice, pageNumber.Value, allocation, result);

                sw.Stop();

                await _progressReporter.ReportAsync(progress, 4, 4,
                    $"Content ready — page {pageNumber}/{allocation.TotalPages}", cancellationToken);

                _logger.LogInformation(Resources.LogGetPrContentCompleted,
                    prNumber, pageNumber, allocation.TotalPages, pageSlice.Items.Count, sw.ElapsedMilliseconds);

                return blocks;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, Resources.LogGetPrContentPrNotFound, prNumber);
                throw new McpException(string.Format(Resources.ErrorPullRequestNotFound, ex.Message));
            }
            catch (McpException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, Resources.LogGetPrContentError, prNumber, pageNumber);
                throw new McpException(string.Format(Resources.ErrorRetrievingPrContent, ex.Message));
            }
        }

        private static List<ContentBlock> BuildPageBlocks(
            PageSlice pageSlice, int pageNumber, PageAllocation allocation, PrEnrichmentResult result)
        {
            var blocks = new List<ContentBlock>(pageSlice.Items.Count + 2);

            // Feature 013 FR-004: prepend the content-only mode indicator so the IDE prompt
            // can detect the mode on every response.
            blocks.Add(new TextContentBlock { Text = PlainTextFormatter.FormatContentOnlyModeHeader() });

            // Per-file enriched text, in the order produced by the page allocator.
            var pagePathsOrdered = new List<string>(pageSlice.Items.Count);
            foreach (var item in pageSlice.Items)
            {
                var path = result.SortedCandidates[item.OriginalIndex].Path;
                pagePathsOrdered.Add(path);
                if (result.EnrichedByPath.TryGetValue(path, out var enrichedText))
                    blocks.Add(new TextContentBlock { Text = enrichedText });
            }

            var categories = BuildCategoryBreakdown(pagePathsOrdered, result.SortedCandidates);

            blocks.Add(new TextContentBlock
            {
                Text = PlainTextFormatter.FormatSimplePaginationBlock(
                    pageNumber, allocation.TotalPages,
                    pagePathsOrdered.Count, allocation.TotalItems,
                    pageSlice.BudgetUsed,
                    categories)
            });

            return blocks;
        }

        private static List<ContentBlock> BuildCopilotAssistedBlocks(
            int prNumber, Core.Models.CopilotReview.CopilotReviewResult copilotResult)
        {
            var blocks = new List<ContentBlock>(copilotResult.PageReviews.Count + 1);

            // First block: mode indicator + header (FR-004, FR-006).
            blocks.Add(new TextContentBlock
            {
                Text = PlainTextFormatter.FormatCopilotReviewHeader(
                    prNumber,
                    copilotResult.TotalPages,
                    copilotResult.SucceededPages,
                    copilotResult.FailedPages)
            });

            // One block per page (success or failure) in page-number order (FR-005, FR-007a).
            foreach (var pageReview in copilotResult.PageReviews)
            {
                blocks.Add(new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatCopilotPageReviewBlock(pageReview)
                });
            }

            return blocks;
        }

        private static List<ContentBlock> BuildFriendlyStillPreparingBlocks(int prNumber, int pageNumber)
        {
            return new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: "Response is still being prepared",
                        explanation: $"Background enrichment for PR #{prNumber} is still running.",
                        suggestedNextAction: $"Retry get_pr_content with pageNumber={pageNumber} in a moment")
                }
            };
        }

        private static List<ContentBlock> BuildFriendlyFailureBlocks(int prNumber, int pageNumber, PrEnrichmentFailure failure)
        {
            return new List<ContentBlock>
            {
                new TextContentBlock
                {
                    Text = PlainTextFormatter.FormatFriendlyStatus(
                        headline: $"Background enrichment failed for PR #{prNumber}",
                        explanation: $"{failure.ExceptionTypeName}: {failure.SanitizedMessage}",
                        suggestedNextAction: $"Retry get_pr_content with pageNumber={pageNumber}")
                }
            };
        }

        private static Dictionary<string, int> BuildCategoryBreakdown(
            List<string> pagePaths, IReadOnlyList<PackingCandidate> sortedCandidates)
        {
            var categories = new Dictionary<string, int>();
            var pagePathSet = new HashSet<string>(pagePaths, StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in sortedCandidates)
            {
                if (!pagePathSet.Contains(candidate.Path)) continue;
                var key = candidate.Category.ToString().ToLowerInvariant();
                categories[key] = categories.GetValueOrDefault(key) + 1;
            }
            return categories;
        }

        /// <summary>
        /// Polls <see cref="ICopilotReviewOrchestrator.TryGetSnapshot"/> at a configurable
        /// interval while waiting for the review to finish, sending incremental progress
        /// notifications as pages complete. Returns the final result once all pages are done.
        /// </summary>
        private async Task<Core.Models.CopilotReview.CopilotReviewResult> WaitForReviewWithProgressAsync(
            int prNumber,
            IProgress<ProgressNotificationValue>? progress,
            CancellationToken cancellationToken)
        {
            var reviewTask = _copilotReviewOrchestrator.WaitForReviewAsync(prNumber, cancellationToken);
            var pollingIntervalMs = _workflowOptions.Value.CopilotReviewProgressPollingIntervalMs;

            int lastReportedCompleted = 0;
            string? lastReportedActivity = null;
            int progressStep = 4; // continues after step 3 ("review started")

            while (!reviewTask.IsCompleted)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var snapshot = _copilotReviewOrchestrator.TryGetSnapshot(prNumber);
                if (snapshot is not null)
                {
                    // Report page completions (higher priority).
                    if (snapshot is { TotalPages: > 0, CompletedPages: > 0 } && snapshot.CompletedPages > lastReportedCompleted)
                    {
                        lastReportedCompleted = snapshot.CompletedPages;
                        lastReportedActivity = null; // reset so next activity change is reported
                        await _progressReporter.ReportAsync(progress,
                            progressStep++, null,
                            $"Copilot review — {snapshot.CompletedPages}/{snapshot.TotalPages} pages complete",
                            cancellationToken);
                    }
                    // Report intermediate activity changes (e.g. "Page 2/5: creating session").
                    else if (snapshot.CurrentActivity is not null && snapshot.CurrentActivity != lastReportedActivity)
                    {
                        lastReportedActivity = snapshot.CurrentActivity;
                        await _progressReporter.ReportAsync(progress,
                            progressStep++, null,
                            snapshot.CurrentActivity,
                            cancellationToken);
                    }
                }

                await Task.WhenAny(reviewTask, Task.Delay(pollingIntervalMs, cancellationToken));
            }

            // Final snapshot check — the loop may exit before reporting the last page
            // because reviewTask completes at the same time as the last page finishes.
            var finalSnapshot = _copilotReviewOrchestrator.TryGetSnapshot(prNumber);
            if (finalSnapshot is { TotalPages: > 0 } && finalSnapshot.CompletedPages > lastReportedCompleted)
            {
                await _progressReporter.ReportAsync(progress,
                    progressStep++, null,
                    $"Copilot review — {finalSnapshot.CompletedPages}/{finalSnapshot.TotalPages} pages complete",
                    cancellationToken);
            }

            return await reviewTask;
        }
    }
}
