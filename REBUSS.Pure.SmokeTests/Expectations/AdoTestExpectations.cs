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

    // --- get_pr_files ---
    public const int TotalFiles = 2;
    public static readonly string[] FilePaths =
        ["src/Calculator.cs", "src/Logger.cs"];
    public static readonly Dictionary<string, string> FileStatuses = new()
    {
        ["src/Calculator.cs"] = "modified",
        ["src/Logger.cs"] = "added"
    };

    // --- get_file_content_at_ref ---
    public const string OriginalCodeFragment = "public class Calculator";
    public const string ModifiedCodeFragment = "Negate";
}
