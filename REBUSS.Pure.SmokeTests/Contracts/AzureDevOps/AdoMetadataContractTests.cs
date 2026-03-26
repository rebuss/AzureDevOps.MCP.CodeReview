using System.Text.Json;
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
        var content = response.GetToolContent();

        Assert.Equal(TestSettings.AdoPrNumber, content.GetProperty("prNumber").GetInt32());
    }

    [SkippableFact]
    public async Task Metadata_ReturnsTitle()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(AdoTestExpectations.Title, content.GetProperty("title").GetString());
    }

    [SkippableFact]
    public async Task Metadata_ReturnsState()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(AdoTestExpectations.State, content.GetProperty("state").GetString());
    }

    [SkippableFact]
    public async Task Metadata_ReturnsBranches()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var baseRef = content.GetProperty("base").GetProperty("ref").GetString()!;
        var headRef = content.GetProperty("head").GetProperty("ref").GetString()!;

        Assert.Contains(AdoTestExpectations.TargetBranchContains, baseRef);
        Assert.Contains(AdoTestExpectations.SourceBranchContains, headRef);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsAuthor()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var author = content.GetProperty("author");
        Assert.False(string.IsNullOrWhiteSpace(author.GetProperty("displayName").GetString()));
    }

    [SkippableFact]
    public async Task Metadata_ReturnsStats()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var stats = content.GetProperty("stats");
        Assert.Equal(AdoTestExpectations.TotalFiles, stats.GetProperty("changedFiles").GetInt32());
        Assert.True(stats.GetProperty("commits").GetInt32() >= 1);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsCommitShas()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var commitShas = content.GetProperty("commitShas");
        Assert.True(commitShas.GetArrayLength() > 0, "Expected at least one commit SHA.");

        foreach (var sha in commitShas.EnumerateArray())
        {
            var value = sha.GetString()!;
            Assert.Equal(40, value.Length);
            Assert.True(value.All(c => "0123456789abcdef".Contains(c)),
                $"SHA '{value}' contains non-hex characters.");
        }
    }

    [SkippableFact]
    public async Task Metadata_ReturnsSource()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var source = content.GetProperty("source");
        var repo = source.GetProperty("repository").GetString()!;
        Assert.Contains(TestSettings.AdoRepo!, repo, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsDescription()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var description = content.GetProperty("description");
        var text = description.GetProperty("text").GetString()!;
        Assert.Contains(AdoTestExpectations.DescriptionFragment, text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_IsDraft()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(AdoTestExpectations.IsDraft, content.GetProperty("isDraft").GetBoolean());
    }
}
