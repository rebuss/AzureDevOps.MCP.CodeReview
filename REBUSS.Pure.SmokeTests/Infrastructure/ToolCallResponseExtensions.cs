using System.Text.Json;

namespace REBUSS.Pure.SmokeTests.Infrastructure;

/// <summary>
/// Helper extension methods for parsing MCP tool-call responses in contract tests.
/// </summary>
public static class ToolCallResponseExtensions
{
    /// <summary>
    /// Extracts the tool result <c>content[0].text</c> as a parsed <see cref="JsonElement"/>.
    /// Throws if the response indicates an error.
    /// </summary>
    public static JsonElement GetToolContent(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");

        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorText = result.TryGetProperty("content", out var ec)
                ? ec[0].GetProperty("text").GetString() ?? "unknown"
                : "unknown";
            throw new InvalidOperationException($"Tool returned error: {errorText}");
        }

        var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>
    /// Returns true if the tool response indicates an error.
    /// </summary>
    public static bool IsToolError(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");
        return result.TryGetProperty("isError", out var isError) && isError.GetBoolean();
    }

    /// <summary>
    /// Gets the error message text from an error tool response.
    /// </summary>
    public static string GetToolErrorMessage(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");
        if (result.TryGetProperty("content", out var content))
            return content[0].GetProperty("text").GetString() ?? string.Empty;
        return string.Empty;
    }
}
