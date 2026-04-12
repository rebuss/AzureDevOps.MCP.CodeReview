using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs with a <c>[file-structure]</c> annotation indicating whether
/// the after-version of the file has valid syntax and balanced braces. This metadata
/// helps AI reviewers avoid false positives about structural issues (e.g., incorrectly
/// reporting missing closing braces in unified diffs where the brace is outside the hunk).
/// </summary>
public partial class FileStructureValidationEnricher : IDiffEnricher
{
    private static readonly Regex HeaderLineRegex = HeaderLinePattern();

    [GeneratedRegex(@"^=== .+ ===\s*$", RegexOptions.Multiline)]
    private static partial Regex HeaderLinePattern();

    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<FileStructureValidationEnricher> _logger;

    public FileStructureValidationEnricher(
        DiffSourceResolver sourceResolver,
        ILogger<FileStructureValidationEnricher> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    /// <summary>
    /// Runs last — after all content enrichers have processed the diff.
    /// The annotation is purely metadata about the underlying file structure.
    /// </summary>
    public int Order => 400;

    public bool CanEnrich(string diff)
    {
        return DiffLanguageDetector.IsCSharp(diff) && !DiffLanguageDetector.IsSkipped(diff);
    }

    public async Task<string> EnrichAsync(string diff, CancellationToken ct = default)
    {
        try
        {
            if (DiffParser.ParseHunks(diff).Count == 0)
                return diff;

            var pair = await _sourceResolver.ResolveAsync(diff, ct);
            if (pair == null)
                return diff;

            var (syntaxValid, balancedBraces) = Validate(pair.AfterCode);
            var annotation = FormatAnnotation(syntaxValid, balancedBraces);

            return InsertAfterHeader(diff, annotation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during file structure validation, returning diff unchanged");
            return diff;
        }
    }

    /// <summary>
    /// Validates the source code for syntax errors and balanced braces using Roslyn
    /// syntax-only parsing (no compilation required). Brace balance is checked via raw
    /// character counting rather than Roslyn tokens, because Roslyn's error recovery
    /// inserts synthetic missing tokens that mask structural imbalances.
    /// </summary>
    public static (bool SyntaxValid, bool BalancedBraces) Validate(string sourceCode)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var syntaxValid = !tree.GetDiagnostics().Any(d => d.Severity == DiagnosticSeverity.Error);

        int openBraces = 0, closeBraces = 0;
        foreach (var ch in sourceCode)
        {
            if (ch == '{') openBraces++;
            else if (ch == '}') closeBraces++;
        }

        return (syntaxValid, openBraces == closeBraces);
    }

    public static string FormatAnnotation(bool syntaxValid, bool balancedBraces)
    {
        var compiles = syntaxValid ? "yes" : "no";
        var braces = balancedBraces ? "yes" : "no";
        return $"[file-structure: compiles={compiles}, balanced-braces={braces}]\n";
    }

    private static string InsertAfterHeader(string diff, string annotation)
    {
        var headerMatch = HeaderLineRegex.Match(diff);
        if (!headerMatch.Success)
            return diff;

        var insertPos = headerMatch.Index + headerMatch.Length;
        if (insertPos < diff.Length && diff[insertPos] == '\n')
            insertPos++;

        return diff.Insert(insertPos, annotation);
    }
}
