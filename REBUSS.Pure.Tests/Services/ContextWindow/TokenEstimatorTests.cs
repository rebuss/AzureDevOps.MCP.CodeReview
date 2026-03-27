using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.ContextWindow;

namespace REBUSS.Pure.Tests.Services.ContextWindow;

public class TokenEstimatorTests
{
    private static TokenEstimator CreateEstimator(
        double charsPerToken = 4.0,
        ILogger<TokenEstimator>? logger = null)
    {
        var options = Options.Create(new ContextWindowOptions { CharsPerToken = charsPerToken });
        return new TokenEstimator(options, logger ?? Substitute.For<ILogger<TokenEstimator>>());
    }

    [Fact]
    public void TokenEstimator_Estimate_TypicalJson_ReturnsCorrectTokenEstimate()
    {
        var estimator = CreateEstimator(charsPerToken: 4.0);
        var json = new string('x', 400); // 400 chars / 4.0 = 100 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(100, result.EstimatedTokens);
        Assert.True(result.FitsWithinBudget);
    }

    [Fact]
    public void TokenEstimator_Estimate_EmptyString_ReturnsZeroTokensAndFits()
    {
        var estimator = CreateEstimator();

        var result = estimator.Estimate(string.Empty, safeBudgetTokens: 1000);

        Assert.Equal(0, result.EstimatedTokens);
        Assert.Equal(0.0, result.PercentageUsed);
        Assert.True(result.FitsWithinBudget);
    }

    [Fact]
    public void TokenEstimator_Estimate_NullInput_ReturnsZeroTokensAndFits()
    {
        var estimator = CreateEstimator();

        var result = estimator.Estimate(null!, safeBudgetTokens: 1000);

        Assert.Equal(0, result.EstimatedTokens);
        Assert.Equal(0.0, result.PercentageUsed);
        Assert.True(result.FitsWithinBudget);
    }

    [Fact]
    public void TokenEstimator_Estimate_ContentExactlyAtBudget_FitsWithinBudget()
    {
        var estimator = CreateEstimator(charsPerToken: 4.0);
        var json = new string('x', 4000); // 4000 chars / 4.0 = 1000 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(1000, result.EstimatedTokens);
        Assert.True(result.FitsWithinBudget);
        Assert.Equal(100.0, result.PercentageUsed, precision: 1);
    }

    [Fact]
    public void TokenEstimator_Estimate_ContentExceedsBudget_DoesNotFit()
    {
        var estimator = CreateEstimator(charsPerToken: 4.0);
        var json = new string('x', 4004); // 4004 chars / 4.0 = 1001 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(1001, result.EstimatedTokens);
        Assert.False(result.FitsWithinBudget);
        Assert.True(result.PercentageUsed > 100.0);
    }

    [Fact]
    public void TokenEstimator_Estimate_PercentageCalculation_IsAccurate()
    {
        var estimator = CreateEstimator(charsPerToken: 4.0);
        var json = new string('x', 2000); // 2000 chars / 4.0 = 500 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(500, result.EstimatedTokens);
        Assert.Equal(50.0, result.PercentageUsed, precision: 1);
    }

    [Fact]
    public void TokenEstimator_Estimate_CustomCharsPerTokenRatio_AppliedCorrectly()
    {
        var estimator = CreateEstimator(charsPerToken: 2.0);
        var json = new string('x', 400); // 400 chars / 2.0 = 200 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(200, result.EstimatedTokens);
        Assert.True(result.FitsWithinBudget);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.0)]
    [InlineData(-100.0)]
    public void TokenEstimator_Estimate_InvalidCharsPerToken_FallsBackToDefault(double invalidRatio)
    {
        var estimator = CreateEstimator(charsPerToken: invalidRatio);
        var json = new string('x', 400); // 400 chars / default 4.0 = 100 tokens

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(100, result.EstimatedTokens);
    }

    [Fact]
    public void TokenEstimator_Estimate_RoundsUp_WhenCharsDontDivideEvenly()
    {
        var estimator = CreateEstimator(charsPerToken: 4.0);
        var json = new string('x', 401); // 401 / 4.0 = 100.25, ceil = 101

        var result = estimator.Estimate(json, safeBudgetTokens: 1000);

        Assert.Equal(101, result.EstimatedTokens);
    }

    [Fact]
    public void TokenEstimator_Estimate_ZeroSafeBudget_PercentageIs100()
    {
        var estimator = CreateEstimator();
        var json = new string('x', 100);

        var result = estimator.Estimate(json, safeBudgetTokens: 0);

        Assert.Equal(100.0, result.PercentageUsed);
        Assert.False(result.FitsWithinBudget);
    }
}
