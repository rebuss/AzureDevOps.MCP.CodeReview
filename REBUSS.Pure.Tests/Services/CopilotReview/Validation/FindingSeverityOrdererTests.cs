using REBUSS.Pure.Services.CopilotReview.Validation;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Validation;

/// <summary>Unit tests for <see cref="FindingSeverityOrderer"/>.</summary>
public class FindingSeverityOrdererTests
{
    private record Item(string Severity, int Tag);

    [Fact]
    public void Order_PutsCriticalBeforeMajorBeforeMinor()
    {
        var input = new[]
        {
            new Item("minor", 1),
            new Item("critical", 2),
            new Item("major", 3),
        };

        var ordered = FindingSeverityOrderer.Order(input, i => i.Severity);

        Assert.Equal(new[] { "critical", "major", "minor" }, ordered.Select(i => i.Severity));
    }

    [Fact]
    public void Order_IsStableWithinSameSeverity()
    {
        var input = new[]
        {
            new Item("major", 1),
            new Item("critical", 2),
            new Item("major", 3),
            new Item("critical", 4),
            new Item("major", 5),
        };

        var ordered = FindingSeverityOrderer.Order(input, i => i.Severity);

        // Critical group keeps original relative order (2, 4); major group keeps (1, 3, 5).
        Assert.Equal(new[] { 2, 4, 1, 3, 5 }, ordered.Select(i => i.Tag));
    }

    [Fact]
    public void Order_IsCaseInsensitive()
    {
        var input = new[]
        {
            new Item("MINOR", 1),
            new Item("Critical", 2),
            new Item("MAJOR", 3),
        };

        var ordered = FindingSeverityOrderer.Order(input, i => i.Severity);

        Assert.Equal(new[] { 2, 3, 1 }, ordered.Select(i => i.Tag));
    }

    [Fact]
    public void Order_UnknownSeverityGoesLast()
    {
        var input = new[]
        {
            new Item("info", 1),
            new Item("major", 2),
            new Item("warning", 3),
            new Item("critical", 4),
        };

        var ordered = FindingSeverityOrderer.Order(input, i => i.Severity);

        // critical → major → unknown (info, warning) preserved in original relative order.
        Assert.Equal(new[] { 4, 2, 1, 3 }, ordered.Select(i => i.Tag));
    }

    [Fact]
    public void Order_EmptyInput_ReturnsEmpty()
    {
        var ordered = FindingSeverityOrderer.Order(Array.Empty<Item>(), i => i.Severity);

        Assert.Empty(ordered);
    }

    [Fact]
    public void IsOrderedBySeverity_AscendingByRank_True()
    {
        var input = new[]
        {
            new Item("critical", 1),
            new Item("critical", 2),
            new Item("major", 3),
            new Item("minor", 4),
        };

        Assert.True(FindingSeverityOrderer.IsOrderedBySeverity(input, i => i.Severity));
    }

    [Fact]
    public void IsOrderedBySeverity_MajorBeforeCritical_False()
    {
        var input = new[]
        {
            new Item("major", 1),
            new Item("critical", 2),
        };

        Assert.False(FindingSeverityOrderer.IsOrderedBySeverity(input, i => i.Severity));
    }

    [Fact]
    public void IsOrderedBySeverity_SingleItem_True()
    {
        var input = new[] { new Item("major", 1) };

        Assert.True(FindingSeverityOrderer.IsOrderedBySeverity(input, i => i.Severity));
    }

    [Fact]
    public void IsOrderedBySeverity_Empty_True()
    {
        Assert.True(FindingSeverityOrderer.IsOrderedBySeverity(Array.Empty<Item>(), i => i.Severity));
    }
}
