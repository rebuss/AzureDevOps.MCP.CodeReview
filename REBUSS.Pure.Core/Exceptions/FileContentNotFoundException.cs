namespace REBUSS.Pure.Core.Exceptions;

/// <summary>
/// Exception thrown when a file cannot be found at the specified Git ref.
/// </summary>
public class FileContentNotFoundException : Exception
{
    public FileContentNotFoundException(string message) : base(message) { }
    public FileContentNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
