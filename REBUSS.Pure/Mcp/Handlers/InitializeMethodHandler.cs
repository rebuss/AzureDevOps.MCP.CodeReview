using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Mcp.Models;
using System.Text.Json;

namespace REBUSS.Pure.Mcp.Handlers
{
    /// <summary>
    /// Handles the JSON-RPC <c>initialize</c> method.
    /// Extracts MCP roots from the request, stores them via <see cref="IWorkspaceRootProvider"/>,
    /// and returns protocol version, capabilities, and server info.
    /// </summary>
    public class InitializeMethodHandler : IMcpMethodHandler
    {
        private readonly IWorkspaceRootProvider _workspaceRootProvider;
        private readonly IJsonRpcSerializer _serializer;
        private readonly ILogger<InitializeMethodHandler> _logger;

        public string MethodName => "initialize";

        public InitializeMethodHandler(
            IWorkspaceRootProvider workspaceRootProvider,
            IJsonRpcSerializer serializer,
            ILogger<InitializeMethodHandler> logger)
        {
            _workspaceRootProvider = workspaceRootProvider;
            _serializer = serializer;
            _logger = logger;
        }

        public Task<object> HandleAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        {
            if (request.Params is System.Text.Json.JsonElement paramsForLog)
                _logger.LogDebug("initialize request params: {Params}", paramsForLog.GetRawText());

            ExtractAndStoreRoots(request);

            var result = new InitializeResult
            {
                ProtocolVersion = "2024-11-05",
                Capabilities = new ServerCapabilities
                {
                    Tools = new ToolsCapability { ListChanged = false }
                },
                ServerInfo = new ServerInfo
                {
                    Name = "REBUSS.Pure",
                    Version = "1.0.0"
                }
            };

            return Task.FromResult<object>(result);
        }

        private void ExtractAndStoreRoots(JsonRpcRequest request)
        {
            try
            {
                if (request.Params is not JsonElement paramsElement)
                    return;

                var initParams = _serializer.Deserialize<InitializeParams>(paramsElement.GetRawText());
                if (initParams?.Roots is null || initParams.Roots.Count == 0)
                {
                    _logger.LogDebug("No MCP roots provided in initialize request");
                    return;
                }

                var rootUris = initParams.Roots
                    .Where(r => !string.IsNullOrWhiteSpace(r.Uri))
                    .Select(r => r.Uri)
                    .ToList();

                _workspaceRootProvider.SetRoots(rootUris);

                _logger.LogDebug("Stored {RootCount} MCP root(s) from initialize request", rootUris.Count);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to extract MCP roots from initialize request");
            }
        }
    }
}
