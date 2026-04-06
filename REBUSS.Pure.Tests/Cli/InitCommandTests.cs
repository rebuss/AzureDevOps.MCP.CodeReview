using REBUSS.Pure.AzureDevOps.Configuration;
using REBUSS.Pure.Cli;
using REBUSS.Pure.GitHub.Configuration;

namespace REBUSS.Pure.Tests.Cli;

public class InitCommandTests
{
    /// <summary>
    /// Mock process runner that simulates Azure CLI not being installed (all commands fail).
    /// </summary>
    private static readonly Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>> AzCliNotInstalled =
        (_, _) => Task.FromResult((-1, string.Empty, "az: command not found"));

    /// <summary>
    /// Creates an InitCommand with a mock process runner (Azure CLI unavailable by default).
    /// The input reader defaults to "n" (decline install prompt).
    /// </summary>
    private static InitCommand CreateCommand(
        TextWriter output, string workingDirectory, string executablePath, string? pat = null,
        Func<string, CancellationToken, Task<(int ExitCode, string StdOut, string StdErr)>>? processRunner = null,
        TextReader? input = null, string? detectedProvider = null, bool isGlobal = false, string? ide = null,
        ILocalConfigStore? localConfigStore = null, IGitHubConfigStore? gitHubConfigStore = null,
        Func<List<McpConfigTarget>>? globalConfigTargetsResolver = null)
    {
        return new InitCommand(output, input ?? new StringReader("n"), workingDirectory, executablePath, pat,
            isGlobal, ide, detectedProvider ?? "AzureDevOps", processRunner ?? AzCliNotInstalled,
            localConfigStore, gitHubConfigStore, globalConfigTargetsResolver);
    }
    // -------------------------------------------------------------------------
    // Error cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ClearsBothProviderCaches_OnSuccess()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var azdoClearCalled = false;
            var githubClearCalled = false;

