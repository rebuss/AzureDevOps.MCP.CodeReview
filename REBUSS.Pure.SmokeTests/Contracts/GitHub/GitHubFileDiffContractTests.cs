using System.Text.Json;
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
        var content = response.GetToolContent();

        Assert.Equal(1, content.GetProperty("files").GetArrayLength());
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsCorrectPath()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = GitHubTestExpectations.FilePaths[0] });
        var content = response.GetToolContent();

        var path = content.GetProperty("files")[0].GetProperty("path").GetString();
        Assert.Equal(GitHubTestExpectations.FilePaths[0], path);
    }

    [SkippableFact]
    public async Task FileDiff_HasHunks()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.GhPrNumber, path = GitHubTestExpectations.FilePaths[0] });
        var content = response.GetToolContent();

        var hunks = content.GetProperty("files")[0].GetProperty("hunks");
        Assert.True(hunks.GetArrayLength() >= 1, "Expected at least one hunk.");
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
