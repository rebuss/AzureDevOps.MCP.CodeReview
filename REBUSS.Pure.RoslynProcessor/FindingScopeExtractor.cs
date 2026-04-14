using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Extracts the full source body of the enclosing method, property, constructor, or
/// type that contains a given line number. Reuses <see cref="ScopeResolver.FindEnclosingMember"/>
/// and <see cref="ScopeResolver.Resolve"/> to keep the walk logic and formatted name in sync.
/// Feature 021: used by <c>FindingScopeResolver</c> to produce validation input.
/// </summary>
public static class FindingScopeExtractor
{
    /// <summary>
    /// Attempts to extract the enclosing scope body for <paramref name="lineNumber"/> (1-based).
    /// Truncates the body to at most <paramref name="maxLines"/> lines centered on the finding line.
    /// </summary>
    /// <returns>
    /// <c>(ScopeBody, ScopeName, Resolved=true)</c> when a scope is found.
    /// <c>("", "", Resolved=false)</c> when the syntax tree is empty, the line is out of range,
    /// or no enclosing member can be located (e.g., top-level statement, class-level field
    /// with no containing method).
    /// </returns>
    public static (string ScopeBody, string ScopeName, bool Resolved) ExtractScopeBody(
        string sourceCode,
        int lineNumber,
        int maxLines)
    {
        if (string.IsNullOrEmpty(sourceCode) || lineNumber < 1 || maxLines <= 0)
            return ("", "", false);

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetRoot();
        var text = root.SyntaxTree.GetText();

        if (lineNumber > text.Lines.Count)
            return ("", "", false);

        var lineSpan = text.Lines[lineNumber - 1].Span;
        var node = root.FindNode(lineSpan, getInnermostNodeForTie: true);
        if (node == null)
            return ("", "", false);

        var enclosing = ScopeResolver.FindEnclosingMember(node);
        if (enclosing == null)
            return ("", "", false);

        var scopeName = ScopeResolver.Resolve(root, lineNumber) ?? "";
        var scopeBody = enclosing.ToFullString();
        var truncated = TruncateAroundLine(scopeBody, enclosing, lineNumber, text, maxLines);

        return (truncated, scopeName, true);
    }

    /// <summary>
    /// If the scope body exceeds <paramref name="maxLines"/>, returns a window of
    /// <paramref name="maxLines"/> lines centered on the finding's line (converted to a
    /// line offset within the scope). Otherwise returns the body unchanged.
    /// </summary>
    private static string TruncateAroundLine(
        string scopeBody,
        SyntaxNode enclosing,
        int findingLine,
        Microsoft.CodeAnalysis.Text.SourceText sourceText,
        int maxLines)
    {
        var bodyLines = scopeBody.Replace("\r\n", "\n").Split('\n');
        if (bodyLines.Length <= maxLines)
            return scopeBody;

        // Map the absolute finding line (1-based in full source) to a 0-based offset
        // within the extracted scope body.
        var scopeStartLine = sourceText.Lines.GetLinePosition(enclosing.SpanStart).Line; // 0-based
        var findingLineZeroBased = findingLine - 1;
        var offsetInScope = Math.Max(0, Math.Min(bodyLines.Length - 1, findingLineZeroBased - scopeStartLine));

        var half = maxLines / 2;
        var start = Math.Max(0, offsetInScope - half);
        var end = Math.Min(bodyLines.Length, start + maxLines);
        // Pull start back if end hit the upper bound.
        start = Math.Max(0, end - maxLines);

        var window = bodyLines[start..end];
        var prefix = start > 0 ? $"// ... ({start} lines omitted) ...\n" : "";
        var suffix = end < bodyLines.Length ? $"\n// ... ({bodyLines.Length - end} lines omitted) ..." : "";
        return prefix + string.Join("\n", window) + suffix;
    }
}
