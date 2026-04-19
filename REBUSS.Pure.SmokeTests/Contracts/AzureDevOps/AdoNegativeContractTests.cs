using System.Text.Json;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoNegativeContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoNegativeContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task NonExistentPr_Metadata_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = 999999 });

        Assert.True(response.IsToolError());
    }

    [SkippableFact]
    public async Task InvalidPrNumber_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = -1 });

        Assert.True(response.IsToolError());
        var message = response.GetToolErrorMessage();
        Assert.Contains("greater than 0", message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MissingPrNumber_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { });

        Assert.True(response.IsToolError());
        var message = response.GetToolErrorMessage();
        Assert.Contains("Missing", message, StringComparison.OrdinalIgnoreCase);
    }
}
