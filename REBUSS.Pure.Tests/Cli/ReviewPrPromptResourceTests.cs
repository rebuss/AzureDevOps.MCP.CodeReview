using System.Reflection;
using REBUSS.Pure.Cli;

namespace REBUSS.Pure.Tests.Cli;

/// <summary>
/// Content-assertion tests for the embedded `review-pr.prompt.md` resource.
/// Locks SC-003 and SC-004 from feature 015 by verifying structural correctness
/// of the deployed slash command prompt without requiring a live LLM.
/// </summary>
public class ReviewPrPromptResourceTests
{
    private static string LoadPromptText()
    {
        var assembly = typeof(InitCommand).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("review-pr.prompt.md", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(resourceName);
        using var stream = assembly.GetManifestResourceStream(resourceName!)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    [Fact]
    public void Prompt_Contains_AllFourLifecycleToolNames()
    {
        var text = LoadPromptText();
        Assert.Contains("begin_pr_review", text);
        Assert.Contains("next_review_item", text);
        Assert.Contains("record_review_observation", text);
        Assert.Contains("submit_pr_review", text);
    }

    [Fact]
    public void Prompt_Contains_BothMemoryToolNames()
    {
        var text = LoadPromptText();
        Assert.Contains("refetch_review_item", text);
        Assert.Contains("query_review_notes", text);
    }

    [Fact]
    public void Prompt_DoesNotContain_LegacyToolName()
    {
        var text = LoadPromptText();
        Assert.DoesNotContain("get_pr_content", text);
    }

    [Fact]
    public void Prompt_Contains_HardRulesHeader()
    {
        var text = LoadPromptText();
        Assert.Contains("Hard rules", text);
    }

    [Fact]
    public void Prompt_Contains_ArgumentVariable()
    {
        var text = LoadPromptText();
        Assert.Contains("$ARGUMENT", text);
    }

    [Fact]
    public void Prompt_Contains_BeginNowCallToAction()
    {
        var text = LoadPromptText();
        Assert.Contains("Begin now", text);
    }
}
