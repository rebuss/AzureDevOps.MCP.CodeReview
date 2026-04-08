namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Result of <see cref="IReviewFileClassifier.Classify(string)"/>.
/// <see cref="MatchedPattern"/> is the first pattern in configuration order that
/// matched, or <c>null</c> when <see cref="Classification"/> is <see cref="ReviewFileClassification.Deep"/>.
/// </summary>
public sealed record ReviewFileClassificationResult(
    ReviewFileClassification Classification,
    string? MatchedPattern);
