using System.Text.Json;
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
        var content = response.GetToolContent();

        Assert.Equal(TestSettings.GhPrNumber, content.GetProperty("prNumber").GetInt32());
    }

    [SkippableFact]
    public async Task Metadata_ReturnsTitle()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(GitHubTestExpectations.Title, content.GetProperty("title").GetString());
    }

    [SkippableFact]
    public async Task Metadata_ReturnsState()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        // GitHub parser maps "open" -> "active" internally
        var state = content.GetProperty("state").GetString()!;
        Assert.True(state is "active" or "open",
            $"Expected state 'active' or 'open', got '{state}'.");
    }

    [SkippableFact]
    public async Task Metadata_ReturnsBranches()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var baseRef = content.GetProperty("base").GetProperty("ref").GetString()!;
        var headRef = content.GetProperty("head").GetProperty("ref").GetString()!;

        Assert.Contains(GitHubTestExpectations.TargetBranchContains, baseRef);
        Assert.Contains(GitHubTestExpectations.SourceBranchContains, headRef);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsAuthor()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var author = content.GetProperty("author");
        Assert.False(string.IsNullOrWhiteSpace(author.GetProperty("displayName").GetString()));
    }

    [SkippableFact]
    public async Task Metadata_ReturnsStats()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var stats = content.GetProperty("stats");
        Assert.Equal(GitHubTestExpectations.TotalFiles, stats.GetProperty("changedFiles").GetInt32());
        Assert.True(stats.GetProperty("commits").GetInt32() >= 1);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsCommitShas()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
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
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var source = content.GetProperty("source");
        var url = source.GetProperty("url").GetString()!;
        Assert.Contains("github.com", url, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_ReturnsDescription()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var description = content.GetProperty("description");
        var text = description.GetProperty("text").GetString()!;
        Assert.Contains(GitHubTestExpectations.DescriptionFragment, text, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Metadata_IsDraft()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_metadata", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(GitHubTestExpectations.IsDraft, content.GetProperty("isDraft").GetBoolean());
    }
}
