namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Classification of a file in a review session — determines whether the file
/// is delivered in full or as a synthetic scan-only summary (feature 014).
/// Default for any path that matches no scan pattern is <see cref="Deep"/>.
/// </summary>
public enum ReviewFileClassification
{
    /// <summary>Reviewed in full — `next_review_item` returns the file's enriched content.</summary>
    Deep,

    /// <summary>Reviewed as a synthetic summary — `next_review_item` returns a small summary block. Acknowledgment is still required.</summary>
    Scan,
}
