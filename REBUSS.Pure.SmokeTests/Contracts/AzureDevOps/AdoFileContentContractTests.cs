using System.Text.Json;
using REBUSS.Pure.SmokeTests.Expectations;
using REBUSS.Pure.SmokeTests.Infrastructure;

namespace REBUSS.Pure.SmokeTests.Contracts.AzureDevOps;

[Collection("AdoContract")]
[Trait("Category", "ContractAdo")]
public class AdoFileContentContractTests
{
    private readonly AdoMcpProcessFixture _fixture;

    public AdoFileContentContractTests(AdoMcpProcessFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task FileContent_ReturnsContentAtMain()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = AdoTestExpectations.FilePaths[0], @ref = "main" });
        var content = response.GetToolContent();

        var text = content.GetProperty("content").GetString()!;
        Assert.Contains(AdoTestExpectations.OriginalCodeFragment, text);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsContentAtBranch()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = AdoTestExpectations.FilePaths[0], @ref = "test/fixture-pr" });
        var content = response.GetToolContent();

        var text = content.GetProperty("content").GetString()!;
        Assert.Contains(AdoTestExpectations.ModifiedCodeFragment, text);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsMetadata()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = AdoTestExpectations.FilePaths[0], @ref = "main" });
        var content = response.GetToolContent();

        Assert.False(content.GetProperty("isBinary").GetBoolean());
        Assert.True(content.GetProperty("size").GetInt32() > 0);
    }

    [SkippableFact]
    public async Task FileContent_ForNonExistentFile_ReturnsError()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = "nonexistent.cs", @ref = "main" });

        Assert.True(response.IsToolError());
    }
}
