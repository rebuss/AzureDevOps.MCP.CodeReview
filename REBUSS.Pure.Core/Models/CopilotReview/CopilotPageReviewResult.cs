namespace REBUSS.Pure.Core.Models.CopilotReview;

/// <summary>
/// The outcome of reviewing one page of enriched PR content through
/// <see cref="Services.CopilotReview.ICopilotPageReviewer"/>, potentially after
/// up to 3 retry attempts inside <see cref="Services.CopilotReview.ICopilotReviewOrchestrator"/>.
/// <para>
/// Invariants (enforced by the <see cref="Success"/> / <see cref="Failure"/> factory methods):
/// <list type="bullet">
///   <item><c>Succeeded == true</c> ⇒ <c>ReviewText != null</c>, <c>FailedFilePaths.Count == 0</c>, <c>ErrorMessage == null</c></item>
///   <item><c>Succeeded == false</c> ⇒ <c>ReviewText == null</c>, <c>FailedFilePaths.Count &gt; 0</c>, <c>ErrorMessage != null</c></item>
///   <item><c>1 &lt;= AttemptsMade &lt;= 3</c></item>
/// </list>
/// </para>
/// </summary>
public sealed record CopilotPageReviewResult
{
    public required int PageNumber { get; init; }
    public required bool Succeeded { get; init; }
    public string? ReviewText { get; init; }
    public required IReadOnlyList<string> FailedFilePaths { get; init; }
    public string? ErrorMessage { get; init; }
    public required int AttemptsMade { get; init; }

    public static CopilotPageReviewResult Success(int pageNumber, string reviewText, int attemptsMade)
    {
        ArgumentNullException.ThrowIfNull(reviewText);
        if (attemptsMade is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(attemptsMade), "AttemptsMade must be in [1,3].");

        return new CopilotPageReviewResult
        {
            PageNumber = pageNumber,
            Succeeded = true,
            ReviewText = reviewText,
            FailedFilePaths = Array.Empty<string>(),
            ErrorMessage = null,
            AttemptsMade = attemptsMade,
        };
    }

    public static CopilotPageReviewResult Failure(
        int pageNumber,
        IReadOnlyList<string> failedFilePaths,
        string errorMessage,
        int attemptsMade)
    {
        ArgumentNullException.ThrowIfNull(failedFilePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(errorMessage);
        if (attemptsMade is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(attemptsMade), "AttemptsMade must be in [1,3].");

        return new CopilotPageReviewResult
        {
            PageNumber = pageNumber,
            Succeeded = false,
            ReviewText = null,
            FailedFilePaths = failedFilePaths,
            ErrorMessage = errorMessage,
            AttemptsMade = attemptsMade,
        };
    }
}
