namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Determines how much surrounding context to add to a diff hunk
/// based on Roslyn syntax analysis of the change.
/// </summary>
public enum ContextDecision
{
    /// <summary>Cosmetic changes only — no context added.</summary>
    None = 0,

    /// <summary>Structural changes — 3 lines before/after.</summary>
    Minimal = 1,

    /// <summary>Semantic changes — up to 10 lines before/after.</summary>
    Full = 2
}
