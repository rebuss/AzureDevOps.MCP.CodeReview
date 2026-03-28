using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models;

/// <summary>
/// Included in PR tool responses when the data fingerprint has changed
/// between page requests (staleness detection).
/// </summary>
public sealed class StalenessWarningResult
{
    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;

    [JsonPropertyName("originalFingerprint")]
    public string OriginalFingerprint { get; set; } = string.Empty;

    [JsonPropertyName("currentFingerprint")]
    public string CurrentFingerprint { get; set; } = string.Empty;
}
