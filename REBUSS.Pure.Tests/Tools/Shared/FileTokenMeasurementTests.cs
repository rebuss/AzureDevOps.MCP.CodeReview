using NSubstitute;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Tools.Shared;

namespace REBUSS.Pure.Tests.Tools.Shared;

public class FileTokenMeasurementTests
{
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();

    private static PullRequestDiff CreateDiff(params FileChange[] files)
    {
        return new PullRequestDiff
        {
            Title = "Test",
            Status = "active",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            Files = files.ToList()
        };
    }

    private static FileChange CreateFileChange(
        string path, string changeType = "edit",
        int additions = 10, int deletions = 5,
        string? skipReason = null)
    {
        return new FileChange
        {
            Path = path,
            ChangeType = changeType,
            Additions = additions,
            Deletions = deletions,
            SkipReason = skipReason,
            Hunks = new List<DiffHunk>
            {
                new()
                {
                    OldStart = 1, OldCount = 5, NewStart = 1, NewCount = 10,
                    Lines = new List<DiffLine>
                    {
                        new() { Op = '+', Text = "added line" },
                        new() { Op = ' ', Text = "context line" },
                        new() { Op = '-', Text = "removed line" }
                    }
                }
            }
        };
    }

    // --- BuildCandidatesFromDiff ---

    [Fact]
    public void BuildCandidatesFromDiff_ReturnsOneCandidatePerFile()
    {
        var diff = CreateDiff(
            CreateFileChange("src/A.cs"),
            CreateFileChange("src/B.cs"),
            CreateFileChange("docs/README.md"));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Equal(3, candidates.Count);
    }

    [Fact]
    public void BuildCandidatesFromDiff_CallsEstimateTokenCountForEachFile()
    {
        var diff = CreateDiff(
            CreateFileChange("src/A.cs"),
            CreateFileChange("src/B.cs"));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        _tokenEstimator.Received(2).EstimateTokenCount(Arg.Any<string>());
    }

    [Fact]
    public void BuildCandidatesFromDiff_CandidateHasCorrectPath()
    {
        var diff = CreateDiff(CreateFileChange("src/MyFile.cs"));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(123);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Equal("src/MyFile.cs", candidates[0].Path);
    }

    [Fact]
    public void BuildCandidatesFromDiff_CandidateHasMeasuredTokenCount()
    {
        var diff = CreateDiff(CreateFileChange("src/A.cs", additions: 50, deletions: 20));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(750);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Equal(750, candidates[0].EstimatedTokens);
    }

    [Fact]
    public void BuildCandidatesFromDiff_CandidateHasCorrectTotalChanges()
    {
        var diff = CreateDiff(CreateFileChange("src/A.cs", additions: 30, deletions: 10));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Equal(40, candidates[0].TotalChanges);
    }

    [Fact]
    public void BuildCandidatesFromDiff_ClassifiesEachFile()
    {
        _fileClassifier.Classify("src/A.cs")
            .Returns(new FileClassification { Category = FileCategory.Source });
        _fileClassifier.Classify("docs/README.md")
            .Returns(new FileClassification { Category = FileCategory.Docs });

        var diff = CreateDiff(
            CreateFileChange("src/A.cs"),
            CreateFileChange("docs/README.md"));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(500);

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Equal(FileCategory.Source, candidates[0].Category);
        Assert.Equal(FileCategory.Docs, candidates[1].Category);
    }

    [Fact]
    public void BuildCandidatesFromDiff_EmptyDiff_ReturnsEmptyList()
    {
        var diff = CreateDiff();

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Empty(candidates);
    }

    [Fact]
    public void BuildCandidatesFromDiff_SkippedFile_StillIncludedWithMeasuredTokens()
    {
        var diff = CreateDiff(CreateFileChange("large.bin", skipReason: "Binary file"));

        _tokenEstimator.EstimateTokenCount(Arg.Any<string>()).Returns(100);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Binary });

        var candidates = FileTokenMeasurement.BuildCandidatesFromDiff(diff, _tokenEstimator, _fileClassifier);

        Assert.Single(candidates);
        Assert.Equal("large.bin", candidates[0].Path);
        Assert.Equal(100, candidates[0].EstimatedTokens);
    }

    // --- MapToStructured ---

    [Fact]
    public void MapToStructured_MapsPath()
    {
        var file = CreateFileChange("src/Service.cs");

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Equal("src/Service.cs", result.Path);
    }

    [Fact]
    public void MapToStructured_MapsChangeType()
    {
        var file = CreateFileChange("src/Service.cs", changeType: "add");

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Equal("add", result.ChangeType);
    }

    [Fact]
    public void MapToStructured_MapsSkipReason()
    {
        var file = CreateFileChange("large.bin", skipReason: "Binary file too large");

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Equal("Binary file too large", result.SkipReason);
    }

    [Fact]
    public void MapToStructured_MapsAdditionsAndDeletions()
    {
        var file = CreateFileChange("src/A.cs", additions: 30, deletions: 12);

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Equal(30, result.Additions);
        Assert.Equal(12, result.Deletions);
    }

    [Fact]
    public void MapToStructured_MapsHunks()
    {
        var file = CreateFileChange("src/A.cs");

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Single(result.Hunks);
        Assert.Equal(1, result.Hunks[0].OldStart);
        Assert.Equal(5, result.Hunks[0].OldCount);
        Assert.Equal(1, result.Hunks[0].NewStart);
        Assert.Equal(10, result.Hunks[0].NewCount);
    }

    [Fact]
    public void MapToStructured_MapsLines_OpAsString()
    {
        var file = CreateFileChange("src/A.cs");

        var result = FileTokenMeasurement.MapToStructured(file);

        var lines = result.Hunks[0].Lines;
        Assert.Equal(3, lines.Count);
        Assert.Equal("+", lines[0].Op);
        Assert.Equal("added line", lines[0].Text);
        Assert.Equal(" ", lines[1].Op);
        Assert.Equal("context line", lines[1].Text);
        Assert.Equal("-", lines[2].Op);
        Assert.Equal("removed line", lines[2].Text);
    }

    [Fact]
    public void MapToStructured_NullSkipReason_IsNull()
    {
        var file = CreateFileChange("src/A.cs", skipReason: null);

        var result = FileTokenMeasurement.MapToStructured(file);

        Assert.Null(result.SkipReason);
    }
}
