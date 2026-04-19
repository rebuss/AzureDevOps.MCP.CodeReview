using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs with structural change annotations.
/// Detects method signature changes, added/removed members, type changes, and base type changes
/// by comparing syntax trees (no compilation required).
/// </summary>
public partial class StructuralChangeEnricher : IDiffEnricher
{
    private const int MaxAnnotations = 15;

    private static readonly Regex HeaderLineRegex = HeaderLinePattern();

    [GeneratedRegex(@"^=== .+ ===\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLinePattern();

    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<StructuralChangeEnricher> _logger;

    public StructuralChangeEnricher(
        DiffSourceResolver sourceResolver,
        ILogger<StructuralChangeEnricher> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    public int Order => 200;

    public bool CanEnrich(string diff)
    {
        return DiffLanguageDetector.IsCSharp(diff) && !DiffLanguageDetector.IsSkipped(diff);
    }

    public async Task<string> EnrichAsync(string diff, CancellationToken ct = default)
    {
        try
        {
            // Feature 011: zero-hunk files (e.g. renames) must not get spurious annotation
            // blocks. The spec's "zero-hunk file" edge case requires pass-through behavior.
            if (DiffParser.ParseHunks(diff).Count == 0)
                return diff;

            var pair = await _sourceResolver.ResolveAsync(diff, ct);
            if (pair == null)
                return diff;

            var beforeTree = CSharpSyntaxTree.ParseText(pair.BeforeCode);
            var afterTree = CSharpSyntaxTree.ParseText(pair.AfterCode);

            var changes = StructuralChangeDetector.DetectChanges(beforeTree, afterTree);
            if (changes.Count == 0)
                return diff;

            return InsertAnnotationBlock(diff, changes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during structural change enrichment, returning diff unchanged");
            return diff;
        }
    }

    public static string InsertAnnotationBlock(string diff, IReadOnlyList<StructuralChange> changes)
    {
        // Find insertion point: after "=== ... ===" line, before first "@@ ..." line
        var headerMatch = HeaderLineRegex.Match(diff);
        if (!headerMatch.Success)
            return diff;

        var insertPos = headerMatch.Index + headerMatch.Length;
        // Ensure we're after the newline
        if (insertPos < diff.Length && diff[insertPos] == '\n')
            insertPos++;

        var block = new StringBuilder();
        block.AppendLine("[structural-changes]");

        var count = Math.Min(changes.Count, MaxAnnotations);
        for (int i = 0; i < count; i++)
        {
            var change = changes[i];
            var emoji = GetEmoji(change.Kind);
            block.AppendLine($"  {emoji} {change.Description}");
        }

        if (changes.Count > MaxAnnotations)
        {
            block.AppendLine($"  ... and {changes.Count - MaxAnnotations} more structural changes");
        }

        block.AppendLine("[/structural-changes]");

        return diff.Insert(insertPos, block.ToString());
    }

    private static string GetEmoji(StructuralChangeKind kind)
    {
        return kind switch
        {
            StructuralChangeKind.SignatureChanged => "\U0001f504",  // 🔄
            StructuralChangeKind.BaseTypeChanged => "\U0001f504",   // 🔄
            StructuralChangeKind.MemberAdded => "\u2795",           // ➕
            StructuralChangeKind.TypeAdded => "\u2795",             // ➕
            StructuralChangeKind.MemberRemoved => "\u26a0",         // ⚠
            StructuralChangeKind.TypeRemoved => "\u26a0",           // ⚠
            _ => "\u2022"                                           // •
        };
    }
}
