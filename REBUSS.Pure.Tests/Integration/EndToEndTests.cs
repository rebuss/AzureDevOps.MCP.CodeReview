using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;
using System.Text;
using System.Text.Json;

namespace REBUSS.Pure.Tests.Integration;

/// <summary>
/// End-to-end tests: JSON-RPC request → McpServer → real handler → mocked diff provider → JSON-RPC response.
/// </summary>
public class EndToEndTests
{
    private readonly IPullRequestDataProvider _diffProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();

    public EndToEndTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });
    }

    private McpServer BuildServer(Stream input, Stream output)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.None));
        services.AddSingleton(_diffProvider);
        services.AddSingleton<IResponsePacker, ResponsePacker>();
        services.AddSingleton(_budgetResolver);
        services.AddSingleton(_tokenEstimator);
        services.AddSingleton(_fileClassifier);
        services.AddSingleton<IMcpToolHandler, GetPullRequestDiffToolHandler>();
        services.AddSingleton(sp =>
            new McpServer(
                sp.GetRequiredService<ILogger<McpServer>>(),
                sp.GetRequiredService<IEnumerable<IMcpToolHandler>>(),
                input,
                output));

        return services.BuildServiceProvider().GetRequiredService<McpServer>();
    }

    private async Task<JsonDocument> SendAsync(string requestJson)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(requestJson + "\n"));
        using var output = new MemoryStream();

        var server = BuildServer(input, output);
        await server.RunAsync(CancellationToken.None);

        output.Position = 0;
        return JsonDocument.Parse(new StreamReader(output).ReadToEnd().Trim());
    }

    [Fact]
    public async Task FullPipeline_ReturnsStructuredJson_ByDefault()
    {
        _diffProvider
            .GetDiffAsync(42, Arg.Any<CancellationToken>())
            .Returns(new PullRequestDiff
            {
                Title = "Fix bug",
                Status = "active",
                SourceBranch = "feature/fix",
                TargetBranch = "main",
                Files = new List<FileChange>
                {
                    new()
                    {
                        Path = "/src/App.cs",
                        ChangeType = "edit",
                        Additions = 1,
                        Deletions = 1,
                        Hunks = new List<DiffHunk>
                        {
                            new()
                            {
                                OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 1,
                                Lines = new List<DiffLine>
                                {
                                    new() { Op = '-', Text = "old" },
                                    new() { Op = '+', Text = "new" }
                                }
                            }
                        }
                    }
                }
            });

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "e2e-1",
            method = "tools/call",
            @params = new { name = "get_pr_diff", arguments = new { prNumber = 42 } }
        });

        var doc = await SendAsync(request);
        var result = doc.RootElement.GetProperty("result");

        Assert.False(result.GetProperty("isError").GetBoolean());

        var innerJson = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        var structured = JsonDocument.Parse(innerJson).RootElement;

        Assert.Equal(42, structured.GetProperty("prNumber").GetInt32());
        Assert.True(structured.TryGetProperty("files", out var files));
        Assert.Equal(1, files.GetArrayLength());
        Assert.Equal("/src/App.cs", files[0].GetProperty("path").GetString());
        Assert.True(files[0].TryGetProperty("hunks", out var hunks));
        Assert.Equal(1, hunks.GetArrayLength());
        Assert.False(structured.TryGetProperty("title", out _));
    }

    [Fact]
    public async Task FullPipeline_PrNotFound_ReturnsToolError()
    {
        _diffProvider
            .GetDiffAsync(999, Arg.Any<CancellationToken>())
            .Returns<PullRequestDiff>(x => throw new PullRequestNotFoundException("PR 999 not found"));

        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "e2e-3",
            method = "tools/call",
            @params = new { name = "get_pr_diff", arguments = new { prNumber = 999 } }
        });

        var doc = await SendAsync(request);
        var result = doc.RootElement.GetProperty("result");

        Assert.True(result.GetProperty("isError").GetBoolean());
        Assert.Contains("not found", result.GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task FullPipeline_InitializeThenToolsList_ReturnsToolWithSchema()
    {
        var requests =
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "1", method = "initialize", @params = new { } }) + "\n" +
            JsonSerializer.Serialize(new { jsonrpc = "2.0", id = "2", method = "tools/list" }) + "\n";

        using var input = new MemoryStream(Encoding.UTF8.GetBytes(requests));
        using var output = new MemoryStream();

        var server = BuildServer(input, output);
        await server.RunAsync(CancellationToken.None);

        output.Position = 0;
        var lines = new StreamReader(output).ReadToEnd()
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(2, lines.Length);

        // Verify tools/list response has schema from the real handler
        var toolsDoc = JsonDocument.Parse(lines[1]);
        var tool = toolsDoc.RootElement.GetProperty("result").GetProperty("tools")[0];
        Assert.Equal("get_pr_diff", tool.GetProperty("name").GetString());

        var props = tool.GetProperty("inputSchema").GetProperty("properties");
        Assert.True(props.TryGetProperty("prNumber", out _));
        Assert.False(props.TryGetProperty("format", out _));
    }
}