            var localConfigStore = new FakeLocalConfigStore(() => azdoClearCalled = true);
            var gitHubConfigStore = new FakeGitHubConfigStore(() => githubClearCalled = true);

            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                localConfigStore: localConfigStore, gitHubConfigStore: gitHubConfigStore);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(azdoClearCalled, "Azure DevOps config store Clear() was not called");
            Assert.True(githubClearCalled, "GitHub config store Clear() was not called");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotClearCaches_WhenNotInGitRepository()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var azdoClearCalled = false;
            var githubClearCalled = false;

            var localConfigStore = new FakeLocalConfigStore(() => azdoClearCalled = true);
            var gitHubConfigStore = new FakeGitHubConfigStore(() => githubClearCalled = true);

            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                localConfigStore: localConfigStore, gitHubConfigStore: gitHubConfigStore);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(1, exitCode);
            Assert.False(azdoClearCalled, "Azure DevOps config store Clear() should not be called on failure");
            Assert.False(githubClearCalled, "GitHub config store Clear() should not be called on failure");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Error cases
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenNotInGitRepository()
    {
        var output = new StringWriter();
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(1, exitCode);
            Assert.Contains("Not inside a Git repository", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Fallback: no IDE markers ? VS Code only
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreatesBothMcpJsons_WhenNoIdeMarkersPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, @"C:\tools\REBUSS.Pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var vsCodeConfig = Path.Combine(tempDir, ".vscode", "mcp.json");
            var vsConfig = Path.Combine(tempDir, ".vs", "mcp.json");
            Assert.True(File.Exists(vsCodeConfig));
            Assert.True(File.Exists(vsConfig));

            var content = await File.ReadAllTextAsync(vsCodeConfig);
            Assert.Contains("REBUSS.Pure", content);
            Assert.Contains("--repo", content);
            Assert.DoesNotContain("${workspaceFolder}", content);
            Assert.Contains(tempDir.Replace("\\", "\\\\"), content);
            Assert.Contains(@"C:\\tools\\REBUSS.Pure.exe", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WorksFromSubdirectory_FindsGitRoot()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var subDir = Path.Combine(tempDir, "src", "app");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(subDir);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, subDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // VS Code detection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreatesVsCodeMcpJson_WhenVsCodeDirExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.Contains("VS Code", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesVsCodeMcpJson_WhenCodeWorkspaceFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "project.code-workspace"), "{}");

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Visual Studio detection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreatesVsMcpJson_WhenVsDirExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.Contains("Visual Studio", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesVsMcpJson_WhenSlnFileExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "MySolution.sln"), "");

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Both IDEs detected
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreatesBothConfigs_WhenBothIdeMarkersPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));

            var outputText = output.ToString();
            Assert.Contains("VS Code", outputText);
            Assert.Contains("Visual Studio", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UpdatesExistingConfig_WritesOther_WhenOnlyOneExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"), "{}");

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            // The VS Code config should be updated (merged)
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            // The VS config should be freshly created
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));

            var outputText = output.ToString();
            Assert.Contains("Updated MCP configuration (VS Code)", outputText);
            Assert.Contains("Created MCP configuration (Visual Studio)", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // MergeConfigContent
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeConfigContent_UpsertRebussEntry_IntoEmptyServers()
    {
        var existing = "{\"servers\": {}}";

        var result = InitCommand.MergeConfigContent(existing, "exe", @"C:\repo");

        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
        Assert.Contains("C:\\\\repo", result);
    }

    [Fact]
    public void MergeConfigContent_PreservesOtherServers()
    {
        var existing = """
            {
              "servers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "exe", "C:\\\\repo");

        Assert.Contains("\"OtherTool\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
    }

    [Fact]
    public void MergeConfigContent_OverwritesExistingRebussEntry()
    {
        var existing = """
            {
              "servers": {
                "REBUSS.Pure": { "type": "stdio", "command": "old.exe", "args": ["--repo", "old"] }
              }
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "new.exe", @"C:\newrepo");

        Assert.Contains("\"new.exe\"", result);
        Assert.Contains("C:\\\\newrepo", result);
        Assert.DoesNotContain("old.exe", result);
        Assert.DoesNotContain("\"old\"", result);
    }

    [Fact]
    public void MergeConfigContent_IncludesPat_WhenProvided()
    {
        var existing = "{\"servers\": {}}";

        var result = InitCommand.MergeConfigContent(existing, "exe", @"C:\repo", "my-pat");

        Assert.Contains("\"--pat\"", result);
        Assert.Contains("\"my-pat\"", result);
    }

    [Fact]
    public void MergeConfigContent_FallsBackToBuildConfigContent_WhenInvalidJson()
    {
        var result = InitCommand.MergeConfigContent("not valid json !!!", "exe", @"C:\repo");

        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"--repo\"", result);
    }

    [Fact]
    public void MergeConfigContent_PreservesTopLevelProperties()
    {
        var existing = """
            {
              "inputs": [],
              "servers": {}
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "exe", @"C:\repo");

        Assert.Contains("\"inputs\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
    }

    [Fact]
    public async Task ExecuteAsync_MergesConfig_WhenCalledTwice()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var command1 = CreateCommand(new StringWriter(), tempDir, "rebuss-pure.exe");
            await command1.ExecuteAsync();

            var output2 = new StringWriter();
            var command2 = CreateCommand(output2, tempDir, "rebuss-pure-v2.exe");
            var exitCode = await command2.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.Contains("Updated", output2.ToString());

            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"));
            Assert.Contains("rebuss-pure-v2", content);
            // No duplicate REBUSS.Pure keys
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(content, "\"REBUSS\\.Pure\""));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ResolveConfigTargets unit tests
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveConfigTargets_ReturnsBoth_WhenNoMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir);

            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, t => t.IdeName == "VS Code");
            Assert.Contains(targets, t => t.IdeName == "Visual Studio");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_ReturnsVsOnly_WhenOnlyVsMarker()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir);

            Assert.Single(targets);
            Assert.Equal("Visual Studio", targets[0].IdeName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_ReturnsBoth_WhenBothMarkersPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir);

            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, t => t.IdeName == "VS Code");
            Assert.Contains(targets, t => t.IdeName == "Visual Studio");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // BuildConfigContent
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildConfigContent_ProducesValidJsonStructure()
    {
        var content = InitCommand.BuildConfigContent("C:\\\\tools\\\\REBUSS.Pure.exe", "C:\\\\repo\\\\myproject");

        Assert.Contains("\"REBUSS.Pure\"", content);
        Assert.Contains("\"stdio\"", content);
        Assert.Contains("\"--repo\"", content);
        Assert.Contains("\"C:\\\\repo\\\\myproject\"", content);
        Assert.DoesNotContain("${workspaceFolder}", content);
        Assert.DoesNotContain("--pat", content);
    }

    [Fact]
    public void BuildConfigContent_IncludesPat_WhenProvided()
    {
        var content = InitCommand.BuildConfigContent("C:\\\\tools\\\\REBUSS.Pure.exe", "C:\\\\repo", "my-secret-pat");

        Assert.Contains("\"--pat\"", content);
        Assert.Contains("\"my-secret-pat\"", content);
    }

    [Fact]
    public void BuildConfigContent_OmitsPat_WhenNullOrEmpty()
    {
        var contentNull  = InitCommand.BuildConfigContent("exe", "C:\\\\repo", null);
        var contentEmpty = InitCommand.BuildConfigContent("exe", "C:\\\\repo", "");
        var contentWhite = InitCommand.BuildConfigContent("exe", "C:\\\\repo", "   ");

        Assert.DoesNotContain("--pat", contentNull);
        Assert.DoesNotContain("--pat", contentEmpty);
        Assert.DoesNotContain("--pat", contentWhite);
    }

    // -------------------------------------------------------------------------
    // PAT integration
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_WithPat_IncludesPatInConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", "my-pat-value");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"));
            Assert.Contains("\"--pat\"", content);
            Assert.Contains("\"my-pat-value\"", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_WithoutPat_OmitsPatFromConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"));
            Assert.DoesNotContain("--pat", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MergeConfigContent_CarriesOverExistingPat_WhenNoPATProvided()
    {
        var existing = """
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "old.exe",
                  "args": ["--repo", "C:\\\\old", "--pat", "saved-pat"]
                }
              }
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "new.exe", @"C:\newrepo");

        Assert.Contains("\"--pat\"", result);
        Assert.Contains("\"saved-pat\"", result);
    }

    [Fact]
    public void MergeConfigContent_DoesNotDuplicatePat_WhenPatAlsoProvided()
    {
        var existing = """
            {
              "servers": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "old.exe",
                  "args": ["--repo", "C:\\\\old", "--pat", "old-pat"]
                }
              }
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "new.exe", @"C:\newrepo", "new-pat");

        Assert.Contains("\"new-pat\"", result);
        Assert.DoesNotContain("old-pat", result);
    }

    // -------------------------------------------------------------------------
    // Prompt files
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CopiesPromptFiles_ToGitHubPromptsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.prompt.md");
            var selfReviewPath = Path.Combine(tempDir, ".github", "prompts", "self-review.prompt.md");
            var createPrPath = Path.Combine(tempDir, ".github", "prompts", "create-pr.md");

            Assert.True(File.Exists(reviewPrPath), $"Expected prompt file at {reviewPrPath}");
            Assert.True(File.Exists(selfReviewPath), $"Expected prompt file at {selfReviewPath}");
            Assert.False(File.Exists(createPrPath), "create-pr.md should not be deployed yet");

            var reviewPrContent = await File.ReadAllTextAsync(reviewPrPath);
            Assert.Contains("Pull Request Code Review", reviewPrContent);
            Assert.Contains("REBUSS.Pure", reviewPrContent);

            var selfReviewContent = await File.ReadAllTextAsync(selfReviewPath);
            Assert.Contains("Self-Review", selfReviewContent);
            Assert.Contains("get_local_content", selfReviewContent);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCreateInstructionFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var instructionsDir = Path.Combine(tempDir, ".github", "instructions");
            Assert.False(Directory.Exists(instructionsDir),
                "init must not create .github/instructions/");

            var outputText = output.ToString();
            Assert.DoesNotContain("instruction file(s)", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesPromptFiles_WhenAlreadyExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var promptsDir = Path.Combine(tempDir, ".github", "prompts");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(promptsDir);

        var existingContent = "# My custom review prompt";
        await File.WriteAllTextAsync(Path.Combine(promptsDir, "review-pr.prompt.md"), existingContent);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            // Existing prompt file should be overwritten with embedded content
            var reviewPrContent = await File.ReadAllTextAsync(Path.Combine(promptsDir, "review-pr.prompt.md"));
            Assert.NotEqual(existingContent, reviewPrContent);
            Assert.Contains("Pull Request Code Review", reviewPrContent);

            var selfReviewPath = Path.Combine(promptsDir, "self-review.prompt.md");
            Assert.True(File.Exists(selfReviewPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DeletesLegacyPromptFiles_WhenPresent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var promptsDir = Path.Combine(tempDir, ".github", "prompts");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(promptsDir);

        var legacyReviewPr = Path.Combine(promptsDir, "review-pr.md");
        var legacySelfReview = Path.Combine(promptsDir, "self-review.md");
        await File.WriteAllTextAsync(legacyReviewPr, "# old review-pr");
        await File.WriteAllTextAsync(legacySelfReview, "# old self-review");

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(legacyReviewPr), "Legacy review-pr.md should be deleted");
            Assert.False(File.Exists(legacySelfReview), "Legacy self-review.md should be deleted");

            var outputText = output.ToString();
            Assert.Contains("Deleted legacy prompt file", outputText);
            Assert.Contains("review-pr.md", outputText);
            Assert.Contains("self-review.md", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_PreservesPreExistingInstructionFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var instructionsDir = Path.Combine(tempDir, ".github", "instructions");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(instructionsDir);

        var existingContent = "# My custom instruction";
        var existingPath = Path.Combine(instructionsDir, "review-pr.instructions.md");
        await File.WriteAllTextAsync(existingPath, existingContent);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            // init must not touch user instruction files
            Assert.Equal(existingContent, await File.ReadAllTextAsync(existingPath));
            Assert.False(File.Exists(Path.Combine(instructionsDir, "self-review.instructions.md")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OutputMentionsPromptCopy()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            await command.ExecuteAsync();

            var outputText = output.ToString();
            Assert.Contains("Copied", outputText);
            Assert.Contains("prompt file(s)", outputText);
            Assert.DoesNotContain("instruction file(s)", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CopiesPrompts_FromSubdirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var subDir = Path.Combine(tempDir, "src", "app");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(subDir);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, subDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.prompt.md");
            Assert.True(File.Exists(reviewPrPath));
            Assert.False(File.Exists(Path.Combine(subDir, ".github", "prompts", "review-pr.prompt.md")));

            Assert.False(Directory.Exists(Path.Combine(tempDir, ".github", "instructions")));
            Assert.False(Directory.Exists(Path.Combine(subDir, ".github", "instructions")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Claude Code detection
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_CreatesClaudeCodeMcpJson_WhenClaudeDirExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".claude"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var claudeConfig = Path.Combine(tempDir, ".mcp.json");
            Assert.True(File.Exists(claudeConfig), $"Expected Claude Code config at {claudeConfig}");
            Assert.Contains("Claude Code", output.ToString());

            var content = await File.ReadAllTextAsync(claudeConfig);
            Assert.Contains("\"mcpServers\"", content);
            Assert.Contains("\"REBUSS.Pure\"", content);
            Assert.DoesNotContain("\"servers\"", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesBackupFile_WhenOverwritingExistingConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".claude"));
        var claudeConfig = Path.Combine(tempDir, ".mcp.json");

        // Pre-existing config with an unrelated top-level key that must survive merge.
        var originalContent = """
            {
              "someUserSetting": "preserve me",
              "mcpServers": {
                "OtherServer": { "type": "stdio", "command": "other.exe" }
              }
            }
            """;
        await File.WriteAllTextAsync(claudeConfig, originalContent);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            // Backup file exists with the original content verbatim.
            var backupPath = claudeConfig + ".bak";
            Assert.True(File.Exists(backupPath), $"Expected backup file at {backupPath}");
            Assert.Equal(originalContent, await File.ReadAllTextAsync(backupPath));

            // Merged file preserves the unrelated key and the other server,
            // and adds our REBUSS.Pure entry.
            var mergedContent = await File.ReadAllTextAsync(claudeConfig);
            Assert.Contains("\"someUserSetting\"", mergedContent);
            Assert.Contains("\"OtherServer\"", mergedContent);
            Assert.Contains("\"REBUSS.Pure\"", mergedContent);

            // User saw the backup notice in the output.
            Assert.Contains("Backed up", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesClaudeCodeMcpJson_WhenClaudeMdExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        await File.WriteAllTextAsync(Path.Combine(tempDir, "CLAUDE.md"), "# Instructions");

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var claudeConfig = Path.Combine(tempDir, ".mcp.json");
            Assert.True(File.Exists(claudeConfig));

            var content = await File.ReadAllTextAsync(claudeConfig);
            Assert.Contains("\"mcpServers\"", content);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesClaudeCodeAndVsCode_WhenBothDetected()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".claude"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.True(File.Exists(Path.Combine(tempDir, ".mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));

            // VS Code uses "servers", Claude Code uses "mcpServers"
            var vsCodeContent = await File.ReadAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"));
            Assert.Contains("\"servers\"", vsCodeContent);

            var claudeContent = await File.ReadAllTextAsync(Path.Combine(tempDir, ".mcp.json"));
            Assert.Contains("\"mcpServers\"", claudeContent);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_DoesNotCreateClaudeCodeConfig_WhenNoClaudeMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesOnlyClaudeCodeConfig_WhenIdeIsClaude()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", ide: "claude");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
            Assert.Contains("Claude Code", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Claude Code: ResolveConfigTargets
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveConfigTargets_ReturnsClaudeCodeOnly_WhenIdeIsClaude()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir, "claude");

            Assert.Single(targets);
            Assert.Equal("Claude Code", targets[0].IdeName);
            Assert.Equal(tempDir, targets[0].Directory);
            Assert.Equal(Path.Combine(tempDir, ".mcp.json"), targets[0].ConfigPath);
            Assert.True(targets[0].UseMcpServersKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_IncludesClaudeCode_WhenClaudeDirExists()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".claude"));

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir);

            Assert.Contains(targets, t => t.IdeName == "Claude Code");
            Assert.Contains(targets, t => t.UseMcpServersKey);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_DoesNotIncludeClaudeCode_WhenNoClaudeMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir);

            Assert.DoesNotContain(targets, t => t.IdeName == "Claude Code");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DetectsClaudeCode_ReturnsTrueForClaudeDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".claude"));

        try
        {
            Assert.True(InitCommand.DetectsClaudeCode(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DetectsClaudeCode_ReturnsTrueForClaudeMd()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "CLAUDE.md"), "# Instructions");

        try
        {
            Assert.True(InitCommand.DetectsClaudeCode(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void DetectsClaudeCode_ReturnsFalseWhenNoMarkers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            Assert.False(InitCommand.DetectsClaudeCode(tempDir));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // BuildConfigContent with mcpServers key
    // -------------------------------------------------------------------------

    [Fact]
    public void BuildConfigContent_UsesMcpServersKey_WhenFlagIsTrue()
    {
        var content = InitCommand.BuildConfigContent("exe", "C:\\\\repo", null, useMcpServersKey: true);

        Assert.Contains("\"mcpServers\"", content);
        Assert.DoesNotContain("\"servers\"", content);
        Assert.Contains("\"REBUSS.Pure\"", content);
    }

    [Fact]
    public void BuildConfigContent_UsesServersKey_WhenFlagIsFalse()
    {
        var content = InitCommand.BuildConfigContent("exe", "C:\\\\repo", null, useMcpServersKey: false);

        Assert.Contains("\"servers\"", content);
        Assert.DoesNotContain("\"mcpServers\"", content);
    }

    // -------------------------------------------------------------------------
    // MergeConfigContent with mcpServers key
    // -------------------------------------------------------------------------

    [Fact]
    public void MergeConfigContent_UsesMcpServersKey_WhenFlagIsTrue()
    {
        var existing = "{\"mcpServers\": {}}";

        var result = InitCommand.MergeConfigContent(existing, "exe", @"C:\repo", useMcpServersKey: true);

        Assert.Contains("\"mcpServers\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
    }

    [Fact]
    public void MergeConfigContent_PreservesOtherServers_WithMcpServersKey()
    {
        var existing = """
            {
              "mcpServers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """;

        var result = InitCommand.MergeConfigContent(existing, "exe", @"C:\repo", useMcpServersKey: true);

        Assert.Contains("\"OtherTool\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
        Assert.Contains("\"mcpServers\"", result);
    }

    [Fact]
    public void MergeConfigContent_FallsBackWithMcpServersKey_WhenInvalidJson()
    {
        var result = InitCommand.MergeConfigContent("not valid json", "exe", @"C:\repo", useMcpServersKey: true);

        Assert.Contains("\"mcpServers\"", result);
        Assert.Contains("\"REBUSS.Pure\"", result);
    }

    // -------------------------------------------------------------------------
    // Claude Code: global mode
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveGlobalConfigTargets_IncludesClaudeCode()
    {
        var targets = InitCommand.ResolveGlobalConfigTargets();

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.IdeName == "Visual Studio (global)");
        Assert.Contains(targets, t => t.IdeName == "VS Code (global)");
        Assert.Contains(targets, t => t.IdeName == "Claude Code (global)");

        var claudeTarget = targets.First(t => t.IdeName == "Claude Code (global)");
        Assert.True(claudeTarget.UseMcpServersKey);

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Equal(Path.Combine(userHome, ".claude.json"), claudeTarget.ConfigPath);
    }

    [Fact]
    public async Task ExecuteAsync_Global_WritesClaudeCodeConfig()
    {
        using var ctx = new GlobalTestContext();
        var output = new StringWriter();
        var command = CreateCommand(output, ctx.RepoDir, "rebuss-pure.exe", isGlobal: true,
            globalConfigTargetsResolver: () => ctx.GlobalTargets);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(ctx.GlobalClaudeCodeConfig), $"Expected global Claude Code config at {ctx.GlobalClaudeCodeConfig}");

        var content = await File.ReadAllTextAsync(ctx.GlobalClaudeCodeConfig);
        Assert.Contains("\"mcpServers\"", content);
        Assert.Contains("\"REBUSS.Pure\"", content);
    }

