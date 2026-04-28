using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor.Members;

/// <summary>
/// Compares the members of two matched type declarations and emits the
/// <see cref="StructuralChangeKind.MemberAdded"/> / <see cref="StructuralChangeKind.MemberRemoved"/> /
/// <see cref="StructuralChangeKind.SignatureChanged"/> events. Four phases:
/// <list type="number">
///   <item>Build identity-key maps via <see cref="MemberKeyExtractor.Key"/> (members
///         whose kind is unsupported — <see cref="MemberKeyExtractor.Key"/> returning
///         null — are silently skipped).</item>
///   <item><b>Pair 1:1 orphans</b> sharing the same name+kind via
///         <see cref="MemberKeyExtractor.GroupKey"/> as <c>SignatureChanged</c> — e.g.
///         the sole overload of <c>Foo</c> had its parameter list changed; reporting
///         it as remove + add would be misleading. Real overload additions/removals
///         keep multiple entries per name and fall through to <c>MemberAdded</c> /
///         <c>MemberRemoved</c>.</item>
///   <item>Emit <c>MemberAdded</c> / <c>MemberRemoved</c> for unpaired orphans.</item>
///   <item>For keys present in both maps, emit <c>SignatureChanged</c> when
///         <see cref="MemberSignatureComparer.AreEqual"/> returns false.</item>
/// </list>
/// </summary>
internal static class MemberChangeDetector
{
    public static IEnumerable<StructuralChange> Detect(
        TypeDeclarationSyntax before, TypeDeclarationSyntax after)
    {
        var changes = new List<StructuralChange>();

        var beforeMap = BuildKeyMap(before);
        var afterMap = BuildKeyMap(after);

        var addedKeys = afterMap.Keys.Except(beforeMap.Keys).ToList();
        var removedKeys = beforeMap.Keys.Except(afterMap.Keys).ToList();

        // Phase 2: pair 1:1 orphans (same name+kind, single match on both sides) as
        // SignatureChanged so a parameter-list edit of the sole overload reads as one
        // change rather than remove+add.
        var pairedBefore = new HashSet<string>();
        var pairedAfter = new HashSet<string>();

        var removedByGroup = removedKeys.GroupBy(k => MemberKeyExtractor.GroupKey(beforeMap[k]));
        var addedByGroup = addedKeys.GroupBy(k => MemberKeyExtractor.GroupKey(afterMap[k]))
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var group in removedByGroup)
        {
            if (group.Count() != 1) continue;
            if (!addedByGroup.TryGetValue(group.Key, out var addedInGroup)) continue;
            if (addedInGroup.Count != 1) continue;

            var beforeKey = group.First();
            var afterKey = addedInGroup[0];
            var beforeMember = beforeMap[beforeKey];
            var afterMember = afterMap[afterKey];

            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.SignatureChanged,
                Description = MemberChangeFormatter.FormatSignatureChanged(beforeMember, afterMember, afterKey),
                LineNumber = afterMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });

            pairedBefore.Add(beforeKey);
            pairedAfter.Add(afterKey);
        }

        // Phase 3a: added members
        foreach (var key in addedKeys.Where(k => !pairedAfter.Contains(k)))
        {
            var member = afterMap[key];
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.MemberAdded,
                Description = MemberChangeFormatter.FormatAdded(member),
                LineNumber = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        // Phase 3b: removed members
        foreach (var key in removedKeys.Where(k => !pairedBefore.Contains(k)))
        {
            var member = beforeMap[key];
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.MemberRemoved,
                Description = MemberChangeFormatter.FormatRemoved(member),
                LineNumber = null
            });
        }

        // Phase 4: signature changes for matched-by-key pairs
        foreach (var key in beforeMap.Keys.Intersect(afterMap.Keys))
        {
            var beforeMember = beforeMap[key];
            var afterMember = afterMap[key];

            if (!MemberSignatureComparer.AreEqual(beforeMember, afterMember))
            {
                changes.Add(new StructuralChange
                {
                    Kind = StructuralChangeKind.SignatureChanged,
                    Description = MemberChangeFormatter.FormatSignatureChanged(beforeMember, afterMember, key),
                    LineNumber = afterMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                });
            }
        }

        return changes;
    }

    private static Dictionary<string, MemberDeclarationSyntax> BuildKeyMap(TypeDeclarationSyntax type)
    {
        var map = new Dictionary<string, MemberDeclarationSyntax>();
        foreach (var member in type.Members)
        {
            var key = MemberKeyExtractor.Key(member);
            if (key != null)
                map.TryAdd(key, member);
        }
        return map;
    }
}
