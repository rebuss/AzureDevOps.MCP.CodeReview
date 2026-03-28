using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.Core.Exceptions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.AzureDevOps.Providers;

namespace REBUSS.Pure.AzureDevOps.Tests.Providers;

public class AzureDevOpsDiffProviderTests
{
    private readonly IAzureDevOpsApiClient _apiClient = Substitute.For<IAzureDevOpsApiClient>();
    private readonly AzureDevOpsDiffProvider _provider;

    // Minimal valid JSON responses from Azure DevOps API
    private const string PrDetailsJson = """
        {
            "title": "Fix bug #42",
            "status": "active",
            "sourceRefName": "refs/heads/feature/fix-42",
            "targetRefName": "refs/heads/main"
        }
        """;

    private const string IterationsJson = """
        {
            "value": [
                {
                    "id": 1,
                    "sourceRefCommit": { "commitId": "aaa111" },
                    "commonRefCommit": { "commitId": "bbb222" }
                }
            ]
        }
        """;

    private const string ChangesJson = """
        {
            "changeEntries": [
                {
                    "changeType": "edit",
                    "item": { "path": "/src/File.cs" }
                }
            ]
        }
        """;

    public AzureDevOpsDiffProviderTests()
    {
        _provider = new AzureDevOpsDiffProvider(
            _apiClient,
            new PullRequestMetadataParser(NullLogger<PullRequestMetadataParser>.Instance),
            new IterationInfoParser(NullLogger<IterationInfoParser>.Instance),
            new FileChangesParser(NullLogger<FileChangesParser>.Instance),
            new StructuredDiffBuilder(new LcsDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance),
            new FileClassifier(),
            NullLogger<AzureDevOpsDiffProvider>.Instance);
    }

