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
        var content = response.GetAllToolText();

        Assert.Contains($"=== {AdoTestExpectations.FilePaths[0]}", content);
    }

    [SkippableFact]
    public async Task FileDiff_ReturnsCorrectPath()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = AdoTestExpectations.FilePaths[0] });
        var content = response.GetAllToolText();

        Assert.Contains(AdoTestExpectations.FilePaths[0], content);
    }

    [SkippableFact]
    public async Task FileDiff_HasHunks()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_diff",
            new { prNumber = TestSettings.AdoPrNumber, path = AdoTestExpectations.FilePaths[0] });
        var content = response.GetAllToolText();

        Assert.True(content.Contains("+", StringComparison.Ordinal) || content.Contains("-", StringComparison.Ordinal));
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
