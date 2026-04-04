using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubFileDiffContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubFileDiffContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsSingleFile()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = GitHubTestExpectations.FilePaths[0] });
        var content = response.GetAllToolText();

        Assert.Contains($"=== {GitHubTestExpectations.FilePaths[0]}", content);
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsCorrectPath()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = GitHubTestExpectations.FilePaths[0] });
        var content = response.GetAllToolText();

        Assert.Contains(GitHubTestExpectations.FilePaths[0], content);
    }

    [SkippableFact]
    public async Task FileDiff_HasHunks()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = GitHubTestExpectations.FilePaths[0] });
        var content = response.GetAllToolText();

        Assert.True(content.Contains("+", StringComparison.Ordinal) || content.Contains("-", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task FileDiff_ForNonExistentFile_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = "nonexistent.cs" });

        Assert.True(response.IsToolError());
    }
}
