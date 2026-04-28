using Microsoft.CodeAnalysis;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Lightweight, syntax-only detector of structural changes between before and after
/// C# source. Operates on <see cref="SyntaxTree"/> (ParseText), never on Compilation.
/// All methods are synchronous — zero I/O.
/// <para>
/// Top-level orchestrator (~30 LOC) composing six focused collaborators living next
/// to it under <c>RoslynProcessor/</c>:
/// <list type="bullet">
///   <item><see cref="TypeDeclarationIndex"/> — indexes top-level types by qualified name; owns kind-name + name-qualification helpers.</item>
///   <item><see cref="TypeChangeDetector"/> — emits <c>TypeAdded</c> / <c>TypeRemoved</c> via set diff over the indices.</item>
///   <item><see cref="BaseTypeChangeDetector"/> — emits <c>BaseTypeChanged</c> for matched types whose <c>BaseList</c> changed.</item>
///   <item><see cref="Members.MemberChangeDetector"/> — emits <c>MemberAdded</c> / <c>MemberRemoved</c> / <c>SignatureChanged</c> with the orphan-pairing heuristic for sole-overload signature edits.</item>
///   <item><see cref="Members.MemberKeyExtractor"/>, <see cref="Members.MemberSignatureComparer"/>, <see cref="Members.MemberChangeFormatter"/> — identity, equality, and description-string helpers consumed by <see cref="Members.MemberChangeDetector"/>.</item>
/// </list>
/// The output is sorted by <see cref="StructuralChange.LineNumber"/> (nulls last)
/// so the consumer (<c>StructuralChangeEnricher</c>) can render changes in source order.
/// </para>
/// </summary>
public static class StructuralChangeDetector
{
    public static IReadOnlyList<StructuralChange> DetectChanges(SyntaxTree before, SyntaxTree after)
    {
        var beforeIndex = TypeDeclarationIndex.BuildFrom(before.GetRoot());
        var afterIndex = TypeDeclarationIndex.BuildFrom(after.GetRoot());

        var changes = new List<StructuralChange>();
        changes.AddRange(TypeChangeDetector.Detect(beforeIndex, afterIndex));

        foreach (var name in beforeIndex.Names.Intersect(afterIndex.Names))
        {
            var beforeType = beforeIndex[name];
            var afterType = afterIndex[name];

            changes.AddRange(BaseTypeChangeDetector.Detect(beforeType, afterType));
            changes.AddRange(Members.MemberChangeDetector.Detect(beforeType, afterType));
        }

        SortByLineNumber(changes);
        return changes;
    }

    private static void SortByLineNumber(List<StructuralChange> changes) => changes.Sort((a, b) =>
    {
        if (a.LineNumber == null && b.LineNumber == null) return 0;
        if (a.LineNumber == null) return 1;
        if (b.LineNumber == null) return -1;
        return a.LineNumber.Value.CompareTo(b.LineNumber.Value);
    });
}
