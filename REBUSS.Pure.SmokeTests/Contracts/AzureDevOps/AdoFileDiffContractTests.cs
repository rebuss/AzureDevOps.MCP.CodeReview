using System.Text.Json;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoFileDiffContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoFileDiffContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsSingleFile()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = AdoTestExpectations.FilePaths[0] });
        var content = response.GetToolContent();

        Assert.Equal(1, content.GetProperty("files").GetArrayLength());
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsCorrectPath()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = AdoTestExpectations.FilePaths[0] });
        var content = response.GetToolContent();

        var path = content.GetProperty("files")[0].GetProperty("path").GetString();
        Assert.Equal(AdoTestExpectations.FilePaths[0], path?.TrimStart('/'));
    }

    [SkippableFact]
    public async Task FileDiff_HasHunks()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = AdoTestExpectations.FilePaths[0] });
        var content = response.GetToolContent();

        var hunks = content.GetProperty("files")[0].GetProperty("hunks");
        Assert.True(hunks.GetArrayLength() >= 1, "Expected at least one hunk.");
    }

    [SkippableFact]
    public async Task FileDiff_ForNonExistentFile_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = "nonexistent.cs" });

        Assert.True(response.IsToolError());
    }
}
