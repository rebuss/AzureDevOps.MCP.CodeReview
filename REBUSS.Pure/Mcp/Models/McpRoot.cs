using System.Text.Json.Serialization;

namespace REBUSS.Pure.Mcp.Models
{
    /// <summary>
    /// Represents an MCP root provided by the client during initialization.
    /// Each root contains a <c>file://</c> URI pointing to a workspace directory.
    /// </summary>
    public class McpRoot
    {
        [JsonPropertyName("uri")]
        public string Uri { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}