    [Fact]
    public async Task GetDiffAsync_ReturnsDiff_WithCorrectMetadata()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Equal("active", result.Status);
        Assert.Equal("feature/fix-42", result.SourceBranch);
        Assert.Equal("main", result.TargetBranch);
        Assert.Single(result.Files);
        Assert.Equal("/src/File.cs", result.Files[0].Path);
        Assert.Equal("edit", result.Files[0].ChangeType);
    }

    [Fact]
    public async Task GetDiffAsync_GeneratesStructuredDiff_ForModifiedFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.NotEmpty(result.Files[0].Hunks);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetDiffAsync_SetsAdditionsAndDeletions()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new line");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal(1, result.Files[0].Additions);
        Assert.Equal(1, result.Files[0].Deletions);
    }

    [Fact]
    public async Task GetDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetDiffAsync(999));
    }

    [Fact]
    public async Task GetDiffAsync_HandlesNoIterations()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns("""{"value":[]}""");

        var result = await _provider.GetDiffAsync(42);

        Assert.Empty(result.Files);
    }

    [Fact]
    public async Task GetDiffAsync_SkipsFolderEntries()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);

        var changesWithFolder = """
            {
                "changeEntries": [
                    {
                        "changeType": "edit",
                        "item": { "path": "/src/", "isFolder": true }
                    },
                    {
                        "changeType": "edit",
                        "item": { "path": "/src/File.cs" }
                    }
                ]
            }
            """;
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns(changesWithFolder);
        _apiClient.GetFileContentAtCommitAsync(Arg.Any<string>(), "/src/File.cs").Returns("content");

        var result = await _provider.GetDiffAsync(42);

        Assert.Single(result.Files);
        Assert.Equal("/src/File.cs", result.Files[0].Path);
    }

    [Fact]
    public async Task GetDiffAsync_LeavesEmptyHunks_WhenNoCommitSHAs()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        // Iterations with no commit SHAs
        _apiClient.GetPullRequestIterationsAsync(42).Returns("""{"value":[{"id":1}]}""");
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns(ChangesJson);

        var result = await _provider.GetDiffAsync(42);

        // Files should be listed but have no hunks
        Assert.Single(result.Files);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetDiffAsync_RespectsCancellation()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provider.GetDiffAsync(42, cts.Token));
    }

    // --- GetFileDiffAsync ---

    [Fact]
    public async Task GetFileDiffAsync_ReturnsDiff_ForMatchingFile()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old line");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new line");

        var result = await _provider.GetFileDiffAsync(42, "/src/File.cs");

        Assert.Equal("Fix bug #42", result.Title);
        Assert.Single(result.Files);
        Assert.Equal("/src/File.cs", result.Files[0].Path);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old line");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new line");
    }

    [Fact]
    public async Task GetFileDiffAsync_NormalizesLeadingSlash()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new");

        // Pass path without leading slash while data has leading slash
        var result = await _provider.GetFileDiffAsync(42, "src/File.cs");

        Assert.Single(result.Files);
        Assert.Equal("/src/File.cs", result.Files[0].Path);
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsFileNotFound_WhenPathDoesNotExist()
    {
        SetupStandardMocks();

        await Assert.ThrowsAsync<FileNotFoundInPullRequestException>(
            () => _provider.GetFileDiffAsync(42, "/src/NonExistent.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_ThrowsPullRequestNotFound_On404()
    {
        _apiClient.GetPullRequestDetailsAsync(999)
            .ThrowsAsync(new HttpRequestException("Not Found", null, HttpStatusCode.NotFound));

        await Assert.ThrowsAsync<PullRequestNotFoundException>(
            () => _provider.GetFileDiffAsync(999, "/src/File.cs"));
    }

    [Fact]
    public async Task GetFileDiffAsync_RespectsCancellation()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync(Arg.Any<string>(), Arg.Any<string>()).Returns("x");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _provider.GetFileDiffAsync(42, "/src/File.cs", cts.Token));
    }

    [Fact]
    public async Task GetFileDiffAsync_IsCaseInsensitive()
    {
        SetupStandardMocks();
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new");

        var result = await _provider.GetFileDiffAsync(42, "/SRC/FILE.CS");

        Assert.Single(result.Files);
    }

    private void SetupStandardMocks()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns(ChangesJson);
    }

    // --- Diff skip: delete / rename ---

    [Theory]
    [InlineData("delete", "file deleted")]
    [InlineData("rename", "file renamed")]
    public async Task GetDiffAsync_SkipsDiff_ForChangeType(string changeType, string expectedSkipReason)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns($$"""
            {
                "changeEntries": [
                    { "changeType": "{{changeType}}", "item": { "path": "/src/File.cs" } }
                ]
            }
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Single(result.Files);
        Assert.Equal(expectedSkipReason, result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
        await _apiClient.DidNotReceive().GetFileContentAtCommitAsync(Arg.Any<string>(), "/src/File.cs");
    }

    // --- Diff skip: Binary files ---

    [Theory]
    [InlineData("/lib/external.dll")]
    [InlineData("/images/photo.jpg")]
    [InlineData("/packages/archive.zip")]
    [InlineData("/docs/manual.pdf")]
    [InlineData("/assets/font.woff2")]
    public async Task GetDiffAsync_SkipsDiff_ForVariousBinaryExtensions(string path)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns($$"""
            {
                "changeEntries": [
                    { "changeType": "edit", "item": { "path": "{{path}}" } }
                ]
            }
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("binary file", result.Files[0].SkipReason);
    }

    // --- Diff skip: Generated files ---

    [Theory]
    [InlineData("/obj/Debug/net8.0/AssemblyInfo.cs")]
    [InlineData("/bin/Release/net8.0/app.dll")]
    [InlineData("/src/Models/Client.g.cs")]
    [InlineData("/src/Views/Main.designer.cs")]
    public async Task GetDiffAsync_SkipsDiff_ForVariousGeneratedFiles(string path)
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns($$"""
            {
                "changeEntries": [
                    { "changeType": "edit", "item": { "path": "{{path}}" } }
                ]
            }
            """);

        var result = await _provider.GetDiffAsync(42);

        Assert.NotNull(result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    // --- Diff skip: Full-file rewrite ---

    [Fact]
    public async Task GetDiffAsync_SkipsDiff_ForFullFileRewrite()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns(ChangesJson);

        // Generate content where every line is different and file is >= 10 lines
        var oldContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"old line {i}"));
        var newContent = string.Join("\n", Enumerable.Range(1, 15).Select(i => $"new line {i}"));
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns(oldContent);
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns(newContent);

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal("full file rewrite", result.Files[0].SkipReason);
        Assert.Empty(result.Files[0].Hunks);
    }

    [Fact]
    public async Task GetDiffAsync_DoesNotSkip_ForPartialChange()
    {
        SetupStandardMocks();

        // Change only one line in a multi-line file
        var oldContent = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\nline11";
        var newContent = "line1\nline2\nCHANGED\nline4\nline5\nline6\nline7\nline8\nline9\nline10\nline11";
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns(oldContent);
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns(newContent);

        var result = await _provider.GetDiffAsync(42);

        Assert.Null(result.Files[0].SkipReason);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "line3");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "CHANGED");
    }

    [Fact]
    public async Task GetDiffAsync_DoesNotSkipFullRewrite_ForSmallFiles()
    {
        SetupStandardMocks();

        // Small file (< 10 lines) with all lines different - should NOT be flagged
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/File.cs").Returns("old1\nold2\nold3");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/File.cs").Returns("new1\nnew2\nnew3");

        var result = await _provider.GetDiffAsync(42);

        Assert.Null(result.Files[0].SkipReason);
        var allLines = result.Files[0].Hunks.SelectMany(h => h.Lines).ToList();
        Assert.Contains(allLines, l => l.Op == '-' && l.Text == "old1");
        Assert.Contains(allLines, l => l.Op == '+' && l.Text == "new1");
    }

    // --- Mixed scenarios ---

    [Fact]
    public async Task GetDiffAsync_MixedFiles_OnlySkipsApplicable()
    {
        _apiClient.GetPullRequestDetailsAsync(42).Returns(PrDetailsJson);
        _apiClient.GetPullRequestIterationsAsync(42).Returns(IterationsJson);
        _apiClient.GetPullRequestIterationChangesAsync(42, 1).Returns("""
            {
                "changeEntries": [
                    { "changeType": "edit",   "item": { "path": "/src/Service.cs" } },
                    { "changeType": "delete", "item": { "path": "/src/Removed.cs" } },
                    { "changeType": "edit",   "item": { "path": "/assets/icon.png" } }
                ]
            }
            """);
        _apiClient.GetFileContentAtCommitAsync("bbb222", "/src/Service.cs").Returns("old");
        _apiClient.GetFileContentAtCommitAsync("aaa111", "/src/Service.cs").Returns("new");

        var result = await _provider.GetDiffAsync(42);

        Assert.Equal(3, result.Files.Count);
        Assert.Null(result.Files[0].SkipReason);       // edit — computed normally
        Assert.Equal("file deleted", result.Files[1].SkipReason);  // delete — skipped
        Assert.Equal("binary file", result.Files[2].SkipReason);   // binary — skipped
    }

    // --- IsFullFileRewrite unit tests ---

    [Fact]
    public void IsFullFileRewrite_ReturnsFalse_WhenBaseContentIsNull()
    {
        Assert.False(AzureDevOpsDiffProvider.IsFullFileRewrite(null, "content", new List<DiffHunk>()));
    }

    [Fact]
    public void IsFullFileRewrite_ReturnsFalse_WhenTargetContentIsNull()
    {
        Assert.False(AzureDevOpsDiffProvider.IsFullFileRewrite("content", null, new List<DiffHunk>()));
    }

    [Fact]
    public void IsFullFileRewrite_ReturnsFalse_WhenHunksAreEmpty()
    {
        Assert.False(AzureDevOpsDiffProvider.IsFullFileRewrite("old", "new", new List<DiffHunk>()));
    }

    [Fact]
    public void IsFullFileRewrite_ReturnsFalse_WhenFilesAreTooSmall()
    {
        var hunks = new List<DiffHunk>
        {
            new()
            {
                OldStart = 1, OldCount = 3, NewStart = 1, NewCount = 3,
                Lines = new List<DiffLine>
                {
                    new() { Op = '-', Text = "a" },
                    new() { Op = '-', Text = "b" },
                    new() { Op = '-', Text = "c" },
                    new() { Op = '+', Text = "x" },
                    new() { Op = '+', Text = "y" },
                    new() { Op = '+', Text = "z" }
                }
            }
        };
        Assert.False(AzureDevOpsDiffProvider.IsFullFileRewrite("a\nb\nc", "x\ny\nz", hunks));
    }

    [Fact]
    public void IsFullFileRewrite_ReturnsTrue_WhenNoContextLines()
    {
        var oldLines = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"old{i}"));
        var newLines = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"new{i}"));
        var hunks = new List<DiffHunk>
        {
            new()
            {
                OldStart = 1, OldCount = 12, NewStart = 1, NewCount = 12,
                Lines = Enumerable.Range(1, 12).Select(i => new DiffLine { Op = '-', Text = $"old{i}" })
                    .Concat(Enumerable.Range(1, 12).Select(i => new DiffLine { Op = '+', Text = $"new{i}" }))
                    .ToList()
            }
        };

        Assert.True(AzureDevOpsDiffProvider.IsFullFileRewrite(oldLines, newLines, hunks));
    }

    [Fact]
    public void IsFullFileRewrite_ReturnsFalse_WhenContextLinesExist()
    {
        var oldLines = string.Join("\n", Enumerable.Range(1, 12).Select(i => $"line{i}"));
        var newLines = "line1\nline2\nCHANGED\n" +
                       string.Join("\n", Enumerable.Range(4, 9).Select(i => $"line{i}"));
        var hunks = new List<DiffHunk>
        {
            new()
            {
                OldStart = 1, OldCount = 4, NewStart = 1, NewCount = 4,
                Lines = new List<DiffLine>
                {
                    new() { Op = ' ', Text = "line1" },
                    new() { Op = ' ', Text = "line2" },
                    new() { Op = '-', Text = "line3" },
                    new() { Op = '+', Text = "CHANGED" },
                    new() { Op = ' ', Text = "line4" }
                }
            }
        };

        Assert.False(AzureDevOpsDiffProvider.IsFullFileRewrite(oldLines, newLines, hunks));
    }

    // --- GetSkipReason unit tests ---

    [Fact]
    public void GetSkipReason_ReturnsFileDeleted_ForDeleteChangeType()
    {
        var file = new FileChange { Path = "/src/File.cs", ChangeType = "delete" };
        Assert.Equal("file deleted", _provider.GetSkipReason(file));
    }

    [Fact]
    public void GetSkipReason_ReturnsFileRenamed_ForRenameChangeType()
    {
        var file = new FileChange { Path = "/src/File.cs", ChangeType = "rename" };
        Assert.Equal("file renamed", _provider.GetSkipReason(file));
    }

    [Fact]
    public void GetSkipReason_ReturnsBinaryFile_ForBinaryExtension()
    {
        var file = new FileChange { Path = "/assets/logo.png", ChangeType = "edit" };
        Assert.Equal("binary file", _provider.GetSkipReason(file));
    }

    [Fact]
    public void GetSkipReason_ReturnsGeneratedFile_ForGeneratedPath()
    {
        var file = new FileChange { Path = "/obj/Debug/net8.0/AssemblyInfo.cs", ChangeType = "edit" };
        Assert.Equal("generated file", _provider.GetSkipReason(file));
    }

    [Fact]
    public void GetSkipReason_ReturnsNull_ForNormalSourceFile()
    {
        var file = new FileChange { Path = "/src/Service.cs", ChangeType = "edit" };
        Assert.Null(_provider.GetSkipReason(file));
    }

    [Fact]
    public void GetSkipReason_ReturnsNull_ForNewFile()
    {
        var file = new FileChange { Path = "/src/NewService.cs", ChangeType = "add" };
        Assert.Null(_provider.GetSkipReason(file));
    }
}
