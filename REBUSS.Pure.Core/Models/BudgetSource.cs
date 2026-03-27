namespace REBUSS.Pure.Core.Models;

/// <summary>
/// How the context window budget was determined.
/// </summary>
public enum BudgetSource
{
    /// <summary>Agent provided an explicit token count.</summary>
    Explicit,

    /// <summary>Resolved from model identifier via registry.</summary>
    Registry,

    /// <summary>Fallback — neither explicit nor registry match.</summary>
    Default
}
