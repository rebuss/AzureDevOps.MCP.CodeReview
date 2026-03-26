namespace REBUSS.Pure.SmokeTests.Infrastructure;

/// <summary>
/// Reads contract-test environment variables, validates completeness,
/// and provides skip-reason strings when credentials are missing.
/// </summary>
public static class TestSettings
{
    // Azure DevOps
    public static string? AdoPat => Environment.GetEnvironmentVariable("REBUSS_ADO_PAT");
    public static string? AdoOrg => Environment.GetEnvironmentVariable("REBUSS_ADO_ORG");
    public static string? AdoProject => Environment.GetEnvironmentVariable("REBUSS_ADO_PROJECT");
    public static string? AdoRepo => Environment.GetEnvironmentVariable("REBUSS_ADO_REPO");
    public static string? AdoPrNumberRaw => Environment.GetEnvironmentVariable("REBUSS_ADO_PR_NUMBER");

    // GitHub
    public static string? GhPat => Environment.GetEnvironmentVariable("REBUSS_GH_PAT");
    public static string? GhOwner => Environment.GetEnvironmentVariable("REBUSS_GH_OWNER");
    public static string? GhRepo => Environment.GetEnvironmentVariable("REBUSS_GH_REPO");
    public static string? GhPrNumberRaw => Environment.GetEnvironmentVariable("REBUSS_GH_PR_NUMBER");

    public static bool IsAdoConfigured =>
        !string.IsNullOrWhiteSpace(AdoPat) &&
        !string.IsNullOrWhiteSpace(AdoOrg) &&
        !string.IsNullOrWhiteSpace(AdoProject) &&
        !string.IsNullOrWhiteSpace(AdoRepo) &&
        !string.IsNullOrWhiteSpace(AdoPrNumberRaw);

    public static bool IsGitHubConfigured =>
        !string.IsNullOrWhiteSpace(GhPat) &&
        !string.IsNullOrWhiteSpace(GhOwner) &&
        !string.IsNullOrWhiteSpace(GhRepo) &&
        !string.IsNullOrWhiteSpace(GhPrNumberRaw);

    public static int AdoPrNumber =>
        int.TryParse(AdoPrNumberRaw, out var n) ? n : 0;

    public static int GhPrNumber =>
        int.TryParse(GhPrNumberRaw, out var n) ? n : 0;

    public const string AdoSkipReason =
        "Azure DevOps contract-test env vars not configured (REBUSS_ADO_PAT, REBUSS_ADO_ORG, REBUSS_ADO_PROJECT, REBUSS_ADO_REPO, REBUSS_ADO_PR_NUMBER).";

    public const string GitHubSkipReason =
        "GitHub contract-test env vars not configured (REBUSS_GH_PAT, REBUSS_GH_OWNER, REBUSS_GH_REPO, REBUSS_GH_PR_NUMBER).";
}
