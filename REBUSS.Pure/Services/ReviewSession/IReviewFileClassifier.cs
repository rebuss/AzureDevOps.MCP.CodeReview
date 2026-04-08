namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Pattern-based file classifier for the stateful PR review session (feature 014).
/// Singleton, stateless. Reads the configured pattern list once at construction
/// and provides O(#patterns) classification per file thereafter.
/// </summary>
public interface IReviewFileClassifier
{
    ReviewFileClassificationResult Classify(string filePath);
}
