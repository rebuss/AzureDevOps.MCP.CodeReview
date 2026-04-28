using System.Reflection;
using REBUSS.Pure.Properties;

namespace REBUSS.Pure.Cli.Deployment;

/// <summary>
/// Deploys user-facing Copilot/IDE prompt files to <c>.github/prompts/&lt;name&gt;.prompt.md</c>
/// in the target repository, and deletes any legacy non-suffixed copies left over from
/// older versions. Always runs regardless of <c>--agent</c> (Feature 024 D4): Claude users
/// still benefit from <c>.github/prompts/</c> if their IDE picks them up via Copilot Chat.
/// </summary>
internal sealed class PromptDeployer
{
    internal const string ResourcePrefix = AppConstants.ServerName + ".Cli.Prompts.";

    internal static readonly string[] PromptFileNames =
    {
        "review-pr.prompt.md",
        "self-review.prompt.md"
    };

    internal static readonly string[] LegacyPromptFileNames =
    {
        "review-pr.md",
        "self-review.md"
    };

    private readonly TextWriter _output;
    private readonly Assembly _assembly;

    public PromptDeployer(TextWriter output, Assembly? assembly = null)
    {
        _output = output;
        _assembly = assembly ?? Assembly.GetExecutingAssembly();
    }

    public async Task DeployAsync(string gitRoot, CancellationToken cancellationToken)
    {
        var promptsTargetDir = Path.Combine(gitRoot, ".github", "prompts");
        Directory.CreateDirectory(promptsTargetDir);

        await DeleteLegacyPromptFilesAsync(promptsTargetDir);

        var promptsWritten = 0;

        foreach (var promptFileName in PromptFileNames)
        {
            var resourceName = EmbeddedResourceLocator.Find(_assembly, ResourcePrefix, promptFileName);

            if (resourceName is null)
            {
                await _output.WriteLineAsync(string.Format(Resources.WarnEmbeddedPromptResourceNotFound, promptFileName));
                continue;
            }

            using var stream = _assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync(cancellationToken);

            // Overwrite enables prompt updates on subsequent init runs.
            var promptPath = Path.Combine(promptsTargetDir, promptFileName);
            await File.WriteAllTextAsync(promptPath, content, cancellationToken);
            promptsWritten++;
        }

        if (promptsWritten > 0)
            await _output.WriteLineAsync(string.Format(Resources.MsgCopiedPrompts, promptsWritten, promptsTargetDir));
    }

    private async Task DeleteLegacyPromptFilesAsync(string promptsTargetDir)
    {
        foreach (var legacyFileName in LegacyPromptFileNames)
        {
            var legacyPath = Path.Combine(promptsTargetDir, legacyFileName);
            if (!File.Exists(legacyPath))
                continue;

            File.Delete(legacyPath);
            await _output.WriteLineAsync(string.Format(Resources.MsgDeletedLegacyPromptFile, legacyPath));
        }
    }
}
