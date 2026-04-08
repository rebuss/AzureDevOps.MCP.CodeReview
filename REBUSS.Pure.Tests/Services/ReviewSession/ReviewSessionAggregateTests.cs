using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.ReviewSession;
using RSession = REBUSS.Pure.Services.ReviewSession.ReviewSession;

namespace REBUSS.Pure.Tests.Services.ReviewSession;

public class ReviewSessionAggregateTests
{
    private static (RSession Session, ISingleFileChunker Chunker) NewSession(
        params (string Path, string Content)[] files)
    {
        var entries = files
            .Select(f => new ReviewFileEntry(f.Path, FileCategory.Source, f.Content.Length))
            .ToList();
        var enriched = files.ToDictionary(f => f.Path, f => f.Content);

        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);

        var s = new RSession("sid", 42, "head", 10_000, entries, enriched, DateTimeOffset.UtcNow);
        return (s, chunker);
    }

    [Fact]
    public void HappyPath_DeliverThenAck_TransitionsToReviewedComplete()
    {
        var (s, ch) = NewSession(("a.cs", "content-a"));
        var now = DateTimeOffset.UtcNow;

        var r = s.NextItem(ch, now);
        Assert.Equal(NextItemKind.Delivered, r.Kind);
        Assert.Equal("a.cs", r.File!.Path);
        Assert.Equal(ReviewItemStatus.DeliveredAwaitingObservation, r.File.Status);

        var rec = s.RecordObservation("a.cs", "looks good", ReviewItemStatus.ReviewedComplete, now);
        Assert.Equal(RecordKind.Ok, rec.Kind);
        Assert.Equal(ReviewItemStatus.ReviewedComplete, s.Files[0].Status);
        Assert.Equal(1, rec.AcknowledgedCount);
    }

    [Fact]
    public void NextItem_BeforeAck_RefusesToAdvance()
    {
        var (s, ch) = NewSession(("a.cs", "x"), ("b.cs", "y"));
        s.NextItem(ch, DateTimeOffset.UtcNow);

        var second = s.NextItem(ch, DateTimeOffset.UtcNow);
        Assert.Equal(NextItemKind.NeedsAcknowledgment, second.Kind);
        Assert.Equal("a.cs", second.File!.Path);
    }

    [Fact]
    public void RecordObservation_OnPending_Rejected()
    {
        var (s, _) = NewSession(("a.cs", "x"));
        var r = s.RecordObservation("a.cs", "obs", ReviewItemStatus.ReviewedComplete, DateTimeOffset.UtcNow);
        Assert.Equal(RecordKind.RejectedFileNotDelivered, r.Kind);
    }

    [Fact]
    public void AppendOnly_TwoObservationsPreservedWithMonotonicSeq()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "first take", ReviewItemStatus.ReviewedComplete, now);
        s.RecordObservation("a.cs", "second thought", ReviewItemStatus.ReviewedComplete, now);

        var obs = s.Files[0].Observations;
        Assert.Equal(2, obs.Count);
        Assert.Equal(1, obs[0].SequenceNumber);
        Assert.Equal(2, obs[1].SequenceNumber);
        Assert.Equal("first take", obs[0].Text);
        Assert.Equal("second thought", obs[1].Text);
    }

    [Fact]
    public void StatusReflectsMostRecentAcknowledgment()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);
        s.RecordObservation("a.cs", "actually skip", ReviewItemStatus.SkippedWithReason, now);
        Assert.Equal(ReviewItemStatus.SkippedWithReason, s.Files[0].Status);
    }

    [Fact]
    public void ChunkedFile_DeliveredAcrossMultipleNextItemCalls()
    {
        // Force chunking by giving a very small budget via a separate constructor.
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", safeBudgetTokens: 25, entries, enriched, DateTimeOffset.UtcNow);

        var r1 = session.NextItem(chunker, DateTimeOffset.UtcNow);
        Assert.Equal(NextItemKind.Delivered, r1.Kind);
        Assert.True(r1.TotalChunks >= 2);
        Assert.Equal(ReviewItemStatus.DeliveredPartial, session.Files[0].Status);

        // Subsequent calls without ack return next chunk (NOT NeedsAcknowledgment).
        for (int i = 2; i <= r1.TotalChunks; i++)
        {
            var ri = session.NextItem(chunker, DateTimeOffset.UtcNow);
            Assert.Equal(NextItemKind.Delivered, ri.Kind);
            Assert.Equal(i, ri.ChunkIndex);
        }
        Assert.Equal(ReviewItemStatus.DeliveredAwaitingObservation, session.Files[0].Status);

        // Recording on a partially-delivered file is rejected (mid-chunk).
        // — verified separately by the partial-test below.
    }

    [Fact]
    public void RecordObservation_OnPartial_Rejected()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", safeBudgetTokens: 25, entries, enriched, DateTimeOffset.UtcNow);

        session.NextItem(chunker, DateTimeOffset.UtcNow); // chunk 1, file in DeliveredPartial
        var rec = session.RecordObservation("big.cs", "premature", ReviewItemStatus.ReviewedComplete, DateTimeOffset.UtcNow);
        Assert.Equal(RecordKind.RejectedFilePartial, rec.Kind);
    }

    [Fact]
    public void Submit_AllAcknowledged_Accepted()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);
        var sub = s.Submit("final review", force: false, now);
        Assert.Equal(SubmitKind.Accepted, sub.Kind);
        Assert.True(s.IsSubmitted);
        Assert.Equal("final review", s.FinalReviewText);
    }

    [Fact]
    public void Submit_Incomplete_NoForce_Rejected()
    {
        var (s, ch) = NewSession(("a.cs", "x"), ("b.cs", "y"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);

        var sub = s.Submit("review", force: false, now);
        Assert.Equal(SubmitKind.RejectedIncomplete, sub.Kind);
        Assert.Single(sub.UnacknowledgedFiles);
        Assert.Equal("b.cs", sub.UnacknowledgedFiles[0].Path);
        Assert.False(s.IsSubmitted);
    }

    [Fact]
    public void Submit_Incomplete_WithForce_AcceptsAndRecordsOverride()
    {
        var (s, ch) = NewSession(("a.cs", "x"), ("b.cs", "y"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);

        var sub = s.Submit("review", force: true, now);
        Assert.Equal(SubmitKind.AcceptedWithForce, sub.Kind);
        Assert.True(s.IsSubmitted);
        Assert.True(s.SubmissionUsedForce);
        Assert.Single(sub.UnacknowledgedFiles);
    }

    [Fact]
    public void NextItem_AfterSubmit_ReturnsSessionSubmitted()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);
        s.Submit("r", false, now);

        var r = s.NextItem(ch, now);
        Assert.Equal(NextItemKind.SessionSubmitted, r.Kind);
    }

    [Fact]
    public void EnrichmentMissing_AutoSkipsAndContinues()
    {
        var entries = new List<ReviewFileEntry>
        {
            new("missing.cs", FileCategory.Source, 1),
            new("present.cs", FileCategory.Source, 1),
        };
        var enriched = new Dictionary<string, string> { ["present.cs"] = "ok" };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", 1000, entries, enriched, DateTimeOffset.UtcNow);

        var r = session.NextItem(chunker, DateTimeOffset.UtcNow);
        Assert.Equal(NextItemKind.Delivered, r.Kind);
        Assert.Equal("present.cs", r.File!.Path);
        Assert.Equal(ReviewItemStatus.SkippedWithReason, session.Files[0].Status); // missing.cs auto-skipped
        Assert.Single(session.Files[0].Observations);
    }

    // ─── Feature 013: Refetch ─────────────────────────────────────────────────────

    [Fact]
    public void Refetch_OnPending_ReturnsFilePending()
    {
        var (s, _) = NewSession(("a.cs", "content"));
        var r = s.Refetch("a.cs", 1);
        Assert.Equal(RefetchKind.FilePending, r.Kind);
    }

    [Fact]
    public void Refetch_OnPartial_ReturnsFilePartial()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", safeBudgetTokens: 25, entries, enriched, DateTimeOffset.UtcNow);
        session.NextItem(chunker, DateTimeOffset.UtcNow); // file goes into DeliveredPartial

        var r = session.Refetch("big.cs", 1);
        Assert.Equal(RefetchKind.FilePartial, r.Kind);
    }

    [Fact]
    public void Refetch_OnReviewedComplete_ReturnsExactOriginalContent()
    {
        var (s, ch) = NewSession(("a.cs", "exact-content"));
        var now = DateTimeOffset.UtcNow;
        var deliv = s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);

        var r = s.Refetch("a.cs", 1);
        Assert.Equal(RefetchKind.Ok, r.Kind);
        Assert.Equal(deliv.Content, r.Content);
        Assert.Equal(1, r.ChunkIndex);
        Assert.Equal(1, r.TotalChunks);
    }

    [Fact]
    public void Refetch_OnSubmittedSession_StillSucceeds()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);
        s.Submit("done", false, now);

        var r = s.Refetch("a.cs", 1);
        Assert.Equal(RefetchKind.Ok, r.Kind);
        Assert.Equal("x", r.Content);
    }

    [Fact]
    public void Refetch_OnFileNotInSession_ReturnsFileNotInSession()
    {
        var (s, _) = NewSession(("a.cs", "x"));
        var r = s.Refetch("zzz.cs", 1);
        Assert.Equal(RefetchKind.FileNotInSession, r.Kind);
    }

    [Fact]
    public void Refetch_OnChunkedFile_DefaultIndex1_ReturnsChunk1()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", 25, entries, enriched, DateTimeOffset.UtcNow);
        // Walk all chunks to reach DeliveredAwaitingObservation
        var first = session.NextItem(chunker, DateTimeOffset.UtcNow);
        var total = first.TotalChunks;
        for (int i = 2; i <= total; i++) session.NextItem(chunker, DateTimeOffset.UtcNow);

        var r = session.Refetch("big.cs", 1);
        Assert.Equal(RefetchKind.Ok, r.Kind);
        Assert.Equal(1, r.ChunkIndex);
        Assert.Equal(total, r.TotalChunks);
    }

    [Fact]
    public void Refetch_OnChunkedFile_Index2_ReturnsChunk2()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", 25, entries, enriched, DateTimeOffset.UtcNow);
        var first = session.NextItem(chunker, DateTimeOffset.UtcNow);
        var total = first.TotalChunks;
        Assert.True(total >= 2, "test setup must produce at least 2 chunks");
        for (int i = 2; i <= total; i++) session.NextItem(chunker, DateTimeOffset.UtcNow);

        var r = session.Refetch("big.cs", 2);
        Assert.Equal(RefetchKind.Ok, r.Kind);
        Assert.Equal(2, r.ChunkIndex);
    }

    [Fact]
    public void Refetch_ChunkIndexOutOfRange_ReturnsChunkOutOfRange()
    {
        var entries = new List<ReviewFileEntry> { new("big.cs", FileCategory.Source, 1) };
        var content = "@@ -1,2 +1,2 @@\nline-a\n@@ -10,2 +10,2 @@\nline-b\n@@ -20,2 +20,2 @@\nline-c";
        var enriched = new Dictionary<string, string> { ["big.cs"] = content };
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", 25, entries, enriched, DateTimeOffset.UtcNow);
        var first = session.NextItem(chunker, DateTimeOffset.UtcNow);
        var total = first.TotalChunks;
        for (int i = 2; i <= total; i++) session.NextItem(chunker, DateTimeOffset.UtcNow);

        Assert.Equal(RefetchKind.ChunkOutOfRange, session.Refetch("big.cs", 0).Kind);
        Assert.Equal(RefetchKind.ChunkOutOfRange, session.Refetch("big.cs", total + 1).Kind);
    }

    [Fact]
    public void Refetch_OnUnchunkedFile_NonOneIndex_ReturnsChunkOutOfRange()
    {
        var (s, ch) = NewSession(("a.cs", "x"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "ok", ReviewItemStatus.ReviewedComplete, now);

        Assert.Equal(RefetchKind.ChunkOutOfRange, s.Refetch("a.cs", 2).Kind);
        Assert.Equal(RefetchKind.ChunkOutOfRange, s.Refetch("a.cs", 0).Kind);
    }

    [Fact]
    public void Refetch_DoesNotMutateSessionState()
    {
        var (s, ch) = NewSession(("a.cs", "x"), ("b.cs", "y"));
        var now = DateTimeOffset.UtcNow;
        s.NextItem(ch, now);
        s.RecordObservation("a.cs", "first", ReviewItemStatus.ReviewedComplete, now);

        var beforeStatuses = s.Files.Select(f => f.Status).ToArray();
        var beforeObsCounts = s.Files.Select(f => f.Observations.Count).ToArray();
        var beforeAck = s.AcknowledgedCount;

        // Multiple refetches
        s.Refetch("a.cs", 1);
        s.Refetch("a.cs", 1);
        s.Refetch("zzz.cs", 1);

        Assert.Equal(beforeStatuses, s.Files.Select(f => f.Status).ToArray());
        Assert.Equal(beforeObsCounts, s.Files.Select(f => f.Observations.Count).ToArray());
        Assert.Equal(beforeAck, s.AcknowledgedCount);
    }

    // ─── Feature 013: Query ───────────────────────────────────────────────────────

    private static RSession SessionWithObservations(params (string Path, string ObsText, ReviewItemStatus Status)[] obs)
    {
        var paths = obs.Select(o => o.Path).Distinct().ToArray();
        var entries = paths.Select(p => new ReviewFileEntry(p, FileCategory.Source, 1)).ToList();
        var enriched = paths.ToDictionary(p => p, p => "content-" + p);
        var est = Substitute.For<ITokenEstimator>();
        est.EstimateTokenCount(Arg.Any<string>()).Returns(ci => ((string)ci[0]).Length);
        var chunker = new SingleFileChunker(est);
        var session = new RSession("s", 1, "h", 10_000, entries, enriched, DateTimeOffset.UtcNow);
        var now = DateTimeOffset.UtcNow;
        // Deliver each file then record the observation
        var deliveredPaths = new HashSet<string>();
        foreach (var (path, text, status) in obs)
        {
            if (!deliveredPaths.Contains(path))
            {
                // Walk to that file
                while (true)
                {
                    var r = session.NextItem(chunker, now);
                    if (r.Kind == NextItemKind.Delivered && r.File!.Path == path) break;
                    if (r.Kind == NextItemKind.Delivered)
                        session.RecordObservation(r.File!.Path, "filler", ReviewItemStatus.ReviewedComplete, now);
                }
                deliveredPaths.Add(path);
            }
            session.RecordObservation(path, text, status, now);
        }
        return session;
    }

    [Fact]
    public void Query_EmptyString_ReturnsEmptyQuery()
    {
        var s = SessionWithObservations(("a.cs", "obs", ReviewItemStatus.ReviewedComplete));
        Assert.Equal(QueryKind.EmptyQuery, s.QueryObservations("", 5).Kind);
        Assert.Equal(QueryKind.EmptyQuery, s.QueryObservations("   ", 5).Kind);
        Assert.Equal(QueryKind.EmptyQuery, s.QueryObservations(null, 5).Kind);
    }

    [Fact]
    public void Query_NoMatches_ReturnsOkWithEmptyEntries()
    {
        var s = SessionWithObservations(("a.cs", "completely unrelated", ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("validation", 5);
        Assert.Equal(QueryKind.Ok, r.Kind);
        Assert.Empty(r.Entries);
    }

    [Fact]
    public void Query_SimpleSubstringMatch()
    {
        var s = SessionWithObservations(
            ("a.cs", "validation now requires CancellationToken", ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("validation", 5);
        Assert.Single(r.Entries);
        Assert.Contains("validation", r.Entries[0].Text);
    }

    [Fact]
    public void Query_CaseInsensitive()
    {
        var s = SessionWithObservations(
            ("a.cs", "Validation now requires Token", ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("VALIDATION", 5);
        Assert.Single(r.Entries);
    }

    [Fact]
    public void Query_MultiToken_ScoredByDistinctTokenCount()
    {
        var s = SessionWithObservations(
            ("a.cs", "validation null inputs handled", ReviewItemStatus.ReviewedComplete),
            ("b.cs", "validation only", ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("validation null", 5);
        Assert.Equal(2, r.Entries.Count);
        Assert.Equal("a.cs", r.Entries[0].FilePath); // higher score (2 tokens)
        Assert.Equal(2, r.Entries[0].MatchScore);
        Assert.Equal(1, r.Entries[1].MatchScore);
    }

    [Fact]
    public void Query_TieBreakAlphabeticalByFilePath()
    {
        var s = SessionWithObservations(
            ("zeta.cs", "match", ReviewItemStatus.ReviewedComplete),
            ("alpha.cs", "match", ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("match", 5);
        Assert.Equal("alpha.cs", r.Entries[0].FilePath);
        Assert.Equal("zeta.cs", r.Entries[1].FilePath);
    }

    [Fact]
    public void Query_TruncatesObservationOver2000Chars_AndSetsIsTruncated()
    {
        var longObs = "validation " + new string('x', 5000);
        var s = SessionWithObservations(("a.cs", longObs, ReviewItemStatus.ReviewedComplete));
        var r = s.QueryObservations("validation", 5);
        Assert.Single(r.Entries);
        Assert.True(r.Entries[0].IsTruncated);
        Assert.Equal(RSession.MaxObservationCharsInResult, r.Entries[0].Text.Length);
    }

    [Fact]
    public void Query_LimitDefault5_AndCappedAt20()
    {
        var observations = Enumerable.Range(0, 30)
            .Select(i => ($"f{i:D3}.cs", "match here", ReviewItemStatus.ReviewedComplete))
            .ToArray();
        var s = SessionWithObservations(observations);
        var r = s.QueryObservations("match", 100);
        Assert.Equal(20, r.Entries.Count);
        Assert.Equal(30, r.TotalMatches);
    }

    [Fact]
    public void Query_DoesNotMutateSessionState()
    {
        var s = SessionWithObservations(
            ("a.cs", "validation matches", ReviewItemStatus.ReviewedComplete),
            ("b.cs", "skipped due to validation noise", ReviewItemStatus.SkippedWithReason));

        var beforeStatuses = s.Files.Select(f => f.Status).ToArray();
        var beforeObsCounts = s.Files.Select(f => f.Observations.Count).ToArray();

        s.QueryObservations("validation", 5);
        s.QueryObservations("anything", 20);
        s.QueryObservations("nomatch", 1);

        Assert.Equal(beforeStatuses, s.Files.Select(f => f.Status).ToArray());
        Assert.Equal(beforeObsCounts, s.Files.Select(f => f.Observations.Count).ToArray());
    }

    [Fact]
    public void Query_IncludesObservationsFromSkippedFiles()
    {
        var s = SessionWithObservations(
            ("a.cs", "skipped: generated file", ReviewItemStatus.SkippedWithReason));
        var r = s.QueryObservations("generated", 5);
        Assert.Single(r.Entries);
        Assert.Equal(ReviewItemStatus.SkippedWithReason, r.Entries[0].Status);
    }

    [Fact]
    public void Files_AreDeliveredInListOrder()
    {
        // Caller is responsible for sorting; aggregate respects the supplied order.
        var (s, ch) = NewSession(("a.cs", "1"), ("b.cs", "2"), ("c.cs", "3"));
        var now = DateTimeOffset.UtcNow;
        var first = s.NextItem(ch, now);
        Assert.Equal("a.cs", first.File!.Path);
        s.RecordObservation("a.cs", "x", ReviewItemStatus.ReviewedComplete, now);
        var second = s.NextItem(ch, now);
        Assert.Equal("b.cs", second.File!.Path);
    }
}
