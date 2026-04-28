using System.Reflection;
using REBUSS.Pure.Cli.Mcp;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Cli.Deployment;

/// <summary>
/// Feature 024 — deploys Claude Code skills to <c>.claude/skills/&lt;name&gt;/SKILL.md</c>
/// inside the target repository. Runs regardless of <c>--agent</c> so the project ships
/// skills even for Copilot users (harmless when Claude Code is not the configured agent).
/// Drift policy: when an existing on-disk skill differs from the embedded source, back
/// the user version up to <c>SKILL.md.bak</c> before overwriting — same convention used
/// for MCP config files. Idempotent on identical content. The <see cref="SkillNames"/>
/// array is also consumed by <see cref="LegacyClaudeCommandBackup"/> to enumerate the
/// same skill-name set when retiring pre-024 slash-command files.
/// </summary>
internal sealed class ClaudeSkillDeployer
{
    internal const string SkillsResourcePrefix = AppConstants.ServerName + ".Cli.Skills.";

    /// <summary>
    /// Feature 024 — Claude Code skills shipped alongside Copilot prompts. The names
    /// here are <c>&lt;skill-name&gt;</c>; the embedded resource is
    /// <c>SkillsResourcePrefix + name + ".SKILL.md"</c> (matching the LogicalName in
    /// REBUSS.Pure.csproj), and the deploy target is
    /// <c>.claude/skills/&lt;name&gt;/SKILL.md</c>.
    /// </summary>
    internal static readonly string[] SkillNames =
    {
        "review-pr",
        "self-review"
    };

    private readonly TextWriter _output;
    private readonly Assembly _assembly;

    public ClaudeSkillDeployer(TextWriter output, Assembly? assembly = null)
    {
        _output = output;
        _assembly = assembly ?? Assembly.GetExecutingAssembly();
    }

    public async Task DeployAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var skillsRoot = Path.Combine(gitRoot, McpConfigTargetResolver.ClaudeCodeDir, "skills");

        foreach (var skillName in SkillNames)
        {
            // Defensive symmetry with prompts: even though every shipped skill pins an
            // explicit LogicalName in REBUSS.Pure.csproj, route through EmbeddedResourceLocator
            // so a future skill added without that pin still resolves via the
            // hyphen→underscore mangled-name fallback instead of silently warning + skipping.
            var resourceName = EmbeddedResourceLocator.Find(_assembly, SkillsResourcePrefix, skillName + ".SKILL.md");
            if (resourceName is null)
            {
                await _output.WriteLineAsync(string.Format(Resources.WarnEmbeddedPromptResourceNotFound, SkillsResourcePrefix + skillName + ".SKILL.md"));
                continue;
            }

            var resourceStream = _assembly.GetManifestResourceStream(resourceName)!;

            string embeddedContent;
            await using (resourceStream.ConfigureAwait(false))
            {
                using var reader = new StreamReader(resourceStream);
                embeddedContent = await reader.ReadToEndAsync(cancellationToken);
            }

            var skillDir = Path.Combine(skillsRoot, skillName);
            Directory.CreateDirectory(skillDir);
            var skillPath = Path.Combine(skillDir, "SKILL.md");

            if (File.Exists(skillPath))
            {
                var existing = await File.ReadAllTextAsync(skillPath, cancellationToken);
                if (string.Equals(existing, embeddedContent, StringComparison.Ordinal))
                {
                    await _output.WriteLineAsync(string.Format(Resources.LogInitSkillUnchanged, skillName));
                    continue;
                }
                File.Copy(skillPath, skillPath + ".bak", overwrite: true);
            }

            await File.WriteAllTextAsync(skillPath, embeddedContent, cancellationToken);
            await _output.WriteLineAsync(string.Format(Resources.LogInitDeployingClaudeSkill, skillName));
        }
    }
}
