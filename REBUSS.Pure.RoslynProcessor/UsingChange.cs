namespace REBUSS.Pure.RoslynProcessor;

/// <summary>Classification of a using directive change.</summary>
public enum UsingChangeKind
{
    Added,
    Removed
}

/// <summary>A detected addition or removal of a <c>using</c> directive.</summary>
public sealed record UsingChange
{
    public required UsingChangeKind Kind { get; init; }
    public required string Namespace { get; init; }
}
