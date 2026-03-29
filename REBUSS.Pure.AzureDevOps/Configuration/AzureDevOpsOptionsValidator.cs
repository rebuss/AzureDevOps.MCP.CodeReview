using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOps.Properties;

namespace REBUSS.Pure.AzureDevOps.Configuration
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
                failures.Add(string.Format(Resources.ErrorPropertyMustNotContainSpaces, nameof(AzureDevOpsOptions.OrganizationName)));

            if (options.ProjectName is not null && options.ProjectName.Contains(' '))
                failures.Add(string.Format(Resources.ErrorPropertyMustNotContainSpaces, nameof(AzureDevOpsOptions.ProjectName)));

            if (options.RepositoryName is not null && options.RepositoryName.Contains(' '))
                failures.Add(string.Format(Resources.ErrorPropertyMustNotContainSpaces, nameof(AzureDevOpsOptions.RepositoryName)));

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}
