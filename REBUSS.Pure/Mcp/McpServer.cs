using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Mcp.Handlers;
using REBUSS.Pure.Mcp.Models;
using System.Text.Json;

namespace REBUSS.Pure.Mcp
{
    /// <summary>
    /// MCP server communicating via stdio (JSON-RPC 2.0).
    /// Orchestrates the message loop: reads from <see cref="IJsonRpcTransport"/>,
    /// parses via <see cref="IJsonRpcSerializer"/>, and dispatches each method
    /// to a registered <see cref="IMcpMethodHandler"/>.
    /// </summary>
    public class McpServer
    {
        private readonly ILogger<McpServer> _logger;
        private readonly Dictionary<string, IMcpMethodHandler> _methodHandlers;
        private readonly IJsonRpcTransport _transport;
        private readonly IJsonRpcSerializer _serializer;

        /// <summary>
        /// Primary constructor for full dependency injection.
        /// All collaborators are supplied as abstractions (DIP).
        /// New methods can be supported by registering additional <see cref="IMcpMethodHandler"/>
        /// instances without modifying this class (OCP).
        /// </summary>
        public McpServer(
            ILogger<McpServer> logger,
            IEnumerable<IMcpMethodHandler> methodHandlers,
            IJsonRpcTransport transport,
            IJsonRpcSerializer serializer)
        {
            _logger = logger;
            _methodHandlers = methodHandlers.ToDictionary(h => h.MethodName);
            _transport = transport;
            _serializer = serializer;
        }

        /// <summary>
        /// Convenience constructor for backward compatibility.
        /// Builds the default set of method handlers from the supplied tool handlers
        /// and wraps the provided streams in a <see cref="StreamJsonRpcTransport"/>.
        /// </summary>
        public McpServer(
            ILogger<McpServer> logger,
            IEnumerable<IMcpToolHandler> toolHandlers,
            Stream? inputStream = null,
            Stream? outputStream = null)
            : this(
                logger,
                BuildMethodHandlers(toolHandlers),
                new StreamJsonRpcTransport(
                    inputStream ?? Console.OpenStandardInput(),
                    outputStream ?? Console.OpenStandardOutput()),
                new SystemTextJsonSerializer())
        { }

        private static IEnumerable<IMcpMethodHandler> BuildMethodHandlers(IEnumerable<IMcpToolHandler> toolHandlers)
        {
            var serializer = new SystemTextJsonSerializer();
            var tools = toolHandlers.ToList();
            var emptyConfiguration = new ConfigurationBuilder().Build();
            var workspaceRootProvider = new McpWorkspaceRootProvider(
                emptyConfiguration,
                NullLogger<McpWorkspaceRootProvider>.Instance);
            return new IMcpMethodHandler[]
            {
                new InitializeMethodHandler(workspaceRootProvider, serializer, NullLogger<InitializeMethodHandler>.Instance),
                new ToolsListMethodHandler(tools),
                new ToolsCallMethodHandler(tools, serializer, NullLogger<ToolsCallMethodHandler>.Instance)
            };
        }

        /// <summary>
        /// Start the MCP server loop.
        /// </summary>
        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("MCP Diff Server starting...");
            _logger.LogInformation("Protocol: Model Context Protocol (MCP) over stdio");
            _logger.LogInformation("Communication: JSON-RPC 2.0");

            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ProcessNextMessageAsync(cancellationToken))
                    break;
            }

            if (_transport is IAsyncDisposable disposableTransport)
                await disposableTransport.DisposeAsync();

            _logger.LogInformation("MCP Diff Server shutting down.");
        }

        /// <summary>
        /// Reads and processes one message from the transport.
        /// Returns <c>false</c> when the stream ends and the loop should stop.
        /// </summary>
        private async Task<bool> ProcessNextMessageAsync(CancellationToken cancellationToken)
        {
            try
            {
                var line = await _transport.ReadMessageAsync(cancellationToken);
                if (line == null)
                {
                    _logger.LogInformation("End of input stream. Shutting down.");
                    return false;
                }

                if (string.IsNullOrWhiteSpace(line))
                    return true;

                _logger.LogDebug("Received request: {Request}", line);

                var response = await ProcessRequestAsync(line, cancellationToken);
                if (response != null)
                    await SendResponseAsync(response, cancellationToken);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing request");
                await SendResponseAsync(InternalErrorResponse(id: null, ex.Message), cancellationToken);
                return true;
            }
        }

        private async Task SendResponseAsync(JsonRpcResponse response, CancellationToken cancellationToken)
        {
            var responseJson = _serializer.Serialize(response);
            await _transport.WriteMessageAsync(responseJson, cancellationToken);
            _logger.LogDebug("Sent response: {Response}", responseJson);
        }

        /// <summary>
        /// Parses the raw JSON line and dispatches it to the appropriate method handler.
        /// </summary>
        private async Task<JsonRpcResponse?> ProcessRequestAsync(string requestJson, CancellationToken cancellationToken)
        {
            if (!TryDeserializeRequest(requestJson, out var request, out var parseError))
                return parseError;

            _logger.LogInformation("Processing method: {Method}", request!.Method);

            return await DispatchAsync(request, cancellationToken);
        }

        private bool TryDeserializeRequest(string requestJson, out JsonRpcRequest? request, out JsonRpcResponse? errorResponse)
        {
            try
            {
                request = _serializer.Deserialize<JsonRpcRequest>(requestJson);
                if (request == null)
                {
                    errorResponse = new JsonRpcResponse { Error = new JsonRpcError { Code = -32600, Message = "Invalid Request" } };
                    return false;
                }

                errorResponse = null;
                return true;
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "Failed to parse JSON-RPC request");
                request = null;
                errorResponse = new JsonRpcResponse { Error = new JsonRpcError { Code = -32700, Message = "Parse error", Data = ex.Message } };
                return false;
            }
        }

        private async Task<JsonRpcResponse> DispatchAsync(JsonRpcRequest request, CancellationToken cancellationToken)
        {
            try
            {
                if (!_methodHandlers.TryGetValue(request.Method, out var handler))
                    throw new McpMethodNotFoundException($"Method not found: {request.Method}");

                var result = await handler.HandleAsync(request, cancellationToken);
                return new JsonRpcResponse { Id = request.Id, Result = result };
            }
            catch (McpMethodNotFoundException ex)
            {
                _logger.LogWarning(ex, "Method not found: {Method}", request.Method);
                return new JsonRpcResponse
                {
                    Id = request.Id,
                    Error = new JsonRpcError { Code = -32601, Message = "Method not found", Data = ex.Message }
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error handling method: {Method}", request.Method);
                return InternalErrorResponse(request.Id, ex.Message);
            }
        }

        private static JsonRpcResponse InternalErrorResponse(object? id, string data) => new()
        {
            Id = id,
            Error = new JsonRpcError { Code = -32603, Message = "Internal error", Data = data }
        };
    }
}
