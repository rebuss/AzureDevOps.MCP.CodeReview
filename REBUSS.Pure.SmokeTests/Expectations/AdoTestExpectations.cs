namespace REBUSS.Pure.SmokeTests.Expectations;

/// <summary>
/// Expected values from the Azure DevOps fixture PR.
/// Populate with actual values after creating the fixture repository and PR.
/// </summary>
public static class AdoTestExpectations
{
    // --- get_pr_metadata ---
    public const string Title = "Contract test fixture PR \u2014 do not merge";
    public const string DescriptionFragment = "do not merge";
    public const string State = "active";
    public const bool IsDraft = false;
    public const string SourceBranchContains = "test/fixture-pr";
    public const string TargetBranchContains = "main";
    public const int ExpectedCommitCount = 1;
    public const int TotalFiles = 2;
}
