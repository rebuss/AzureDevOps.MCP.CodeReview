using System.Text.RegularExpressions;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.GitHub;

[Collection("GitHubContract")]
[Trait("Category", "ContractGitHub")]
public class GitHubDiffContractTests
{
    private readonly GitHubMcpProcessFixture _fixture;

    public GitHubDiffContractTests(GitHubMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Diff_ReturnsPrNumber()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains(GitHubTestExpectations.FilePaths[0], content);
        Assert.Contains(GitHubTestExpectations.FilePaths[1], content);
    }

    [SkippableFact]
    public async Task Diff_ReturnsExpectedFileCount()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        var blockCount = content.Split("=== ", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(blockCount >= GitHubTestExpectations.TotalFiles,
            $"Expected at least {GitHubTestExpectations.TotalFiles} file blocks, got {blockCount}.");
    }

    [SkippableFact]
    public async Task Diff_ReturnsHunksForEditedFile()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        var calcBlock = content.GetFileBlock("Calculator.cs");
        Assert.NotNull(calcBlock);
        Assert.Contains("+", calcBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_HunkHasCorrectStructure()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains("===", content);
        Assert.Matches(new Regex(@"@@ -\d+,\d+ \+\d+,\d+ @@"), content);
    }

    [SkippableFact]
    public async Task Diff_LinesHaveValidOps()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        var diffLines = content.Split('\n')
            .Where(l => l.Length > 0 && !l.StartsWith("===") && !l.StartsWith("@@ ") &&
                        !l.StartsWith("Manifest") && !l.StartsWith("Budget") &&
                        !l.StartsWith("Reason:") && !l.StartsWith("---") &&
                        !l.TrimStart().StartsWith('~'))
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToList();

        Assert.NotEmpty(diffLines);
        Assert.All(diffLines, line =>
            Assert.True(line.StartsWith('+') || line.StartsWith('-') || line.StartsWith(' '),
                $"Diff line has invalid prefix: '{line}'"));
    }

    [SkippableFact]
    public async Task Diff_EditedFileHasAdditionsAndDeletions()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        var calcBlock = content.GetFileBlock("Calculator.cs");
        Assert.NotNull(calcBlock);
        Assert.Contains("+", calcBlock, StringComparison.Ordinal);
        Assert.Contains("\n-", calcBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_NewFileHasOnlyAdditions()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        var loggerBlock = content.GetFileBlock("Logger.cs");
        Assert.NotNull(loggerBlock);
        Assert.Contains("+", loggerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\n-", loggerBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_ContainsExpectedCodeFragment()
    {
        Skip.IfNot(TestSettings.IsGitHubConfigured, TestSettings.GitHubSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.GhPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains(GitHubTestExpectations.ExpectedCodeFragment, content, StringComparison.OrdinalIgnoreCase);
    }
}
