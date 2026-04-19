using Microsoft.CodeAnalysis.CSharp;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Extracts and compares <c>using</c> directives from before/after C# source.
/// Pure logic, synchronous, no I/O.
/// </summary>
public static class UsingsChangeDetector
{
    /// <summary>
    /// Extracts all using directives from C# source code as normalized strings.
    /// Handles standard, static, alias, and global usings.
    /// </summary>
    public static IReadOnlyList<string> ExtractUsings(string sourceCode)
    {
        if (string.IsNullOrWhiteSpace(sourceCode))
            return [];

        var tree = CSharpSyntaxTree.ParseText(sourceCode);
        var root = tree.GetCompilationUnitRoot();
        var result = new List<string>();

        foreach (var u in root.Usings)
        {
            var parts = new List<string>();

            if (u.GlobalKeyword.ValueText == "global")
                parts.Add("global");
            if (u.StaticKeyword.ValueText == "static")
                parts.Add("static");
            if (u.Alias != null)
                parts.Add($"{u.Alias.Name} =");

            parts.Add(u.Name?.ToString() ?? u.ToString().Trim().TrimEnd(';').Replace("using ", ""));

            result.Add(string.Join(" ", parts));
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>
    /// Detects added and removed using directives.
    /// Removed directives are listed before added ones.
    /// </summary>
    public static IReadOnlyList<UsingChange> DetectChanges(
        IReadOnlyList<string> beforeUsings,
        IReadOnlyList<string> afterUsings)
    {
        var beforeSet = new HashSet<string>(beforeUsings);
        var afterSet = new HashSet<string>(afterUsings);

        var removed = beforeUsings.Where(u => !afterSet.Contains(u)).OrderBy(u => u).ToList();
        var added = afterUsings.Where(u => !beforeSet.Contains(u)).OrderBy(u => u).ToList();

        var changes = new List<UsingChange>();

        foreach (var ns in removed)
            changes.Add(new UsingChange { Kind = UsingChangeKind.Removed, Namespace = ns });
        foreach (var ns in added)
            changes.Add(new UsingChange { Kind = UsingChangeKind.Added, Namespace = ns });

        return changes;
    }
}
