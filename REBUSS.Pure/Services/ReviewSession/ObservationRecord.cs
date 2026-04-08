namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// One immutable entry in a file's append-only observation history (FR-014, FR-015).
/// Once added to a <see cref="ReviewFileEntry.Observations"/> list, never modified.
/// </summary>
public sealed record ObservationRecord(
    int SequenceNumber,
    string Text,
    ReviewItemStatus Status,
    DateTimeOffset RecordedAt);
