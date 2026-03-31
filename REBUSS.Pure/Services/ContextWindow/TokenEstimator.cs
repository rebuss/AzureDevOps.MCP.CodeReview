using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ContextWindow;

/// <summary>
/// Estimates the token count of serialized response content using a
/// character-count heuristic with a configurable chars-per-token ratio.
/// Schema-independent — operates on the serialized JSON wire format.
/// </summary>
public sealed class TokenEstimator : ITokenEstimator
{
    private const double DefaultCharsPerToken = 4.0;
    private const int AvgTokensPerLine = 15;
    private const int PerFileOverhead = 50;

    private readonly IOptions<ContextWindowOptions> _options;
    private readonly ILogger<TokenEstimator> _logger;

    public TokenEstimator(IOptions<ContextWindowOptions> options, ILogger<TokenEstimator> logger)
    {
        _options = options;
        _logger = logger;
    }

    public TokenEstimationResult Estimate(string serializedContent, int safeBudgetTokens)
    {
        if (string.IsNullOrEmpty(serializedContent))
            return new TokenEstimationResult(EstimatedTokens: 0, PercentageUsed: 0.0, FitsWithinBudget: true);

        var charsPerToken = _options.Value.CharsPerToken;
        if (charsPerToken <= 0)
        {
            _logger.LogWarning(
                "Invalid CharsPerToken value ({CharsPerToken}); falling back to default {Default}",
                charsPerToken, DefaultCharsPerToken);
            charsPerToken = DefaultCharsPerToken;
        }

        var estimatedTokens = (int)Math.Ceiling(serializedContent.Length / charsPerToken);

        var percentageUsed = safeBudgetTokens > 0
            ? (double)estimatedTokens / safeBudgetTokens * 100.0
            : 100.0;

        var fitsWithinBudget = estimatedTokens <= safeBudgetTokens;

        return new TokenEstimationResult(estimatedTokens, percentageUsed, fitsWithinBudget);
    }

    public int EstimateFromStats(int additions, int deletions)
    {
        var lineCount = Math.Max(0, additions) + Math.Max(0, deletions);
        return lineCount * AvgTokensPerLine + PerFileOverhead;
    }

    public int EstimateTokenCount(string serializedContent)
    {
        if (string.IsNullOrEmpty(serializedContent))
            return 0;

        var charsPerToken = _options.Value.CharsPerToken;
        if (charsPerToken <= 0)
            charsPerToken = DefaultCharsPerToken;

        return (int)Math.Ceiling(serializedContent.Length / charsPerToken);
    }
}
