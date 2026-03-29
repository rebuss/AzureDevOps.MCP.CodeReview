using Microsoft.Extensions.Options;
using REBUSS.Pure.GitHub.Properties;

namespace REBUSS.Pure.GitHub.Configuration;

/// <summary>
/// Validates <see cref="GitHubOptions"/> when explicitly provided.
/// All fields are optional — they can be auto-detected from Git remote or cached locally.
/// This validator only enforces format correctness on values that are present.
/// </summary>
public class GitHubOptionsValidator : IValidateOptions<GitHubOptions>
{
    public ValidateOptionsResult Validate(string? name, GitHubOptions options)
    {
        var failures = new List<string>();

        if (options.Owner is not null && options.Owner.Contains(' '))
            failures.Add(string.Format(Resources.ErrorPropertyMustNotContainSpaces, nameof(GitHubOptions.Owner)));

        if (options.RepositoryName is not null && options.RepositoryName.Contains(' '))
            failures.Add(string.Format(Resources.ErrorPropertyMustNotContainSpaces, nameof(GitHubOptions.RepositoryName)));

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
