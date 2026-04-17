using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using GitHub.Copilot.SDK;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Services.CopilotReview;
using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Validates review findings by presenting each (with its enclosing scope source)
/// to a second Copilot SDK pass. Findings whose scope could not be resolved bypass
/// the validator with a deterministic verdict per spec US3.1 / US3.2. Feature 021.
/// <para>
/// Resolvable findings are ordered by severity (critical → major → minor) and split
/// across one or more SDK calls using <see cref="IPageAllocator"/> with the same
/// <c>ReviewBudgetTokens</c> as the review pass — the budget, not a fixed batch size,
/// determines how many findings fit per call. Each SDK call corresponds to one
/// inspection capture pair (<c>validation-{N}</c> prompt + response).
/// </para>
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
    private readonly IPageAllocator _pageAllocator;
    private readonly ITokenEstimator _tokenEstimator;
    private readonly ILogger<FindingValidator> _logger;

    public FindingValidator(
        ICopilotSessionFactory sessionFactory,
        IOptions<CopilotReviewOptions> options,
        ICopilotInspectionWriter inspection,
        IPageAllocator pageAllocator,
        ITokenEstimator tokenEstimator,
        ILogger<FindingValidator> logger)
    {
        _sessionFactory = sessionFactory;
        _options = options;
        _inspection = inspection;
        _pageAllocator = pageAllocator;
        _tokenEstimator = tokenEstimator;
        _logger = logger;
    }

    /// <summary>
    /// Validates each finding. Options are read at method invocation (Principle V),
    /// not cached in the constructor, so configuration hot-reload is respected.
    /// <para>
    /// <b>Ordering contract (load-bearing — do not change silently):</b>
    /// <list type="bullet">
    ///   <item>The returned list has exactly <paramref name="findings"/>.Count elements.</item>
    ///   <item>For every <c>i</c>, <c>result[i]</c> corresponds to <c>findings[i]</c> —
    ///   same order, no reordering even when severity-ordering or token-budget pagination
    ///   shuffles the internal work sequence.</item>
    ///   <item>No slot is null. If parsing a Copilot response drops a verdict, the slot
    ///   is back-filled with a conservative <see cref="FindingVerdict.Valid"/> fallback so
    ///   callers can index without null-checks.</item>
    /// </list>
    /// <c>CopilotReviewOrchestrator</c> relies on this to slice the validator's flat
    /// output per page via <c>validated[offset + j]</c> — any drift would silently
    /// mis-attribute verdicts to the wrong finding.
    /// </para>
    /// </summary>
    /// <param name="findings">Findings paired with their resolved scope.</param>
    /// <param name="reviewKey">Opaque review identifier for inspection capture.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="pageProgress">
    /// Optional callback invoked once per Copilot-bound validation page with
    /// <c>(pageNumber, totalPages)</c> so callers can surface intra-validation
    /// progress (e.g., update <c>CurrentActivity</c>). Not invoked for the deterministic
    /// phase-1 verdicts that bypass Copilot. Never throws — swallowed on callback failure.
    /// </param>
    public async Task<IReadOnlyList<ValidatedFinding>> ValidateAsync(
        IReadOnlyList<FindingWithScope> findings,
        string reviewKey,
        CancellationToken ct,
        Action<int, int>? pageProgress = null)
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

        // Phase 2: order by severity so the most important findings reach Copilot first
        // both within a single page and across pages (page 1 carries the most critical).
        var ordered = FindingSeverityOrderer.Order(toValidate, t => t.Item.Finding.Severity);

        // Phase 3: estimate per-finding token cost and let IPageAllocator split across
        // SDK calls using the same budget the review pass uses. The synthetic candidate
        // captures the actual prompt section that the finding will contribute, so the
        // allocator's bin-packing matches the real call cost.
        var candidates = new List<PackingCandidate>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var section = BuildFindingSection(ordered[i].Item, sequenceNumber: 1);
            var tokens = _tokenEstimator.EstimateTokenCount(section);
            candidates.Add(new PackingCandidate(
                ordered[i].Item.Finding.FilePath,
                tokens,
                FileCategory.Source,
                TotalChanges: 0));
        }

        // Subtract the prompt template overhead from the per-page budget — the allocator
        // only knows about the per-item content cost, not the wrapping template.
        var templateOverhead = _tokenEstimator.EstimateTokenCount(
            LoadPromptTemplate().Replace("{findingsWithScopes}", string.Empty));
        var perPageBudget = Math.Max(1, opts.ReviewBudgetTokens - templateOverhead);

        var allocation = _pageAllocator.Allocate(candidates, perPageBudget);
        var totalPages = allocation.TotalPages;

        // Phase 4: one SDK call per allocator page (kind = "validation-{pageNumber}").
        foreach (var page in allocation.Pages)
        {
            ct.ThrowIfCancellationRequested();

            if (pageProgress is not null)
            {
                try { pageProgress(page.PageNumber, totalPages); }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "pageProgress callback threw; ignoring");
                }
            }

            // Map page items back to their (Item, Index) tuples through the severity-ordered
            // list. PageSliceItem.OriginalIndex is the index into the candidates list, which
            // is parallel to `ordered`.
            var pageBatch = new List<(FindingWithScope Item, int Index)>(page.Items.Count);
            foreach (var pageItem in page.Items)
                pageBatch.Add(ordered[pageItem.OriginalIndex]);

            var inspectionKind = $"validation-{page.PageNumber}";
            try
            {
                var pageVerdicts = await ValidatePageAsync(
                    pageBatch, opts.Model, reviewKey, inspectionKind, page.PageNumber, ct).ConfigureAwait(false);

                for (var j = 0; j < pageBatch.Count; j++)
                {
                    var (item, resultIdx) = pageBatch[j];
                    results[resultIdx] = pageVerdicts[j]
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
                    "Validation page {Page} failed; findings will pass through unfiltered",
                    page.PageNumber);
                foreach (var (item, resultIdx) in pageBatch)
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

    private async Task<ValidatedFinding?[]> ValidatePageAsync(
        IReadOnlyList<(FindingWithScope Item, int Index)> pageBatch,
        string model,
        string reviewKey,
        string inspectionKind,
        int pageNumber,
        CancellationToken ct)
    {
        var prompt = BuildPrompt(pageBatch);

        await _inspection.WritePromptAsync(reviewKey, inspectionKind, prompt, ct).ConfigureAwait(false);

        ICopilotSessionHandle? handle = null;
        try
        {
            handle = await _sessionFactory.CreateSessionAsync(model, ct).ConfigureAwait(false);

            var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var contentBuilder = new StringBuilder();

            using var subscription = handle.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageEvent msg:
                        // Phased-output models (e.g. thinking + response) emit multiple
                        // AssistantMessageEvents per session; accumulate every non-null
                        // Content fragment so we resolve the TCS with the full response
                        // on idle. Empty strings are legitimate stream chunks and must be
                        // appended (no-op for StringBuilder — silently dropping them would
                        // obscure what the model actually emitted); only null is filtered.
                        var chunk = msg.Data?.Content;
                        if (chunk is not null)
                            contentBuilder.Append(chunk);
                        break;
                    case SessionErrorEvent err:
                        tcs.TrySetException(new InvalidOperationException(
                            err.Data?.Message ?? "session error (no message)"));
                        break;
                    case SessionIdleEvent:
                        var captured = contentBuilder.ToString();
                        if (!string.IsNullOrWhiteSpace(captured))
                            tcs.TrySetResult(captured);
                        else
                            tcs.TrySetException(new InvalidOperationException(
                                "session idle without assistant message content"));
                        break;
                }
            });

            await handle.SendAsync(prompt, ct).ConfigureAwait(false);
            var responseText = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);

            await _inspection.WriteResponseAsync(reviewKey, inspectionKind, responseText, ct).ConfigureAwait(false);

            VerifyResponseOrder(responseText, pageBatch, reviewKey, pageNumber);

            return ParseVerdicts(responseText, pageBatch);
        }
        finally
        {
            if (handle is not null)
            {
                try { await handle.DisposeAsync().ConfigureAwait(false); } catch { /* swallow */ }
            }
        }
    }

    /// <summary>
    /// Sanity check: the validator sends findings to Copilot in severity order
    /// (critical → major → minor). When the response lists verdicts in a different
    /// order, log a warning. Verdict mapping itself is index-based via
    /// <see cref="ParseVerdicts"/>, so correctness is preserved either way.
    /// </summary>
    private void VerifyResponseOrder(
        string responseText,
        IReadOnlyList<(FindingWithScope Item, int Index)> pageBatch,
        string reviewKey,
        int pageNumber)
    {
        var matches = VerdictPattern().Matches(responseText);
        if (matches.Count <= 1)
            return;

        var responseOrdered = new List<FindingWithScope>(matches.Count);
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups["n"].Value, out var n) || n < 1 || n > pageBatch.Count)
                continue;
            responseOrdered.Add(pageBatch[n - 1].Item);
        }

        if (!FindingSeverityOrderer.IsOrderedBySeverity(responseOrdered, f => f.Finding.Severity))
        {
            _logger.LogWarning(
                "Copilot validation for '{ReviewKey}' page {Page} returned verdicts out of severity order; mapping is index-based so verdicts remain correct",
                reviewKey, pageNumber);
        }
    }

    private static string BuildPrompt(IReadOnlyList<(FindingWithScope Item, int Index)> pageBatch)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < pageBatch.Count; i++)
            sb.Append(BuildFindingSection(pageBatch[i].Item, sequenceNumber: i + 1));

        return LoadPromptTemplate().Replace("{findingsWithScopes}", sb.ToString());
    }

    /// <summary>
    /// Renders one finding's section of the validation prompt. Shared between prompt
    /// assembly and per-finding token estimation so the budget calculus matches the
    /// real call cost.
    /// </summary>
    private static string BuildFindingSection(FindingWithScope scope, int sequenceNumber)
    {
        var finding = scope.Finding;
        var sb = new StringBuilder();
        sb.AppendLine($"## Finding {sequenceNumber}");
        sb.AppendLine($"**Severity:** {finding.Severity}");
        sb.AppendLine($"**File:** {finding.FilePath}{(finding.LineNumber is int ln ? $" (line {ln})" : "")}");
        sb.AppendLine($"**Issue:** {finding.Description}");
        sb.AppendLine();
        sb.AppendLine($"### Source code of enclosing scope: `{scope.ScopeName}`");
        sb.AppendLine("```csharp");
        sb.AppendLine(scope.ScopeSource);
        sb.AppendLine("```");
        sb.AppendLine();
        return sb.ToString();
    }

    private static ValidatedFinding?[] ParseVerdicts(
        string responseText,
        IReadOnlyList<(FindingWithScope Item, int Index)> pageBatch)
    {
        var verdicts = new ValidatedFinding?[pageBatch.Count];
        var matches = VerdictPattern().Matches(responseText);

        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups["n"].Value, out var n) || n < 1 || n > pageBatch.Count)
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
                Finding = pageBatch[n - 1].Item.Finding,
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
