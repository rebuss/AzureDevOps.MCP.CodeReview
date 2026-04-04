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
    /// Use for multi-block tool responses (e.g. <c>get_pr_files</c>) where
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
    /// Searches for a <c>=== ... ===</c> header line whose path contains <paramref name="fileName"/>.
    /// </summary>
    public static string? GetFileBlock(this string allText, string fileName)
    {
        var searchPos = 0;
        while (searchPos < allText.Length)
        {
            var blockStart = allText.IndexOf("=== ", searchPos, StringComparison.Ordinal);
            if (blockStart < 0) return null;

            var lineEnd = allText.IndexOf('\n', blockStart);
            var header = lineEnd >= 0 ? allText[blockStart..lineEnd] : allText[blockStart..];

            if (header.Contains(fileName, StringComparison.OrdinalIgnoreCase))
            {
                var nextBlock = allText.IndexOf("\n=== ", blockStart + 1, StringComparison.Ordinal);
                return nextBlock >= 0 ? allText[blockStart..nextBlock] : allText[blockStart..];
            }

            searchPos = (lineEnd >= 0 ? lineEnd : allText.Length) + 1;
        }

        return null;
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
