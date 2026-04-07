namespace REBUSS.Pure.Services.PrEnrichment;

/// <summary>
/// Sanitized failure context surfaced to the user via the friendly-status
/// pipeline. Contains no stack trace and no fully-qualified type names.
/// Local file paths under <c>%LOCALAPPDATA%</c> are redacted to honor
/// Constitution Principle VIII (Privacy &amp; Local Processing).
/// </summary>
public sealed record PrEnrichmentFailure
{
    public required string ExceptionTypeName { get; init; }
    public required string SanitizedMessage { get; init; }
    public required DateTimeOffset FailedAt { get; init; }

    /// <summary>
    /// Build a sanitized failure record from a thrown exception.
    /// Strips namespace, redacts local-app-data paths, never includes stack trace.
    /// </summary>
    public static PrEnrichmentFailure From(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);

        var typeName = ex.GetType().Name;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var message = ex.Message ?? string.Empty;

        if (!string.IsNullOrEmpty(localAppData))
        {
            message = message.Replace(localAppData, "<localappdata>", StringComparison.OrdinalIgnoreCase);
        }

        return new PrEnrichmentFailure
        {
            ExceptionTypeName = typeName,
            SanitizedMessage = message,
            FailedAt = DateTimeOffset.UtcNow,
        };
    }
}
