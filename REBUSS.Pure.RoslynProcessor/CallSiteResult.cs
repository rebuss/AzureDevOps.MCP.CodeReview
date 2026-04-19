namespace REBUSS.Pure.RoslynProcessor;

/// <summary>Classification of a call site search target.</summary>
public enum CallSiteTargetKind
{
    Method,
    Constructor,
    Type
}

/// <summary>A member name to search for in the repository, with context about why.</summary>
public sealed record CallSiteTarget
{
    public required string Name { get; init; }
    public required string Reason { get; init; }
    public CallSiteTargetKind Kind { get; init; } = CallSiteTargetKind.Method;
}

/// <summary>A file:line location where a target identifier is used.</summary>
public sealed record CallSiteLocation
{
    public required string FilePath { get; init; }
    public required int Line { get; init; }
}

/// <summary>Call site search results for one target.</summary>
public sealed record CallSiteResult
{
    public required string TargetName { get; init; }
    public required string Reason { get; init; }
    public required IReadOnlyList<CallSiteLocation> Locations { get; init; }
    public int TotalCount { get; init; }
}
