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
