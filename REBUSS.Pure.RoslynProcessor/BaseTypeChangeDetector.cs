using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Emits <see cref="StructuralChangeKind.BaseTypeChanged"/> events when a type's
/// <c>BaseList</c> (base class + implemented interfaces) changes between before
/// and after. Three branches: full replacement (added + removed) renders as
/// "Base type changed: X → Y"; pure additions render as "Implements added: …";
/// pure removals render as "Implements removed: …". Empty before/after lists
/// (<c>BaseList == null</c>) are normalised to empty enumerations.
/// </summary>
internal static class BaseTypeChangeDetector
{
    public static IEnumerable<StructuralChange> Detect(
        TypeDeclarationSyntax before, TypeDeclarationSyntax after)
    {
        var beforeBases = before.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [];
        var afterBases = after.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [];

        if (beforeBases.SequenceEqual(afterBases))
            yield break;

        var added = afterBases.Except(beforeBases).ToList();
        var removed = beforeBases.Except(afterBases).ToList();

        if (added.Count > 0 && removed.Count > 0)
        {
            yield return new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Base type changed: {string.Join(", ", removed)} → {string.Join(", ", added)}",
                LineNumber = after.BaseList?.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
        else if (added.Count > 0)
        {
            yield return new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Implements added: {string.Join(", ", added)}",
                LineNumber = after.BaseList?.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
        else if (removed.Count > 0)
        {
            yield return new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Implements removed: {string.Join(", ", removed)}",
                LineNumber = after.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            };
        }
    }
}
