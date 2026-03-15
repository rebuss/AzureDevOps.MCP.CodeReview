using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Handlers;
using REBUSS.Pure.Mcp.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tests.Mcp;

public class InitializeMethodHandlerTests
{
    private readonly IWorkspaceRootProvider _workspaceRootProvider = Substitute.For<IWorkspaceRootProvider>();
    private readonly IJsonRpcSerializer _serializer = new SystemTextJsonSerializer();

    private InitializeMethodHandler CreateHandler()
    {
        return new InitializeMethodHandler(
            _workspaceRootProvider,
            _serializer,
            NullLogger<InitializeMethodHandler>.Instance);
    }

    [Fact]
    public async Task HandleAsync_ReturnsInitializeResult()
    {
        var handler = CreateHandler();
        var request = new JsonRpcRequest { Id = "1", Method = "initialize" };

        var result = await handler.HandleAsync(request, CancellationToken.None);

        var initResult = Assert.IsType<InitializeResult>(result);
        Assert.Equal("2024-11-05", initResult.ProtocolVersion);
        Assert.Equal("REBUSS.Pure", initResult.ServerInfo.Name);
    }

    [Fact]
    public async Task HandleAsync_StoresRoots_WhenProvidedInParams()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.SerializeToElement(new
        {
            roots = new object[]
            {
                new { uri = "file:///c:/projects/repo1", name = "repo1" },
                new { uri = "file:///c:/projects/repo2" }
            }
        });

        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = paramsJson
        };

        await handler.HandleAsync(request, CancellationToken.None);

        _workspaceRootProvider.Received(1).SetRoots(
            Arg.Is<IReadOnlyList<string>>(roots =>
                roots.Count == 2 &&
                roots[0] == "file:///c:/projects/repo1" &&
                roots[1] == "file:///c:/projects/repo2"));
    }

    [Fact]
    public async Task HandleAsync_DoesNotStoreRoots_WhenNoParamsProvided()
    {
        var handler = CreateHandler();
        var request = new JsonRpcRequest { Id = "1", Method = "initialize" };

        await handler.HandleAsync(request, CancellationToken.None);

        _workspaceRootProvider.DidNotReceive().SetRoots(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task HandleAsync_DoesNotStoreRoots_WhenRootsIsEmpty()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.SerializeToElement(new { roots = Array.Empty<object>() });
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = paramsJson
        };

        await handler.HandleAsync(request, CancellationToken.None);

        _workspaceRootProvider.DidNotReceive().SetRoots(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task HandleAsync_DoesNotStoreRoots_WhenRootsIsNull()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.SerializeToElement(new { capabilities = new { } });
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = paramsJson
        };

        await handler.HandleAsync(request, CancellationToken.None);

        _workspaceRootProvider.DidNotReceive().SetRoots(Arg.Any<IReadOnlyList<string>>());
    }

    [Fact]
    public async Task HandleAsync_SkipsRoots_WithEmptyUri()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.SerializeToElement(new
        {
            roots = new[]
            {
                new { uri = "file:///c:/projects/repo1" },
                new { uri = "" },
                new { uri = "file:///c:/projects/repo2" }
            }
        });

        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = paramsJson
        };

        await handler.HandleAsync(request, CancellationToken.None);

        _workspaceRootProvider.Received(1).SetRoots(
            Arg.Is<IReadOnlyList<string>>(roots =>
                roots.Count == 2 &&
                roots[0] == "file:///c:/projects/repo1" &&
                roots[1] == "file:///c:/projects/repo2"));
    }

    [Fact]
    public async Task HandleAsync_DoesNotThrow_WhenParamsHasUnexpectedShape()
    {
        var handler = CreateHandler();
        var paramsJson = JsonSerializer.SerializeToElement(new { unexpected = "value" });
        var request = new JsonRpcRequest
        {
            Id = "1",
            Method = "initialize",
            Params = paramsJson
        };

        var result = await handler.HandleAsync(request, CancellationToken.None);

        Assert.IsType<InitializeResult>(result);
        _workspaceRootProvider.DidNotReceive().SetRoots(Arg.Any<IReadOnlyList<string>>());
    }
}
