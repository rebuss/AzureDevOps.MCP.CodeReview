namespace REBUSS.Pure.Core.Exceptions;

/// <summary>
/// Exception thrown when a file is not found in a Pull Request.
/// </summary>
public class FileNotFoundInPullRequestException : Exception
{
    public FileNotFoundInPullRequestException(string message) : base(message) { }
    public FileNotFoundInPullRequestException(string message, Exception innerException) : base(message, innerException) { }
}
