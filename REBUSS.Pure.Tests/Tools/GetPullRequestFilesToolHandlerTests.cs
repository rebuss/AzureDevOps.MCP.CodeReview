using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.ResponsePacking;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestFilesToolHandlerTests
{
    private readonly IPullRequestDataProvider _filesProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IPageReferenceCodec _pageReferenceCodec = Substitute.For<IPageReferenceCodec>();
    private readonly GetPullRequestFilesToolHandler _handler;

    private static readonly PullRequestFiles SampleFiles = new()
    {
        Files = new List<PullRequestFileInfo>
        {
            new()
            {
                Path = "src/Service.cs", Status = "modified",
                Additions = 10, Deletions = 3, Changes = 13,
                Extension = ".cs", IsBinary = false, IsGenerated = false,
                IsTestFile = false, ReviewPriority = "high"
            },
            new()
            {
                Path = "tests/ServiceTests.cs", Status = "modified",
                Additions = 5, Deletions = 1, Changes = 6,
                Extension = ".cs", IsBinary = false, IsGenerated = false,
                IsTestFile = true, ReviewPriority = "medium"
            }
        },
        Summary = new PullRequestFilesSummary
        {
            SourceFiles = 1, TestFiles = 1, ConfigFiles = 0,
            DocsFiles = 0, BinaryFiles = 0, GeneratedFiles = 0,
            HighPriorityFiles = 1
        }
    };

    public GetPullRequestFilesToolHandlerTests()
    {
        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.Estimate(Arg.Any<string>(), Arg.Any<int>())
            .Returns(new TokenEstimationResult(100, 0.07, true));
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source, Extension = ".cs", IsBinary = false, IsGenerated = false, IsTestFile = false, ReviewPriority = "high" });

        _handler = new GetPullRequestFilesToolHandler(
            _filesProvider,
            new ResponsePacker(NullLogger<ResponsePacker>.Instance),
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            _pageReferenceCodec,
            NullLogger<GetPullRequestFilesToolHandler>.Instance);
    }

    // --- Happy path ---

    [Fact]
    public async Task ExecuteAsync_ReturnsStructuredJson()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var result = await _handler.ExecuteAsync(CreateArgs(42));

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        var root = doc.RootElement;

        Assert.Equal(42, root.GetProperty("prNumber").GetInt32());
        Assert.Equal(2, root.GetProperty("totalFiles").GetInt32());
        Assert.Equal(2, root.GetProperty("files").GetArrayLength());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectFileProperties()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var result = await _handler.ExecuteAsync(CreateArgs(42));
        var doc = JsonDocument.Parse(result.Content[0].Text);
        var firstFile = doc.RootElement.GetProperty("files")[0];

        Assert.Equal("src/Service.cs", firstFile.GetProperty("path").GetString());
        Assert.Equal("modified", firstFile.GetProperty("status").GetString());
        Assert.Equal(10, firstFile.GetProperty("additions").GetInt32());
        Assert.Equal(3, firstFile.GetProperty("deletions").GetInt32());
        Assert.Equal(13, firstFile.GetProperty("changes").GetInt32());
        Assert.Equal(".cs", firstFile.GetProperty("extension").GetString());
        Assert.False(firstFile.GetProperty("isBinary").GetBoolean());
        Assert.False(firstFile.GetProperty("isGenerated").GetBoolean());
        Assert.False(firstFile.GetProperty("isTestFile").GetBoolean());
        Assert.Equal("high", firstFile.GetProperty("reviewPriority").GetString());
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsCorrectSummary()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var result = await _handler.ExecuteAsync(CreateArgs(42));
        var doc = JsonDocument.Parse(result.Content[0].Text);
        var summary = doc.RootElement.GetProperty("summary");

        Assert.Equal(1, summary.GetProperty("sourceFiles").GetInt32());
        Assert.Equal(1, summary.GetProperty("testFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("configFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("docsFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("binaryFiles").GetInt32());
        Assert.Equal(0, summary.GetProperty("generatedFiles").GetInt32());
        Assert.Equal(1, summary.GetProperty("highPriorityFiles").GetInt32());
    }

    // --- Validation errors ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenArgumentsNull()
    {
        var result = await _handler.ExecuteAsync(null);

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberMissing()
    {
        var result = await _handler.ExecuteAsync(new Dictionary<string, object>());

        Assert.True(result.IsError);
        Assert.Contains("Missing required parameter", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNotInteger()
    {
        var args = new Dictionary<string, object> { ["prNumber"] = "not-a-number" };
        var result = await _handler.ExecuteAsync(args);

        Assert.True(result.IsError);
        Assert.Contains("must be an integer", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberZero()
    {
        var result = await _handler.ExecuteAsync(CreateArgs(0));

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPrNumberNegative()
    {
        var result = await _handler.ExecuteAsync(CreateArgs(-1));

        Assert.True(result.IsError);
        Assert.Contains("greater than 0", result.Content[0].Text);
    }

    // --- JsonElement input (real MCP scenario) ---

    [Fact]
    public async Task ExecuteAsync_HandlesPrNumberAsJsonElement()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var json = JsonSerializer.Deserialize<Dictionary<string, object>>(
            """{"prNumber": 42}""")!;
        var result = await _handler.ExecuteAsync(json);

        Assert.False(result.IsError);
    }

    // --- Provider exceptions ---

    [Fact]
    public async Task ExecuteAsync_ReturnsError_WhenPullRequestNotFound()
    {
        _filesProvider.GetFilesAsync(999, Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR #999 not found"));

        var result = await _handler.ExecuteAsync(CreateArgs(999));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content[0].Text);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsError_OnUnexpectedException()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Something broke"));

        var result = await _handler.ExecuteAsync(CreateArgs(42));

        Assert.True(result.IsError);
        Assert.Contains("Something broke", result.Content[0].Text);
    }

    // --- Tool definition ---

    [Fact]
    public void ToolName_IsGetPrFiles()
    {
        Assert.Equal("get_pr_files", _handler.ToolName);
    }

    [Fact]
    public void GetToolDefinition_HasCorrectSchema()
    {
        var tool = _handler.GetToolDefinition();

        Assert.Equal("get_pr_files", tool.Name);
        Assert.Contains("prNumber", tool.InputSchema.Properties.Keys);
        Assert.Equal("integer", tool.InputSchema.Properties["prNumber"].Type);
        Assert.Empty(tool.InputSchema.Required!); // prNumber optional per Q17/Q22 when pageReference used
        Assert.Contains("modelName", tool.InputSchema.Properties.Keys);
        Assert.Contains("maxTokens", tool.InputSchema.Properties.Keys);
        Assert.Contains("pageReference", tool.InputSchema.Properties.Keys);
        Assert.Contains("pageNumber", tool.InputSchema.Properties.Keys);
    }

    // --- Packing integration ---

    [Fact]
    public async Task ExecuteAsync_IncludesManifest_InResponse()
    {
        _filesProvider.GetFilesAsync(42, Arg.Any<CancellationToken>()).Returns(SampleFiles);

        var result = await _handler.ExecuteAsync(CreateArgs(42));

        Assert.False(result.IsError);
        var doc = JsonDocument.Parse(result.Content[0].Text);
        Assert.True(doc.RootElement.TryGetProperty("manifest", out var manifest));
        Assert.True(manifest.TryGetProperty("summary", out var summary));
        Assert.Equal(2, summary.GetProperty("totalItems").GetInt32());
    }

    // --- Helpers ---

    private static Dictionary<string, object> CreateArgs(int prNumber)
    {
        return new Dictionary<string, object> { ["prNumber"] = prNumber };
    }
}
