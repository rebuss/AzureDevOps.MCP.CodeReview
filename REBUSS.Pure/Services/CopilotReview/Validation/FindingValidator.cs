using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Validates review findings by presenting each (with its enclosing scope source)
/// to a second Copilot pass. Findings whose scope could not be resolved bypass the
/// validator with a deterministic verdict per spec US3.1 / US3.2. Feature 021.
/// </summary>
internal sealed partial class FindingValidator
{
    private const string PromptResourceName = "REBUSS.Pure.Services.CopilotReview.Prompts.copilot-finding-validation.md";
    private static string? _cachedPromptTemplate;

    [GeneratedRegex(
        @"\*\*\s*Finding\s+(?<n>\d+)\s*:\s*(?<verdict>VALID|FALSE_POSITIVE|UNCERTAIN)\s*\*\*\s*(?:[—\-–:]\s*(?<reason>[^\n\r]*))?",
        RegexOptions.IgnoreCase)]
    private static partial Regex VerdictPattern();

    private readonly ICopilotSessionFactory _sessionFactory;
    private readonly IOptions<CopilotReviewOptions> _options;
    private readonly ICopilotInspectionWriter _inspection;
    private readonly ILogger<FindingValidator> _logger;

    public FindingValidator(
        ICopilotSessionFactory sessionFactory,
        IOptions<CopilotReviewOptions> options,
        ICopilotInspectionWriter inspection,
        ILogger<FindingValidator> logger)
    {
        _sessionFactory = sessionFactory;
        _options = options;
        _inspection = inspection;
        _logger = logger;
    }

