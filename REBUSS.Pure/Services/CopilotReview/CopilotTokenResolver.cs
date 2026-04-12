using Microsoft.Extensions.Options;
using REBUSS.Pure.Core.Services.CopilotReview;

namespace REBUSS.Pure.Services.CopilotReview;

/// <summary>
/// Resolves the token to pass to <c>CopilotClientOptions.GitHubToken</c>, along
/// with the <see cref="CopilotTokenSource"/> label that identifies the winning
/// channel. Pure function over the environment and
/// <see cref="IOptions{CopilotReviewOptions}"/>. Feature 018 (FR-009..FR-013a).
/// <para>
/// Precedence (highest first, FR-011, Clarification Q2):
/// <list type="number">
///   <item><c>REBUSS_COPILOT_TOKEN</c> environment variable</item>
///   <item><c>CopilotReview:GitHubToken</c> configuration key</item>
///   <item><c>(null, LoggedInUser)</c> — the SDK falls back to <c>gh auth login</c></item>
/// </list>
/// Blank / whitespace values at any step are treated as unset (FR-012) and the
/// resolver advances to the next source.
/// </para>
/// </summary>
internal interface ICopilotTokenResolver
{
    /// <summary>
    /// Resolves the token and its source. Performs no I/O beyond
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> and an options
    /// read. Never logs. Never throws. The token value is returned as a string
    /// for onward use; callers MUST NOT log, serialize, or persist it.
    /// </summary>
    (string? Token, CopilotTokenSource Source) Resolve();
}

/// <inheritdoc />
internal sealed class CopilotTokenResolver : ICopilotTokenResolver
{
    private readonly IOptions<CopilotReviewOptions> _options;

    public CopilotTokenResolver(IOptions<CopilotReviewOptions> options)
    {
        _options = options;
    }

    public (string? Token, CopilotTokenSource Source) Resolve()
    {
        // 1. Environment variable override — highest priority (FR-011 + Clarification Q2).
        var envValue = Environment.GetEnvironmentVariable(
            CopilotReviewOptions.GitHubTokenEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envValue))
            return (envValue, CopilotTokenSource.EnvironmentOverride);

        // 2. Configuration key override.
        var configValue = _options.Value.GitHubToken;
        if (!string.IsNullOrWhiteSpace(configValue))
            return (configValue, CopilotTokenSource.ConfigOverride);

        // 3. Fall through — SDK will use UseLoggedInUser = true.
        return (null, CopilotTokenSource.LoggedInUser);
    }
}
