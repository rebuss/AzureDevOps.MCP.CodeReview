using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Services.ContextWindow;

/// <summary>
/// Resolves the token budget for an MCP request using a three-tier resolution chain:
/// explicit token count → model registry lookup → safe default fallback.
/// Applies gateway cap, min/max guardrails and a configurable safety margin.
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
        totalBudget = ApplyGatewayCap(totalBudget, opts, warnings);

        _logger.LogDebug($"GatewayMaxTokens={opts.GatewayMaxTokens}, totalBudget={totalBudget}");

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
            var normalized = NormalizeModelId(modelIdentifier.Trim());
            int bestTokens = 0;
            int bestMatchLen = -1;

            foreach (var entry in opts.ModelRegistry)
            {
                if (entry.Value <= 0) continue;
                var normalizedKey = NormalizeModelId(entry.Key);

                if (normalizedKey == normalized)
                    return (entry.Value, BudgetSource.Registry);

                if (normalizedKey.Length > bestMatchLen
                    && normalized.StartsWith(normalizedKey + "-", StringComparison.Ordinal))
                {
                    bestMatchLen = normalizedKey.Length;
                    bestTokens = entry.Value;
                }
            }

            if (bestMatchLen >= 0)
                return (bestTokens, BudgetSource.Registry);

            warnings.Add($"Model '{modelIdentifier.Trim()}' not found in registry; using default budget");
        }

        // Priority 3: Safe default
        var defaultBudget = GetValidDefault(opts);
        warnings.Add($"No context window declared; using default budget of {defaultBudget} tokens");
        return (defaultBudget, BudgetSource.Default);
    }

    private static int ApplyGatewayCap(int totalBudget, ContextWindowOptions opts, List<string> warnings)
    {
        if (opts.GatewayMaxTokens is not > 0)
            return totalBudget;

        var cap = opts.GatewayMaxTokens.Value;
        if (totalBudget <= cap)
            return totalBudget;

        warnings.Add($"Budget {totalBudget} exceeds gateway cap {cap}; clamped to gateway limit");
        return cap;
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

    // Canonical slug: lowercase, spaces/dots/underscores/hyphens → single hyphen, trailing hyphens stripped.
    // "Claude Sonnet 4.6" → "claude-sonnet-4-6", "gpt-4.1-mini" → "gpt-4-1-mini"
    private static string NormalizeModelId(string s)
    {
        var chars = new char[s.Length];
        int len = 0;
        bool prevWasSep = false;
        foreach (char c in s)
        {
            if (c is ' ' or '.' or '_' or '-')
            {
                if (!prevWasSep) { chars[len++] = '-'; prevWasSep = true; }
            }
            else
            {
                chars[len++] = char.ToLowerInvariant(c);
                prevWasSep = false;
            }
        }
        while (len > 0 && chars[len - 1] == '-') len--;
        return new string(chars, 0, len);
    }
}
