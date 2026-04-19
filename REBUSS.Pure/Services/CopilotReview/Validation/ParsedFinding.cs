namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// A single review finding extracted from Copilot's <c>ReviewText</c> by
/// <see cref="FindingParser"/>. Feature 021.
/// </summary>
public sealed record ParsedFinding
{
    /// <summary>Ordinal position in the review text (0-based). Used for stable ordering.</summary>
    public required int Index { get; init; }

    /// <summary>File path cited in the finding (e.g., <c>src/Services/Foo.cs</c>).</summary>
    public required string FilePath { get; init; }

    /// <summary>Line number cited in the finding; <c>null</c> if Copilot omitted a line reference.</summary>
    public int? LineNumber { get; init; }

    /// <summary>One of <c>critical</c>, <c>major</c>, <c>minor</c>.</summary>
    public required string Severity { get; init; }

    /// <summary>Human-readable issue description.</summary>
    public required string Description { get; init; }

    /// <summary>
    /// Raw text block for this finding as it appears in <c>ReviewText</c>.
    /// Used by <see cref="FindingFilterer"/> to locate and remove/tag the finding.
    /// </summary>
    public required string OriginalText { get; init; }
}
