namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Emits <see cref="StructuralChangeKind.TypeAdded"/> for qualified names present
/// in <c>after</c> but not <c>before</c>, and <see cref="StructuralChangeKind.TypeRemoved"/>
/// for the reverse. Pure set diff over <see cref="TypeDeclarationIndex"/>. Removed
/// types deliberately have <c>LineNumber = null</c> — there is no after-state line
/// to surface in the rebuilt diff and the orchestrator's sort places them at the end.
/// </summary>
internal static class TypeChangeDetector
{
    public static IEnumerable<StructuralChange> Detect(
        TypeDeclarationIndex before, TypeDeclarationIndex after)
    {
        foreach (var name in after.Names.Except(before.Names))
        {
            var type = after[name];
            var kind = TypeDeclarationIndex.GetKindName(type);
            yield return new StructuralChange
            {
                Kind = StructuralChangeKind.TypeAdded,
                Description = $"New {kind}: {name}",
                LineNumber = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }

        foreach (var name in before.Names.Except(after.Names))
        {
            var type = before[name];
            var kind = TypeDeclarationIndex.GetKindName(type);
            yield return new StructuralChange
            {
                Kind = StructuralChangeKind.TypeRemoved,
                Description = $"{kind} removed: {name}",
                LineNumber = null
            };
        }
    }
}
