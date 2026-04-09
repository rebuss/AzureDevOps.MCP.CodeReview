using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.ContextWindow;

namespace REBUSS.Pure.Tests.Services.ContextWindow;

public class ContextBudgetResolverTests
{
    private static ContextBudgetResolver CreateResolver(
        int safetyMarginPercent = 30,
        int defaultBudgetTokens = 128_000,
        int minBudgetTokens = 4_000,
        int maxBudgetTokens = 2_000_000,
        int? gatewayMaxTokens = null,
        int largeContextThresholdTokens = 100_000,
        int largeContextCeilingTokens = 0,
        Dictionary<string, int>? modelRegistry = null,
        ILogger<ContextBudgetResolver>? logger = null)
    {
        var options = Options.Create(new ContextWindowOptions
        {
            SafetyMarginPercent = safetyMarginPercent,
            DefaultBudgetTokens = defaultBudgetTokens,
            MinBudgetTokens = minBudgetTokens,
            MaxBudgetTokens = maxBudgetTokens,
            GatewayMaxTokens = gatewayMaxTokens,
            LargeContextThresholdTokens = largeContextThresholdTokens,
            LargeContextCeilingTokens = largeContextCeilingTokens,
            ModelRegistry = modelRegistry ?? new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        });
        return new ContextBudgetResolver(options, logger ?? Substitute.For<ILogger<ContextBudgetResolver>>());
    }

