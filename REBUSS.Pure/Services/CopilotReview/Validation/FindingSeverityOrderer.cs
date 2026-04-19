namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Orders findings by severity (critical → major → minor → other) so the validator
/// presents the most important issues first to Copilot. Stable: items with the same
/// severity preserve their original relative order.
/// <para>
/// Also exposes a verification helper used after parsing the SDK response to detect
/// when Copilot scrambled the verdict order — the validator logs a warning when that
/// happens. Verdict mapping itself is index-based (regex captures <c>Finding {N}</c>),
/// so the warning is informational; correctness of mapping does not depend on response
/// order.
/// </para>
/// </summary>
internal static class FindingSeverityOrderer
{
    /// <summary>
    /// Returns a new list with the source items reordered: critical first, then major,
    /// then minor, then any unknown severity. Stable within each rank.
    /// </summary>
    public static IReadOnlyList<T> Order<T>(IReadOnlyList<T> source, Func<T, string> severitySelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(severitySelector);

        // Project to (item, originalIndex) so OrderBy's stable sort gives us a tiebreaker
        // for items sharing a rank.
        return source
            .Select((item, idx) => (item, idx))
            .OrderBy(t => SeverityRank(severitySelector(t.item)))
            .ThenBy(t => t.idx)
            .Select(t => t.item)
            .ToList();
    }

    /// <summary>
    /// Returns <c>true</c> when the severity ranks of <paramref name="source"/> are
    /// non-decreasing — i.e., critical findings appear before major, which appear
    /// before minor. Used to verify Copilot's response order matches what the
    /// validator sent.
    /// </summary>
    public static bool IsOrderedBySeverity<T>(IReadOnlyList<T> source, Func<T, string> severitySelector)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(severitySelector);

        var lastRank = int.MinValue;
        foreach (var item in source)
        {
            var rank = SeverityRank(severitySelector(item));
            if (rank < lastRank)
                return false;
            lastRank = rank;
        }
        return true;
    }

    private static int SeverityRank(string? severity)
    {
        if (string.IsNullOrEmpty(severity))
            return int.MaxValue;

        return severity.ToLowerInvariant() switch
        {
            "critical" => 0,
            "major" => 1,
            "minor" => 2,
            _ => int.MaxValue,
        };
    }
}
