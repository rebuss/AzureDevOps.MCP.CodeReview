namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Abstraction for executing logic at specific stages of diff processing.
/// Implementations can enrich or transform diff content at defined pipeline points.
/// </summary>
public interface ICodeProcessor
{
    /// <summary>
    /// Adds before/after context to a formatted diff string.
    /// Invoked after the diff is retrieved and formatted but before the final response is returned.
    /// </summary>
    Task<string> AddBeforeAfterContext(string diff, CancellationToken ct = default);
}