    private static Dictionary<string, int> DefaultRegistry() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["claude-sonnet-4"] = 200_000,
        ["claude-sonnet-3.5"] = 200_000,
        ["gpt-4o"] = 128_000,
        ["gpt-4.1"] = 1_048_576,
        ["gpt-4.1-mini"] = 1_048_576,
        ["gemini-2.5-pro"] = 1_048_576
    };

    // ──────────────────────────────────────────────
    // Phase 3 / US1: Explicit budget resolution
    // ──────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_Explicit32000_ReturnsTotalAndSafeBudget()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve(explicitTokens: 32_000, modelIdentifier: null);

        Assert.Equal(32_000, result.TotalBudgetTokens);
        Assert.Equal(22_400, result.SafeBudgetTokens); // 32000 * 0.70
        Assert.Equal(BudgetSource.Explicit, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitZero_FallsBackToDefault()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve(explicitTokens: 0, modelIdentifier: null);

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("Invalid explicit budget"));
        Assert.Contains(result.Warnings, w => w.Contains("default budget"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitNegative_FallsBackToDefault()
    {
        var resolver = CreateResolver();

        var result = resolver.Resolve(explicitTokens: -1, modelIdentifier: null);

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("Invalid explicit budget (-1)"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitBelowMin_ClampedToMinimum()
    {
        var resolver = CreateResolver(minBudgetTokens: 4_000);

        var result = resolver.Resolve(explicitTokens: 1_000, modelIdentifier: null);

        Assert.Equal(4_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Explicit, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("clamped to minimum"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitAboveMax_ClampedToMaximum()
    {
        var resolver = CreateResolver(maxBudgetTokens: 2_000_000);

        var result = resolver.Resolve(explicitTokens: 5_000_000, modelIdentifier: null);

        Assert.Equal(2_000_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Explicit, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("clamped to maximum"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitWithSafetyMargin_CalculatesSafeBudgetCorrectly()
    {
        var resolver = CreateResolver(safetyMarginPercent: 30);

        var result = resolver.Resolve(explicitTokens: 100_000, modelIdentifier: null);

        Assert.Equal(100_000, result.TotalBudgetTokens);
        Assert.Equal(70_000, result.SafeBudgetTokens); // 100000 * (100 - 30) / 100
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_NullExplicitNullModel_ReturnsDefaultFallback()
    {
        var resolver = CreateResolver(defaultBudgetTokens: 128_000);

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: null);

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("No context window declared"));
    }

    // ──────────────────────────────────────────────
    // Phase 4 / US2: Model registry resolution
    // ──────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_KnownModel_ReturnsBudgetFromRegistry()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(140_000, result.SafeBudgetTokens); // 200000 * 0.70
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_CaseInsensitiveModel_MatchesRegistry()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "Claude-Sonnet-4");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_WhitespaceTrimmedModel_MatchesRegistry()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "  gpt-4o  ");

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_UnknownModel_FallsBackToDefault()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "unknown-model-xyz");

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("'unknown-model-xyz' not found in registry"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_EmptyStringModel_TreatedAsNoModel()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "");

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("default budget"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExplicitOverridesRegistry_ExplicitWins()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: 50_000, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(50_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Explicit, result.Source);
    }

    // ──────────────────────────────────────────────
    // Phase 5 / US3: Default fallback & edge cases
    // ──────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_NullNull_ReturnsDefaultBudget()
    {
        var resolver = CreateResolver(defaultBudgetTokens: 128_000);

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: null);

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("No context window declared"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_UnrecognisedModel_DefaultWithWarning()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "future-model");

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("'future-model' not found in registry"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_EmptyRegistry_AllModelsFallThrough()
    {
        var resolver = CreateResolver(modelRegistry: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase));

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("not found in registry"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_CustomDefaultBudget_UsedForFallback()
    {
        var resolver = CreateResolver(defaultBudgetTokens: 64_000);

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: null);

        Assert.Equal(64_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Default, result.Source);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_SafetyMarginAppliedToDefault_Correctly()
    {
        var resolver = CreateResolver(safetyMarginPercent: 30, defaultBudgetTokens: 128_000);

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: null);

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(89_600, result.SafeBudgetTokens); // 128000 * 0.70
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_CustomSafetyMargin_AppliedCorrectly()
    {
        var resolver = CreateResolver(safetyMarginPercent: 50);

        var result = resolver.Resolve(explicitTokens: 100_000, modelIdentifier: null);

        Assert.Equal(100_000, result.TotalBudgetTokens);
        Assert.Equal(50_000, result.SafeBudgetTokens); // 100000 * 0.50
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_InvalidExplicitWithModel_UsesRegistry()
    {
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: -5, modelIdentifier: "gpt-4o");

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("Invalid explicit budget"));
    }

    // ──────────────────────────────────────────────
    // Model normalization and prefix matching
    // ──────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_FreeFormSpaceSeparated_NormalizesToExactMatch()
    {
        // "Claude Sonnet 4" normalizes to "claude-sonnet-4" — exact registry hit
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "Claude Sonnet 4");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_FreeFormWithMinorVersion_PrefixMatchesRegistryKey()
    {
        // "Claude Sonnet 4.6" normalizes to "claude-sonnet-4-6";
        // prefix-matches registry key "claude-sonnet-4" → 200 K
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "Claude Sonnet 4.6");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(140_000, result.SafeBudgetTokens); // 200000 * 0.70
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_DotVersionKeyInRegistry_NormalizedExactMatch()
    {
        // "claude-sonnet-3.5" normalizes to "claude-sonnet-3-5";
        // registry key "claude-sonnet-3.5" also normalizes to "claude-sonnet-3-5" → exact match
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-3.5");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_FreeFormWithMinorVersionDot_NormalizesToExactMatch()
    {
        // "Claude Sonnet 3.5" normalizes to "claude-sonnet-3-5" → exact match on "claude-sonnet-3.5"
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "Claude Sonnet 3.5");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_ExactKeyBeatsPrefix_WhenBothCouldMatch()
    {
        // "gpt-4.1-mini" normalizes to "gpt-4-1-mini" → exact match on "gpt-4.1-mini",
        // even though "gpt-4.1" would also be a prefix candidate
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "gpt-4.1-mini");

        Assert.Equal(1_048_576, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_PrefixMatchPrefersLongestKey()
    {
        // "gpt-4.1-mini-2025" normalizes to "gpt-4-1-mini-2025";
        // both "gpt-4.1" ("gpt-4-1") and "gpt-4.1-mini" ("gpt-4-1-mini") are prefix candidates;
        // longest normalized key wins → "gpt-4.1-mini" → 1 048 576
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "gpt-4.1-mini-2025");

        Assert.Equal(1_048_576, result.TotalBudgetTokens);
        Assert.Equal(734_003, result.SafeBudgetTokens); // 1048576 * 0.70 (integer arithmetic)
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_NormalizationDoesNotCrossModelFamily()
    {
        // "claude-sonnet-40" normalizes to "claude-sonnet-40";
        // must NOT prefix-match "claude-sonnet-4" ("claude-sonnet-4") because the next
        // char after the key segment is '0', not '-'
        var resolver = CreateResolver(modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-40");

        Assert.Equal(BudgetSource.Default, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("'claude-sonnet-40' not found in registry"));
    }

    // ──────────────────────────────────────────────────
    // Gateway cap
    // ──────────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_ClampsRegistryBudget()
    {
        // Registry says 200K for Claude, but gateway caps at 128K
        var resolver = CreateResolver(gatewayMaxTokens: 128_000, modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(89_600, result.SafeBudgetTokens); // 128000 * 0.70
        Assert.Equal(BudgetSource.Registry, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("gateway cap"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_ClampsExplicitBudget()
    {
        var resolver = CreateResolver(gatewayMaxTokens: 128_000);

        var result = resolver.Resolve(explicitTokens: 200_000, modelIdentifier: null);

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Equal(BudgetSource.Explicit, result.Source);
        Assert.Contains(result.Warnings, w => w.Contains("gateway cap"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_NoClamping_WhenBudgetBelowCap()
    {
        var resolver = CreateResolver(gatewayMaxTokens: 128_000, modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "gpt-4o");

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("gateway cap"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_Null_DoesNotClamp()
    {
        // Null gateway cap = disabled (e.g. direct API access)
        var resolver = CreateResolver(gatewayMaxTokens: null, modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.Equal(140_000, result.SafeBudgetTokens);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("gateway cap"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_Zero_DoesNotClamp()
    {
        // Zero gateway cap = disabled
        var resolver = CreateResolver(gatewayMaxTokens: 0, modelRegistry: DefaultRegistry());

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-sonnet-4");

        Assert.Equal(200_000, result.TotalBudgetTokens);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("gateway cap"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_GatewayCap_ClampsDefaultFallback()
    {
        var resolver = CreateResolver(defaultBudgetTokens: 200_000, gatewayMaxTokens: 128_000);

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: null);

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.Contains(result.Warnings, w => w.Contains("gateway cap"));
    }

    // ──────────────────────────────────────────────
    // Large-context-model ceiling
    // ──────────────────────────────────────────────

    [Fact]
    public void ContextBudgetResolver_Resolve_LargeContextCeiling_CapsRegistryBudget()
    {
        // claude-opus-4 has 200_000 in registry → above 100_000 threshold → capped to 17_000.
        var resolver = CreateResolver(
            largeContextCeilingTokens: 17_000,
            modelRegistry: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["claude-opus-4"] = 200_000 });

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "claude-opus-4");

        Assert.Equal(17_000, result.TotalBudgetTokens);
        Assert.Contains(result.Warnings, w => w.Contains("Large-context"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_LargeContextCeiling_DoesNotApplyToExplicitBudget()
    {
        // Explicit per-call maxTokens always wins.
        var resolver = CreateResolver(largeContextCeilingTokens: 17_000);

        var result = resolver.Resolve(explicitTokens: 80_000, modelIdentifier: null);

        Assert.Equal(80_000, result.TotalBudgetTokens);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Large-context"));
    }

    [Fact]
    public void ContextBudgetResolver_Resolve_LargeContextCeiling_LeavesSmallContextModelsUntouched()
    {
        // gpt-4o is 128_000 (above threshold) but well below ceiling — no warning, no clamp.
        var resolver = CreateResolver(
            largeContextCeilingTokens: 150_000,
            modelRegistry: new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["gpt-4o"] = 128_000 });

        var result = resolver.Resolve(explicitTokens: null, modelIdentifier: "gpt-4o");

        Assert.Equal(128_000, result.TotalBudgetTokens);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("Large-context"));
    }
}
