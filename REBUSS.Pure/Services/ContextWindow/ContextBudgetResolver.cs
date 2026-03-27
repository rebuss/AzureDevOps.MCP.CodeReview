using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ContextWindow;

/// <summary>
/// Resolves the token budget for an MCP request using a three-tier resolution chain:
/// explicit token count → model registry lookup → safe default fallback.
/// Applies min/max guardrails and a configurable safety margin.
/// </summary>
public sealed class ContextBudgetResolver : IContextBudgetResolver
{
    private readonly IOptions<ContextWindowOptions> _options;
    private readonly ILogger<ContextBudgetResolver> _logger;

    public ContextBudgetResolver(IOptions<ContextWindowOptions> options, ILogger<ContextBudgetResolver> logger)
    {
        _options = options;
        _logger = logger;
    }

    public BudgetResolutionResult Resolve(int? explicitTokens, string? modelIdentifier)
    {
        var opts = _options.Value;
        var warnings = new List<string>();

        var (totalBudget, source) = ResolveTotalBudget(explicitTokens, modelIdentifier, opts, warnings);
        totalBudget = ApplyGuardrails(totalBudget, source, opts, warnings);

        var safetyMargin = GetValidSafetyMargin(opts, warnings);
        var safeBudget = totalBudget * (100 - safetyMargin) / 100;

        return new BudgetResolutionResult(totalBudget, safeBudget, source, warnings.AsReadOnly());
    }

    private static (int totalBudget, BudgetSource source) ResolveTotalBudget(
        int? explicitTokens, string? modelIdentifier,
        ContextWindowOptions opts, List<string> warnings)
    {
        // Priority 1: Explicit token count
        if (explicitTokens.HasValue)
        {
            if (explicitTokens.Value > 0)
                return (explicitTokens.Value, BudgetSource.Explicit);

            warnings.Add($"Invalid explicit budget ({explicitTokens.Value}); using default budget");
        }

        // Priority 2: Model registry lookup
        if (!string.IsNullOrWhiteSpace(modelIdentifier))
        {
            var normalized = modelIdentifier.Trim();
            if (opts.ModelRegistry.TryGetValue(normalized, out var registryTokens) && registryTokens > 0)
                return (registryTokens, BudgetSource.Registry);

            warnings.Add($"Model '{modelIdentifier.Trim()}' not found in registry; using default budget");
        }

        // Priority 3: Safe default
        var defaultBudget = GetValidDefault(opts);
        warnings.Add($"No context window declared; using default budget of {defaultBudget} tokens");
        return (defaultBudget, BudgetSource.Default);
    }

    private static int ApplyGuardrails(
        int totalBudget, BudgetSource source,
        ContextWindowOptions opts, List<string> warnings)
    {
        var min = opts.MinBudgetTokens > 0 ? opts.MinBudgetTokens : 4_000;
        var max = opts.MaxBudgetTokens >= min ? opts.MaxBudgetTokens : 2_000_000;

        if (totalBudget < min)
        {
            if (source != BudgetSource.Default)
                warnings.Add($"Explicit budget {totalBudget} clamped to minimum {min}");
            return min;
        }

        if (totalBudget > max)
        {
            if (source != BudgetSource.Default)
                warnings.Add($"Explicit budget {totalBudget} clamped to maximum {max}");
            return max;
        }

        return totalBudget;
    }

    private int GetValidSafetyMargin(ContextWindowOptions opts, List<string> warnings)
    {
        if (opts.SafetyMarginPercent is >= 1 and <= 90)
            return opts.SafetyMarginPercent;

        var clamped = Math.Clamp(opts.SafetyMarginPercent, 1, 90);
        _logger.LogWarning(
            "SafetyMarginPercent {Value} outside valid range 1–90; clamped to {Clamped}",
            opts.SafetyMarginPercent, clamped);
        return clamped;
    }

    private static int GetValidDefault(ContextWindowOptions opts)
    {
        var min = opts.MinBudgetTokens > 0 ? opts.MinBudgetTokens : 4_000;
        return opts.DefaultBudgetTokens >= min ? opts.DefaultBudgetTokens : min;
    }
}
