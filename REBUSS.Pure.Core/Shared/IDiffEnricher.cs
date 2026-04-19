namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// A unit of diff enrichment. Implementations transform a single-file diff string,
/// adding contextual information (e.g., before/after context, structural change annotations).
/// Registered via DI and auto-discovered by the composite code processor.
/// </summary>
public interface IDiffEnricher
{
    /// <summary>
    /// Execution priority — lower values execute first.
    /// Convention: 100 = before/after context, 200 = structural changes, 300 = multi-language.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Synchronous gate — returns <c>false</c> to skip this enricher for the given diff.
    /// Must not perform I/O.
    /// </summary>
    bool CanEnrich(string diff);

    /// <summary>
    /// Enriches the formatted diff of a single file.
    /// Must return the original diff unchanged if enrichment fails (graceful fallback).
    /// </summary>
    Task<string> EnrichAsync(string diff, CancellationToken ct = default);
}
