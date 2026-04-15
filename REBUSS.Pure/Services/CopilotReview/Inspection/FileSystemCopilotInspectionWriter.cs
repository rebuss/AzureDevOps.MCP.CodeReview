using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.Services.CopilotReview.Inspection;

/// <summary>
/// Captures prompts and responses to files under a per-user local directory.
/// Feature 022 (internal diagnostic). Env-var-gated at DI composition — when the gate is off,
/// <see cref="NoOpCopilotInspectionWriter"/> is registered instead.
/// </summary>
/// <remarks>
/// <para>
/// Directory layout: <c>{base}/{safeKey}/{yyyyMMdd-HHmmss-fff}-{seq:D3}-{kind}-{role}.md</c>.
/// The per-PR subdirectory is reused across sessions — all material for a given review
/// identifier accumulates in one place. File names carry per-write timestamp + sequence,
/// guaranteeing uniqueness across parallel writes AND across process invocations.
/// </para>
/// <para>
/// <b>Security audit (feature 022, T023 — 2026-04-14):</b> Grep-verified that the
/// <c>content</c> parameter passed to <see cref="WritePromptAsync"/> /
/// <see cref="WriteResponseAsync"/> flows only from (a) the review-prompt template + enriched
/// page content assembled by <c>CopilotPageReviewer</c>, and (b) the Copilot response text.
/// No auth material (<c>GitHubToken</c>, <c>REBUSS_COPILOT_TOKEN</c>, <c>Bearer</c>,
/// <c>Authorization</c>, session IDs) is sourced from any call site. Patterns checked:
/// <c>GitHubToken</c>, <c>REBUSS_COPILOT_TOKEN</c>, <c>Bearer\s</c>, <c>Authorization</c>,
/// <c>_sessionId</c>, <c>Cookie</c>, <c>ApiKey</c>, <c>ClientSecret</c>, <c>Password</c>.
/// Zero matches in the Inspection/ directory. Network-API patterns (<c>HttpClient</c>,
/// <c>WebClient</c>, <c>.SendAsync(</c>, <c>Socket</c>, <c>TcpClient</c>) also verified zero
/// matches — the writer is filesystem-only, satisfying FR-014.
/// </para>
/// </remarks>
internal sealed class FileSystemCopilotInspectionWriter : ICopilotInspectionWriter
{
    internal const string InspectionSubdirName = "copilot-inspection";
    private const string TimestampFormat = "yyyyMMdd-HHmmss-fff";
    private const int MaxSafeKeyLength = 100;

    private static readonly TimeSpan RetentionMaxAge = TimeSpan.FromHours(24);

    private readonly string _baseDirectory;
    private readonly ILogger<FileSystemCopilotInspectionWriter> _logger;

    // Per-safeKey mutable counter. Principle VI exception (feature 022 Complexity Tracking).
    // Wrapper class is required because `Interlocked.Increment` needs a ref to a heap field;
    // a value-type struct in a dictionary can't be atomically incremented in place.
    private readonly ConcurrentDictionary<string, Counter> _counters = new(StringComparer.Ordinal);

    /// <summary>
    /// Production constructor used by DI. Resolves the base directory via
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> and falls back to
    /// <see cref="Path.GetTempPath"/> if the AppData path is not available (e.g., sandboxed
    /// environments). Kicks off fire-and-forget retention cleanup.
    /// </summary>
    public FileSystemCopilotInspectionWriter(ILogger<FileSystemCopilotInspectionWriter> logger)
        : this(ResolveBaseDirectory(logger), logger)
    {
    }

    /// <summary>
    /// Test constructor — accepts an explicit base directory so tests can point at a temp dir
    /// and never touch real <c>%LOCALAPPDATA%</c>. Feature 022 tests use this exclusively.
    /// </summary>
    internal FileSystemCopilotInspectionWriter(
        string baseDirectory,
        ILogger<FileSystemCopilotInspectionWriter> logger)
    {
        _baseDirectory = baseDirectory;
        _logger = logger;

        try
        {
            Directory.CreateDirectory(_baseDirectory);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Failed to create Copilot inspection base directory {BaseDir}; writes will log warnings",
                _baseDirectory);
        }

