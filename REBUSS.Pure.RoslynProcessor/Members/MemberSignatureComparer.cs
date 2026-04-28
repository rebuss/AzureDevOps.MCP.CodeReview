using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor.Members;

/// <summary>
/// Decides whether two member declarations are signature-equal — relevant when
/// matching a before/after member by identity key and asking "did anything that
/// affects the public surface change". Per-member-kind switch with fallback to
/// <see cref="SyntaxFactory.AreEquivalent(SyntaxNode, SyntaxNode, bool)"/> for
/// kinds we do not special-case.
/// </summary>
internal static class MemberSignatureComparer
{
    public static bool AreEqual(MemberDeclarationSyntax a, MemberDeclarationSyntax b)
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
}
