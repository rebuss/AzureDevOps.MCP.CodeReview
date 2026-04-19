using System.Globalization;
using System.Text.RegularExpressions;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.GitHub.Parsers;

/// <summary>
/// Parses a unified-diff patch string (the <c>patch</c> field returned by GitHub's
/// <c>/pulls/{n}/files</c> endpoint) into <see cref="DiffHunk"/> instances. The patch
/// already encodes the structured diff that <see cref="REBUSS.Pure.Core.Shared.IStructuredDiffBuilder"/>
/// would otherwise compute by re-diffing the full file contents — using GitHub's pre-built
/// patch eliminates two API calls per changed file.
/// </summary>
public static class GitHubPatchHunkParser
{
    private static readonly Regex HunkHeaderRegex =
        new(@"^@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@", RegexOptions.Compiled);

    /// <summary>
    /// Parses the given patch into a list of hunks. Returns an empty list when the patch is
    /// null/empty or contains no hunk headers.
    /// </summary>
    public static List<DiffHunk> Parse(string? patch)
    {
        var hunks = new List<DiffHunk>();
        if (string.IsNullOrEmpty(patch))
            return hunks;

        var lines = patch.Replace("\r\n", "\n").Split('\n');
        DiffHunk? current = null;

        foreach (var line in lines)
        {
            var headerMatch = HunkHeaderRegex.Match(line);
            if (headerMatch.Success)
            {
                if (current is not null)
                    hunks.Add(current);

                current = new DiffHunk
                {
                    OldStart = ParseInt(headerMatch.Groups[1].Value),
                    OldCount = headerMatch.Groups[2].Success ? ParseInt(headerMatch.Groups[2].Value) : 1,
                    NewStart = ParseInt(headerMatch.Groups[3].Value),
                    NewCount = headerMatch.Groups[4].Success ? ParseInt(headerMatch.Groups[4].Value) : 1,
                    Lines = new List<DiffLine>()
                };
                continue;
            }

            if (current is null)
                continue; // pre-header noise (file headers, "\ No newline at end of file", etc.)

            if (line.Length == 0)
            {
                // A real blank context line is encoded as `" "` (length 1) in unified diffs.
                // An empty element here comes from Split('\n') on a trailing or doubled newline;
                // emitting a synthetic context line would shift alignment and corrupt the hunk.
                continue;
            }

            var op = line[0];
            switch (op)
            {
                case '+':
                case '-':
                case ' ':
                    current.Lines.Add(new DiffLine { Op = op, Text = line[1..] });
                    break;
                case '\\':
                    // "\ No newline at end of file" — ignore.
                    break;
                default:
                    // Unknown prefix — treat as context to avoid losing data.
                    current.Lines.Add(new DiffLine { Op = ' ', Text = line });
                    break;
            }
        }

        if (current is not null)
            hunks.Add(current);

        return hunks;
    }

    private static int ParseInt(string s) =>
        int.Parse(s, NumberStyles.Integer, CultureInfo.InvariantCulture);
}
