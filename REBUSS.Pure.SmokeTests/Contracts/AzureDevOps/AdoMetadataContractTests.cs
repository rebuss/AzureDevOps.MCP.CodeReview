using System.Text.RegularExpressions;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoMetadataContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoMetadataContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Metadata_ReturnsPrNumber()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains($"PR #{TestSettings.AdoPrNumber}:", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsTitle()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains(AdoTestExpectations.Title, content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsState()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains("State:", content);
        Assert.Contains(AdoTestExpectations.State, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsBranches()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains(AdoTestExpectations.TargetBranchContains, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AdoTestExpectations.SourceBranchContains, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsAuthor()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains("Author:", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsStats()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains($"{AdoTestExpectations.TotalFiles} file(s)", content);
        Assert.Contains("commit(s)", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsCommitShas()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Matches(new Regex(@"Head SHA:\s+[0-9a-f]{40}", RegexOptions.IgnoreCase), content);
        Assert.Matches(new Regex(@"Base SHA:\s+[0-9a-f]{40}", RegexOptions.IgnoreCase), content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsSource()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains(TestSettings.AdoRepo!, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsDescription()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Contains(AdoTestExpectations.DescriptionFragment, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_IsDraft()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolText();

        Assert.Equal(AdoTestExpectations.IsDraft, content.Contains("[draft]", StringComparison.OrdinalIgnoreCase));
    }
}
