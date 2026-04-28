using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Cli.Mcp;

/// <summary>
/// Writes the MCP configuration file for every <see cref="McpConfigTarget"/> in
/// <see cref="WriteAllAsync"/>. Owns the per-target IO loop: directory creation,
/// existing-file detection, build-vs-merge selection, <c>.bak</c> backup of pre-merge
/// content, write with a clear "config locked" error if a running MCP client holds
/// the file open, and the create/update success log. Path normalization
/// (<c>"\\" → "\\\\"</c>) lives inside this class so the writer is the sole owner of
/// the "raw vs. escaped path" concern — the merge path uses raw paths because
/// <see cref="System.Text.Json.Utf8JsonWriter"/> performs its own escaping.
/// </summary>
internal sealed class McpConfigWriter
{
    private readonly TextWriter _output;

    public McpConfigWriter(TextWriter output)
    {
        _output = output;
    }

    public async Task WriteAllAsync(
        IEnumerable<McpConfigTarget> targets,
        string executablePath,
        string gitRoot,
        string? pat,
        string? agent,
        CancellationToken cancellationToken)
    {
        var normalizedExePath = executablePath.Replace("\\", "\\\\");
        var normalizedRepoPath = gitRoot.Replace("\\", "\\\\");

        foreach (var target in targets)
        {
            Directory.CreateDirectory(target.Directory);

            string newContent;
            bool fileExisted = File.Exists(target.ConfigPath);
            if (fileExisted)
            {
                var existing = await File.ReadAllTextAsync(target.ConfigPath, cancellationToken);
                newContent = McpConfigJsonBuilder.Merge(existing, executablePath, gitRoot, pat, target.UseMcpServersKey, agent);

                // Backup before overwriting: the Claude Code ~/.claude.json file in
                // particular contains unrelated user state, so preserve the pre-merge
                // copy in case our merge mangles something.
                try
                {
                    var backupPath = target.ConfigPath + ".bak";
                    File.Copy(target.ConfigPath, backupPath, overwrite: true);
                    await _output.WriteLineAsync(string.Format(Resources.MsgBackedUpMcpConfiguration, backupPath));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Missing .bak is acceptable — bare-catch would also swallow
                    // OperationCanceledException and mask Ctrl+C during init.
                }
            }
            else
            {
                newContent = McpConfigJsonBuilder.Build(normalizedExePath, normalizedRepoPath, pat, target.UseMcpServersKey, agent);
            }

            try
            {
                await File.WriteAllTextAsync(target.ConfigPath, newContent, cancellationToken);
            }
            catch (IOException ex)
            {
                // File likely held open by a running MCP client (Claude Code keeps
                // ~/.claude.json open). Surface a clear, actionable error and continue
                // with the next target rather than aborting the whole init.
                await _output.WriteLineAsync(string.Format(Resources.ErrMcpConfigLocked, target.IdeName, target.ConfigPath, ex.Message));
                continue;
            }

            await _output.WriteLineAsync(string.Format(
                fileExisted ? Resources.MsgUpdatedMcpConfiguration : Resources.MsgCreatedMcpConfiguration,
                target.IdeName, target.ConfigPath));
        }
    }
}
