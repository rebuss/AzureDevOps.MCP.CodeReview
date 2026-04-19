using System.Text;

namespace REBUSS.Pure.Services.CopilotReview.Validation;

/// <summary>
/// Reconstructs the final review text after validation. Emits <c>Valid</c> findings
/// verbatim, <c>Uncertain</c> with a <c>[uncertain]</c> prefix, and omits
/// <c>FalsePositive</c>. Preserves any unparseable prose that the parser couldn't
/// structure. Appends a summary footer when at least one parseable finding exists.
/// Feature 021.
/// </summary>
public static class FindingFilterer
{
    /// <summary>
    /// Assembles the filtered output.
    /// </summary>
    /// <param name="unparseableRemainder">
    /// Prose from the review that could not be matched as a structured finding
    /// (headings, intros, free-form commentary). Preserved verbatim at the top of the
    /// output so no content is silently dropped (FR-012).
    /// </param>
    /// <param name="validated">Validation verdicts in the same order as the original findings.</param>
    public static string Apply(string unparseableRemainder, IReadOnlyList<ValidatedFinding> validated)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(unparseableRemainder))
            sb.Append(unparseableRemainder.TrimEnd()).AppendLine();

        var validCount = 0;
        var filteredCount = 0;
        var uncertainCount = 0;

        foreach (var v in validated)
        {
            switch (v.Verdict)
            {
                case FindingVerdict.Valid:
                    sb.AppendLine(v.Finding.OriginalText);
                    validCount++;
                    break;
                case FindingVerdict.Uncertain:
                    sb.AppendLine("[uncertain] " + v.Finding.OriginalText);
                    uncertainCount++;
                    break;
                case FindingVerdict.FalsePositive:
                    filteredCount++;
                    break;
            }
        }

        // If everything was filtered and there's no remainder, produce a clear signal.
        if (validated.Count > 0 && validCount == 0 && uncertainCount == 0 && string.IsNullOrWhiteSpace(unparseableRemainder))
        {
            sb.Clear();
            sb.AppendLine("No issues found.");
        }

        // Summary footer is appended only when at least one parseable finding existed
        // (SC-005 scoping). Reviews with zero findings get no footer.
        if (validated.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.Append($"_Validation: {validCount} confirmed, {filteredCount} filtered, {uncertainCount} uncertain_");
        }

        return sb.ToString().TrimEnd();
    }
}
