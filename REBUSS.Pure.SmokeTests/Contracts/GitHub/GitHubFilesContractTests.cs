using System.Text.Json;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubFilesContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubFilesContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Files_ReturnsTotalFileCount()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(GitHubTestExpectations.TotalFiles, content.GetProperty("totalFiles").GetInt32());
    }

    [SkippableFact]
    public async Task Files_ReturnsExpectedPaths()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var paths = content.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();

        foreach (var expected in GitHubTestExpectations.FilePaths)
        {
            Assert.Contains(expected, paths);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsCorrectStatuses()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var files = content.GetProperty("files").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("path").GetString()!,
                f => f.GetProperty("status").GetString()!);

        foreach (var (path, expectedStatus) in GitHubTestExpectations.FileStatuses)
        {
            Assert.True(files.ContainsKey(path), $"File '{path}' not found in response.");
            Assert.Equal(expectedStatus, files[path]);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsAdditionsDeletions()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var files = content.GetProperty("files").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("path").GetString()!,
                f => f);

        // Edited file should have both additions and deletions
        var calculator = files[GitHubTestExpectations.FilePaths[0]];
        Assert.True(calculator.GetProperty("additions").GetInt32() > 0,
            "Edited file should have additions > 0.");
        Assert.True(calculator.GetProperty("deletions").GetInt32() > 0,
            "Edited file should have deletions > 0.");

        // New file should have only additions
        var logger = files[GitHubTestExpectations.FilePaths[1]];
        Assert.True(logger.GetProperty("additions").GetInt32() > 0,
            "New file should have additions > 0.");
        Assert.Equal(0, logger.GetProperty("deletions").GetInt32());
    }

    [SkippableFact]
    public async Task Files_ReturnsExtension()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            Assert.Equal(".cs", file.GetProperty("extension").GetString());
        }
    }

    [SkippableFact]
    public async Task Files_ClassifiesAsSourceFiles()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var summary = content.GetProperty("summary");
        Assert.Equal(GitHubTestExpectations.TotalFiles, summary.GetProperty("sourceFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("testFiles").GetInt32());
    }

    [SkippableFact]
    public async Task Files_ReviewPriority()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        var validPriorities = new HashSet<string> { "low", "medium", "high" };

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            var priority = file.GetProperty("reviewPriority").GetString()!;
            Assert.Contains(priority, validPriorities);
        }
    }

    [SkippableFact]
    public async Task Files_NoBinaryOrGenerated()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolContent();

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            Assert.False(file.GetProperty("isBinary").GetBoolean());
            Assert.False(file.GetProperty("isGenerated").GetBoolean());
        }
    }
}
