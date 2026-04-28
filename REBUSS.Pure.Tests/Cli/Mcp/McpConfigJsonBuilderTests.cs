using REBUSS.Pure.Cli.Mcp;

namespace REBUSS.Pure.Tests.Cli.Mcp;

/// <summary>
/// Step 3 — focused unit tests for the extracted <see cref="McpConfigJsonBuilder"/>.
/// Cases mirror the contract spec in <c>Files/Refactor.md</c> step 3 and provide
/// defense in depth alongside the existing <c>InitCommandTests</c> regression suite.
/// </summary>
public class McpConfigJsonBuilderTests
{
    [Fact]
    public void Build_WithoutPatOrAgent_EmitsServersKey_AndOnlyRepoArg()
    {
        var content = McpConfigJsonBuilder.Build("exe", @"C:\\repo");

        Assert.Contains("\"servers\"", content);
        Assert.DoesNotContain("\"mcpServers\"", content);
        Assert.Contains("\"--repo\"", content);
        Assert.Contains("\"C:\\\\repo\"", content);
        Assert.DoesNotContain("--pat", content);
        Assert.DoesNotContain("--agent", content);
    }

    [Fact]
    public void Build_WithPatAndCopilotAgent_IncludesBothArgs()
    {
        var content = McpConfigJsonBuilder.Build("exe", @"C:\\repo", "secret-pat", agent: "copilot");

        Assert.Contains("\"--pat\"", content);
        Assert.Contains("\"secret-pat\"", content);
        Assert.Contains("\"--agent\"", content);
        Assert.Contains("\"copilot\"", content);
    }

    [Fact]
    public void Build_WithUseMcpServersKey_EmitsMcpServersTopLevelKey()
    {
        var content = McpConfigJsonBuilder.Build("exe", @"C:\\repo", null, useMcpServersKey: true);

        Assert.Contains("\"mcpServers\"", content);
        Assert.DoesNotContain("\"servers\"", content);
    }

    [Fact]
    public void Merge_WithExistingOtherServer_PreservesOtherEntry_AndWritesRebussBlock()
    {
        var existing = """
            {
              "servers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """;

        var result = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo");

        Assert.Contains("\"OtherTool\"", result);
        Assert.Contains("\"other.exe\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
    }

    [Fact]
    public void Merge_WhenExistingHasPat_AndCallerPassesNullPat_CarriesExistingPatOver()
    {
        var existing = """
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "old.exe",
                  "args": ["--repo", "old", "--pat", "carried-over-pat"]
                }
              }
            }
            """;

        var result = McpConfigJsonBuilder.Merge(existing, "new.exe", @"C:\newrepo", pat: null);

        Assert.Contains("\"--pat\"", result);
        Assert.Contains("\"carried-over-pat\"", result);
        Assert.Contains("\"new.exe\"", result);
    }

    [Fact]
    public void Merge_WhenCallerPassesNewPat_NewPatWins_OverExistingPat()
    {
        var existing = """
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "old.exe",
                  "args": ["--repo", "old", "--pat", "old-pat"]
                }
              }
            }
            """;

        var result = McpConfigJsonBuilder.Merge(existing, "new.exe", @"C:\newrepo", pat: "new-pat");

        Assert.Contains("\"new-pat\"", result);
        Assert.DoesNotContain("\"old-pat\"", result);
    }

    [Fact]
    public void Merge_WhenInputIsInvalidJson_FallsBackToBuildShape()
    {
        var result = McpConfigJsonBuilder.Merge("not valid json !!!", "exe", @"C:\repo");

        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
        // Build path uses backslash-escaped paths via interpolation
        Assert.Contains("C:\\\\repo", result);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"hello\"")]
    [InlineData("42")]
    [InlineData("true")]
    public void Merge_WhenInputIsValidJsonButNotAnObject_FallsBackToBuildShape(string existing)
    {
        // Regression: JsonDocument.Parse accepts arrays / nulls / scalars, but root.EnumerateObject()
        // throws InvalidOperationException (not JsonException) on those. Without the explicit
        // ValueKind guard the exception escapes the catch (JsonException) block and crashes init.
        var result = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo");

        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
        Assert.Contains("C:\\\\repo", result);
    }

    [Fact]
    public void Merge_WhenServersKeyHoldsNonObjectValue_OmitsExistingServersBlock_AndStillUpsertsRebussEntry()
    {
        // E.g. a hand-edit broke the shape: `"servers"` exists but holds an array, not an object.
        // Previously this would throw InvalidOperationException from EnumerateObject on `serversEl`;
        // the guard now skips the inner copy so the merged document still has a fresh REBUSS.Pure entry.
        var existing = "{\"servers\": [], \"inputs\": []}";

        var result = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo");

        // Top-level keys other than `servers` are preserved verbatim
        Assert.Contains("\"inputs\"", result);
        // REBUSS.Pure entry is written into a fresh `servers` object
        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
    }

    [Fact]
    public void Merge_WithBlankAgent_OmitsAgentArg_NonBlankAgentInsertsIt()
    {
        var existing = "{\"servers\": {}}";

        var withoutAgent = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo", pat: null, useMcpServersKey: false, agent: null);
        var withWhitespace = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo", pat: null, useMcpServersKey: false, agent: "   ");
        var withClaude = McpConfigJsonBuilder.Merge(existing, "exe", @"C:\repo", pat: null, useMcpServersKey: false, agent: "claude");

        Assert.DoesNotContain("--agent", withoutAgent);
        Assert.DoesNotContain("--agent", withWhitespace);
        Assert.Contains("\"--agent\"", withClaude);
        Assert.Contains("\"claude\"", withClaude);
    }
}
