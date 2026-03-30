namespace REBUSS.Pure.Core.Exceptions;

/// <summary>
/// Exception thrown when the token budget is too small for pagination.
/// </summary>
public class BudgetTooSmallException : Exception
{
    public BudgetTooSmallException(string message) : base(message) { }
    public BudgetTooSmallException(string message, Exception innerException) : base(message, innerException) { }
}
