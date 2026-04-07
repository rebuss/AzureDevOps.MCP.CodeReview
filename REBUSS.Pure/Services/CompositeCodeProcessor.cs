using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Services;

/// <summary>
/// Implements <see cref="ICodeProcessor"/> by chaining all registered <see cref="IDiffEnricher"/>
/// implementations in ascending <see cref="IDiffEnricher.Order"/>.
/// Each enricher is applied sequentially; failures are logged and the diff is passed through unchanged.
/// </summary>
public class CompositeCodeProcessor : ICodeProcessor
{
    private readonly IReadOnlyList<IDiffEnricher> _enrichers;
    private readonly ILogger<CompositeCodeProcessor> _logger;

    public CompositeCodeProcessor(
        IEnumerable<IDiffEnricher> enrichers,
        ILogger<CompositeCodeProcessor> logger)
    {
        _enrichers = enrichers.OrderBy(e => e.Order).ToList();
        _logger = logger;
    }

    public async Task<string> AddBeforeAfterContext(string diff, CancellationToken ct = default)
    {
        // Idempotence short-circuit (feature 011): if the input already carries any of
        // the enricher-emitted markers, return it untouched. This single point of policy
        // covers all five enrichers in the chain so a future enricher inherits the
        // guarantee for free, and a second pass through the chain is a byte-identical
        // no-op.
        if (DiffLanguageDetector.IsAlreadyEnriched(diff))
            return diff;

        var currentDiff = diff;

        foreach (var enricher in _enrichers)
        {
            if (!enricher.CanEnrich(currentDiff))
                continue;

            try
            {
                currentDiff = await enricher.EnrichAsync(currentDiff, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Diff enricher {Enricher} (Order={Order}) failed, continuing with diff unchanged",
                    enricher.GetType().Name, enricher.Order);
            }
        }

        return currentDiff;
    }
}
