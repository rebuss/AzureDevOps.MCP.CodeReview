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
        var content = response.GetToolText();

        Assert.Contains(AdoTestExpectations.OriginalCodeFragment, content);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsContentAtBranch()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = AdoTestExpectations.FilePaths[0], @ref = "test/fixture-pr" });
        var content = response.GetToolText();

        Assert.Contains(AdoTestExpectations.ModifiedCodeFragment, content);
    }

    [SkippableFact]
    public async Task FileContent_ReturnsMetadata()
    {
        Skip.IfNot(TestSettings.IsAdoConfigured, TestSettings.AdoSkipReason);

        var response = await _fixture.Server.SendToolCallAsync(
            "get_file_content_at_ref",
            new { path = AdoTestExpectations.FilePaths[0], @ref = "main" });
        var content = response.GetToolText();

        Assert.DoesNotContain("[binary file", content, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"=== {AdoTestExpectations.FilePaths[0]} @ main ===", content);
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
