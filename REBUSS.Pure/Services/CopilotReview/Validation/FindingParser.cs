using System.Text;
using System.Text.RegularExpressions;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Parses Copilot review output into structured <see cref="ParsedFinding"/> records.
/// Relies on the structured output format enforced by <c>copilot-page-review.md</c>:
/// <c>**[severity]** `file/path.cs` (line N): description</c>.
/// Feature 021.
/// </summary>
public static partial class FindingParser
{
    // One line per finding. Captures: severity, filePath, optional lineNumber, description.
    // Example: **[critical]** `src/Foo.cs` (line 42): Null deref when X is empty
    [GeneratedRegex(
        @"^\*\*\[(?<sev>critical|major|minor)\]\*\*\s+`(?<file>[^`]+)`(?:\s*\(line\s+(?<line>\d+)\))?\s*:\s*(?<desc>.+?)$",
        RegexOptions.Multiline | RegexOptions.IgnoreCase)]
    private static partial Regex FindingPattern();

    /// <summary>
    /// Parses <paramref name="reviewText"/> into structured findings plus any
    /// unparseable remainder. The remainder is preserved verbatim so
    /// <see cref="FindingFilterer"/> can reassemble the output without data loss
    /// (FR-012 — never silently drop content).
    /// </summary>
    public static (IReadOnlyList<ParsedFinding> Findings, string UnparseableRemainder) Parse(string reviewText)
    {
        if (string.IsNullOrWhiteSpace(reviewText))
            return (Array.Empty<ParsedFinding>(), reviewText ?? "");

        var findings = new List<ParsedFinding>();
        var remainder = new StringBuilder();

        var matches = FindingPattern().Matches(reviewText);
        if (matches.Count == 0)
            return (Array.Empty<ParsedFinding>(), reviewText);

        // Everything before the first match is remainder prose (e.g., a heading
        // like "## Critical Issues" or intro text).
        var cursor = 0;
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];

            if (m.Index > cursor)
                remainder.Append(reviewText, cursor, m.Index - cursor);

            var sev = m.Groups["sev"].Value.ToLowerInvariant();
            var file = m.Groups["file"].Value.Trim();
            var lineStr = m.Groups["line"].Success ? m.Groups["line"].Value : null;
            var desc = m.Groups["desc"].Value.Trim();

            findings.Add(new ParsedFinding
            {
                Index = i,
                FilePath = file,
                LineNumber = lineStr is null ? null : int.Parse(lineStr),
                Severity = sev,
                Description = desc,
                OriginalText = m.Value,
            });

            cursor = m.Index + m.Length;
        }

        // Trailing prose after the last finding (if any) is also remainder.
        if (cursor < reviewText.Length)
            remainder.Append(reviewText, cursor, reviewText.Length - cursor);

        return (findings, remainder.ToString());
    }
}
