using System.Text;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Enriches C# file diffs with a <c>[call-sites]</c> annotation block showing
/// where changed members are used in the repository.
/// </summary>
public class CallSiteEnricher : IDiffEnricher
{
    private const int MaxTargetsDisplayed = 5;
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(3);

    private readonly IRepositoryDownloadOrchestrator _orchestrator;
    private readonly CallSiteScanner _scanner;
    private readonly ILogger<CallSiteEnricher> _logger;

    public CallSiteEnricher(
        IRepositoryDownloadOrchestrator orchestrator,
        CallSiteScanner scanner,
        ILogger<CallSiteEnricher> logger)
    {
        _orchestrator = orchestrator;
        _scanner = scanner;
        _logger = logger;
    }

    public int Order => 300;

    public bool CanEnrich(string diff)
    {
        return DiffLanguageDetector.IsCSharp(diff) && !DiffLanguageDetector.IsSkipped(diff);
    }

    public async Task<string> EnrichAsync(string diff, CancellationToken ct = default)
    {
        try
        {
            // Feature 011: zero-hunk files (renames) must pass through unchanged.
            if (DiffParser.ParseHunks(diff).Count == 0)
                return diff;

            // 1. Get repo path
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(DownloadTimeout);

            string? repoPath;
            try
            {
                repoPath = await _orchestrator.GetExtractedPathAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return diff;
            }

            if (repoPath == null)
                return diff;

            var repoRoot = RepositoryFileResolver.ResolveRoot(repoPath);

            // 2. Extract targets
            var targets = CallSiteTargetExtractor.ExtractTargets(diff);
            if (targets.Count == 0)
                return diff;

            // 3. Get current file path (exclude from results)
            var currentFile = DiffParser.TryParseFilePath(diff);

            // 4. Scan
            var results = await _scanner.ScanAsync(repoRoot, targets, currentFile, ct);

            // 5. Format block
            var block = FormatBlock(results);
            if (block == null)
                return diff;

            // 6. Insert before first @@ (after all annotation blocks)
            return InsertBlock(diff, block);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during call site enrichment, returning diff unchanged");
            return diff;
        }
    }

    private static string? FormatBlock(IReadOnlyList<CallSiteResult> results)
    {
        // Filter: keep targets with call sites, or "new" targets with 0 call sites
        var displayable = results
            .Where(r => r.TotalCount > 0 || r.Reason is "new method" or "new type")
            .OrderByDescending(r => r.TotalCount)
            .ToList();

        if (displayable.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine("[call-sites]");

        var count = Math.Min(displayable.Count, MaxTargetsDisplayed);
        for (int i = 0; i < count; i++)
        {
            var result = displayable[i];
            var targetName = result.TargetName == ".ctor"
                ? "constructor"
                : result.TargetName;

            if (result.TotalCount == 0)
            {
                sb.AppendLine($"  \U0001f4cd {targetName} ({result.Reason}) \u2014 no existing call sites");
                continue;
            }

            var siteWord = result.TotalCount == 1 ? "call site" : "call sites";
            sb.AppendLine($"  \U0001f4cd {targetName} ({result.Reason}) \u2014 {result.TotalCount} {siteWord}:");

            foreach (var location in result.Locations)
            {
                sb.AppendLine($"     {location.FilePath}:{location.Line}");
            }

            if (result.TotalCount > result.Locations.Count)
            {
                sb.AppendLine($"     ... and {result.TotalCount - result.Locations.Count} more");
            }
        }

        if (displayable.Count > MaxTargetsDisplayed)
        {
            sb.AppendLine($"  ... and {displayable.Count - MaxTargetsDisplayed} more changed members");
        }

        sb.AppendLine("[/call-sites]");
        return sb.ToString();
    }

    private static string InsertBlock(string diff, string block)
    {
        // Insert before first @@ (after all other annotation blocks)
        var insertBefore = diff.IndexOf("@@ ", StringComparison.Ordinal);
        if (insertBefore < 0)
            insertBefore = diff.Length;

        return diff.Insert(insertBefore, block);
    }
}
