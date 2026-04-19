using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Recovers a usable line number for a review finding when Copilot omitted one
/// (<c>(line unknown)</c>) or gave an imprecise value (<c>~138</c>, <c>100-150</c>,
/// <c>approx 100</c>). Extracts backtick-quoted identifiers from the finding's
/// description and uses Roslyn to locate their declarations inside the file source.
/// When a hint line is supplied, ties are broken by proximity to the hint.
/// Feature 021 — used by <c>FindingScopeResolver</c>.
/// </summary>
public static class FindingLineResolver
{
    // Backtick-quoted identifier anywhere in the description:  `OnShutdown`, `_downloadCts`.
    // Also accepts member access inside the backticks (`Foo.Bar`) — we only match the last segment.
    private static readonly Regex BacktickIdentifier =
        new(@"`(?<id>[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)`",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Attempts to resolve a line number for the finding by locating one of the
    /// identifiers cited (in backticks) in the description. Scoring prefers method /
    /// property declarations over types, fields, and local variables; when
    /// <paramref name="hintLine"/> is provided, candidates nearer the hint score higher.
    /// </summary>
    /// <returns>
    /// A 1-based line number when any cited identifier matches a declaration in the
    /// parsed source; otherwise <c>null</c> (caller falls back to whole-file context).
    /// </returns>
    public static int? TryResolveLine(string sourceCode, string description, int? hintLine)
    {
        if (string.IsNullOrEmpty(sourceCode) || string.IsNullOrWhiteSpace(description))
            return null;

        var identifiers = ExtractBacktickIdentifiers(description);
        if (identifiers.Count == 0)
            return null;

        SyntaxTree tree;
        try
        {
            tree = CSharpSyntaxTree.ParseText(sourceCode);
        }
        catch
        {
            return null;
        }

        var root = tree.GetRoot();
        var text = root.SyntaxTree.GetText();

        var best = (Line: -1, Score: int.MinValue);
        foreach (var node in root.DescendantNodes())
        {
            var (name, typeScore) = ClassifyNode(node);
            if (name is null || !identifiers.Contains(name))
                continue;

            var line = text.Lines.GetLinePosition(node.SpanStart).Line + 1;
            var score = typeScore + ProximityBonus(line, hintLine);
            if (score > best.Score)
                best = (line, score);
        }

        return best.Line > 0 ? best.Line : null;
    }

    private static HashSet<string> ExtractBacktickIdentifiers(string description)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in BacktickIdentifier.Matches(description))
        {
            var id = m.Groups["id"].Value;
            // For qualified names (`Foo.Bar`), also track the trailing member segment —
            // Copilot often writes the member's fully-qualified path but Roslyn
            // declarations only carry the leaf identifier.
            var lastDot = id.LastIndexOf('.');
            if (lastDot >= 0 && lastDot + 1 < id.Length)
                set.Add(id[(lastDot + 1)..]);
            else
                set.Add(id);
        }
        return set;
    }

    /// <summary>
    /// Maps a syntax node to <c>(identifier name, type priority score)</c>. Higher
    /// scores mean "more likely to be what the finding is about". Methods / properties
    /// top the list because Copilot findings usually cite member-level issues.
    /// </summary>
    private static (string? Name, int TypeScore) ClassifyNode(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => (m.Identifier.ValueText, 100),
        LocalFunctionStatementSyntax lf => (lf.Identifier.ValueText, 95),
        PropertyDeclarationSyntax p => (p.Identifier.ValueText, 90),
        IndexerDeclarationSyntax => ("this", 85),
        ConstructorDeclarationSyntax c => (c.Identifier.ValueText, 80),
        DestructorDeclarationSyntax d => (d.Identifier.ValueText, 80),
        EventDeclarationSyntax e => (e.Identifier.ValueText, 75),
        ClassDeclarationSyntax cl => (cl.Identifier.ValueText, 40),
        StructDeclarationSyntax s => (s.Identifier.ValueText, 40),
        RecordDeclarationSyntax r => (r.Identifier.ValueText, 40),
        InterfaceDeclarationSyntax i => (i.Identifier.ValueText, 40),
        EnumDeclarationSyntax en => (en.Identifier.ValueText, 40),
        VariableDeclaratorSyntax v => (v.Identifier.ValueText, 20), // fields + locals
        ParameterSyntax pa => (pa.Identifier.ValueText, 10),
        _ => (null, 0),
    };

    /// <summary>
    /// Adds a bonus up to <c>±25</c> when a declaration sits close to the model's hint
    /// line — useful when Copilot's approximation was near-miss (<c>~138</c> vs the
    /// real 135) so we prefer the nearest overload instead of the first-in-file match.
    /// </summary>
    private static int ProximityBonus(int candidateLine, int? hintLine)
    {
        if (hintLine is not int hint || hint <= 0)
            return 0;
        var distance = Math.Abs(candidateLine - hint);
        if (distance >= 250)
            return 0;
        return 25 - (distance / 10);
    }
}
