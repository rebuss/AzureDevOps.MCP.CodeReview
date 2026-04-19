namespace REBUSS.Pure.SmokeTests.Expectations;

/// <summary>
/// Expected values from the GitHub fixture PR.
/// Populate with actual values after creating the fixture repository and PR.
/// </summary>
public static class GitHubTestExpectations
{
    // --- get_pr_metadata ---
    public const string Title = "Contract test fixture PR — do not merge";
    public const string DescriptionFragment = "do not merge";
    public const string State = "open";                  // GitHub uses "open", not "active"
    public const bool IsDraft = false;
    public const string SourceBranchContains = "test/fixture-pr";
    public const string TargetBranchContains = "main";
    public const int ExpectedCommitCount = 1;            // ← update after creating fixture
    public const int TotalFiles = 2;
}
