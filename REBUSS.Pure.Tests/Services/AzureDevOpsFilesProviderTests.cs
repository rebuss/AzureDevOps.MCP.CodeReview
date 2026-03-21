using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.AzureDevOps.Api;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.AzureDevOps.Parsers;
using REBUSS.Pure.AzureDevOps.Providers;

namespace REBUSS.Pure.Tests.Services;

public class AzureDevOpsFilesProviderTests
{
    private readonly AzureDevOpsDiffProvider _diffProvider = Substitute.For<AzureDevOpsDiffProvider>(
        Substitute.For<IAzureDevOpsApiClient>(),
        Substitute.For<IPullRequestMetadataParser>(),
        Substitute.For<IIterationInfoParser>(),
        Substitute.For<IFileChangesParser>(),
        Substitute.For<IStructuredDiffBuilder>(),
        Substitute.For<IFileClassifier>(),
        NullLogger<AzureDevOpsDiffProvider>.Instance);
    private readonly AzureDevOpsFilesProvider _provider;

    public AzureDevOpsFilesProviderTests()
    {
        _provider = new AzureDevOpsFilesProvider(
            _diffProvider,
            new FileClassifier(),
            NullLogger<AzureDevOpsFilesProvider>.Instance);
    }

    [Fact]
    public async Task GetFilesAsync_MapsFileInfoCorrectly()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new()
                {
                    Path = "/src/Service.cs", ChangeType = "edit",
                    Additions = 2, Deletions = 1,
                    Hunks = new List<DiffHunk>
                    {
                        new()
                        {
                            OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 2,
                            Lines = new List<DiffLine>
                            {
                                new() { Op = '-', Text = "old" },
                                new() { Op = '+', Text = "new" },
                                new() { Op = '+', Text = "extra" }
                            }
                        }
                    }
                }
            }
        });

        var result = await _provider.GetFilesAsync(42);

        Assert.Single(result.Files);
        var file = result.Files[0];
        Assert.Equal("src/Service.cs", file.Path);
        Assert.Equal("modified", file.Status);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Equal(3, file.Changes);
        Assert.Equal(".cs", file.Extension);
        Assert.False(file.IsBinary);
        Assert.False(file.IsGenerated);
        Assert.False(file.IsTestFile);
        Assert.Equal("high", file.ReviewPriority);
    }

    [Fact]
    public async Task GetFilesAsync_MapsStatusCorrectly()
    {
        _diffProvider.GetDiffAsync(1, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/a.cs", ChangeType = "add" },
                new() { Path = "/b.cs", ChangeType = "edit" },
                new() { Path = "/c.cs", ChangeType = "delete" },
                new() { Path = "/d.cs", ChangeType = "rename" }
            }
        });

        var result = await _provider.GetFilesAsync(1);

        Assert.Equal("added", result.Files[0].Status);
        Assert.Equal("modified", result.Files[1].Status);
        Assert.Equal("removed", result.Files[2].Status);
        Assert.Equal("renamed", result.Files[3].Status);
    }

    [Fact]
    public async Task GetFilesAsync_BuildsSummaryCorrectly()
    {
        _diffProvider.GetDiffAsync(10, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/App.cs", ChangeType = "edit", Additions = 2 },
                new() { Path = "/tests/AppTests.cs", ChangeType = "edit", Additions = 1 },
                new() { Path = "/appsettings.json", ChangeType = "edit" },
                new() { Path = "/docs/readme.md", ChangeType = "edit" },
                new() { Path = "/lib/tool.dll", ChangeType = "add" },
                new() { Path = "/obj/Debug/net8.0/out.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(10);

        Assert.Equal(6, result.Files.Count);
        Assert.Equal(1, result.Summary.SourceFiles);
        Assert.Equal(1, result.Summary.TestFiles);
        Assert.Equal(1, result.Summary.ConfigFiles);
        Assert.Equal(1, result.Summary.DocsFiles);
        Assert.Equal(1, result.Summary.BinaryFiles);
        Assert.Equal(1, result.Summary.GeneratedFiles);
        Assert.Equal(1, result.Summary.HighPriorityFiles);
    }

    [Fact]
    public async Task GetFilesAsync_HandlesEmptyFileList()
    {
        _diffProvider.GetDiffAsync(5, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>()
        });

        var result = await _provider.GetFilesAsync(5);

        Assert.Empty(result.Files);
        Assert.Equal(0, result.Summary.SourceFiles);
        Assert.Equal(0, result.Summary.HighPriorityFiles);
    }

    [Fact]
    public async Task GetFilesAsync_HandlesFileWithNoHunks()
    {
        _diffProvider.GetDiffAsync(6, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/Empty.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(6);

        var file = Assert.Single(result.Files);
        Assert.Equal(0, file.Additions);
        Assert.Equal(0, file.Deletions);
        Assert.Equal(0, file.Changes);
    }

    [Fact]
    public async Task GetFilesAsync_StripsLeadingSlashFromPath()
    {
        _diffProvider.GetDiffAsync(7, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/A.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(7);

        Assert.Equal("src/A.cs", result.Files[0].Path);
    }
}
