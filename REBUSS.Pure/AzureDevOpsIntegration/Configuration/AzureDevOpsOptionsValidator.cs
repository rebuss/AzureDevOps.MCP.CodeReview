using Microsoft.Extensions.Options;

namespace REBUSS.Pure.AzureDevOpsIntegration.Configuration
{
    /// <summary>
    /// Validates <see cref="AzureDevOpsOptions"/> when explicitly provided.
    /// All fields are optional — they can be auto-detected from Git remote or cached locally.
    /// This validator only enforces format correctness on values that are present.
    /// </summary>
    public class AzureDevOpsOptionsValidator : IValidateOptions<AzureDevOpsOptions>
    {
        public ValidateOptionsResult Validate(string? name, AzureDevOpsOptions options)
        {
            // All fields are optional — auto-detection and caching fill in the blanks.
            // Only validate format when values are explicitly provided.
            var failures = new List<string>();

            if (options.OrganizationName is not null && options.OrganizationName.Contains(' '))
                failures.Add($"{nameof(AzureDevOpsOptions.OrganizationName)} must not contain spaces");

            if (options.ProjectName is not null && options.ProjectName.Contains(' '))
                failures.Add($"{nameof(AzureDevOpsOptions.ProjectName)} must not contain spaces");

            if (options.RepositoryName is not null && options.RepositoryName.Contains(' '))
                failures.Add($"{nameof(AzureDevOpsOptions.RepositoryName)} must not contain spaces");

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}
