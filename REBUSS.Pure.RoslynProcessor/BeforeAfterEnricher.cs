using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs with before/after context lines using Roslyn syntax analysis
/// to determine context level (None/Minimal/Full).
/// </summary>
public class BeforeAfterEnricher : IDiffEnricher
{
    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<BeforeAfterEnricher> _logger;

    public BeforeAfterEnricher(
        DiffSourceResolver sourceResolver,
        ILogger<BeforeAfterEnricher> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    public int Order => 100;

    public bool CanEnrich(string diff)
    {
        return DiffLanguageDetector.IsCSharp(diff) && !DiffLanguageDetector.IsSkipped(diff);
    }

    public async Task<string> EnrichAsync(string diff, CancellationToken ct = default)
    {
        try
        {
            var pair = await _sourceResolver.ResolveAsync(diff, ct);
            if (pair == null)
                return diff;

            var afterLines = pair.AfterCode.Replace("\r\n", "\n").Split('\n');

            var hunks = DiffParser.ParseHunks(diff);
            if (hunks.Count == 0)
                return diff;

            var decision = BeforeAfterAnalyzer.Analyze(pair.BeforeCode, pair.AfterCode);

            return DiffParser.RebuildDiffWithContext(diff, afterLines, hunks, decision);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during before/after context enrichment, returning diff unchanged");
            return diff;
        }
    }
}