        // Fire-and-forget retention. Must not block DI composition.
        _ = Task.Run(() => CleanupAsync(_baseDirectory, RetentionMaxAge));
    }

    public Task WritePromptAsync(string reviewKey, string kind, string content, CancellationToken ct)
        => WriteAsync(reviewKey, kind, role: "prompt", content, ct);

    public Task WriteResponseAsync(string reviewKey, string kind, string content, CancellationToken ct)
        => WriteAsync(reviewKey, kind, role: "response", content, ct);

    public Task WriteSummaryAsync(string reviewKey, string content, CancellationToken ct)
        => WriteAsync(reviewKey, kind: "final", role: "summary", content, ct);

    private async Task WriteAsync(string reviewKey, string kind, string role, string content, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var safeKey = Sanitize(reviewKey);
        var dir = Path.Combine(_baseDirectory, safeKey);

        string name = "";
        try
        {
            Directory.CreateDirectory(dir); // idempotent

            var counter = _counters.GetOrAdd(safeKey, _ => new Counter());
            var seq = Interlocked.Increment(ref counter.Value);
            var ts = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture);
            var safeKind = Sanitize(kind);
            name = $"{ts}-{seq:D3}-{safeKind}-{role}.md";
            var fullPath = Path.Combine(dir, name);

            await File.WriteAllTextAsync(fullPath, content, Encoding.UTF8, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Copilot inspection write failed for {SafeKey}/{File}", safeKey, name);
        }
    }

    /// <summary>
    /// Normalizes a caller-supplied identifier into a filesystem-safe form. Pipeline per
    /// research.md Decision 3: colon-to-dash, invalid-char-to-dash, collapse runs of dashes,
    /// trim leading/trailing dashes and dots, fallback to <c>"review"</c> if empty, cap length.
    /// </summary>
    internal static string Sanitize(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "review";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (var ch in input)
        {
            if (ch == ':')
                sb.Append('-');
            else if (Array.IndexOf(invalid, ch) >= 0)
                sb.Append('-');
            else
                sb.Append(ch);
        }

        // Collapse runs of '-' into one.
        var raw = sb.ToString();
        var collapsed = new StringBuilder(raw.Length);
        var prevDash = false;
        foreach (var ch in raw)
        {
            if (ch == '-')
            {
                if (!prevDash) collapsed.Append('-');
                prevDash = true;
            }
            else
            {
                collapsed.Append(ch);
                prevDash = false;
            }
        }

        var trimmed = collapsed.ToString().Trim('-', '.');
        if (trimmed.Length == 0)
            return "review";

        if (trimmed.Length > MaxSafeKeyLength)
            trimmed = trimmed[..MaxSafeKeyLength];

        return trimmed;
    }

    /// <summary>
    /// Resolves the base directory: AppData local → Path.GetTempPath() fallback. Always logs
    /// which path was chosen so maintainers can locate captured material.
    /// </summary>
    private static string ResolveBaseDirectory(ILogger<FileSystemCopilotInspectionWriter> logger)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(appData))
            return Path.Combine(appData, "REBUSS.Pure", InspectionSubdirName);

        var tempBased = Path.Combine(Path.GetTempPath(), "REBUSS.Pure", InspectionSubdirName);
        logger.LogWarning(
            "LocalApplicationData unavailable; falling back to temp path {Path} for Copilot inspection",
            tempBased);
        return tempBased;
    }

    /// <summary>
    /// Per-file retention sweep. Deletes files older than <paramref name="maxAge"/>, then
    /// removes any per-PR subdirectory that became empty. One file or one dir's failure never
    /// aborts the sweep — all exceptions are caught per-item and logged at Warning.
    /// </summary>
    private Task CleanupAsync(string baseDir, TimeSpan maxAge)
    {
        var deletedFiles = 0;
        var deletedDirs = 0;

        // Track which dirs we actually removed files from. Only those become candidates
        // for empty-dir removal — this avoids racing with concurrent writes that may have
        // just created an empty subdir but haven't written its first file yet.
        var dirsWithDeletions = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            if (!Directory.Exists(baseDir))
                return Task.CompletedTask;

            var now = DateTime.UtcNow;

            foreach (var prDir in Directory.EnumerateDirectories(baseDir))
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(prDir))
                    {
                        try
                        {
                            var fileTime = ParseLeadingTimestamp(Path.GetFileName(file))
                                ?? new FileInfo(file).CreationTimeUtc;

                            if (now - fileTime > maxAge)
                            {
                                File.Delete(file);
                                deletedFiles++;
                                dirsWithDeletions.Add(prDir);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex,
                                "Copilot inspection cleanup: failed to evaluate/delete {File}", file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Copilot inspection cleanup: failed enumerating files under {Dir}", prDir);
                }
            }

            // Second pass: only consider dirs we removed files from, and only delete if
            // they're empty now. Brand-new dirs that the writer just created (no prior
            // contents → no deletions here) are left alone.
            foreach (var prDir in dirsWithDeletions)
            {
                try
                {
                    if (Directory.Exists(prDir) && !Directory.EnumerateFileSystemEntries(prDir).Any())
                    {
                        Directory.Delete(prDir);
                        deletedDirs++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Copilot inspection cleanup: failed removing empty dir {Dir}", prDir);
                }
            }

            _logger.LogDebug(
                "Copilot inspection cleanup: {Files} files, {Dirs} empty dirs removed from {Base}",
                deletedFiles, deletedDirs, baseDir);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Copilot inspection cleanup top-level failure under {Base}; proceeding", baseDir);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Parses the leading <c>yyyyMMdd-HHmmss-fff</c> (19 chars) from a filename.
    /// Returns <c>null</c> when the prefix is not a valid timestamp — caller falls back to
    /// <see cref="FileInfo.CreationTimeUtc"/>.
    /// </summary>
    private static DateTime? ParseLeadingTimestamp(string fileName)
    {
        if (fileName.Length < TimestampFormat.Length)
            return null;

        var prefix = fileName[..TimestampFormat.Length];
        if (DateTime.TryParseExact(
                prefix, TimestampFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var dt))
        {
            return dt;
        }
        return null;
    }

    /// <summary>
    /// Mutable int wrapper so <see cref="Interlocked.Increment(ref int)"/> can operate on it.
    /// Principle VI exception: per-safeKey in-memory counter, process-lifetime scope.
    /// </summary>
    private sealed class Counter
    {
        public int Value;
    }
}
