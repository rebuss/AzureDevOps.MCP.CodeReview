using REBUSS.Pure.Cli;

namespace REBUSS.Pure.Tests.Cli;

public class InitCommandTests
{
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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, @"C:\tools\REBUSS.Pure.exe");

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
            var command = new InitCommand(output, subDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command1 = new InitCommand(new StringWriter(), tempDir, "rebuss-pure.exe");
            await command1.ExecuteAsync();

            var output2 = new StringWriter();
            var command2 = new InitCommand(output2, tempDir, "rebuss-pure-v2.exe");
            var exitCode = await command2.ExecuteAsync();

            Assert.Equal(0, exitCode);
            Assert.Contains("Updated", output2.ToString());

            var content = await File.ReadAllTextAsync(Path.Combine(tempDir, ".vscode", "mcp.json"));
            Assert.Contains("rebuss-pure-v2", content);
            // No duplicate REBUSS.Pure keys
            Assert.Equal(1, System.Text.RegularExpressions.Regex.Matches(content, "\"REBUSS\\.Pure\"").Count);
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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe", "my-pat-value");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.prompt.md");
            var selfReviewPath = Path.Combine(tempDir, ".github", "prompts", "self-review.prompt.md");

            Assert.True(File.Exists(reviewPrPath), $"Expected prompt file at {reviewPrPath}");
            Assert.True(File.Exists(selfReviewPath), $"Expected prompt file at {selfReviewPath}");

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
    public async Task ExecuteAsync_SkipsPromptFiles_WhenAlreadyExist()
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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrContent = await File.ReadAllTextAsync(Path.Combine(promptsDir, "review-pr.prompt.md"));
            Assert.Equal(existingContent, reviewPrContent);

            var selfReviewPath = Path.Combine(promptsDir, "self-review.prompt.md");
            Assert.True(File.Exists(selfReviewPath));

            Assert.Contains("already exists, skipping", output.ToString());
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
            var command = new InitCommand(output, tempDir, "rebuss-pure.exe");

            await command.ExecuteAsync();

            var outputText = output.ToString();
            Assert.Contains("Copied", outputText);
            Assert.Contains("prompt file(s)", outputText);
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
            var command = new InitCommand(output, subDir, "rebuss-pure.exe");

            var exitCode = await command.ExecuteAsync();

            Assert.Equal(0, exitCode);

            var reviewPrPath = Path.Combine(tempDir, ".github", "prompts", "review-pr.prompt.md");
            Assert.True(File.Exists(reviewPrPath));
            Assert.False(File.Exists(Path.Combine(subDir, ".github", "prompts", "review-pr.prompt.md")));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}

