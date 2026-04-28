using System.Reflection;
using REBUSS.Pure.Cli.Deployment;

namespace REBUSS.Pure.Tests.Cli.Deployment;

/// <summary>
/// Step 5 — focused unit tests for <see cref="EmbeddedResourceLocator.Find"/>.
/// Uses the production assembly (<see cref="Assembly.GetExecutingAssembly"/> on the
/// REBUSS.Pure exe) so we exercise real manifest names — same surface as the
/// deployers do at runtime.
/// </summary>
public class EmbeddedResourceLocatorTests
{
    private static Assembly ProductionAssembly =>
        typeof(REBUSS.Pure.Cli.InitCommand).Assembly;

    [Fact]
    public void Find_ExactMatch_ReturnsExactName()
    {
        // The Copilot prompt template ships with no hyphenated-directory ambiguity, so
        // the exact-name path is guaranteed to hit.
        var resourceName = EmbeddedResourceLocator.Find(
            ProductionAssembly,
            REBUSS.Pure.AppConstants.ServerName + ".Cli.Prompts.",
            "review-pr.prompt.md");

        Assert.NotNull(resourceName);
        Assert.EndsWith("review-pr.prompt.md", resourceName);
    }

    [Fact]
    public void Find_HyphenInPathButLogicalNamePinned_StillResolvesViaExactMatch()
    {
        // Skill resources pin LogicalName so the exact name is the canonical resource —
        // exercises the same lookup path as the deployer at runtime, regardless of which
        // mangling the SDK applied to the Compile-time path.
        var resourceName = EmbeddedResourceLocator.Find(
            ProductionAssembly,
            REBUSS.Pure.AppConstants.ServerName + ".Cli.Skills.",
            "review-pr.SKILL.md");

        Assert.NotNull(resourceName);
        Assert.Contains("Skills", resourceName);
        Assert.EndsWith("SKILL.md", resourceName);
    }

    [Fact]
    public void Find_NeitherExactNorMangled_ReturnsNull()
    {
        var resourceName = EmbeddedResourceLocator.Find(
            ProductionAssembly,
            "REBUSS.Pure.NotARealPrefix.",
            "definitely-not-here.md");

        Assert.Null(resourceName);
    }
}
