using System.Text;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs with a <c>[dependency-changes]</c> annotation block
/// showing added and removed <c>using</c> directives.
/// </summary>
public class UsingsChangeEnricher : IDiffEnricher
{
    private const int MaxAnnotations = 10;

    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<UsingsChangeEnricher> _logger;

    public UsingsChangeEnricher(
        DiffSourceResolver sourceResolver,
        ILogger<UsingsChangeEnricher> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    public int Order => 250;

    public bool CanEnrich(string diff)
    {
        return DiffLanguageDetector.IsCSharp(diff) && !DiffLanguageDetector.IsSkipped(diff);
    }

    public async Task<string> EnrichAsync(string diff, CancellationToken ct = default)
    {
        try
        {
            // Feature 011: zero-hunk files (renames) must pass through unchanged.
            if (DiffParser.ParseHunks(diff).Count == 0)
                return diff;

            var pair = await _sourceResolver.ResolveAsync(diff, ct);
            if (pair == null)
                return diff;

            var beforeUsings = UsingsChangeDetector.ExtractUsings(pair.BeforeCode);
            var afterUsings = UsingsChangeDetector.ExtractUsings(pair.AfterCode);
            var changes = UsingsChangeDetector.DetectChanges(beforeUsings, afterUsings);

            if (changes.Count == 0)
                return diff;

            var block = FormatBlock(changes);
            return InsertBlock(diff, block);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during dependency change enrichment, returning diff unchanged");
            return diff;
        }
    }

    private static string FormatBlock(IReadOnlyList<UsingChange> changes)
    {
        var sb = new StringBuilder();
        sb.AppendLine("[dependency-changes]");

        var count = Math.Min(changes.Count, MaxAnnotations);
        for (int i = 0; i < count; i++)
        {
            var change = changes[i];
            var emoji = change.Kind == UsingChangeKind.Added ? "\u2795" : "\u26a0";
            var prefix = change.Kind == UsingChangeKind.Removed ? "removed " : "";
            sb.AppendLine($"  {emoji} {prefix}{change.Namespace}");
        }

        if (changes.Count > MaxAnnotations)
            sb.AppendLine($"  ... and {changes.Count - MaxAnnotations} more dependency changes");

        sb.AppendLine("[/dependency-changes]");
        return sb.ToString();
    }

    private static string InsertBlock(string diff, string block)
    {
        // Insert before [structural-changes] if present, else before first @@ hunk
        var insertBefore = diff.IndexOf("[structural-changes]", StringComparison.Ordinal);
        if (insertBefore < 0)
            insertBefore = diff.IndexOf("@@ ", StringComparison.Ordinal);
        if (insertBefore < 0)
            insertBefore = diff.Length;

        return diff.Insert(insertBefore, block);
    }
}
