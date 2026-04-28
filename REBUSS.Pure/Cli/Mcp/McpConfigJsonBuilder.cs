using System.Text.Json;

namespace REBUSS.Pure.Cli.Mcp;

/// <summary>
/// Authors the JSON content of an MCP server configuration file (<c>mcp.json</c> /
/// <c>.mcp.json</c> / <c>.claude.json</c>). Two entry points:
/// <list type="bullet">
///   <item><see cref="Build"/> — emit a fresh document with a single <c>REBUSS.Pure</c>
///         server entry (callers pass already-escaped paths suitable for direct string interpolation).</item>
///   <item><see cref="Merge"/> — preserve all unrelated top-level keys and other server
///         entries already present in the file, swap in / overwrite the <c>REBUSS.Pure</c>
///         entry, and carry over an existing <c>--pat</c> argument when the caller did
///         not supply one. Falls back to <see cref="Build"/> when the existing content
///         is not valid JSON.</item>
/// </list>
/// </summary>
internal static class McpConfigJsonBuilder
{
    /// <summary>
    /// Emits a fresh MCP configuration document containing a single <c>REBUSS.Pure</c>
    /// server entry. Path arguments must already be escaped for direct string
    /// interpolation (e.g. backslash-doubled on Windows) — escaping is the caller's
    /// responsibility on this path because the body is hand-formatted, not produced
    /// by <see cref="Utf8JsonWriter"/>.
    /// </summary>
    internal static string Build(
        string normalizedExePath,
        string normalizedRepoPath,
        string? pat = null,
        bool useMcpServersKey = false,
        string? agent = null)
    {
        var patArgs = string.IsNullOrWhiteSpace(pat)
            ? string.Empty
            : $", \"--pat\", {JsonSerializer.Serialize(pat)}";

        // JsonSerializer.Serialize emits the surrounding quotes and escapes the value.
        // Today NormalizeAgent constrains agent to "copilot" / "claude", but the
        // signature accepts any string?, so we escape the same way as patArgs.
        var agentArgs = string.IsNullOrWhiteSpace(agent)
            ? string.Empty
            : $", \"--agent\", {JsonSerializer.Serialize(agent)}";

        var serversKey = useMcpServersKey ? "mcpServers" : "servers";

        return $$"""
            {
              "{{serversKey}}": {
                "REBUSS.Pure": {
                  "type": "stdio",
                  "command": "{{normalizedExePath}}",
                  "args": ["--repo", "{{normalizedRepoPath}}"{{patArgs}}{{agentArgs}}]
                }
              }
            }
            """;
    }

    /// <summary>
    /// Merges the <c>REBUSS.Pure</c> server entry into an existing MCP configuration
    /// document. All unrelated top-level keys and all other server entries are
    /// preserved verbatim. When the caller passes <paramref name="pat"/> as null /
    /// whitespace AND the existing document already contains a <c>--pat</c> arg under
    /// <c>REBUSS.Pure</c>, the existing PAT is carried over. When the existing
    /// content is not valid JSON, falls back to <see cref="Build"/> (replacing the
    /// file entirely). Accepts raw, unescaped paths — JSON escaping is handled by
    /// <see cref="Utf8JsonWriter"/>.
    /// </summary>
    internal static string Merge(
        string existingJson,
        string rawExePath,
        string rawRepoPath,
        string? pat = null,
        bool useMcpServersKey = false,
        string? agent = null)
    {
        var serversKey = useMcpServersKey ? "mcpServers" : "servers";

        try
        {
            using var doc = JsonDocument.Parse(existingJson);
            var root = doc.RootElement;

            // JsonDocument.Parse accepts any well-formed JSON value (array, null, string,
            // number, bool) — but every downstream `EnumerateObject` / `TryGetProperty`
            // call below assumes the root is an object and throws InvalidOperationException
            // when it isn't. Treat a non-object root the same as malformed input: replace
            // the file with a fresh Build document so the user never hits an uncaught crash
            // from a hand-edited mcp.json that happens to contain `[]` or `null`.
            if (root.ValueKind != JsonValueKind.Object)
                return BuildFallback();

            var options = new JsonWriterOptions { Indented = true };
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms, options))
            {
                writer.WriteStartObject();

                // Copy all top-level properties except the servers key verbatim
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name != serversKey)
                        prop.WriteTo(writer);
                }

                // Write merged servers block
                writer.WritePropertyName(serversKey);
                writer.WriteStartObject();

                // Copy existing servers except REBUSS.Pure. The servers key may be present
                // but hold a non-object value (e.g. `"servers": []` after a bad hand-edit) —
                // skip the inner copy in that case rather than letting EnumerateObject throw.
                if (root.TryGetProperty(serversKey, out var serversEl) && serversEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var server in serversEl.EnumerateObject())
                    {
                        if (server.Name != "REBUSS.Pure")
                            server.WriteTo(writer);
                    }
                }

                // Write the REBUSS.Pure entry — Utf8JsonWriter handles JSON escaping of raw paths
                // If no PAT was supplied, carry over any existing PAT from the current config.
                var effectivePat = pat;
                if (string.IsNullOrWhiteSpace(effectivePat))
                    effectivePat = ExtractExistingPat(root, serversKey);

                writer.WritePropertyName("REBUSS.Pure");
                writer.WriteStartObject();
                writer.WriteString("type", "stdio");
                writer.WriteString("command", rawExePath);
                writer.WritePropertyName("args");
                writer.WriteStartArray();
                writer.WriteStringValue("--repo");
                writer.WriteStringValue(rawRepoPath);
                if (!string.IsNullOrWhiteSpace(effectivePat))
                {
                    writer.WriteStringValue("--pat");
                    writer.WriteStringValue(effectivePat);
                }
                if (!string.IsNullOrWhiteSpace(agent))
                {
                    writer.WriteStringValue("--agent");
                    writer.WriteStringValue(agent);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();

                writer.WriteEndObject(); // servers
                writer.WriteEndObject(); // root
            }

            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }
        catch (JsonException)
        {
            // Existing file is not valid JSON — replace it entirely
            return BuildFallback();
        }

        string BuildFallback()
        {
            var normalizedExePath = rawExePath.Replace("\\", "\\\\");
            var normalizedRepoPath = rawRepoPath.Replace("\\", "\\\\");
            return Build(normalizedExePath, normalizedRepoPath, pat, useMcpServersKey, agent);
        }
    }

    /// <summary>
    /// Extracts the <c>--pat</c> argument value from an existing <c>REBUSS.Pure</c>
    /// server entry, or returns <c>null</c> when no PAT is present.
    /// </summary>
    private static string? ExtractExistingPat(JsonElement root, string serversKey)
    {
        // Each TryGetProperty / EnumerateArray below requires the receiving element to
        // be the right kind (object or array) — guard explicitly so a hand-edited
        // mcp.json with mismatched shapes (e.g. `"servers": []`, `"args": "string"`)
        // returns null instead of throwing InvalidOperationException up to the caller.
        if (root.ValueKind != JsonValueKind.Object)
            return null;

        if (!root.TryGetProperty(serversKey, out var servers) || servers.ValueKind != JsonValueKind.Object)
            return null;

        if (!servers.TryGetProperty("REBUSS.Pure", out var entry) || entry.ValueKind != JsonValueKind.Object)
            return null;

        if (!entry.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
            return null;

        var argList = args.EnumerateArray().Select(a => a.GetString()).ToList();
        var patIndex = argList.IndexOf("--pat");
        if (patIndex >= 0 && patIndex + 1 < argList.Count)
            return argList[patIndex + 1];

        return null;
    }
}
