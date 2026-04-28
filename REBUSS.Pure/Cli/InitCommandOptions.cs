namespace REBUSS.Pure.Cli;

/// <summary>
/// Immutable carrier for the user-facing inputs to <c>init</c>: working directory,
/// resolved executable path, optional PAT, <c>--global</c>/<c>--ide</c>/<c>--agent</c>
/// flags, optional pre-detected SCM provider. Collaborator/test seams
/// (<see cref="TextWriter"/>/<see cref="TextReader"/>, process runner, config stores,
/// global-target resolver) are intentionally NOT part of this record — they are
/// passed alongside the options to <see cref="InitCommand"/>'s canonical constructor.
/// </summary>
internal sealed record InitCommandOptions(
    string WorkingDirectory,
    string ExecutablePath,
    string? Pat,
    bool IsGlobal,
    string? Ide,
    string? Agent,
    string? DetectedProvider);
