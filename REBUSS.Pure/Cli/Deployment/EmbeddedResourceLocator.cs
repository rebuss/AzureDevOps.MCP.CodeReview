using System.Reflection;

namespace REBUSS.Pure.Cli.Deployment;

/// <summary>
/// Locates an embedded resource by its <c>prefix + fileName</c> name. The MSBuild SDK
/// may mangle hyphens to underscores in the resource path depending on version
/// (notably when the file lives under a directory whose name contains a hyphen), so
/// the lookup tries the exact name first and falls back to the hyphen→underscore
/// variant. Shared between <see cref="PromptDeployer"/> and <see cref="ClaudeSkillDeployer"/>
/// so a future addition under either prefix automatically inherits the same
/// defensive resolution.
/// </summary>
internal static class EmbeddedResourceLocator
{
    internal static string? Find(Assembly assembly, string prefix, string fileName)
    {
        var resources = assembly.GetManifestResourceNames();

        var exactName = prefix + fileName;
        if (Array.Exists(resources, r => r == exactName))
            return exactName;

        var mangledName = prefix + fileName.Replace('-', '_');
        if (Array.Exists(resources, r => r == mangledName))
            return mangledName;

        return null;
    }
}
