using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Core.Tests.Shared;

public class StructuredDiffBuilderTests
{
    private readonly IStructuredDiffBuilder _builder =
        new StructuredDiffBuilder(new DiffPlexDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance);

    [Fact]
    public void Build_ReturnsEmpty_WhenBothContentIdentical()
    {
        var result = _builder.Build("/src/File.cs", "hello", "hello");
        Assert.Empty(result);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenBothNull()
    {
        var result = _builder.Build("/src/File.cs", null, null);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_NewFile_ContainsAddedLines()
    {
        var result = _builder.Build("/src/New.cs", null, "line1\nline2");

        Assert.Single(result);
        var hunk = result[0];
        Assert.Equal(2, hunk.Lines.Count);
        Assert.All(hunk.Lines, l => Assert.Equal('+', l.Op));
        Assert.Equal("line1", hunk.Lines[0].Text);
        Assert.Equal("line2", hunk.Lines[1].Text);
    }

    [Fact]
    public void Build_DeletedFile_ContainsRemovedLines()
    {
        var result = _builder.Build("/src/Old.cs", "line1\nline2", null);

        Assert.Single(result);
        var hunk = result[0];
        Assert.Equal(2, hunk.Lines.Count);
        Assert.All(hunk.Lines, l => Assert.Equal('-', l.Op));
        Assert.Equal("line1", hunk.Lines[0].Text);
        Assert.Equal("line2", hunk.Lines[1].Text);
    }

    [Fact]
    public void Build_ModifiedFile_ContainsMinusAndPlusLines()
    {
        var result = _builder.Build("src/File.cs", "aaa\nbbb\nccc", "aaa\nBBB\nccc");

        Assert.Single(result);
        var hunk = result[0];
        Assert.Contains(hunk.Lines, l => l.Op == '-' && l.Text == "bbb");
        Assert.Contains(hunk.Lines, l => l.Op == '+' && l.Text == "BBB");
        Assert.Contains(hunk.Lines, l => l.Op == ' ');
    }

    [Fact]
    public void Build_ContainsHunkMetadata()
    {
        var result = _builder.Build("a.txt", "old", "new");

        Assert.Single(result);
        var hunk = result[0];
        Assert.True(hunk.OldStart > 0);
        Assert.True(hunk.NewStart > 0);
        Assert.True(hunk.OldCount > 0);
        Assert.True(hunk.NewCount > 0);
    }

    [Fact]
    public void Build_HandlesCrlf()
    {
        var result = _builder.Build("a.txt", "aaa\r\nbbb", "aaa\r\nccc");

        Assert.Single(result);
        Assert.Contains(result[0].Lines, l => l.Op == '-' && l.Text == "bbb");
        Assert.Contains(result[0].Lines, l => l.Op == '+' && l.Text == "ccc");
    }

    [Fact]
    public void Build_NewFile_HunkMetadata_CorrectCounts()
    {
        var result = _builder.Build("new.txt", null, "a\nb\nc");

        Assert.Single(result);
        Assert.Equal(0, result[0].OldCount);
        Assert.Equal(3, result[0].NewCount);
    }

    [Fact]
    public void Build_DeletedFile_HunkMetadata_CorrectCounts()
    {
        var result = _builder.Build("old.txt", "a\nb\nc", null);

        Assert.Single(result);
        Assert.Equal(3, result[0].OldCount);
        Assert.Equal(0, result[0].NewCount);
    }
}
