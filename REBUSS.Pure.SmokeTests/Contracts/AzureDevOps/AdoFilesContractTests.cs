using System.Text.Json;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoFilesContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoFilesContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task Files_ReturnsTotalFileCount()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        Assert.Equal(AdoTestExpectations.TotalFiles, content.GetProperty("totalFiles").GetInt32());
    }

    [SkippableFact]
    public async Task Files_ReturnsExpectedPaths()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var paths = content.GetProperty("files").EnumerateArray()
            .Select(f => f.GetProperty("path").GetString())
            .ToList();

        foreach (var expected in AdoTestExpectations.FilePaths)
        {
            Assert.Contains(expected, paths);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsCorrectStatuses()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var files = content.GetProperty("files").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("path").GetString()!,
                f => f.GetProperty("status").GetString()!);

        foreach (var (path, expectedStatus) in AdoTestExpectations.FileStatuses)
        {
            Assert.True(files.ContainsKey(path), $"File '{path}' not found in response.");
            Assert.Equal(expectedStatus, files[path]);
        }
    }

    [SkippableFact]
    public async Task Files_ReturnsAdditionsDeletions()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var files = content.GetProperty("files").EnumerateArray()
            .ToDictionary(
                f => f.GetProperty("path").GetString()!,
                f => f);

        // Azure DevOps iteration changes API does not provide line counts,
        // so additions/deletions are 0. Verify the fields exist and are non-negative.
        var calculator = files[AdoTestExpectations.FilePaths[0]];
        Assert.True(calculator.GetProperty("additions").GetInt32() >= 0,
            "Edited file additions should be non-negative.");
        Assert.True(calculator.GetProperty("deletions").GetInt32() >= 0,
            "Edited file deletions should be non-negative.");

        var logger = files[AdoTestExpectations.FilePaths[1]];
        Assert.True(logger.GetProperty("additions").GetInt32() >= 0,
            "New file additions should be non-negative.");
        Assert.True(logger.GetProperty("deletions").GetInt32() >= 0,
            "New file deletions should be non-negative.");
    }

    [SkippableFact]
    public async Task Files_ReturnsExtension()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            Assert.Equal(".cs", file.GetProperty("extension").GetString());
        }
    }

    [SkippableFact]
    public async Task Files_ClassifiesAsSourceFiles()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        var summary = content.GetProperty("summary");
        Assert.Equal(AdoTestExpectations.TotalFiles, summary.GetProperty("sourceFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("testFiles").GetInt32());
    }

    [SkippableFact]
    public async Task Files_ReviewPriority()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
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
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_pr_files", new { prNumber = TestSettings.AdoPrNumber });
        var content = response.GetToolContent();

        foreach (var file in content.GetProperty("files").EnumerateArray())
        {
            Assert.False(file.GetProperty("isBinary").GetBoolean());
            Assert.False(file.GetProperty("isGenerated").GetBoolean());
        }
    }
}
