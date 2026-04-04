using System.Text.RegularExpressions;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubMetadataContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubMetadataContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Metadata_ReturnsPrNumber()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains($"PR #{TestSettings.GhPrNumber}:", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsTitle()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains(GitHubTestExpectations.Title, content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsState()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains("State:", content);
        Assert.True(
            content.Contains("active", StringComparison.OrdinalIgnoreCase) ||
            content.Contains("open", StringComparison.OrdinalIgnoreCase),
            "Expected state marker 'active' or 'open' in output.");
    }

    [SkippableFact]
    public async Task Metadata_ReturnsBranches()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains(GitHubTestExpectations.TargetBranchContains, content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(GitHubTestExpectations.SourceBranchContains, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsAuthor()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains("Author:", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsStats()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains($"{GitHubTestExpectations.TotalFiles} file(s)", content);
        Assert.Contains("commit(s)", content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsCommitShas()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Matches(new Regex(@"Head SHA:\s+[0-9a-f]{40}", RegexOptions.IgnoreCase), content);
        Assert.Matches(new Regex(@"Base SHA:\s+[0-9a-f]{40}", RegexOptions.IgnoreCase), content);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsSource()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains("github.com", content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsDescription()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains(GitHubTestExpectations.DescriptionFragment, content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_IsDraft()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Equal(GitHubTestExpectations.IsDraft, content.Contains("[draft]", StringComparison.OrdinalIgnoreCase));
    }
}
