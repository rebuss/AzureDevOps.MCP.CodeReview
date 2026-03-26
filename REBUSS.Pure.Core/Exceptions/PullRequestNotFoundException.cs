namespace REBUSS.Pure.Core.Exceptions;

/// <summary>
/// Exception thrown when a Pull Request is not found.
/// </summary>
public class PullRequestNotFoundException : Exception
{
    public PullRequestNotFoundException(string message) : base(message) { }
    public PullRequestNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
