using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoDiffContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoDiffContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Diff_ReturnsPrNumber()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains(AdoTestExpectations.FilePaths[0], content);
        Assert.Contains(AdoTestExpectations.FilePaths[1], content);
    }

    [SkippableFact]
    public async Task Diff_ReturnsExpectedFileCount()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        var blockCount = content.Split("=== ", StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.True(blockCount >= AdoTestExpectations.TotalFiles,
            $"Expected at least {AdoTestExpectations.TotalFiles} file blocks, got {blockCount}.");
    }

    [SkippableFact]
    public async Task Diff_ReturnsHunksForEditedFile()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        var calcBlock = content.GetFileBlock("Calculator.cs");
        Assert.NotNull(calcBlock);
        Assert.Contains("+", calcBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_HunkHasCorrectStructure()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains("===", content);
    }

    [SkippableFact]
    public async Task Diff_LinesHaveValidOps()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        Assert.True(content.Contains("+", StringComparison.Ordinal) || content.Contains("-", StringComparison.Ordinal));
    }

    [SkippableFact]
    public async Task Diff_EditedFileHasAdditionsAndDeletions()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        var calcBlock = content.GetFileBlock("Calculator.cs");
        Assert.NotNull(calcBlock);
        Assert.Contains("+", calcBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_NewFileHasOnlyAdditions()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        var loggerBlock = content.GetFileBlock("Logger.cs");
        Assert.NotNull(loggerBlock);
        Assert.Contains("+", loggerBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("\n-", loggerBlock, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task Diff_ContainsExpectedCodeFragment()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetAllToolText();

        Assert.Contains(AdoTestExpectations.ExpectedCodeFragment, content, StringComparison.OrdinalIgnoreCase);
    }
}
