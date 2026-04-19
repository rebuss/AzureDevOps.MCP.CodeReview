using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services;

namespace REBUSS.Pure.Tests.Services;

public class CompositeCodeProcessorTests
{
    private readonly CompositeCodeProcessor _processor;
    private readonly IDiffEnricher _enricher1 = Substitute.For<IDiffEnricher>();
    private readonly IDiffEnricher _enricher2 = Substitute.For<IDiffEnricher>();

    public CompositeCodeProcessorTests()
    {
        _enricher1.Order.Returns(100);
        _enricher2.Order.Returns(200);
        _processor = new CompositeCodeProcessor(
            new[] { _enricher2, _enricher1 }, // Out of order — should be sorted
            NullLogger<CompositeCodeProcessor>.Instance);
    }

    [Fact]
    public async Task AddBeforeAfterContext_SingleEnricher_DelegatesCorrectly()
    {
        _enricher1.CanEnrich("input").Returns(true);
        _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>()).Returns("enriched");
        _enricher2.CanEnrich(Arg.Any<string>()).Returns(false);

        var result = await _processor.AddBeforeAfterContext("input");

        Assert.Equal("enriched", result);
    }

    [Fact]
    public async Task AddBeforeAfterContext_MultipleEnrichers_AppliedInOrder()
    {
        _enricher1.CanEnrich("input").Returns(true);
        _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>()).Returns("after-100");
        _enricher2.CanEnrich("after-100").Returns(true);
        _enricher2.EnrichAsync("after-100", Arg.Any<CancellationToken>()).Returns("after-200");

        var result = await _processor.AddBeforeAfterContext("input");

        Assert.Equal("after-200", result);
        // Verify Order=100 was called first
        Received.InOrder(() =>
        {
            _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>());
            _enricher2.EnrichAsync("after-100", Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task AddBeforeAfterContext_EnricherCanEnrichFalse_Skipped()
    {
        _enricher1.CanEnrich("input").Returns(false);
        _enricher2.CanEnrich("input").Returns(false);

        var result = await _processor.AddBeforeAfterContext("input");

        Assert.Equal("input", result);
        await _enricher1.DidNotReceive().EnrichAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _enricher2.DidNotReceive().EnrichAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBeforeAfterContext_EnricherThrows_ReturnsDiffUnchanged()
    {
        _enricher1.CanEnrich("input").Returns(true);
        _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        _enricher2.CanEnrich("input").Returns(true);
        _enricher2.EnrichAsync("input", Arg.Any<CancellationToken>()).Returns("after-200");

        var result = await _processor.AddBeforeAfterContext("input");

        // Enricher1 failed, so enricher2 gets the original "input"
        Assert.Equal("after-200", result);
    }

    [Fact]
    public async Task AddBeforeAfterContext_NoEnrichers_ReturnsDiffUnchanged()
    {
        var processor = new CompositeCodeProcessor(
            Array.Empty<IDiffEnricher>(),
            NullLogger<CompositeCodeProcessor>.Instance);

        var result = await processor.AddBeforeAfterContext("input");

        Assert.Equal("input", result);
    }

    [Fact]
    public async Task AddBeforeAfterContext_OperationCancelled_Propagates()
    {
        _enricher1.CanEnrich("input").Returns(true);
        _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException());
        _enricher2.CanEnrich(Arg.Any<string>()).Returns(true);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _processor.AddBeforeAfterContext("input"));

        // Enricher2 should NOT have been called — cancellation short-circuits
        await _enricher2.DidNotReceive().EnrichAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddBeforeAfterContext_ThreeEnrichers_FirstFails_SecondAndThirdGetOriginal()
    {
        var enricher3 = Substitute.For<IDiffEnricher>();
        enricher3.Order.Returns(300);
        var processor = new CompositeCodeProcessor(
            new[] { _enricher1, _enricher2, enricher3 },
            NullLogger<CompositeCodeProcessor>.Instance);

        _enricher1.CanEnrich("input").Returns(true);
        _enricher1.EnrichAsync("input", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        // After enricher1 fails, enricher2 and enricher3 get "input" (unchanged)
        _enricher2.CanEnrich("input").Returns(true);
        _enricher2.EnrichAsync("input", Arg.Any<CancellationToken>()).Returns("after-200");

        enricher3.CanEnrich("after-200").Returns(true);
        enricher3.EnrichAsync("after-200", Arg.Any<CancellationToken>()).Returns("after-300");

        var result = await processor.AddBeforeAfterContext("input");

        Assert.Equal("after-300", result);
    }
}
