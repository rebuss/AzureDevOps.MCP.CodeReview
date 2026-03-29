using REBUSS.Pure.AzureDevOps;

namespace REBUSS.Pure.AzureDevOps.Configuration
{
    public class AzureDevOpsOptions
    {
        public const string SectionName = Names.Provider;

        public string OrganizationName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public string RepositoryName { get; set; } = string.Empty;
        public string PersonalAccessToken { get; set; } = string.Empty;

        /// <summary>
        /// Optional local filesystem path to the Git repository.
        /// Used as a fallback when MCP roots are not provided by the client.
        /// </summary>
        public string LocalRepoPath { get; set; } = string.Empty;
    }
}
