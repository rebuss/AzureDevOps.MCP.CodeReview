using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Lightweight, syntax-only detector of structural changes between before and after C# source.
/// Operates on <see cref="SyntaxTree"/> (ParseText), never on Compilation.
/// All methods are synchronous — zero I/O.
/// </summary>
public static class StructuralChangeDetector
{
    public static IReadOnlyList<StructuralChange> DetectChanges(SyntaxTree before, SyntaxTree after)
    {
        var beforeRoot = before.GetRoot();
        var afterRoot = after.GetRoot();

        var changes = new List<StructuralChange>();

        var beforeTypes = GetTypeDeclarations(beforeRoot);
        var afterTypes = GetTypeDeclarations(afterRoot);

        var beforeTypeMap = new Dictionary<string, TypeDeclarationSyntax>();
        foreach (var t in beforeTypes)
            beforeTypeMap.TryAdd(GetTypeName(t), t);

        var afterTypeMap = new Dictionary<string, TypeDeclarationSyntax>();
        foreach (var t in afterTypes)
            afterTypeMap.TryAdd(GetTypeName(t), t);

        // Detect added/removed types
        foreach (var name in afterTypeMap.Keys.Except(beforeTypeMap.Keys))
        {
            var type = afterTypeMap[name];
            var kind = GetTypeKindName(type);
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.TypeAdded,
                Description = $"New {kind}: {name}",
                LineNumber = type.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        foreach (var name in beforeTypeMap.Keys.Except(afterTypeMap.Keys))
        {
            var type = beforeTypeMap[name];
            var kind = GetTypeKindName(type);
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.TypeRemoved,
                Description = $"{kind} removed: {name}",
                LineNumber = null
            });
        }

        // Compare matched types
        foreach (var name in beforeTypeMap.Keys.Intersect(afterTypeMap.Keys))
        {
            var beforeType = beforeTypeMap[name];
            var afterType = afterTypeMap[name];

            // Compare base types/interfaces
            CompareBaseTypes(beforeType, afterType, changes);

            // Compare members
            CompareMembers(beforeType, afterType, changes);
        }

        // Sort by line number (nulls at end)
        changes.Sort((a, b) =>
        {
            if (a.LineNumber == null && b.LineNumber == null) return 0;
            if (a.LineNumber == null) return 1;
            if (b.LineNumber == null) return -1;
            return a.LineNumber.Value.CompareTo(b.LineNumber.Value);
        });

