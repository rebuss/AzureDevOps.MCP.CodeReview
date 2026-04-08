using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Stateful per-PR review session aggregate. All mutation goes through methods
/// on this class and is serialized by an internal lock. The state machine
/// (Pending → DeliveredPartial → DeliveredAwaitingObservation → ReviewedComplete |
/// SkippedWithReason) is enforced here so that all four MCP tool handlers see
/// identical semantics.
///
/// This is the load-bearing exception to constitution Principle VI documented in
/// FR-030 of the feature spec.
/// </summary>
public sealed class ReviewSession
{
    private readonly object _lock = new();
    private readonly Dictionary<string, ReviewFileEntry> _byPath;
    private readonly IReadOnlyDictionary<string, string> _enrichedByPath;

    public ReviewSession(
        string sessionId,
        int prNumber,
        string headSha,
        int safeBudgetTokens,
        IReadOnlyList<ReviewFileEntry> files,
        IReadOnlyDictionary<string, string> enrichedByPath,
        DateTimeOffset createdAt)
    {
        SessionId = sessionId;
        PrNumber = prNumber;
        HeadSha = headSha;
        SafeBudgetTokens = safeBudgetTokens;
        Files = files;
        _enrichedByPath = enrichedByPath;
        CreatedAt = createdAt;
        _byPath = files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
    }

    public string SessionId { get; }
    public int PrNumber { get; }
    public string HeadSha { get; }
    public DateTimeOffset CreatedAt { get; }
    public int SafeBudgetTokens { get; }
    public IReadOnlyList<ReviewFileEntry> Files { get; }
    public bool IsSubmitted { get; private set; }
    public DateTimeOffset? SubmittedAt { get; private set; }
    public bool SubmissionUsedForce { get; private set; }
    public string? FinalReviewText { get; private set; }

    public int AcknowledgedCount
    {
        get
        {
            lock (_lock)
            {
                return Files.Count(f =>
                    f.Status == ReviewItemStatus.ReviewedComplete ||
                    f.Status == ReviewItemStatus.SkippedWithReason);
            }
        }
    }

    /// <summary>
    /// Returns the next item to deliver: the next chunk of a partially-delivered
    /// file, OR the next pending file (whole or first chunk).
    /// Enforces FR-007 (acknowledgment gate) and FR-012 (post-submit lockout).
    /// </summary>
    public NextItemResult NextItem(ISingleFileChunker chunker, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(chunker);
        lock (_lock)
        {
            if (IsSubmitted)
                return new NextItemResult(NextItemKind.SessionSubmitted, null, null, 0, 0);

            // 1. If any file is in DeliveredPartial → return its next chunk.
            var partial = Files.FirstOrDefault(f => f.Status == ReviewItemStatus.DeliveredPartial);
            if (partial is not null)
                return DeliverNextChunk(partial, now);

            // 2. Acknowledgment gate: if any file is in DeliveredAwaitingObservation, refuse to advance.
            var awaiting = Files.FirstOrDefault(f => f.Status == ReviewItemStatus.DeliveredAwaitingObservation);
            if (awaiting is not null)
                return new NextItemResult(NextItemKind.NeedsAcknowledgment, awaiting, null, 0, 0);

            // 3. Find the next Pending file in alphabetical order (Files is pre-sorted).
            var next = Files.FirstOrDefault(f => f.Status == ReviewItemStatus.Pending);
            if (next is null)
                return new NextItemResult(NextItemKind.AllDelivered, null, null, 0, 0);

            // Defensive: if enrichment did not produce content for this path, auto-skip with reason.
            if (!_enrichedByPath.TryGetValue(next.Path, out var enriched) || enriched is null)
            {
                next.Status = ReviewItemStatus.SkippedWithReason;
                next.DeliveredAt = now;
                next.LastAcknowledgedAt = now;
                next.Observations.Add(new ObservationRecord(
                    SequenceNumber: 1,
                    Text: "auto-skipped: enrichment unavailable for this file",
                    Status: ReviewItemStatus.SkippedWithReason,
                    RecordedAt: now));
                // Recurse via tail-call style: try the next file.
                return NextItem(chunker, now);
            }

            // 4. If the file fits the budget, deliver it whole.
            var chunks = chunker.Split(enriched, SafeBudgetTokens);
            next.DeliveredAt = now;
            if (chunks.Count == 1)
            {
                next.Status = ReviewItemStatus.DeliveredAwaitingObservation;
                next.CurrentChunkIndex = 1;
                next.TotalChunks = 1;
                return new NextItemResult(NextItemKind.Delivered, next, chunks[0], 1, 1);
            }

            // 5. Oversize: cache chunks, return chunk 1, mark partial.
            next.Chunks = chunks;
            next.TotalChunks = chunks.Count;
            next.CurrentChunkIndex = 1;
            next.Status = ReviewItemStatus.DeliveredPartial;
            return new NextItemResult(NextItemKind.Delivered, next, chunks[0], 1, chunks.Count);
        }
    }

