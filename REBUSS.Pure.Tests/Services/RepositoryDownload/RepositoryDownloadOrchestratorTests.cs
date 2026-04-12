using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Services.RepositoryDownload;

namespace REBUSS.Pure.Tests.Services.RepositoryDownload;

public class RepositoryDownloadOrchestratorTests : IDisposable
{
    private readonly IRepositoryArchiveProvider _archiveProvider = Substitute.For<IRepositoryArchiveProvider>();
    private readonly IHostApplicationLifetime _lifetime = Substitute.For<IHostApplicationLifetime>();
    private readonly RepositoryDownloadOrchestrator _orchestrator;
    private readonly CancellationTokenSource _stoppingCts = new();

    public RepositoryDownloadOrchestratorTests()
    {
        _lifetime.ApplicationStopping.Returns(_stoppingCts.Token);
        _orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider,
            _lifetime,
            Array.Empty<IRepositoryReadyHandler>(),
            NullLogger<RepositoryDownloadOrchestrator>.Instance);
    }

    public void Dispose()
    {
        _stoppingCts.Dispose();
    }

    [Fact]
    public async Task TriggerDownload_FirstCall_StartsDownload()
    {
        var tcs = new TaskCompletionSource();
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        _orchestrator.TriggerDownloadAsync(42, "abc123");

        var state = _orchestrator.GetState();
        Assert.Equal(DownloadStatus.Downloading, state.Status);
        Assert.Equal(42, state.PrNumber);
        Assert.Equal("abc123", state.CommitRef);

        tcs.SetResult();
        // Let background task settle (no zip created, so extraction fails — expected)
        await _orchestrator.GetExtractedPathAsync();
    }

    [Fact]
    public async Task TriggerDownload_SecondCall_SamePr_DoesNotDuplicate()
    {
        var callCount = 0;
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Interlocked.Increment(ref callCount);
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        _orchestrator.TriggerDownloadAsync(42, "abc123");
        await _orchestrator.GetExtractedPathAsync();

        _orchestrator.TriggerDownloadAsync(42, "abc123");
        await _orchestrator.GetExtractedPathAsync();

        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task TriggerDownload_DifferentPr_ReplacesOldDownload()
    {
        var callCount = 0;
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                Interlocked.Increment(ref callCount);
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        _orchestrator.TriggerDownloadAsync(42, "abc123");
        await _orchestrator.GetExtractedPathAsync();

        _orchestrator.TriggerDownloadAsync(99, "def456");
        await _orchestrator.GetExtractedPathAsync();

        Assert.Equal(2, callCount);
        var state = _orchestrator.GetState();
        Assert.Equal(99, state.PrNumber);
        Assert.Equal("def456", state.CommitRef);
    }

    [Fact]
    public async Task TriggerDownload_DownloadFails_StateIsFailed()
    {
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        _orchestrator.TriggerDownloadAsync(42, "abc123");
        await _orchestrator.GetExtractedPathAsync();

        var state = _orchestrator.GetState();
        Assert.Equal(DownloadStatus.Failed, state.Status);
        Assert.Equal("Network error", state.ErrorMessage);
    }

    [Fact]
    public async Task TriggerDownload_Success_StateIsReady()
    {
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                // Create a fake ZIP file so extraction doesn't fail
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        _orchestrator.TriggerDownloadAsync(42, "abc123");

        var path = await _orchestrator.GetExtractedPathAsync();

        var state = _orchestrator.GetState();
        Assert.Equal(DownloadStatus.Ready, state.Status);
        Assert.NotNull(state.ExtractedPath);
    }

    [Fact]
    public async Task TriggerDownload_Success_DeletesZipFile()
    {
        string? zipPath = null;
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                zipPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
                CreateMinimalZip(zipPath);
                return Task.CompletedTask;
            });

        _orchestrator.TriggerDownloadAsync(42, "abc123");
        await _orchestrator.GetExtractedPathAsync();

        Assert.NotNull(zipPath);
        Assert.False(File.Exists(zipPath), "ZIP file should be deleted after extraction");
    }

    [Fact]
    public async Task GetExtractedPathAsync_WaitsForCompletion()
    {
        var tcs = new TaskCompletionSource();
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await tcs.Task;
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
            });

        _orchestrator.TriggerDownloadAsync(42, "abc123");

        var resultTask = _orchestrator.GetExtractedPathAsync();
        Assert.False(resultTask.IsCompleted);

        tcs.SetResult();
        var path = await resultTask;

        // Path may or may not be non-null depending on timing; the key is it didn't throw
        // and it waited for the download task to finish.
    }

    [Fact]
    public async Task GetExtractedPathAsync_OnFailure_ReturnsNull()
    {
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("fail"));

        _orchestrator.TriggerDownloadAsync(42, "abc123");

        var path = await _orchestrator.GetExtractedPathAsync();
        Assert.Null(path);
    }

    [Fact]
    public void Shutdown_NoDownload_CompletesWithoutError()
    {
        // Simulate shutdown by cancelling the stopping token
        _stoppingCts.Cancel();

        // No exception should be thrown
        var state = _orchestrator.GetState();
        Assert.Equal(DownloadStatus.NotStarted, state.Status);
    }

    [Fact]
    public async Task TriggerDownload_OnSuccess_CallsAllReadyHandlers()
    {
        var handler1Called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler2Called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler1 = Substitute.For<IRepositoryReadyHandler>();
        var handler2 = Substitute.For<IRepositoryReadyHandler>();
        handler1.OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => { handler1Called.TrySetResult(); return Task.CompletedTask; });
        handler2.OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => { handler2Called.TrySetResult(); return Task.CompletedTask; });

        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        var orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider, _lifetime, new[] { handler1, handler2 },
            NullLogger<RepositoryDownloadOrchestrator>.Instance);

        orchestrator.TriggerDownloadAsync(42, "abc123");
        await orchestrator.GetExtractedPathAsync();
        await Task.WhenAll(handler1Called.Task, handler2Called.Task);

        await handler1.Received(1).OnRepositoryReadyAsync(Arg.Any<string>(), 42, Arg.Any<CancellationToken>());
        await handler2.Received(1).OnRepositoryReadyAsync(Arg.Any<string>(), 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerDownload_OnFailure_DoesNotCallReadyHandlers()
    {
        var handler = Substitute.For<IRepositoryReadyHandler>();

        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new HttpRequestException("Network error"));

        var orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider, _lifetime, new[] { handler },
            NullLogger<RepositoryDownloadOrchestrator>.Instance);

        orchestrator.TriggerDownloadAsync(42, "abc123");
        await orchestrator.GetExtractedPathAsync();

        await handler.DidNotReceive().OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerDownload_HandlerThrows_OtherHandlersStillCalled()
    {
        var handler2Called = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler1 = Substitute.For<IRepositoryReadyHandler>();
        var handler2 = Substitute.For<IRepositoryReadyHandler>();
        handler1.OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("handler1 failed"));
        handler2.OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ci => { handler2Called.TrySetResult(); return Task.CompletedTask; });

        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        var orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider, _lifetime, new[] { handler1, handler2 },
            NullLogger<RepositoryDownloadOrchestrator>.Instance);

        orchestrator.TriggerDownloadAsync(42, "abc123");
        await orchestrator.GetExtractedPathAsync();
        await handler2Called.Task;

        await handler2.Received(1).OnRepositoryReadyAsync(Arg.Any<string>(), 42, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TriggerDownload_NoHandlers_CompletesNormally()
    {
        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        var orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider, _lifetime, Array.Empty<IRepositoryReadyHandler>(),
            NullLogger<RepositoryDownloadOrchestrator>.Instance);

        orchestrator.TriggerDownloadAsync(42, "abc123");
        var path = await orchestrator.GetExtractedPathAsync();

        var state = orchestrator.GetState();
        Assert.Equal(DownloadStatus.Ready, state.Status);
    }

    [Fact]
    public async Task TriggerDownload_NewPrWhileHandlerRunning_HandlerCompletesOrFails()
    {
        var handlerStarted = new TaskCompletionSource();
        var handlerContinue = new TaskCompletionSource();
        var handler = Substitute.For<IRepositoryReadyHandler>();
        handler.OnRepositoryReadyAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                handlerStarted.SetResult();
                await handlerContinue.Task;
            });

        _archiveProvider.DownloadRepositoryZipAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var destPath = callInfo.ArgAt<string>(1);
                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                CreateMinimalZip(destPath);
                return Task.CompletedTask;
            });

        var orchestrator = new RepositoryDownloadOrchestrator(
            _archiveProvider, _lifetime, new[] { handler },
            NullLogger<RepositoryDownloadOrchestrator>.Instance);

        // Start first PR
        orchestrator.TriggerDownloadAsync(42, "abc123");
        await orchestrator.GetExtractedPathAsync();
        await handlerStarted.Task; // Handler is running

        // Start new PR while handler is still running — should not throw
        orchestrator.TriggerDownloadAsync(99, "def456");
        handlerContinue.SetResult(); // Let handler finish

        await orchestrator.GetExtractedPathAsync();
        var state = orchestrator.GetState();
        Assert.Equal(99, state.PrNumber);
    }

    private static void CreateMinimalZip(string path)
    {
        using var archive = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create);
        var entry = archive.CreateEntry("dummy.txt");
        using var writer = new StreamWriter(entry.Open());
        writer.Write("test");
    }
}
