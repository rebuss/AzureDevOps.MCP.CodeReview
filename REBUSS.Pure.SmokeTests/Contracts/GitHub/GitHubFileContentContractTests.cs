using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubFileContentContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubFileContentContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task FileContent_ReturnsContentAtMain()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = GitHubTestExpectations.FilePaths[0], @ref = "main" });
        var content = response.GetToolText();

        Assert.Contains(GitHubTestExpectations.OriginalCodeFragment, content);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsContentAtBranch()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = GitHubTestExpectations.FilePaths[0], @ref = "test/fixture-pr" });
        var content = response.GetToolText();

        Assert.Contains(GitHubTestExpectations.ModifiedCodeFragment, content);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsMetadata()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = GitHubTestExpectations.FilePaths[0], @ref = "main" });
        var content = response.GetToolText();

        Assert.DoesNotContain("[binary file", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"=== {GitHubTestExpectations.FilePaths[0]} @ main ===", content);
    }

    [SkippableFact]
    public async Task FileContent_ForNonExistentFile_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = "nonexistent.cs", @ref = "main" });

        Assert.True(response.IsToolError());
    }
}