        return changes;
    }

    private static List<TypeDeclarationSyntax> GetTypeDeclarations(SyntaxNode root)
    {
        // Get top-level type declarations (class, struct, record, interface)
        // Also handles types inside namespace declarations
        // Excludes nested types (types declared inside other types)
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .ToList();
    }

    private static string GetTypeName(TypeDeclarationSyntax type)
    {
        var namespaceParts = new List<string>();
        foreach (var ns in type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            namespaceParts.Insert(0, ns.Name.ToString());

        var qualifier = string.Join(".", namespaceParts);
        return string.IsNullOrEmpty(qualifier)
            ? type.Identifier.Text
            : $"{qualifier}.{type.Identifier.Text}";
    }

    private static string GetTypeKindName(TypeDeclarationSyntax type)
    {
        return type switch
        {
            ClassDeclarationSyntax => "class",
            StructDeclarationSyntax => "struct",
            RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
            InterfaceDeclarationSyntax => "interface",
            _ => "type"
        };
    }

    private static void CompareBaseTypes(
        TypeDeclarationSyntax before, TypeDeclarationSyntax after,
        List<StructuralChange> changes)
    {
        var beforeBases = before.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [];
        var afterBases = after.BaseList?.Types.Select(t => t.ToString()).ToList() ?? [];

        if (beforeBases.SequenceEqual(afterBases))
            return;

        var added = afterBases.Except(beforeBases).ToList();
        var removed = beforeBases.Except(afterBases).ToList();

        if (added.Count > 0 && removed.Count > 0)
        {
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Base type changed: {string.Join(", ", removed)} \u2192 {string.Join(", ", added)}",
                LineNumber = after.BaseList?.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }
        else if (added.Count > 0)
        {
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Implements added: {string.Join(", ", added)}",
                LineNumber = after.BaseList?.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }
        else if (removed.Count > 0)
        {
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.BaseTypeChanged,
                Description = $"Implements removed: {string.Join(", ", removed)}",
                LineNumber = after.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }
    }

    private static void CompareMembers(
        TypeDeclarationSyntax before, TypeDeclarationSyntax after,
        List<StructuralChange> changes)
    {
        var beforeMembers = GetMembersWithKeys(before);
        var afterMembers = GetMembersWithKeys(after);

        var beforeMap = new Dictionary<string, MemberDeclarationSyntax>();
        foreach (var (key, member) in beforeMembers)
            beforeMap.TryAdd(key, member);

        var afterMap = new Dictionary<string, MemberDeclarationSyntax>();
        foreach (var (key, member) in afterMembers)
            afterMap.TryAdd(key, member);

        var addedKeys = afterMap.Keys.Except(beforeMap.Keys).ToList();
        var removedKeys = beforeMap.Keys.Except(afterMap.Keys).ToList();

        // Pair 1:1 orphans sharing the same name+kind as SignatureChanged (e.g. the sole
        // overload of Foo had its parameter list changed — treat as one signature change,
        // not remove+add). Real overload additions/removals keep multiple entries per name
        // and fall through as MemberAdded/MemberRemoved.
        var pairedBefore = new HashSet<string>();
        var pairedAfter = new HashSet<string>();

        var removedByGroup = removedKeys.GroupBy(k => MemberGroupKey(beforeMap[k]));
        var addedByGroup = addedKeys.GroupBy(k => MemberGroupKey(afterMap[k]))
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
                Description = FormatSignatureChanged(beforeMember, afterMember, afterKey),
                LineNumber = afterMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });

            pairedBefore.Add(beforeKey);
            pairedAfter.Add(afterKey);
        }

        // Added members
        foreach (var key in addedKeys.Where(k => !pairedAfter.Contains(k)))
        {
            var member = afterMap[key];
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.MemberAdded,
                Description = FormatMemberAdded(member),
                LineNumber = member.GetLocation().GetLineSpan().StartLinePosition.Line + 1
            });
        }

        // Removed members
        foreach (var key in removedKeys.Where(k => !pairedBefore.Contains(k)))
        {
            var member = beforeMap[key];
            changes.Add(new StructuralChange
            {
                Kind = StructuralChangeKind.MemberRemoved,
                Description = FormatMemberRemoved(member),
                LineNumber = null
            });
        }

        // Signature changes
        foreach (var key in beforeMap.Keys.Intersect(afterMap.Keys))
        {
            var beforeMember = beforeMap[key];
            var afterMember = afterMap[key];

            if (!SignaturesEqual(beforeMember, afterMember))
            {
                changes.Add(new StructuralChange
                {
                    Kind = StructuralChangeKind.SignatureChanged,
                    Description = FormatSignatureChanged(beforeMember, afterMember, key),
                    LineNumber = afterMember.GetLocation().GetLineSpan().StartLinePosition.Line + 1
                });
            }
        }
    }

    private static List<(string Key, MemberDeclarationSyntax Member)> GetMembersWithKeys(TypeDeclarationSyntax type)
    {
        var result = new List<(string, MemberDeclarationSyntax)>();
        foreach (var member in type.Members)
        {
            var key = MemberKey(member);
            if (key != null)
                result.Add((key, member));
        }
        return result;
    }

    private static string? MemberKey(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => $"method:{m.Identifier.Text}({FormatParamTypeKey(m.ParameterList)})",
            ConstructorDeclarationSyntax c => $"method:.ctor({FormatParamTypeKey(c.ParameterList)})",
            PropertyDeclarationSyntax p => $"property:{p.Identifier.Text}",
            FieldDeclarationSyntax f => f.Declaration.Variables.Count > 0
                ? $"field:{f.Declaration.Variables[0].Identifier.Text}" : null,
            IndexerDeclarationSyntax i => $"property:this[{FormatIndexerParamTypeKey(i.ParameterList)}]",
            OperatorDeclarationSyntax o => $"method:operator {o.OperatorToken.Text}({FormatParamTypeKey(o.ParameterList)})",
            EventDeclarationSyntax e => $"event:{e.Identifier.Text}",
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.Count > 0
                ? $"event:{ef.Declaration.Variables[0].Identifier.Text}" : null,
            _ => null
        };
    }

    private static string FormatParamTypeKey(ParameterListSyntax paramList)
    {
        return string.Join(",", paramList.Parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
    }

    private static string MemberGroupKey(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => $"method:{m.Identifier.Text}",
            ConstructorDeclarationSyntax => "method:.ctor",
            PropertyDeclarationSyntax p => $"property:{p.Identifier.Text}",
            FieldDeclarationSyntax f => f.Declaration.Variables.Count > 0
                ? $"field:{f.Declaration.Variables[0].Identifier.Text}" : "field:?",
            IndexerDeclarationSyntax => "property:this[]",
            OperatorDeclarationSyntax o => $"method:operator {o.OperatorToken.Text}",
            EventDeclarationSyntax e => $"event:{e.Identifier.Text}",
            EventFieldDeclarationSyntax ef => ef.Declaration.Variables.Count > 0
                ? $"event:{ef.Declaration.Variables[0].Identifier.Text}" : "event:?",
            _ => "other:?"
        };
    }

    private static string FormatIndexerParamTypeKey(BracketedParameterListSyntax paramList)
    {
        return string.Join(",", paramList.Parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
    }

    private static bool SignaturesEqual(MemberDeclarationSyntax a, MemberDeclarationSyntax b)
    {
        return (a, b) switch
        {
            (MethodDeclarationSyntax am, MethodDeclarationSyntax bm) =>
                SyntaxFactory.AreEquivalent(am.ParameterList, bm.ParameterList) &&
                SyntaxFactory.AreEquivalent(am.ReturnType, bm.ReturnType) &&
                ModifiersEqual(am.Modifiers, bm.Modifiers) &&
                AreEquivalentOrBothNull(am.TypeParameterList, bm.TypeParameterList),

            (ConstructorDeclarationSyntax ac, ConstructorDeclarationSyntax bc) =>
                SyntaxFactory.AreEquivalent(ac.ParameterList, bc.ParameterList) &&
                ModifiersEqual(ac.Modifiers, bc.Modifiers),

            (PropertyDeclarationSyntax ap, PropertyDeclarationSyntax bp) =>
                SyntaxFactory.AreEquivalent(ap.Type, bp.Type) &&
                ModifiersEqual(ap.Modifiers, bp.Modifiers) &&
                AccessorKindsEqual(ap.AccessorList, bp.AccessorList),

            (FieldDeclarationSyntax af, FieldDeclarationSyntax bf) =>
                SyntaxFactory.AreEquivalent(af.Declaration.Type, bf.Declaration.Type) &&
                ModifiersEqual(af.Modifiers, bf.Modifiers),

            (IndexerDeclarationSyntax ai, IndexerDeclarationSyntax bi) =>
                SyntaxFactory.AreEquivalent(ai.ParameterList, bi.ParameterList) &&
                SyntaxFactory.AreEquivalent(ai.Type, bi.Type) &&
                ModifiersEqual(ai.Modifiers, bi.Modifiers),

            _ => SyntaxFactory.AreEquivalent(a, b)
        };
    }

    private static bool ModifiersEqual(SyntaxTokenList a, SyntaxTokenList b)
    {
        if (a.Count != b.Count) return false;
        var aKinds = a.Select(t => t.Kind()).OrderBy(k => k).ToList();
        var bKinds = b.Select(t => t.Kind()).OrderBy(k => k).ToList();
        return aKinds.SequenceEqual(bKinds);
    }

    private static bool AreEquivalentOrBothNull(SyntaxNode? a, SyntaxNode? b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        return SyntaxFactory.AreEquivalent(a, b);
    }

    private static bool AccessorKindsEqual(AccessorListSyntax? a, AccessorListSyntax? b)
    {
        var aKinds = a?.Accessors.Select(x => x.Kind()).OrderBy(k => k).ToList() ?? [];
        var bKinds = b?.Accessors.Select(x => x.Kind()).OrderBy(k => k).ToList() ?? [];
        return aKinds.SequenceEqual(bKinds);
    }

    // --- Description formatting ---

    private static string FormatMemberAdded(MemberDeclarationSyntax member)
    {
        return member switch
        {
            MethodDeclarationSyntax m =>
                $"New method: {m.Identifier.Text}({FormatParams(m.ParameterList)}) : {m.ReturnType.ToString().Trim()}",
            ConstructorDeclarationSyntax c =>
                $"New constructor({FormatParams(c.ParameterList)})",
            PropertyDeclarationSyntax p =>
                $"New property: {p.Identifier.Text} : {p.Type.ToString().Trim()}",
            FieldDeclarationSyntax f when f.Declaration.Variables.Count > 0 =>
                $"New field: {f.Declaration.Variables[0].Identifier.Text} : {f.Declaration.Type.ToString().Trim()}",
            EventDeclarationSyntax e =>
                $"New event: {e.Identifier.Text}",
            _ => $"New member: {member.ToString().Split('\n')[0].Trim()}"
        };
    }

    private static string FormatMemberRemoved(MemberDeclarationSyntax member)
    {
        var visibility = GetVisibility(member.Modifiers);
        return member switch
        {
            MethodDeclarationSyntax m =>
                $"{visibility}method removed: {m.Identifier.Text}({FormatParams(m.ParameterList)})",
            ConstructorDeclarationSyntax c =>
                $"{visibility}constructor removed({FormatParams(c.ParameterList)})",
            PropertyDeclarationSyntax p =>
                $"{visibility}property removed: {p.Identifier.Text}",
            FieldDeclarationSyntax f when f.Declaration.Variables.Count > 0 =>
                $"{visibility}field removed: {f.Declaration.Variables[0].Identifier.Text}",
            _ => $"{visibility}member removed"
        };
    }

    private static string FormatSignatureChanged(
        MemberDeclarationSyntax before, MemberDeclarationSyntax after, string key)
    {
        return (before, after) switch
        {
            (MethodDeclarationSyntax bm, MethodDeclarationSyntax am) =>
                $"Method signature changed: {bm.Identifier.Text}({FormatParams(bm.ParameterList)}) \u2192 {am.Identifier.Text}({FormatParams(am.ParameterList)})",
            (ConstructorDeclarationSyntax bc, ConstructorDeclarationSyntax ac) =>
                $"Constructor changed: ({FormatParams(bc.ParameterList)}) \u2192 ({FormatParams(ac.ParameterList)})",
            (PropertyDeclarationSyntax bp, PropertyDeclarationSyntax ap) =>
                $"Property changed: {bp.Identifier.Text} : {bp.Type.ToString().Trim()} \u2192 {ap.Type.ToString().Trim()}",
            _ => $"Signature changed: {key.Split(':').Last()}"
        };
    }

    private static string FormatParams(ParameterListSyntax paramList)
    {
        var parameters = paramList.Parameters;
        if (parameters.Count == 0) return "";
        if (parameters.Count <= 3)
            return string.Join(", ", parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
        return string.Join(", ", parameters.Take(2).Select(p => p.Type?.ToString().Trim() ?? "?"))
               + $", ... +{parameters.Count - 2}";
    }

    private static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "Public ";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "Protected ";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "Internal ";
        return "";
    }
}
