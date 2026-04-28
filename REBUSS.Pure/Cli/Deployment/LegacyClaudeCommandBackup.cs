using REBUSS.Pure.Cli.Mcp;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Cli.Deployment;

/// <summary>
/// Feature 024 — moves any pre-024 <c>.claude/commands/&lt;skill-name&gt;.md</c>
/// to <c>&lt;skill-name&gt;.md.bak</c>. Skills replace slash commands (skill wins
/// when both exist, but the orphan command file is misleading dead weight). Backup
/// rather than delete: matches the safety convention used for config files.
/// Unrelated user files in <c>.claude/commands/</c> are left alone; a missing
/// directory is a no-op. Iterates the same skill-name list owned by
/// <see cref="ClaudeSkillDeployer.SkillNames"/>.
/// </summary>
internal sealed class LegacyClaudeCommandBackup
{
    private readonly TextWriter _output;

    public LegacyClaudeCommandBackup(TextWriter output)
    {
        _output = output;
    }

    public async Task RunAsync(string gitRoot, CancellationToken cancellationToken)
    {
        _ = cancellationToken; // file moves are synchronous; method kept async for interface symmetry
        var commandsDir = Path.Combine(gitRoot, McpConfigTargetResolver.ClaudeCodeDir, "commands");
        if (!Directory.Exists(commandsDir))
            return;

        foreach (var skillName in ClaudeSkillDeployer.SkillNames)
        {
            var commandPath = Path.Combine(commandsDir, skillName + ".md");
            if (!File.Exists(commandPath))
                continue;

            var backupPath = commandPath + ".bak";
            File.Move(commandPath, backupPath, overwrite: true);
            await _output.WriteLineAsync(string.Format(Resources.LogInitBackedUpLegacyCommand, commandPath));
        }
    }
}
