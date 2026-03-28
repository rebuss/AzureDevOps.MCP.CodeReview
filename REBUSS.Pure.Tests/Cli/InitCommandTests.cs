using REBUSS.Pure.Cli;

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
        TextReader? input = null, string? detectedProvider = null, bool isGlobal = false)
    {
        return new InitCommand(output, input ?? new StringReader("n"), workingDirectory, executablePath, pat,
            isGlobal, detectedProvider ?? "AzureDevOps", processRunner ?? AzCliNotInstalled);
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

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.md");
            var selfReviewPath = Path.Combine(tempDir, ".github", "prompts", "self-review.md");
            var createPrPath = Path.Combine(tempDir, ".github", "prompts", "create-pr.md");

            Assert.True(File.Exists(reviewPrPath), $"Expected prompt file at {reviewPrPath}");
            Assert.True(File.Exists(selfReviewPath), $"Expected prompt file at {selfReviewPath}");
            Assert.False(File.Exists(createPrPath), "create-pr.md should not be deployed yet");

            var reviewPrContent = await File.ReadAllTextAsync(reviewPrPath);
            Assert.Contains("Pull Request Code Review", reviewPrContent);
            Assert.Contains("REBUSS.Pure", reviewPrContent);

            var selfReviewContent = await File.ReadAllTextAsync(selfReviewPath);
            Assert.Contains("Self-Review", selfReviewContent);
            Assert.Contains("get_local_files", selfReviewContent);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_CopiesInstructionFiles_ToGitHubInstructionsDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrInstr = Path.Combine(tempDir, ".github", "instructions", "review-pr.instructions.md");
            var selfReviewInstr = Path.Combine(tempDir, ".github", "instructions", "self-review.instructions.md");
            var createPrInstr = Path.Combine(tempDir, ".github", "instructions", "create-pr.instructions.md");

            Assert.True(File.Exists(reviewPrInstr), $"Expected instruction file at {reviewPrInstr}");
            Assert.True(File.Exists(selfReviewInstr), $"Expected instruction file at {selfReviewInstr}");
            Assert.False(File.Exists(createPrInstr), "create-pr.instructions.md should not be deployed yet");

            var reviewPrContent = await File.ReadAllTextAsync(reviewPrInstr);
            Assert.Contains("Pull Request Code Review", reviewPrContent);

            var selfReviewContent = await File.ReadAllTextAsync(selfReviewInstr);
            Assert.Contains("Self-Review", selfReviewContent);

            var outputText = output.ToString();
            Assert.Contains("instruction file(s)", outputText);
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
        await File.WriteAllTextAsync(Path.Combine(promptsDir, "review-pr.md"), existingContent);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            // Existing prompt file should be overwritten with embedded content
            var reviewPrContent = await File.ReadAllTextAsync(Path.Combine(promptsDir, "review-pr.md"));
            Assert.NotEqual(existingContent, reviewPrContent);
            Assert.Contains("Pull Request Code Review", reviewPrContent);

            var selfReviewPath = Path.Combine(promptsDir, "self-review.md");
            Assert.True(File.Exists(selfReviewPath));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OverwritesInstructionFiles_WhenAlreadyExist()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var instructionsDir = Path.Combine(tempDir, ".github", "instructions");
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));
        Directory.CreateDirectory(instructionsDir);

        var existingContent = "# My custom instruction";
        await File.WriteAllTextAsync(Path.Combine(instructionsDir, "review-pr.instructions.md"), existingContent);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            // Existing instruction file should be overwritten with embedded content
            var reviewPrContent = await File.ReadAllTextAsync(
                Path.Combine(instructionsDir, "review-pr.instructions.md"));
            Assert.NotEqual(existingContent, reviewPrContent);
            Assert.Contains("Pull Request Code Review", reviewPrContent);

            // Other instruction file should also be created
            var selfReviewInstr = Path.Combine(instructionsDir, "self-review.instructions.md");
            Assert.True(File.Exists(selfReviewInstr));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_OutputMentionsPromptAndInstructionCopy()
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
            Assert.Contains("instruction file(s)", outputText);
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

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.md");
            Assert.True(File.Exists(reviewPrPath));
            Assert.False(File.Exists(Path.Combine(subDir, ".github", "prompts", "review-pr.md")));

            var reviewPrInstr = Path.Combine(tempDir, ".github", "instructions", "review-pr.instructions.md");
            Assert.True(File.Exists(reviewPrInstr));
            Assert.False(File.Exists(Path.Combine(subDir, ".github", "instructions", "review-pr.instructions.md")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // -------------------------------------------------------------------------
    // ToInstructionsFileName
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData("review-pr.md", "review-pr.instructions.md")]
    [InlineData("self-review.md", "self-review.instructions.md")]
    [InlineData("create-pr.md", "create-pr.instructions.md")]
    public void ToInstructionsFileName_ConvertsCorrectly(string input, string expected)
    {
        var result = InitCommand.ToInstructionsFileName(input);
        Assert.Equal(expected, result);
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
        var instructionsExistedDuringLogin = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "azure-cli 2.60.0", ""));
            if (args.Contains("get-access-token"))
            {
                configExistedDuringLogin = File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json"))
                    || File.Exists(Path.Combine(tempDir, ".vs", "mcp.json"));
                promptsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "prompts"));
                instructionsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "instructions"));
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
            Assert.True(instructionsExistedDuringLogin, "Instruction files should be copied before az login is attempted");
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
        var instructionsExistedDuringLogin = false;
        Func<string, CancellationToken, Task<(int, string, string)>> processRunner = (args, _) =>
        {
            if (args == "--version")
                return Task.FromResult((0, "gh version 2.50.0", ""));
            if (args == "auth token")
            {
                configExistedDuringLogin = File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json"))
                    || File.Exists(Path.Combine(tempDir, ".vs", "mcp.json"));
                promptsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "prompts"));
                instructionsExistedDuringLogin = Directory.Exists(Path.Combine(tempDir, ".github", "instructions"));
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
            Assert.True(instructionsExistedDuringLogin, "Instruction files should be copied before gh login is attempted");
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
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", isGlobal: true);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var globalVsConfig = Path.Combine(userHome, ".vs", "mcp.json");
            var globalVsCodeConfig = Path.Combine(userHome, ".vscode", "mcp.json");

            Assert.True(File.Exists(globalVsConfig), $"Expected global VS config at {globalVsConfig}");
            Assert.True(File.Exists(globalVsCodeConfig), $"Expected global VS Code config at {globalVsCodeConfig}");

            var content = await File.ReadAllTextAsync(globalVsConfig);
            Assert.Contains("REBUSS.Pure", content);
            Assert.Contains("--repo", content);

            var outputText = output.ToString();
            Assert.Contains("global", outputText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Global_DoesNotWriteLocalConfigs()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", isGlobal: true);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.False(File.Exists(Path.Combine(tempDir, ".vscode", "mcp.json")));
            Assert.False(File.Exists(Path.Combine(tempDir, ".vs", "mcp.json")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Global_StillCopiesPromptFiles()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", isGlobal: true);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.md");
            Assert.True(File.Exists(reviewPrPath), "Prompt files should still be copied in global mode");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Global_MergesExistingGlobalConfig()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(tempDir, ".git"));

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var globalVsDir = Path.Combine(userHome, ".vs");
        var globalVsConfig = Path.Combine(globalVsDir, "mcp.json");
        Directory.CreateDirectory(globalVsDir);

        string? existingContent = null;
        bool configExisted = File.Exists(globalVsConfig);
        if (configExisted)
            existingContent = await File.ReadAllTextAsync(globalVsConfig);

        var otherServerJson = """
            {
              "servers": {
                "OtherTool": { "type": "stdio", "command": "other.exe", "args": [] }
              }
            }
            """;
        await File.WriteAllTextAsync(globalVsConfig, otherServerJson);

        try
        {
            var output = new StringWriter();
            var command = CreateCommand(output, tempDir, "rebuss-pure.exe", isGlobal: true);

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var content = await File.ReadAllTextAsync(globalVsConfig);
            Assert.Contains("\"OtherTool\"", content);
            Assert.Contains("\"REBUSS.Pure\"", content);
            Assert.Contains("Updated", output.ToString());
        }
        finally
        {
            // Restore original global config
            if (configExisted && existingContent is not null)
                await File.WriteAllTextAsync(globalVsConfig, existingContent);
            else if (!configExisted && File.Exists(globalVsConfig))
                File.Delete(globalVsConfig);

            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void ResolveGlobalConfigTargets_ReturnsBothGlobalTargets()
    {
        var targets = InitCommand.ResolveGlobalConfigTargets();

        Assert.Equal(2, targets.Count);
        Assert.Contains(targets, t => t.IdeName == "Visual Studio (global)");
        Assert.Contains(targets, t => t.IdeName == "VS Code (global)");

        var userHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Assert.Contains(targets, t => t.ConfigPath == Path.Combine(userHome, ".vs", "mcp.json"));
        Assert.Contains(targets, t => t.ConfigPath == Path.Combine(userHome, ".vscode", "mcp.json"));
    }
}

