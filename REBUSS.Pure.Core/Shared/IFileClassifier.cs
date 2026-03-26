using REBUSS.Pure.Core.Models;

namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Classifies a file by its path to determine type, priority, and category.
/// </summary>
public interface IFileClassifier
{
    FileClassification Classify(string path);
}
