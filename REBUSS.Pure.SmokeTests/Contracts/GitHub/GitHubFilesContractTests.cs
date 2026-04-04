using System.Text.RegularExpressions;
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
        var content = response.GetToolText();

        Assert.Contains($"({GitHubTestExpectations.TotalFiles} file(s))", content);
    }

    [SkippableFact]
    public async Task Files_ReturnsExpectedPaths()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        foreach (var expected in GitHubTestExpectations.FilePaths)
        {
            Assert.Contains(expected, content);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsCorrectStatuses()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        foreach (var (path, expectedStatus) in GitHubTestExpectations.FileStatuses)
        {
            var rowPattern = new Regex(
                $@"^\s+{Regex.Escape(path)}\s+{Regex.Escape(expectedStatus)}\s+\+\s*\d+\s+-\s*\d+\s+(low|medium|high)\b",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Assert.Matches(rowPattern, content);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsAdditionsDeletions()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        foreach (var path in GitHubTestExpectations.FilePaths)
        {
            var rowPattern = new Regex(
                $@"^\s+{Regex.Escape(path)}\s+\w+\s+\+\s*\d+\s+-\s*\d+\s+(low|medium|high)\b",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Assert.Matches(rowPattern, content);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsExtension()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains(".cs", content, StringComparison.OrdinalIgnoreCase);
    }

    [SkippableFact]
    public async Task Files_ClassifiesAsSourceFiles()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.Contains("Summary:", content);
        Assert.Contains($"{GitHubTestExpectations.TotalFiles} source", content);
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"\d+ test"), content);
    }

    [SkippableFact]
    public async Task Files_ReviewPriority()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        foreach (var path in GitHubTestExpectations.FilePaths)
        {
            var priorityPattern = new Regex(
                $@"^\s+{Regex.Escape(path)}\s+\w+\s+\+\s*\d+\s+-\s*\d+\s+(low|medium|high)\b",
                RegexOptions.Multiline | RegexOptions.IgnoreCase);
            Assert.Matches(priorityPattern, content);
        }
    }

    [SkippableFact]
    public async Task Files_NoBinaryOrGenerated()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetToolText();

        Assert.DoesNotContain("[binary", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("[generated", content, StringComparison.OrdinalIgnoreCase);
    }
}
