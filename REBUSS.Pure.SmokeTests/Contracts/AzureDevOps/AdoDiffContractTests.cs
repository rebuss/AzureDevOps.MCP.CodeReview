using System.Text.Json;
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
        var content = response.GetToolContent();

        Assert.Equal(TestSettings.AdoPrNumber, content.GetProperty("prNumber").GetInt32());
    }

    [SkippableFact]
    public async Task Diff_ReturnsExpectedFileCount()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(AdoTestExpectations.TotalFiles, content.GetProperty("files").GetArrayLength());
    }

    [SkippableFact]
    public async Task Diff_ReturnsHunksForEditedFile()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var calculator = content.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("path").GetString()!.Contains("Calculator.cs"));

        var hunks = calculator.GetProperty("hunks");
        Assert.True(hunks.GetArrayLength() >= 1, "Edited file should have at least one hunk.");
    }

    [SkippableFact]
    public async Task Diff_HunkHasCorrectStructure()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var file = content.GetProperty("files").EnumerateArray().First();
        var hunk = file.GetProperty("hunks").EnumerateArray().First();

        Assert.True(hunk.GetProperty("oldStart").GetInt32() > 0);
        Assert.True(hunk.GetProperty("oldCount").GetInt32() >= 0);
        Assert.True(hunk.GetProperty("newStart").GetInt32() > 0);
        Assert.True(hunk.GetProperty("newCount").GetInt32() >= 0);
    }

    [SkippableFact]
    public async Task Diff_LinesHaveValidOps()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var validOps = new HashSet<string> { "+", "-", " " };

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            foreach (var hunk in file.GetProperty("hunks").EnumerateArray())
            {
                foreach (var line in hunk.GetProperty("lines").EnumerateArray())
                {
                    var op = line.GetProperty("op").GetString()!;
                    Assert.Contains(op, validOps);
                }
            }
        }
    }

    [SkippableFact]
    public async Task Diff_EditedFileHasAdditionsAndDeletions()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var calculator = content.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("path").GetString()!.Contains("Calculator.cs"));

        Assert.True(calculator.GetProperty("additions").GetInt32() > 0,
            "Edited file should have additions > 0.");
    }

    [SkippableFact]
    public async Task Diff_NewFileHasOnlyAdditions()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var logger = content.GetProperty("files").EnumerateArray()
            .First(f => f.GetProperty("path").GetString()!.Contains("Logger.cs"));

        Assert.True(logger.GetProperty("additions").GetInt32() > 0);
        Assert.Equal(0, logger.GetProperty("deletions").GetInt32());
    }

    [SkippableFact]
    public async Task Diff_ContainsExpectedCodeFragment()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_diff", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var allLines = content.GetProperty("files").EnumerateArray()
            .SelectMany(f => f.GetProperty("hunks").EnumerateArray())
            .SelectMany(h => h.GetProperty("lines").EnumerateArray())
            .Select(l => l.GetProperty("text").GetString() ?? string.Empty);

        Assert.Contains(allLines, l => l.Contains(AdoTestExpectations.ExpectedCodeFragment));
    }
}
