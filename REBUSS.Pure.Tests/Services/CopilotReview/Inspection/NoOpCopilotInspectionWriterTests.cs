using REBUSS.Pure.Services.CopilotReview.Inspection;

namespace REBUSS.Pure.Tests.Services.CopilotReview.Inspection;

/// <summary>
/// Tests for <see cref="NoOpCopilotInspectionWriter"/>. Feature 022. Verifies both methods
/// are no-ops and return completed tasks without touching any resource.
/// </summary>
public class NoOpCopilotInspectionWriterTests
{
    [Fact]
    public void WritePromptAsync_ReturnsCompletedTaskSynchronously()
    {
        var writer = new NoOpCopilotInspectionWriter();

        var task = writer.WritePromptAsync("pr:42", "page-1-review", "anything", CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public void WriteResponseAsync_ReturnsCompletedTaskSynchronously()
    {
        var writer = new NoOpCopilotInspectionWriter();

        var task = writer.WriteResponseAsync("pr:42", "page-1-review", "anything", CancellationToken.None);

        Assert.True(task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task WritePromptAsync_ManyCalls_NoneThrow()
    {
        var writer = new NoOpCopilotInspectionWriter();

        for (var i = 0; i < 100; i++)
        {
            await writer.WritePromptAsync($"pr:{i}", "page-1-review", "content", CancellationToken.None);
            await writer.WriteResponseAsync($"pr:{i}", "page-1-review", "content", CancellationToken.None);
        }
    }
}
