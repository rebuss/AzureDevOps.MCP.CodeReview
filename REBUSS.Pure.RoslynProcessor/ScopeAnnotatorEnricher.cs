using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs by annotating each hunk header with the enclosing scope
/// (e.g., <c>[scope: OrderService.ProcessOrder(Order, CancellationToken)]</c>).
/// </summary>
public class ScopeAnnotatorEnricher : IDiffEnricher
{
    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<ScopeAnnotatorEnricher> _logger;

    public ScopeAnnotatorEnricher(
        DiffSourceResolver sourceResolver,
        ILogger<ScopeAnnotatorEnricher> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    public int Order => 150;

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

            var tree = CSharpSyntaxTree.ParseText(pair.AfterCode);
            var root = tree.GetRoot(ct);

            var hunks = DiffParser.ParseHunks(diff);
            if (hunks.Count == 0)
                return diff;

            // Iterate in reverse to preserve offsets during insertion
            for (int i = hunks.Count - 1; i >= 0; i--)
            {
                var hunk = hunks[i];
                var scope = ScopeResolver.Resolve(root, hunk.NewStart);
                if (scope == null)
                    continue;

                var insertPos = hunk.StartOffset + hunk.HeaderLine.Length;
                diff = diff.Insert(insertPos, $" [scope: {scope}]");
            }

            return diff;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during scope annotation enrichment, returning diff unchanged");
            return diff;
        }
    }
}