// -------------------------------------------------------------------------
    // Azure CLI login during init
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_SkipsAzLogin_WhenPatProvided()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var processRunnerCalled = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (_, _) =>
        {
            processRunnerCalled = true;
            return Task.FromResult((0, "", ""));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", "my-pat", processRunner);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.False(processRunnerCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesExistingAzSession_WhenTokenAlreadyCached()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var expiresOn = DateTime.UtcNow.AddHours(1);
        var tokenJson = $"{{\"accessToken\":\"existing-token\",\"expiresOn\":\"{expiresOn:O}\"}}";
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token"))
                return Task.FromResult((0, tokenJson, ""));
            return Task.FromResult((-1, "", "should not be called"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", null, processRunner);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.Contains("Using existing login session", output.ToString());
            Assert.DoesNotContain("browser window", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsAzLogin_WhenNoExistingSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var callCount = 0;
        var expiresOn = DateTime.UtcNow.AddHours(1);
        var tokenJson = $"{{\"accessToken\":\"new-token\",\"expiresOn\":\"{expiresOn:O}\"}}";
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            callCount++;
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token") && callCount <= 2)
                return Task.FromResult((-1, "", "not logged in")); // first token call: no session
            if (args.Contains("login"))
                return Task.FromResult((0, "", "")); // login succeeds
            if (args.Contains("get-access-token"))
                return Task.FromResult((0, tokenJson, "")); // second token call: token acquired
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", null, processRunner);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("Azure CLI login successful", outputText);
            Assert.Contains("token acquired and cached", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ContinuesGracefully_WhenAzLoginFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("AUTHENTICATION NOT CONFIGURED", outputText);
            Assert.Contains("rebuss-pure init", outputText);
            Assert.Contains("appsettings.Local.json", outputText);
            Assert.Contains("PersonalAccessToken", outputText);
            Assert.Contains("rebuss-pure init --pat", outputText);
            Assert.Contains("dev.azure.com", outputText);
            // MCP config should still be created
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Azure CLI installation prompt
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_PromptsToInstallAzCli_WhenNotInstalled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var input = new StringReader("n");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", input: input);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("Azure CLI is not installed", outputText);
            Assert.Contains("install Azure CLI now", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallsAzCli_WhenUserConfirms()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var callLog = new List<string>();
        var expiresOn = DateTime.UtcNow.AddHours(1);
        var tokenJson = $"{{\"accessToken\":\"new-token\",\"expiresOn\":\"{expiresOn:O}\"}}";
        var azInstalled = false;

        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            callLog.Add(args);
            if (args == "--version" && !azInstalled)
                return Task.FromResult((-1, "", "not found"));
            if (args == "install-az-cli")
            {
                azInstalled = true;
                return Task.FromResult((0, "", ""));
            }
            if (args == "--version" && azInstalled)
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token") && callLog.Count(a => a.Contains("get-access-token")) <= 1)
                return Task.FromResult((-1, "", "not logged in"));
            if (args.Contains("login"))
                return Task.FromResult((0, "", ""));
            if (args.Contains("get-access-token"))
                return Task.FromResult((0, tokenJson, ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var input = new StringReader("y");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", processRunner: processRunner, input: input);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("Installing Azure CLI", outputText);
            Assert.Contains("Azure CLI installed successfully", outputText);
            Assert.Contains("Azure CLI login successful", outputText);
            Assert.Contains("install-az-cli", callLog);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShowsAuthBanner_WhenUserDeclinesInstall()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var input = new StringReader("n");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", input: input);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("AUTHENTICATION NOT CONFIGURED", outputText);
            // MCP config should still be created
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShowsManualInstallHint_WhenInstallFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((-1, "", "not found"));
            if (args == "install-az-cli")
                return Task.FromResult((-1, "", "winget not found"));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var input = new StringReader("y");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", processRunner: processRunner, input: input);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("installation failed", outputText);
            Assert.Contains("https://aka.ms/install-azure-cli", outputText);
            Assert.Contains("AUTHENTICATION NOT CONFIGURED", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SkipsInstallPrompt_WhenAzCliIsInstalled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var expiresOn = DateTime.UtcNow.AddHours(1);
        var tokenJson = $"{{\"accessToken\":\"token\",\"expiresOn\":\"{expiresOn:O}\"}}";
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token"))
                return Task.FromResult((0, tokenJson, ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", processRunner: processRunner);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.DoesNotContain("not installed", outputText);
            Assert.DoesNotContain("install Azure CLI", outputText);
            Assert.Contains("Using existing login session", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesAllowNoSubscriptions_WhenRunningAzLogin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var capturedLoginArgs = string.Empty;
        var expiresOn = DateTime.UtcNow.AddHours(1);
        var tokenJson = $"{{\"accessToken\":\"new-token\",\"expiresOn\":\"{expiresOn:O}\"}}";
        var callCount = 0;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            callCount++;
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token") && callCount <= 2)
                return Task.FromResult((-1, "", "not logged in"));
            if (args.Contains("login"))
            {
                capturedLoginArgs = args;
                return Task.FromResult((0, "", ""));
            }
            if (args.Contains("get-access-token"))
                return Task.FromResult((0, tokenJson, ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", null, processRunner);

            await command.ExecuteAsync();

            Assert.Contains("--allow-no-subscriptions", capturedLoginArgs);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesConfigAndPrompts_BeforeAzLogin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var configExistedDuringLogin = false;
        var promptsExistedDuringLogin = false;
        var instructionsDirCreatedDuringLogin = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token"))
            {
                configExistedDuringLogin = File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json"))
                    || File.Exists(Path.Combine(tempDir, ".vs", "mcp.json"));
                promptsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "prompts"));
                instructionsDirCreatedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "instructions"));
                return Task.FromResult((-1, "", "not logged in"));
            }
            if (args.Contains("login"))
                return Task.FromResult((-1, "", "login failed"));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", null, processRunner);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(configExistedDuringLogin, "MCP config should be written before az login is attempted");
            Assert.True(promptsExistedDuringLogin, "Prompt files should be copied before az login is attempted");
            Assert.False(instructionsDirCreatedDuringLogin, "init must not create .github/instructions/");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // GitHub CLI login during init
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_UsesGitHubFlow_WhenProviderIsGitHub()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "gh version 2.50.0", ""));
            if (args == "auth token")
                return Task.FromResult((0, "ghp_existing-token", ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                processRunner: processRunner, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.Contains("GitHub CLI: Using existing login session", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_RunsGhAuthLogin_WhenNoExistingGitHubSession()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var callCount = 0;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            callCount++;
            if (args == "--version")
                return Task.FromResult((0, "gh version 2.50.0", ""));
            if (args == "auth token" && callCount <= 2)
                return Task.FromResult((-1, "", "not logged in"));
            if (args == "auth login --web")
                return Task.FromResult((0, "", ""));
            if (args == "auth token")
                return Task.FromResult((0, "ghp_new-token", ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                processRunner: processRunner, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("GitHub CLI login successful", outputText);
            Assert.Contains("GitHub token acquired and cached", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShowsGitHubAuthBanner_WhenGhCliNotInstalled()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var input = new StringReader("n");
            var ghNotInstalled = new Func<string, CancellationToken, Task<(int, string, string)>>(
                (_, _) => Task.FromResult((-1, "", "gh: command not found")));
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                processRunner: ghNotInstalled, input: input, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("GitHub CLI is not installed", outputText);
            Assert.Contains("AUTHENTICATION NOT CONFIGURED", outputText);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_InstallsGhCli_WhenUserConfirms()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var callLog = new List<string>();
        var ghInstalled = false;

        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            callLog.Add(args);
            if (args == "--version" && !ghInstalled)
                return Task.FromResult((-1, "", "not found"));
            if (args == "install-gh-cli")
            {
                ghInstalled = true;
                return Task.FromResult((0, "", ""));
            }
            if (args == "--version" && ghInstalled)
                return Task.FromResult((0, "gh version 2.50.0", ""));
            if (args == "auth token" && callLog.Count(a => a == "auth token") <= 1)
                return Task.FromResult((-1, "", "not logged in"));
            if (args == "auth login --web")
                return Task.FromResult((0, "", ""));
            if (args == "auth token")
                return Task.FromResult((0, "ghp_new-token", ""));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var input = new StringReader("y");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                processRunner: processRunner, input: input, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("Installing GitHub CLI", outputText);
            Assert.Contains("GitHub CLI installed successfully", outputText);
            Assert.Contains("GitHub CLI login successful", outputText);
            Assert.Contains("install-gh-cli", callLog);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_SkipsGhLogin_WhenPatProvided_GitHubProvider()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var processRunnerCalled = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (_, _) =>
        {
            processRunnerCalled = true;
            return Task.FromResult((0, "", ""));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", "ghp_my-pat",
                processRunner, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.False(processRunnerCalled);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ShowsGitHubManualInstallHint_WhenInstallFails()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((-1, "", "not found"));
            if (args == "install-gh-cli")
                return Task.FromResult((-1, "", "winget not found"));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var input = new StringReader("y");
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe",
                processRunner: processRunner, input: input, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            var outputText = output.ToString();
            Assert.Contains("installation failed", outputText);
            Assert.Contains("https://cli.github.com/", outputText);
            Assert.Contains("AUTHENTICATION NOT CONFIGURED", outputText);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesConfigAndPrompts_BeforeGhLogin()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var configExistedDuringLogin = false;
        var promptsExistedDuringLogin = false;
        var instructionsDirCreatedDuringLogin = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "gh version 2.50.0", ""));
            if (args == "auth token")
            {
                configExistedDuringLogin = File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json"))
                    || File.Exists(Path.Combine(tempDir, ".vs", "mcp.json"));
                promptsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "prompts"));
                instructionsDirCreatedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "instructions"));
                return Task.FromResult((-1, "", "not logged in"));
            }
            if (args == "auth login --web")
                return Task.FromResult((-1, "", "login failed"));
            return Task.FromResult((-1, "", "unexpected"));
        };

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", null,
                processRunner, detectedProvider: "GitHub");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(configExistedDuringLogin, "MCP config should be written before gh login is attempted");
            Assert.True(promptsExistedDuringLogin, "Prompt files should be copied before gh login is attempted");
            Assert.False(instructionsDirCreatedDuringLogin, "init must not create .github/instructions/");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // Global mode (-g / --global)
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExecuteAsync_Global_WritesConfigToUserHome()
    {
        using var ctx = new GlobalTestContext();
        var output = new StringWriter();
        var command = CreateCommand(output, ctx.RepoDir, "rebuss-pure.exe", isGlobal: true,
            globalConfigTargetsResolver: () => ctx.GlobalTargets);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(0, exitCode);

        Assert.True(File.Exists(ctx.GlobalVsConfig), $"Expected global VS config at {ctx.GlobalVsConfig}");
        Assert.True(File.Exists(ctx.GlobalVsCodeConfig), $"Expected global VS Code config at {ctx.GlobalVsCodeConfig}");
        Assert.True(File.Exists(ctx.GlobalClaudeCodeConfig), $"Expected global Claude Code config at {ctx.GlobalClaudeCodeConfig}");

        var content = await File.ReadAllTextAsync(ctx.GlobalVsConfig);
        Assert.Contains("REBUSS.Pure", content);
        Assert.Contains("--repo", content);

        var outputText = output.ToString();
        Assert.Contains("global", outputText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_Global_DoesNotWriteLocalConfigs()
    {
        using var ctx = new GlobalTestContext();
        var output = new StringWriter();
        var command = CreateCommand(output, ctx.RepoDir, "rebuss-pure.exe", isGlobal: true,
            globalConfigTargetsResolver: () => ctx.GlobalTargets);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(Path.Combine(ctx.RepoDir, ".vscode", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(ctx.RepoDir, ".vs", "mcp.json")));
        Assert.False(File.Exists(Path.Combine(ctx.RepoDir, ".mcp.json")));
    }

    [Fact]
    public async Task ExecuteAsync_Global_StillCopiesPromptFiles()
    {
        using var ctx = new GlobalTestContext();
        var output = new StringWriter();
        var command = CreateCommand(output, ctx.RepoDir, "rebuss-pure.exe", isGlobal: true,
            globalConfigTargetsResolver: () => ctx.GlobalTargets);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(0, exitCode);

        var reviewPrPath = Path.Combine(ctx.RepoDir, ".github", "prompts", "review-pr.prompt.md");
        Assert.True(File.Exists(reviewPrPath), "Prompt files should still be copied in global mode");
    }

    [Fact]
    public async Task ExecuteAsync_Global_MergesExistingGlobalConfig()
    {
        using var ctx = new GlobalTestContext();

        var otherServerJson = """
            {
              "servers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """;

        Directory.CreateDirectory(ctx.GlobalDir);
        await File.WriteAllTextAsync(ctx.GlobalVsConfig, otherServerJson);

        var output = new StringWriter();
        var command = CreateCommand(output, ctx.RepoDir, "rebuss-pure.exe", isGlobal: true,
            globalConfigTargetsResolver: () => ctx.GlobalTargets);

        var exitCode = await command.ExecuteAsync();

        Assert.Equal(0, exitCode);

        var content = await File.ReadAllTextAsync(ctx.GlobalVsConfig);
        Assert.Contains("\"OtherTool\"", content);
        Assert.Contains("\"REBUSS.Pure\"", content);
        Assert.Contains("Updated", output.ToString());
    }

    [Fact]
    public void ResolveGlobalConfigTargets_ReturnsAllGlobalTargets()
    {
        var targets = InitCommand.ResolveGlobalConfigTargets();

        Assert.Equal(3, targets.Count);
        Assert.Contains(targets, t => t.IdeName == "Visual Studio (global)");
        Assert.Contains(targets, t => t.IdeName == "VS Code (global)");
        Assert.Contains(targets, t => t.IdeName == "Claude Code (global)");

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData  = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Contains(targets, t => t.ConfigPath == Path.Combine(userHome, ".mcp.json"));
        Assert.Contains(targets, t => t.ConfigPath == Path.Combine(appData, "Code", "User", "mcp.json"));
        Assert.Contains(targets, t => t.ConfigPath == Path.Combine(userHome, ".claude.json"));
    }

    // -------------------------------------------------------------------------
    // --ide option: explicit IDE targeting
    // -------------------------------------------------------------------------

    [Fact]
    public void ResolveConfigTargets_ReturnsVsCodeOnly_WhenIdeIsVscode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir, "vscode");

            Assert.Single(targets);
            Assert.Equal("VS Code", targets[0].IdeName);
            Assert.Contains(".vscode", targets[0].Directory);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_ReturnsVsOnly_WhenIdeIsVs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir, "vs");

            Assert.Single(targets);
            Assert.Equal("Visual Studio", targets[0].IdeName);
            Assert.Contains(".vs", targets[0].Directory);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_IsCaseInsensitive_ForIdeValue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var vscodeTargets = InitCommand.ResolveConfigTargets(tempDir, "VSCODE");
            Assert.Single(vscodeTargets);
            Assert.Equal("VS Code", vscodeTargets[0].IdeName);

            var vsTargets = InitCommand.ResolveConfigTargets(tempDir, "VS");
            Assert.Single(vsTargets);
            Assert.Equal("Visual Studio", vsTargets[0].IdeName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_FallsBackToAutoDetect_WhenIdeIsNull()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir, null);

            Assert.Equal(2, targets.Count);
            Assert.Contains(targets, t => t.IdeName == "VS Code");
            Assert.Contains(targets, t => t.IdeName == "Visual Studio");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveConfigTargets_IgnoresIdeMarkers_WhenIdeIsExplicit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".vs"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));

        try
        {
            var targets = InitCommand.ResolveConfigTargets(tempDir, "vscode");

            Assert.Single(targets);
            Assert.Equal("VS Code", targets[0].IdeName);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesOnlyVsCodeConfig_WhenIdeIsVscode()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", ide: "vscode");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
            Assert.Contains("VS Code", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CreatesOnlyVsConfig_WhenIdeIsVs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", ide: "vs");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.Contains("Visual Studio", output.ToString());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_UsesAutoDetection_WhenIdeIsNotSpecified()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(Path.Combine(tempDir, ".vscode"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Disposable context for global-mode tests: creates a temp Git repository and a
    /// separate temp directory simulating user-level VS / VS Code config locations.
    /// </summary>
    private sealed class GlobalTestContext : IDisposable
    {
        public string RepoDir { get; }
        public string GlobalDir { get; }
        public string GlobalVsConfig { get; }
        public string GlobalVsCodeConfig { get; }
        public string GlobalClaudeCodeConfig { get; }
        public List<McpConfigTarget> GlobalTargets { get; }

        public GlobalTestContext()
        {
            RepoDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            Directory.CreateDirectory(Path.Combine(RepoDir, ".git"));

            GlobalDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
            GlobalVsConfig = Path.Combine(GlobalDir, ".mcp.json");
            var globalVsCodeDir = Path.Combine(GlobalDir, "Code", "User");
            GlobalVsCodeConfig = Path.Combine(globalVsCodeDir, "mcp.json");
            GlobalClaudeCodeConfig = Path.Combine(GlobalDir, ".claude.json");
            GlobalTargets =
            [
                new McpConfigTarget("Visual Studio (global)", GlobalDir, GlobalVsConfig),
                new McpConfigTarget("VS Code (global)", globalVsCodeDir, GlobalVsCodeConfig),
                new McpConfigTarget("Claude Code (global)", GlobalDir, GlobalClaudeCodeConfig, UseMcpServersKey: true)
            ];
        }

        public void Dispose()
        {
            if (Directory.Exists(RepoDir))
                Directory.Delete(RepoDir, recursive: true);
            if (Directory.Exists(GlobalDir))
                Directory.Delete(GlobalDir, recursive: true);
        }
    }
}

file sealed class FakeLocalConfigStore(Action onClear) : ILocalConfigStore
{
    public CachedConfig? Load() => null;
    public void Save(CachedConfig config) { }
    public void Clear() => onClear();
}

file sealed class FakeGitHubConfigStore(Action onClear) : IGitHubConfigStore
{
    public GitHubCachedConfig? Load() => null;
    public void Save(GitHubCachedConfig config) { }
    public void Clear() => onClear();
}
