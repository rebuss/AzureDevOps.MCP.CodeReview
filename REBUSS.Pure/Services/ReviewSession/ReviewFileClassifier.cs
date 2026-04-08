using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Services.PrEnrichment;

namespace REBUSS.Pure.Services.ReviewSession;

/// <summary>
/// Singleton glob-pattern classifier. At construction time it reads
/// <c>WorkflowOptions.ReviewSession.ScanOnlyPatterns</c>, builds one
/// <see cref="Matcher"/> per pattern (so the matched pattern can be reported
/// to the manifest/summary), logs and drops invalid patterns, and emits a single
/// summary log line. At call time it lowercases the input path and walks the
/// patterns in configuration order, returning the first match.
///
/// Constitution Principle VI: stateless after construction. The pattern list
/// is immutable; the classifier holds no per-call state.
/// </summary>
internal sealed class ReviewFileClassifier : IReviewFileClassifier
{
    private readonly IReadOnlyList<(string Pattern, Matcher Matcher)> _matchers;

    public ReviewFileClassifier(IOptions<WorkflowOptions> options, ILogger<ReviewFileClassifier> logger)
    {
        var patterns = options.Value.ReviewSession?.ScanOnlyPatterns ?? Array.Empty<string>();
        var built = new List<(string, Matcher)>(patterns.Length);
        foreach (var pattern in patterns)
        {
            try
            {
                var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
                matcher.AddInclude(pattern);
                built.Add((pattern, matcher));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Invalid scan-only glob pattern '{Pattern}' — dropped", pattern);
            }
        }
        _matchers = built;
        logger.LogInformation("ReviewFileClassifier loaded with {Count} active patterns", _matchers.Count);
    }

    public ReviewFileClassificationResult Classify(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            return new ReviewFileClassificationResult(ReviewFileClassification.Deep, null);

        // Case-insensitive on all hosts (FR-002): lowercase before matching.
        var lower = filePath.ToLowerInvariant();
        foreach (var (pattern, matcher) in _matchers)
        {
            var result = matcher.Match(lower);
            if (result.HasMatches)
                return new ReviewFileClassificationResult(ReviewFileClassification.Scan, pattern);
        }
        return new ReviewFileClassificationResult(ReviewFileClassification.Deep, null);
    }
}