    /// <summary>
    /// Validates each finding. Options are read at method invocation (Principle V),
    /// not cached in the constructor, so configuration hot-reload is respected.
    /// </summary>
    /// <param name="findings">Findings paired with their resolved scope.</param>
    /// <param name="reviewKey">Opaque review identifier for inspection capture.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<IReadOnlyList<ValidatedFinding>> ValidateAsync(
        IReadOnlyList<FindingWithScope> findings,
        string reviewKey,
        CancellationToken ct)
    {
        if (findings.Count == 0)
            return Array.Empty<ValidatedFinding>();

        var opts = _options.Value;
        var results = new ValidatedFinding[findings.Count];

        // Phase 1: deterministic verdicts for findings whose scope could not be resolved.
        // No Copilot call needed.
        var toValidate = new List<(FindingWithScope Item, int Index)>();
        for (var i = 0; i < findings.Count; i++)
        {
            var f = findings[i];
            switch (f.ResolutionFailure)
            {
                case ScopeResolutionFailure.NotCSharp:
                    // Spec US3.1: non-C# files pass through unfiltered.
                    results[i] = new ValidatedFinding
                    {
                        Finding = f.Finding,
                        Verdict = FindingVerdict.Valid,
                        Reason = "non-C# file; scope validation not applicable",
                    };
                    break;
                case ScopeResolutionFailure.SourceUnavailable:
                    results[i] = new ValidatedFinding
                    {
                        Finding = f.Finding,
                        Verdict = FindingVerdict.Uncertain,
                        Reason = "source code unavailable for validation",
                    };
                    break;
                case ScopeResolutionFailure.ScopeNotFound:
                    results[i] = new ValidatedFinding
                    {
                        Finding = f.Finding,
                        Verdict = FindingVerdict.Uncertain,
                        Reason = "enclosing scope could not be located",
                    };
                    break;
                case ScopeResolutionFailure.None:
                    toValidate.Add((f, i));
                    break;
            }
        }

        if (toValidate.Count == 0)
            return results;

        // Phase 2: batch the resolvable findings and validate against Copilot.
        var batchSize = Math.Max(1, opts.ValidationBatchSize);
        var batchNumber = 0;
        for (var start = 0; start < toValidate.Count; start += batchSize)
        {
            batchNumber++;
            var batch = toValidate.GetRange(start, Math.Min(batchSize, toValidate.Count - start));
            ct.ThrowIfCancellationRequested();

            var inspectionKind = $"validation-batch-{batchNumber}";
            try
            {
                var batchResults = await ValidateBatchAsync(batch, opts.Model, reviewKey, inspectionKind, ct).ConfigureAwait(false);
                for (var j = 0; j < batch.Count; j++)
                {
                    var (item, resultIdx) = batch[j];
                    results[resultIdx] = batchResults[j]
                        ?? new ValidatedFinding
                        {
                            Finding = item.Finding,
                            Verdict = FindingVerdict.Valid, // conservative: unparsed verdict → keep finding
                            Reason = "validator response missing this finding",
                        };
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Validation batch starting at index {Start} failed; findings will pass through unfiltered",
                    start);
                foreach (var (item, resultIdx) in batch)
                {
                    results[resultIdx] = new ValidatedFinding
                    {
                        Finding = item.Finding,
                        Verdict = FindingVerdict.Valid, // graceful degradation (FR-012)
                        Reason = $"validation call failed: {ex.Message}",
                    };
                }
            }
        }

        return results;
    }

    private async Task<ValidatedFinding?[]> ValidateBatchAsync(
        IReadOnlyList<(FindingWithScope Item, int Index)> batch,
        string model,
        string reviewKey,
        string inspectionKind,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(batch);

        await _inspection.WritePromptAsync(reviewKey, inspectionKind, prompt, ct).ConfigureAwait(false);

        ICopilotSessionHandle? handle = null;
        try
        {
            handle = await _sessionFactory.CreateSessionAsync(model, ct).ConfigureAwait(false);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            string? capturedContent = null;

            using var subscription = handle.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        capturedContent = msg.Data?.Content;
                        break;
                    case SessionErrorEvent err:
                        tcs.TrySetException(new InvalidOperationException(
                            err.Data?.Message ?? "session error (no message)"));
                        break;
                    case SessionIdleEvent:
                        if (!string.IsNullOrWhiteSpace(capturedContent))
                            tcs.TrySetResult(capturedContent!);
                        else
                            tcs.TrySetException(new InvalidOperationException(
                                "session idle without assistant message content"));
                        break;
                }
            });

            await handle.SendAsync(prompt, ct).ConfigureAwait(false);
            var responseText = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

            await _inspection.WriteResponseAsync(reviewKey, inspectionKind, responseText, ct).ConfigureAwait(false);

            return ParseVerdicts(responseText, batch);
        }
        finally
        {
            if (handle is not null)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }
        }
    }

    private static string BuildPrompt(IReadOnlyList<(FindingWithScope Item, int Index)> batch)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < batch.Count; i++)
        {
            var finding = batch[i].Item.Finding;
            var scope = batch[i].Item;
            sb.AppendLine($"## Finding {i + 1}");
            sb.AppendLine($"**Severity:** {finding.Severity}");
            sb.AppendLine($"**File:** {finding.FilePath}{(finding.LineNumber is int ln ? $" (line {ln})" : "")}");
            sb.AppendLine($"**Issue:** {finding.Description}");
            sb.AppendLine();
            sb.AppendLine($"### Source code of enclosing scope: `{scope.ScopeName}`");
            sb.AppendLine("```csharp");
            sb.AppendLine(scope.ScopeSource);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return LoadPromptTemplate().Replace("{findingsWithScopes}", sb.ToString());
    }

    private static ValidatedFinding?[] ParseVerdicts(
        string responseText,
        IReadOnlyList<(FindingWithScope Item, int Index)> batch)
    {
        var verdicts = new ValidatedFinding?[batch.Count];
        var matches = VerdictPattern().Matches(responseText);

        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups["n"].Value, out var n) || n < 1 || n > batch.Count)
                continue;

            var verdictStr = m.Groups["verdict"].Value.ToUpperInvariant();
            var verdict = verdictStr switch
            {
                "VALID" => FindingVerdict.Valid,
                "FALSE_POSITIVE" => FindingVerdict.FalsePositive,
                "UNCERTAIN" => FindingVerdict.Uncertain,
                _ => FindingVerdict.Valid, // conservative fallback
            };

            var reason = m.Groups["reason"].Success ? m.Groups["reason"].Value.Trim() : null;
            verdicts[n - 1] = new ValidatedFinding
            {
                Finding = batch[n - 1].Item.Finding,
                Verdict = verdict,
                Reason = string.IsNullOrWhiteSpace(reason) ? null : reason,
            };
        }

        return verdicts;
    }

    private static string LoadPromptTemplate()
    {
        return LazyInitializer.EnsureInitialized(ref _cachedPromptTemplate, () =>
        {
            var assembly = typeof(FindingValidator).Assembly;
            var stream = assembly.GetManifestResourceStream(PromptResourceName);
            if (stream is null)
            {
                var match = Array.Find(
                    assembly.GetManifestResourceNames(),
                    n => n.EndsWith("copilot-finding-validation.md", StringComparison.OrdinalIgnoreCase));
                if (match is not null)
                    stream = assembly.GetManifestResourceStream(match);
            }
            if (stream is null)
                throw new FileNotFoundException(
                    "Embedded resource 'copilot-finding-validation.md' not found in REBUSS.Pure assembly.");

            using (stream)
            using (var reader = new StreamReader(stream))
            {
                return reader.ReadToEnd();
            }
        })!;
    }
}
