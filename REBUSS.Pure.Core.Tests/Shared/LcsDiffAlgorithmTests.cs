using REBUSS.Pure.Core.Shared;

namespace REBUSS.Pure.Core.Tests.Shared;

public class LcsDiffAlgorithmTests
{
    private readonly IDiffAlgorithm _algorithm = new LcsDiffAlgorithm();

    [Fact]
    public void ComputeEdits_EmptyInputs_ReturnsEmptyList()
    {
        var result = _algorithm.ComputeEdits([], []);

        Assert.Empty(result);
    }

    [Fact]
    public void ComputeEdits_IdenticalLines_ReturnsOnlyContextEdits()
    {
        string[] lines = ["a", "b", "c"];

        var result = _algorithm.ComputeEdits(lines, lines);

        Assert.All(result, e => Assert.Equal(' ', e.Kind));
        Assert.Equal(3, result.Count);
    }

    [Fact]
    public void ComputeEdits_AddedLines_ReturnsInsertions()
    {
        var result = _algorithm.ComputeEdits([], ["x", "y"]);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal('+', e.Kind));
    }

    [Fact]
    public void ComputeEdits_DeletedLines_ReturnsDeletions()
    {
        var result = _algorithm.ComputeEdits(["x", "y"], []);

        Assert.Equal(2, result.Count);
        Assert.All(result, e => Assert.Equal('-', e.Kind));
    }

    [Fact]
    public void ComputeEdits_ModifiedLine_ReturnsDeleteThenInsert()
    {
        var result = _algorithm.ComputeEdits(["old"], ["new"]);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, e => e.Kind == '-');
        Assert.Contains(result, e => e.Kind == '+');
    }

    [Fact]
    public void ComputeEdits_ContextLinesHaveCorrectIndices()
    {
        string[] a = ["x", "y", "z"];
        string[] b = ["x", "Y", "z"];

        var result = _algorithm.ComputeEdits(a, b);

        var context = result.Where(e => e.Kind == ' ').ToList();
        Assert.Equal(2, context.Count);
        Assert.Equal(0, context[0].OldIdx);
        Assert.Equal(0, context[0].NewIdx);
        Assert.Equal(2, context[1].OldIdx);
        Assert.Equal(2, context[1].NewIdx);
    }

    [Fact]
    public void ComputeEdits_PreservesOldAndNewIndices()
    {
        string[] a = ["a", "b"];
        string[] b = ["a", "c"];

        var result = _algorithm.ComputeEdits(a, b);

        var deletion = result.Single(e => e.Kind == '-');
        var insertion = result.Single(e => e.Kind == '+');

        Assert.Equal(1, deletion.OldIdx);
        Assert.Equal(1, insertion.NewIdx);
    }
}
