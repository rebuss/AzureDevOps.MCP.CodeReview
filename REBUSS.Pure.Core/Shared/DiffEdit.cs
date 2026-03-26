namespace REBUSS.Pure.Core.Shared;

/// <summary>
/// Represents a single edit operation produced by a diff algorithm.
/// Kind: ' ' = unchanged context, '-' = removed from old, '+' = added in new.
/// </summary>
public readonly record struct DiffEdit(char Kind, int OldIdx, int NewIdx);
