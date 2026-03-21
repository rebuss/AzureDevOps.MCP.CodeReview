using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Core.Analysis;

/// <summary>
/// Orchestrates the review context generation pipeline:
/// 1. Fetches data from the active <see cref="IScmClient"/>.
/// 2. Runs all registered <see cref="IReviewAnalyzer"/>s in priority order.
/// 3. Produces a <see cref="ReviewContext"/> with all sections.
/// </summary>
public class ReviewContextOrchestrator
{
    private readonly IScmClient _scmClient;
    private readonly IEnumerable<IReviewAnalyzer> _analyzers;
    private readonly ILogger<ReviewContextOrchestrator> _logger;

    public ReviewContextOrchestrator(
        IScmClient scmClient,
        IEnumerable<IReviewAnalyzer> analyzers,
        ILogger<ReviewContextOrchestrator> logger)
    {
        _scmClient = scmClient;
        _analyzers = analyzers;
        _logger = logger;
    }

    public async Task<ReviewContext> GenerateAsync(int prNumber, CancellationToken ct = default)
    {
        var diff = await _scmClient.GetDiffAsync(prNumber, ct);
        var metadata = await _scmClient.GetMetadataAsync(prNumber, ct);
        var files = await _scmClient.GetFilesAsync(prNumber, ct);

        var input = new AnalysisInput
        {
            Diff = diff,
            Metadata = metadata,
            Files = files,
            ContentProvider = _scmClient,
            LocalRepositoryRoot = null
        };

        var sections = new Dictionary<string, AnalysisSection>();

        foreach (var analyzer in _analyzers.OrderBy(a => a.Order))
        {
            if (!analyzer.CanAnalyze(input))
            {
                _logger.LogDebug("Skipping analyzer '{Name}': not applicable", analyzer.DisplayName);
                continue;
            }

            input = input with { PreviousSections = new Dictionary<string, AnalysisSection>(sections) };

            var section = await analyzer.AnalyzeAsync(input, ct);
            if (section is not null)
            {
                sections[section.Key] = section;
                _logger.LogInformation("Analyzer '{Name}' produced section '{Key}'",
                    analyzer.DisplayName, section.Key);
            }
        }

        return new ReviewContext { PrNumber = prNumber, Sections = sections };
    }
}
