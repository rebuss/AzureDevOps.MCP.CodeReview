using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure.Tests.Tools;

public class GetPullRequestMetadataToolHandlerTests
{
    private readonly IPullRequestDataProvider _dataProvider = Substitute.For<IPullRequestDataProvider>();
    private readonly IContextBudgetResolver _budgetResolver = Substitute.For<IContextBudgetResolver>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();
    private readonly IPullRequestDiffCache _diffCache = Substitute.For<IPullRequestDiffCache>();
    private readonly GetPullRequestMetadataToolHandler _handler;

    private static readonly FullPullRequestMetadata SampleMetadata = new()
    {
        PullRequestId = 42,
        CodeReviewId = 100,
        Title = "Fix the bug",
        Description = "Some description",
        Status = "active",
        IsDraft = false,
        AuthorLogin = "user1",
        AuthorDisplayName = "User One",
        CreatedDate = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc),
        ClosedDate = null,
        SourceBranch = "feature/x",
        TargetBranch = "main",
        LastMergeSourceCommitId = "abc123",
        LastMergeTargetCommitId = "def456",
        CommitShas = new List<string> { "abc123" },
        ChangedFilesCount = 3,
        Additions = 50,
        Deletions = 10,
        RepositoryFullName = "org/repo",
        WebUrl = "https://example.com/pr/42"
    };

    private static readonly PullRequestDiff SampleDiff = new()
    {
        Title = "Fix the bug",
        Status = "active",
        SourceBranch = "feature/x",
        TargetBranch = "main",
        Files = new List<FileChange>
        {
            new()
            {
                Path = "src/A.cs", ChangeType = "edit", Additions = 30, Deletions = 5,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 5, NewStart = 1, NewCount = 10,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "new line" } }
                    }
                }
            },
            new()
            {
                Path = "src/B.cs", ChangeType = "edit", Additions = 20, Deletions = 5,
                Hunks = new List<DiffHunk>
                {
                    new()
                    {
                        OldStart = 1, OldCount = 5, NewStart = 1, NewCount = 10,
                        Lines = new List<DiffLine> { new() { Op = '+', Text = "another line" } }
                    }
                }
            }
        }
    };

    public GetPullRequestMetadataToolHandlerTests()
    {
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(SampleMetadata);

        _diffCache.GetOrFetchDiffAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(SampleDiff);

        _budgetResolver.Resolve(Arg.Any<int?>(), Arg.Any<string?>())
            .Returns(new BudgetResolutionResult(200000, 140000, BudgetSource.Default, Array.Empty<string>()));
        _tokenEstimator.EstimateTokenCount(Arg.Any<string>())
            .Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var pageSlice = new PageSlice(1, 0, 2,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 500), new PageSliceItem(1, PackingItemStatus.Included, 500) },
            1000, 139000);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { pageSlice }, 1, 2));

        _handler = new GetPullRequestMetadataToolHandler(
            _dataProvider,
            _budgetResolver,
            _tokenEstimator,
            _fileClassifier,
            _pageAllocator,
            _diffCache,
            NullLogger<GetPullRequestMetadataToolHandler>.Instance);
    }

    // --- Existing behavior (backward compatibility) ---

    [Fact]
    public async Task ExecuteAsync_WithoutBudgetParams_ReturnsMetadataWithoutPaging()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(json);
        Assert.Equal(42, doc.RootElement.GetProperty("prNumber").GetInt32());
        Assert.Equal("Fix the bug", doc.RootElement.GetProperty("title").GetString());
        Assert.False(doc.RootElement.TryGetProperty("contentPaging", out _));
    }

    [Fact]
    public async Task ExecuteAsync_PreservesExistingFields()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(json);
        Assert.Equal("user1", doc.RootElement.GetProperty("author").GetProperty("login").GetString());
        Assert.Equal("active", doc.RootElement.GetProperty("state").GetString());
        Assert.Equal("main", doc.RootElement.GetProperty("base").GetProperty("ref").GetString());
        Assert.Equal("feature/x", doc.RootElement.GetProperty("head").GetProperty("ref").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("stats").GetProperty("changedFiles").GetInt32());
    }

    // --- Pagination info (diff-based measurement) ---

    [Fact]
    public async Task ExecuteAsync_WithModelName_ReturnsContentPaging()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        var doc = JsonDocument.Parse(json);
        var paging = doc.RootElement.GetProperty("contentPaging");
        Assert.Equal(1, paging.GetProperty("totalPages").GetInt32());
        Assert.Equal(2, paging.GetProperty("totalFiles").GetInt32());
        Assert.Equal(140000, paging.GetProperty("budgetPerPageTokens").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_WithMaxTokens_ReturnsContentPaging()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, maxTokens: 50000);

        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("contentPaging", out _));
    }

    [Fact]
    public async Task ExecuteAsync_ContentPaging_FilesByPage_HasCorrectShape()
    {
        var json = await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        var doc = JsonDocument.Parse(json);
        var filesByPage = doc.RootElement.GetProperty("contentPaging").GetProperty("filesByPage");
        Assert.Equal(1, filesByPage.GetArrayLength());
        Assert.Equal(1, filesByPage[0].GetProperty("pageNumber").GetInt32());
        Assert.Equal(2, filesByPage[0].GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_ContentPaging_MultiplePages_AllPagesListed()
    {
        var slice1 = new PageSlice(1, 0, 1,
            new[] { new PageSliceItem(0, PackingItemStatus.Included, 500) }, 500, 139500);
        var slice2 = new PageSlice(2, 1, 2,
            new[] { new PageSliceItem(1, PackingItemStatus.Included, 500) }, 500, 139500);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { slice1, slice2 }, 2, 2));

        var json = await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        var doc = JsonDocument.Parse(json);
        var paging = doc.RootElement.GetProperty("contentPaging");
        Assert.Equal(2, paging.GetProperty("totalPages").GetInt32());
        var filesByPage = paging.GetProperty("filesByPage");
        Assert.Equal(2, filesByPage.GetArrayLength());
        Assert.Equal(1, filesByPage[0].GetProperty("fileCount").GetInt32());
        Assert.Equal(1, filesByPage[1].GetProperty("fileCount").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_WithPaging_CallsFileClassifierForEachFile()
    {
        await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        _fileClassifier.Received(2).Classify(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteAsync_WithPaging_CallsEstimateTokenCountForEachFile()
    {
        await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        _tokenEstimator.Received(2).EstimateTokenCount(Arg.Any<string>());
    }

    [Fact]
    public async Task ExecuteAsync_WithPaging_UsesDiffCacheNotFilesApi()
    {
        await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        await _diffCache.Received(1).GetOrFetchDiffAsync(42, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    // --- Error handling ---

    [Fact]
    public async Task ExecuteAsync_NullPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: null));
        Assert.Contains("Missing required parameter", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroPrNumber_ThrowsMcpException()
    {
        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 0));
        Assert.Contains("greater than 0", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_PrNotFound_ThrowsMcpException()
    {
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new PullRequestNotFoundException("PR not found"));

        var ex = await Assert.ThrowsAsync<McpException>(
            () => _handler.ExecuteAsync(prNumber: 999));
        Assert.Contains("Pull Request not found", ex.Message);
    }

    [Fact]
    public async Task ExecuteAsync_DescriptionTruncation_WorksCorrectly()
    {
        var longDescription = new string('x', 1000);
        _dataProvider.GetMetadataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new FullPullRequestMetadata
            {
                Title = "Test",
                Description = longDescription,
                CommitShas = new List<string>()
            });

        var json = await _handler.ExecuteAsync(prNumber: 42);

        var doc = JsonDocument.Parse(json);
        var desc = doc.RootElement.GetProperty("description");
        Assert.True(desc.GetProperty("isTruncated").GetBoolean());
        Assert.Equal(800, desc.GetProperty("returnedLength").GetInt32());
    }

    [Fact]
    public async Task ExecuteAsync_WithPaging_EmptyDiffFiles_ProducesEmptyPaging()
    {
        var emptyDiff = new PullRequestDiff
        {
            Title = "Empty",
            Status = "active",
            SourceBranch = "feature/empty",
            TargetBranch = "main",
            Files = new List<FileChange>()
        };
        _diffCache.GetOrFetchDiffAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(emptyDiff);

        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(Array.Empty<PageSlice>(), 0, 0));

        var json = await _handler.ExecuteAsync(prNumber: 42, modelName: "gpt-4o");

        var doc = JsonDocument.Parse(json);
        var paging = doc.RootElement.GetProperty("contentPaging");
        Assert.Equal(0, paging.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, paging.GetProperty("totalFiles").GetInt32());
    }
}
