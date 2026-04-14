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
                if (f.LineNumber is null)
                {
                    // No line number cited → cannot locate enclosing scope.
                    results[idx] = UnresolvedResult(f, ScopeResolutionFailure.ScopeNotFound);
                    continue;
                }

                var (body, name, resolved) = FindingScopeExtractor.ExtractScopeBody(
                    pair.AfterCode, f.LineNumber.Value, maxScopeLines);

                results[idx] = resolved
                    ? new FindingWithScope
                    {
                        Finding = f,
                        ScopeSource = body,
                        ScopeName = name,
                        ResolutionFailure = ScopeResolutionFailure.None,
                    }
                    : UnresolvedResult(f, ScopeResolutionFailure.ScopeNotFound);
            }
        }

        return results;
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
