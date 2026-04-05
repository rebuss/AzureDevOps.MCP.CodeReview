using System.Text.RegularExpressions;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Extracts call site search targets from a diff string.
/// Prefers the <c>[structural-changes]</c> block when present,
/// falls back to heuristic extraction from <c>+</c> lines.
/// </summary>
public static partial class CallSiteTargetExtractor
{
    [GeneratedRegex(@"\[structural-changes\](.*?)\[/structural-changes\]", RegexOptions.Singleline)]
    private static partial Regex StructuralBlockRegex();

    // Patterns for parsing structural-changes entries
    [GeneratedRegex(@"Method signature changed: (\w+)\(")]
    private static partial Regex SignatureChangedRegex();

    [GeneratedRegex(@"New method: (\w+)\(")]
    private static partial Regex NewMethodRegex();

    [GeneratedRegex(@"Constructor changed:")]
    private static partial Regex ConstructorChangedRegex();

    [GeneratedRegex(@"New class: (\w+)")]
    private static partial Regex NewClassRegex();

    // Heuristic fallback: method declarations in + lines
    [GeneratedRegex(@"^\+\s*(?:public|internal|protected|private)\s+\S+\s+(\w+)\s*\(", RegexOptions.Multiline)]
    private static partial Regex MethodDeclRegex();

    public static IReadOnlyList<CallSiteTarget> ExtractTargets(string diff)
    {
        if (string.IsNullOrEmpty(diff))
            return [];

        // Strategy 1: Parse [structural-changes] block
        var blockMatch = StructuralBlockRegex().Match(diff);
        if (blockMatch.Success)
            return ExtractFromStructuralBlock(blockMatch.Groups[1].Value);

        // Strategy 2: Heuristic fallback from + lines
        return ExtractFromHeuristic(diff);
    }

    private static IReadOnlyList<CallSiteTarget> ExtractFromStructuralBlock(string blockContent)
    {
        var targets = new List<CallSiteTarget>();
        var lines = blockContent.Split('\n');

        foreach (var line in lines)
        {
            // Skip removed members (dead code — no point searching for call sites)
            // Check for ⚠ emoji prefix which marks removals, not generic "removed" text
            // (method names may contain "removed" — e.g., "ProcessRemoved")
            if (line.TrimStart().StartsWith("\u26a0"))
                continue;

            var sigMatch = SignatureChangedRegex().Match(line);
            if (sigMatch.Success)
            {
                targets.Add(new CallSiteTarget
                {
                    Name = sigMatch.Groups[1].Value,
                    Reason = "signature changed",
                    Kind = CallSiteTargetKind.Method
                });
                continue;
            }

            var newMethodMatch = NewMethodRegex().Match(line);
            if (newMethodMatch.Success)
            {
                targets.Add(new CallSiteTarget
                {
                    Name = newMethodMatch.Groups[1].Value,
                    Reason = "new method",
                    Kind = CallSiteTargetKind.Method
                });
                continue;
            }

            if (ConstructorChangedRegex().IsMatch(line))
            {
                targets.Add(new CallSiteTarget
                {
                    Name = ".ctor",
                    Reason = "constructor changed",
                    Kind = CallSiteTargetKind.Constructor
                });
                continue;
            }

            var newClassMatch = NewClassRegex().Match(line);
            if (newClassMatch.Success)
            {
                targets.Add(new CallSiteTarget
                {
                    Name = newClassMatch.Groups[1].Value,
                    Reason = "new type",
                    Kind = CallSiteTargetKind.Type
                });
            }
        }

        return targets;
    }

    private static IReadOnlyList<CallSiteTarget> ExtractFromHeuristic(string diff)
    {
        var targets = new List<CallSiteTarget>();
        var seen = new HashSet<string>();

        foreach (Match match in MethodDeclRegex().Matches(diff))
        {
            var name = match.Groups[1].Value;
            if (seen.Add(name))
            {
                targets.Add(new CallSiteTarget
                {
                    Name = name,
                    Reason = "changed method",
                    Kind = CallSiteTargetKind.Method
                });
            }
        }

        return targets;
    }
}
