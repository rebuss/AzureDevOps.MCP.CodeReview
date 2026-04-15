using Microsoft.Extensions.Logging;
using REBUSS.Pure.RoslynProcessor;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// For each <see cref="ParsedFinding"/>, resolves the full source of its enclosing
/// method/scope using <see cref="DiffSourceResolver"/> (to obtain the after-code for
/// the file) and <see cref="FindingScopeExtractor"/> (to extract the enclosing member
/// body via Roslyn). When scope extraction cannot produce a usable source block, the
/// resolution failure reason is recorded so the validator can map it to the correct
/// verdict (spec US3.1 / US3.2). Feature 021.
/// </summary>
public sealed class FindingScopeResolver
{
    private readonly DiffSourceResolver _sourceResolver;
    private readonly ILogger<FindingScopeResolver> _logger;

    public FindingScopeResolver(
        DiffSourceResolver sourceResolver,
        ILogger<FindingScopeResolver> logger)
    {
        _sourceResolver = sourceResolver;
        _logger = logger;
    }

    /// <summary>
    /// Resolves scope for every finding. Groups lookups by file path to benefit from
    /// <see cref="DiffSourceResolver"/>'s internal per-file cache — one resolve call
    /// per distinct file regardless of finding count.
    /// </summary>
    public async Task<IReadOnlyList<FindingWithScope>> ResolveAsync(
        IReadOnlyList<ParsedFinding> findings,
        int maxScopeLines,
        CancellationToken ct)
    {
        if (findings.Count == 0)
            return Array.Empty<FindingWithScope>();

        var results = new FindingWithScope[findings.Count];
        var byFile = findings
            .Select((f, idx) => (finding: f, idx))
            .GroupBy(pair => pair.finding.FilePath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byFile)
        {
            var filePath = group.Key;

            // (a) Non-C# files — Roslyn does not apply. Passthrough per spec US3.1.
            if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var (f, idx) in group)
                    results[idx] = UnresolvedResult(f, ScopeResolutionFailure.NotCSharp);
                continue;
            }

            // (b) Resolve source once per file. The DiffSourceResolver caches internally,
            // but we still synthesize a diff-like header so the resolver can parse the path.
            var diffHeader = $"=== {filePath} (edit: +0 -0) ===\n@@ -1,1 +1,1 @@\n";
            DiffSourcePair? pair;
            try
            {
                pair = await _sourceResolver.ResolveAsync(diffHeader, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scope resolution failed for {FilePath}; tagging as uncertain", filePath);
                foreach (var (f, idx) in group)
                    results[idx] = UnresolvedResult(f, ScopeResolutionFailure.SourceUnavailable);
                continue;
            }

            if (pair is null)
            {
                foreach (var (f, idx) in group)
                    results[idx] = UnresolvedResult(f, ScopeResolutionFailure.SourceUnavailable);
                continue;
            }

            // (c) Per-finding scope extraction against the single resolved source.
            foreach (var (f, idx) in group)
            {
                // Smart line resolution: when Copilot omitted a line (`(line unknown)`)
                // or wrote an approximation (`~138`) we may have LineNumber == null or
                // a line that doesn't cleanly map to a scope. FindingLineResolver walks
                // the syntax tree looking for the identifiers cited in backticks in the
                // finding description, biased by proximity to the hint line if one was
                // parsed. See Feature 021 clarification: Copilot imprecision is validated
                // and corrected using Roslyn before the scope is extracted.
                var effectiveLine = f.LineNumber
                    ?? FindingLineResolver.TryResolveLine(pair.AfterCode, f.Description, hintLine: null);

                if (effectiveLine is int line)
                {
                    var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody(
                        pair.AfterCode, line, maxScopeLines);

                    if (resolved)
                    {
                        results[idx] = new FindingWithScope
                        {
                            Finding = f,
                            ScopeSource = body,
                            ScopeName = name,
                            ResolutionFailure = ScopeResolutionFailure.None,
                        };
                        continue;
                    }

                    // Hint line resolved but Roslyn could not find an enclosing member
                    // (e.g. line falls on a using, top-level statement, or malformed
                    // syntax) — try once more, without the hint, using identifier search
                    // alone. Sometimes Copilot's line is off by a lot and the identifier
                    // lookup gives a cleaner anchor.
                    var fallbackLine = FindingLineResolver.TryResolveLine(
                        pair.AfterCode, f.Description, hintLine: line);
                    if (fallbackLine is int fl && fl != line)
                    {
                        var (body2, name2, resolved2) = FindingScopeExtractor.ExtractScopeBody(
                            pair.AfterCode, fl, maxScopeLines);
                        if (resolved2)
                        {
                            results[idx] = new FindingWithScope
                            {
                                Finding = f,
                                ScopeSource = body2,
                                ScopeName = name2,
                                ResolutionFailure = ScopeResolutionFailure.None,
                            };
                            continue;
                        }
                    }
                }

                // Whole-file fallback: scope extraction failed OR no line could be
                // recovered at all. Pass the truncated file source to the validator so
                // Copilot still gets a chance to reason about the issue. Budget is
                // MaxScopeLines × 2 — modestly larger than method-level context because
                // the validator may need more surrounding code to locate the issue.
                var (fileBody, fileName) = BuildWholeFileFallback(pair.AfterCode, filePath, maxScopeLines);
                results[idx] = new FindingWithScope
                {
                    Finding = f,
                    ScopeSource = fileBody,
                    ScopeName = fileName,
                    ResolutionFailure = ScopeResolutionFailure.None,
                };
            }
        }

        return results;
    }

    /// <summary>
    /// Produces a whole-file context block used when per-finding scope extraction could
    /// not locate an enclosing member. The body is truncated to
    /// <c>maxScopeLines × 2</c> to keep the validation prompt bounded, with clearly
    /// labelled truncation markers so Copilot knows content was elided.
    /// </summary>
    private static (string Body, string Name) BuildWholeFileFallback(
        string source, string filePath, int maxScopeLines)
    {
        var limit = Math.Max(1, maxScopeLines) * 2;
        var lines = source.Replace("\r\n", "\n").Split('\n');
        if (lines.Length <= limit)
            return (source, $"<entire file: {filePath}>");

        var half = limit / 2;
        var window = new List<string>(limit + 2);
        window.AddRange(lines.Take(half));
        window.Add($"// ... ({lines.Length - limit} lines omitted from middle) ...");
        window.AddRange(lines.Skip(lines.Length - (limit - half)));
        return (string.Join("\n", window), $"<entire file: {filePath} (truncated)>");
    }

    private static FindingWithScope UnresolvedResult(ParsedFinding finding, ScopeResolutionFailure failure) =>
        new()
        {
            Finding = finding,
            ScopeSource = "",
            ScopeName = "",
            ResolutionFailure = failure,
        };
}
