using System.Text.Json;

namespace REBUSS.Pure.SmokeTests.Infrastructure;

/// <summary>
/// Helper extension methods for parsing MCP tool-call responses in contract tests.
/// </summary>
public static class ToolCallResponseExtensions
{
    /// <summary>
    /// Extracts the tool result <c>content[0].text</c> as plain text.
    /// Throws if the response indicates an error.
    /// </summary>
    public static string GetToolText(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");

        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorText = TryGetFirstText(result) ?? "unknown";
            throw new InvalidOperationException($"Tool returned error: {errorText}");
        }

        return TryGetFirstText(result)
            ?? throw new InvalidOperationException("Tool response has no text content.");
    }

    /// <summary>
    /// Extracts the tool result <c>content[0].text</c> as a parsed <see cref="JsonElement"/>.
    /// Throws if the response indicates an error.
    /// </summary>
    public static JsonElement GetToolContent(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");

        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorText = TryGetFirstText(result) ?? "unknown";
            throw new InvalidOperationException($"Tool returned error: {errorText}");
        }

        var text = TryGetFirstText(result)
            ?? throw new InvalidOperationException("Tool response has no text content to parse as JSON.");
        return JsonDocument.Parse(text).RootElement;
    }

    /// <summary>
    /// Concatenates all <c>content[*].text</c> blocks into a single string.
    /// Use for multi-block tool responses (e.g. <c>get_pr_diff</c>) where
    /// each file diff is a separate content block.
    /// Throws if the response indicates an error.
    /// </summary>
    public static string GetAllToolText(this JsonDocument response)
    {
        var result = response.RootElement.GetProperty("result");

        if (result.TryGetProperty("isError", out var isError) && isError.GetBoolean())
        {
            var errorText = TryGetFirstText(result) ?? "unknown";
            throw new InvalidOperationException($"Tool returned error: {errorText}");
        }

        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Tool response has no content array.");

        var texts = new List<string>();
        foreach (var item in content.EnumerateArray())
        {
            if (item.TryGetProperty("text", out var textElement))
            {
                var text = textElement.GetString();
                if (text != null) texts.Add(text);
            }
        }

        return texts.Count > 0
            ? string.Join("\n", texts)
            : throw new InvalidOperationException("Tool response has no text content blocks.");
    }

    /// <summary>
    /// Extracts the text block for a specific file from a multi-block diff response.
    /// Returns the block whose header matches <c>=== {fileName}</c>.
    /// </summary>
    public static string? GetFileBlock(this string allText, string fileName)
    {
        var marker = $"=== {fileName}";
        var start = allText.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var nextBlock = allText.IndexOf("\n=== ", start + marker.Length, StringComparison.Ordinal);
        return nextBlock >= 0
            ? allText[start..nextBlock]
            : allText[start..];
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
        return TryGetFirstText(result) ?? string.Empty;
    }

    private static string? TryGetFirstText(JsonElement result)
    {
        if (!result.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            return null;

        if (content.GetArrayLength() == 0)
            return null;

        var first = content[0];
        if (!first.TryGetProperty("text", out var textElement))
            return null;

        return textElement.GetString();
    }
}
