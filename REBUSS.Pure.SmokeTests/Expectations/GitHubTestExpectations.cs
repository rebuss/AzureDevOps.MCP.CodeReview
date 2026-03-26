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

    // --- get_pr_files ---
    public const int TotalFiles = 2;
    public static readonly string[] FilePaths =
        ["src/Calculator.cs", "src/Logger.cs"];           // no leading slash for GitHub
    public static readonly Dictionary<string, string> FileStatuses = new()
    {
        ["src/Calculator.cs"] = "modified",
        ["src/Logger.cs"] = "added"
    };

    // --- get_pr_files — additions/deletions ---
    public const int CalculatorAdditions = 0;            // ← update after creating fixture
    public const int CalculatorDeletions = 0;            // ← update after creating fixture
    public const int LoggerAdditions = 0;                // ← update after creating fixture

    // --- get_pr_diff ---
    public const string ExpectedCodeFragment = "public double Abs"; // ← update after creating fixture

    // --- get_file_content_at_ref ---
    public const string OriginalCodeFragment = "public double Negate";    // ← update after creating fixture
    public const string ModifiedCodeFragment = "public double Percentage";    // ← update after creating fixture
}