    private NextItemResult DeliverNextChunk(ReviewFileEntry partial, DateTimeOffset now)
    {
        var nextIdx = (partial.CurrentChunkIndex ?? 0) + 1;
        var total = partial.TotalChunks ?? partial.Chunks!.Count;
        partial.CurrentChunkIndex = nextIdx;
        var content = partial.Chunks![nextIdx - 1];
        if (nextIdx >= total)
            partial.Status = ReviewItemStatus.DeliveredAwaitingObservation;
        return new NextItemResult(NextItemKind.Delivered, partial, content, nextIdx, total);
    }

    /// <summary>
    /// Records an observation against a previously-delivered file. Append-only —
    /// existing observations are never modified (FR-014, FR-015, FR-016).
    /// </summary>
    public RecordResult RecordObservation(string filePath, string observations, ReviewItemStatus declaredStatus, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrEmpty(filePath);
        ArgumentNullException.ThrowIfNull(observations);

        if (declaredStatus is not (ReviewItemStatus.ReviewedComplete or ReviewItemStatus.SkippedWithReason))
            return new RecordResult(RecordKind.InvalidStatus, null, 0, 0);

        lock (_lock)
        {
            if (IsSubmitted)
                return new RecordResult(RecordKind.SessionSubmitted, null, 0, 0);

            if (!_byPath.TryGetValue(filePath, out var file))
                return new RecordResult(RecordKind.FileNotFound, null, 0, 0);

            if (file.Status == ReviewItemStatus.Pending)
                return new RecordResult(RecordKind.RejectedFileNotDelivered, file, 0, 0);

            if (file.Status == ReviewItemStatus.DeliveredPartial)
                return new RecordResult(RecordKind.RejectedFilePartial, file, 0, 0);

            var seq = file.Observations.Count == 0 ? 1 : file.Observations[^1].SequenceNumber + 1;
            file.Observations.Add(new ObservationRecord(seq, observations, declaredStatus, now));
            file.Status = declaredStatus;
            file.LastAcknowledgedAt = now;

            int ack = Files.Count(f =>
                f.Status == ReviewItemStatus.ReviewedComplete ||
                f.Status == ReviewItemStatus.SkippedWithReason);
            return new RecordResult(RecordKind.Ok, file, ack, Files.Count);
        }
    }

    /// <summary>
    /// Submits the session. Rejects with a structured result (NOT an exception)
    /// listing unacknowledged files unless <paramref name="force"/> is true (FR-009/FR-010/FR-011).
    /// </summary>
    public SubmitResult Submit(string reviewText, bool force, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(reviewText);
        lock (_lock)
        {
            if (IsSubmitted)
                return new SubmitResult(SubmitKind.AlreadySubmitted, Array.Empty<ReviewFileEntry>());

            var unacked = Files
                .Where(f => f.Status != ReviewItemStatus.ReviewedComplete && f.Status != ReviewItemStatus.SkippedWithReason)
                .ToArray();

            if (unacked.Length > 0 && !force)
                return new SubmitResult(SubmitKind.RejectedIncomplete, unacked);

            IsSubmitted = true;
            SubmittedAt = now;
            SubmissionUsedForce = force && unacked.Length > 0;
            FinalReviewText = reviewText;

            return new SubmitResult(
                unacked.Length > 0 ? SubmitKind.AcceptedWithForce : SubmitKind.Accepted,
                unacked);
        }
    }
}

public enum NextItemKind
{
    Delivered,
    NeedsAcknowledgment,
    AllDelivered,
    SessionSubmitted,
}

public sealed record NextItemResult(
    NextItemKind Kind,
    ReviewFileEntry? File,
    string? Content,
    int ChunkIndex,
    int TotalChunks);

public enum RecordKind
{
    Ok,
    FileNotFound,
    RejectedFileNotDelivered,
    RejectedFilePartial,
    InvalidStatus,
    SessionSubmitted,
}

public sealed record RecordResult(
    RecordKind Kind,
    ReviewFileEntry? File,
    int AcknowledgedCount,
    int TotalCount);

public enum SubmitKind
{
    Accepted,
    AcceptedWithForce,
    RejectedIncomplete,
    AlreadySubmitted,
}

public sealed record SubmitResult(
    SubmitKind Kind,
    IReadOnlyList<ReviewFileEntry> UnacknowledgedFiles);
