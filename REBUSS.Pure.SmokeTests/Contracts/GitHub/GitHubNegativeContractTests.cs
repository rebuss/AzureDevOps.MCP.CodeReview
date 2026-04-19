using System.Text.Json;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubNegativeContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubNegativeContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task NonExistentPr_Metadata_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = 999999 });

        Assert.True(response.IsToolError());
    }

    [SkippableFact]
    public async Task InvalidPrNumber_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = -1 });

        Assert.True(response.IsToolError());
        var message = response.GetToolErrorMessage();
        Assert.Contains("greater than 0", message, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task MissingPrNumber_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { });

        Assert.True(response.IsToolError());
        var message = response.GetToolErrorMessage();
        Assert.Contains("Missing", message, StringComparison.OrdinalIgnoreCase);
    }
}
