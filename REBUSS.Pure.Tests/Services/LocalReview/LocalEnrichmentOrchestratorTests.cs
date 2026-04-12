using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models;
using REBUSS.Pure.Core.Models.Pagination;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Services.LocalReview;

namespace REBUSS.Pure.Tests.Services.LocalReview;

/// <summary>
/// Unit tests for <see cref="LocalEnrichmentOrchestrator"/> (T025).
/// Tests the trigger/wait/snapshot lifecycle of background local enrichment.
/// </summary>
public class LocalEnrichmentOrchestratorTests
{
    private readonly ILocalReviewProvider _localProvider = Substitute.For<ILocalReviewProvider>();
    private readonly ITokenEstimator _tokenEstimator = Substitute.For<ITokenEstimator>();
    private readonly IFileClassifier _fileClassifier = Substitute.For<IFileClassifier>();
    private readonly ICodeProcessor _codeProcessor = Substitute.For<ICodeProcessor>();
    private readonly IPageAllocator _pageAllocator = Substitute.For<IPageAllocator>();

    private LocalEnrichmentOrchestrator CreateOrchestrator()
    {
        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);

        return new LocalEnrichmentOrchestrator(
            _localProvider,
            _tokenEstimator,
            _fileClassifier,
            _codeProcessor,
            _pageAllocator,
            lifetime,
            NullLogger<LocalEnrichmentOrchestrator>.Instance);
    }

    private void SetupDefaultMocks(int fileCount = 2)
    {
        var files = Enumerable.Range(0, fileCount)
            .Select(i => new PullRequestFileInfo
            {
                Path = $"src/F{i}.cs",
                Additions = 10,
                Deletions = 2,
                Changes = 12,
                Extension = ".cs"
            })
            .ToList();

        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new LocalReviewFiles
            {
                RepositoryRoot = "C:\\Projects\\TestRepo",
                CurrentBranch = "feature/test",
                Scope = "working-tree",
                Files = files,
            });

        foreach (var file in files)
        {
            _localProvider.GetFileDiffAsync(file.Path, Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
                .Returns(new PullRequestDiff
                {
                    Files = new List<FileChange>
                    {
                        new()
                        {
                            Path = file.Path,
                            ChangeType = "edit",
                            Additions = 10,
                            Deletions = 2,
                            Hunks = new List<DiffHunk>
                            {
                                new()
                                {
                                    OldStart = 1, OldCount = 2, NewStart = 1, NewCount = 10,
                                    Lines = new List<DiffLine> { new() { Op = '+', Text = "new" } }
                                }
                            }
                        }
                    }
                });
        }

        _tokenEstimator.EstimateFromStats(Arg.Any<int>(), Arg.Any<int>())
            .Returns(500);
        _fileClassifier.Classify(Arg.Any<string>())
            .Returns(new FileClassification { Category = FileCategory.Source });
        _codeProcessor.AddBeforeAfterContext(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<string>()));

        var pageSlice = new PageSlice(1, 0, fileCount,
            Enumerable.Range(0, fileCount)
                .Select(i => new PageSliceItem(i, PackingItemStatus.Included, 500))
                .ToArray(),
            500 * fileCount, 139000);
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(new[] { pageSlice }, 1, fileCount));
    }

    // --- T025-1: Happy path ---

    [Fact]
    public async Task TriggerAndWait_HappyPath_ResultHasEnrichedCandidates()
    {
        SetupDefaultMocks(fileCount: 2);
        var orchestrator = CreateOrchestrator();

        orchestrator.TriggerEnrichment("working-tree", 140_000);
        var result = await orchestrator.WaitForEnrichmentAsync("working-tree", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("C:\\Projects\\TestRepo", result.RepositoryRoot);
        Assert.Equal("feature/test", result.CurrentBranch);
        Assert.Equal("working-tree", result.Scope);
        Assert.Equal(2, result.SortedCandidates.Count);
        Assert.Equal(2, result.EnrichedByPath.Count);
        Assert.True(result.CompletedAt > DateTimeOffset.MinValue);
    }

    // --- T025-2: Idempotent trigger ---

    [Fact]
    public async Task TriggerEnrichment_CalledTwiceSameScope_DoesNotRetrigger()
    {
        SetupDefaultMocks(fileCount: 1);
        var orchestrator = CreateOrchestrator();

        orchestrator.TriggerEnrichment("working-tree", 140_000);
        orchestrator.TriggerEnrichment("working-tree", 140_000); // second call
        orchestrator.TriggerEnrichment("working-tree", 140_000); // third call

        var result = await orchestrator.WaitForEnrichmentAsync("working-tree", CancellationToken.None);
        Assert.NotNull(result);

        // GetFilesAsync should only have been called once (not three times).
        await _localProvider.Received(1).GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>());
    }

    // --- T025-3: Snapshot during flight ---

    [Fact]
    public async Task TryGetSnapshot_DuringEnrichment_ReturnsNonReadyStatus()
    {
        // Set up a provider that delays so we can observe in-flight status.
        var tcs = new TaskCompletionSource<LocalReviewFiles>();
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(tcs.Task);

        var orchestrator = CreateOrchestrator();
        orchestrator.TriggerEnrichment("working-tree", 140_000);

        // Give the background task a moment to start.
        await Task.Delay(50);

        var snapshot = orchestrator.TryGetSnapshot("working-tree");
        Assert.NotNull(snapshot);
        Assert.NotEqual(LocalEnrichmentStatus.Ready, snapshot!.Status);

        // Clean up: let the task complete so the test doesn't hang.
        tcs.SetResult(new LocalReviewFiles
        {
            RepositoryRoot = "C:\\Repo",
            CurrentBranch = "main",
            Scope = "working-tree",
            Files = new List<PullRequestFileInfo>()
        });

        // Set up remaining mocks for empty result.
        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(Array.Empty<PageSlice>(), 0, 0));
    }

    // --- T025-4: Snapshot after completion ---

    [Fact]
    public async Task TryGetSnapshot_AfterCompletion_ReturnsReadyWithResult()
    {
        SetupDefaultMocks(fileCount: 2);
        var orchestrator = CreateOrchestrator();

        orchestrator.TriggerEnrichment("working-tree", 140_000);
        _ = await orchestrator.WaitForEnrichmentAsync("working-tree", CancellationToken.None);

        var snapshot = orchestrator.TryGetSnapshot("working-tree");
        Assert.NotNull(snapshot);
        Assert.Equal(LocalEnrichmentStatus.Ready, snapshot!.Status);
        Assert.NotNull(snapshot.Result);
        Assert.Equal("working-tree", snapshot.Scope);
    }

    // --- T025-5: Failure handling ---

    [Fact]
    public async Task TriggerEnrichment_ProviderThrows_FailedStatusWithFailureInfo()
    {
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("git not found"));

        var orchestrator = CreateOrchestrator();
        orchestrator.TriggerEnrichment("working-tree", 140_000);

        // WaitForEnrichmentAsync should propagate the exception.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => orchestrator.WaitForEnrichmentAsync("working-tree", CancellationToken.None));

        var snapshot = orchestrator.TryGetSnapshot("working-tree");
        Assert.NotNull(snapshot);
        Assert.Equal(LocalEnrichmentStatus.Failed, snapshot!.Status);
        Assert.NotNull(snapshot.Failure);
        Assert.Equal("InvalidOperationException", snapshot.Failure!.ExceptionTypeName);
        Assert.Contains("git not found", snapshot.Failure.SanitizedMessage);
    }

    // --- T025-6: Wait cancellation ---

    [Fact]
    public async Task WaitForEnrichmentAsync_CallerCancels_ThrowsOperationCanceledException()
    {
        // Set up a provider that never completes.
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new TaskCompletionSource<LocalReviewFiles>().Task);

        var orchestrator = CreateOrchestrator();
        orchestrator.TriggerEnrichment("working-tree", 140_000);

        using var cts = new CancellationTokenSource(50);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => orchestrator.WaitForEnrichmentAsync("working-tree", cts.Token));
    }

    // --- T025-7: Empty change set ---

    [Fact]
    public async Task TriggerEnrichment_NoFiles_ResultWithEmptyCandidates()
    {
        _localProvider.GetFilesAsync(Arg.Any<LocalReviewScope>(), Arg.Any<CancellationToken>())
            .Returns(new LocalReviewFiles
            {
                RepositoryRoot = "C:\\EmptyRepo",
                CurrentBranch = "main",
                Scope = "working-tree",
                Files = new List<PullRequestFileInfo>(),
            });

        _pageAllocator.Allocate(Arg.Any<IReadOnlyList<PackingCandidate>>(), Arg.Any<int>())
            .Returns(new PageAllocation(Array.Empty<PageSlice>(), 0, 0));

        var orchestrator = CreateOrchestrator();
        orchestrator.TriggerEnrichment("working-tree", 140_000);
        var result = await orchestrator.WaitForEnrichmentAsync("working-tree", CancellationToken.None);

        Assert.NotNull(result);
        Assert.Empty(result.SortedCandidates);
        Assert.Empty(result.EnrichedByPath);
        Assert.Equal(0, result.Allocation.TotalPages);
    }
}
