using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using REBUSS.Pure.Core.Models.CopilotReview;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Shared helper that polls <see cref="ICopilotReviewOrchestrator.TryGetSnapshot"/>
/// at a configurable interval while waiting for a review to finish, sending
/// incremental MCP progress notifications as pages complete. Used by both
/// <c>GetPullRequestContentToolHandler</c> and <c>GetLocalContentToolHandler</c>.
/// </summary>
public sealed class CopilotReviewWaiter
{
    private readonly ICopilotReviewOrchestrator _orchestrator;
    private readonly IProgressReporter _progressReporter;
    private readonly IOptions<WorkflowOptions> _workflowOptions;

    public CopilotReviewWaiter(
        ICopilotReviewOrchestrator orchestrator,
        IProgressReporter progressReporter,
        IOptions<WorkflowOptions> workflowOptions)
    {
        _orchestrator = orchestrator;
        _progressReporter = progressReporter;
        _workflowOptions = workflowOptions;
    }

    /// <summary>
    /// Waits for the Copilot review to complete, sending progress notifications
    /// as pages finish. Returns the final result.
    /// </summary>
    /// <param name="reviewKey">The review key passed to <see cref="ICopilotReviewOrchestrator.TriggerReview"/>.</param>
    /// <param name="progress">SDK-injected progress reporter, or <c>null</c>.</param>
    /// <param name="startingStep">The progress step number to continue from (caller has already reported steps 0..startingStep-1).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<CopilotReviewResult> WaitWithProgressAsync(
        string reviewKey,
        IProgress<ProgressNotificationValue>? progress,
        int startingStep,
        CancellationToken cancellationToken)
    {
        var reviewTask = _orchestrator.WaitForReviewAsync(reviewKey, cancellationToken);
        var pollingIntervalMs = _workflowOptions.Value.CopilotReviewProgressPollingIntervalMs;

        int lastReportedCompleted = 0;
        string? lastReportedActivity = null;
        int progressStep = startingStep;

        while (!reviewTask.IsCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var snapshot = _orchestrator.TryGetSnapshot(reviewKey);
            if (snapshot is not null)
            {
                if (snapshot is { TotalPages: > 0, CompletedPages: > 0 } && snapshot.CompletedPages > lastReportedCompleted)
                {
                    lastReportedCompleted = snapshot.CompletedPages;
                    lastReportedActivity = null;
                    await _progressReporter.ReportAsync(progress,
                        progressStep++, null,
                        $"Copilot review — {snapshot.CompletedPages}/{snapshot.TotalPages} pages complete",
                        cancellationToken);
                }
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

        var finalSnapshot = _orchestrator.TryGetSnapshot(reviewKey);
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
