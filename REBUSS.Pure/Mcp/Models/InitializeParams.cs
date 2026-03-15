using System.Text.Json.Serialization;

namespace REBUSS.Pure.Mcp.Models
{
    /// <summary>
    /// Parameters sent by the client in the <c>initialize</c> request.
    /// Contains optional <c>roots</c> listing workspace directories.
    /// </summary>
    public class InitializeParams
    {
        [JsonPropertyName("roots")]
        public List<McpRoot>? Roots { get; set; }
    }
}
