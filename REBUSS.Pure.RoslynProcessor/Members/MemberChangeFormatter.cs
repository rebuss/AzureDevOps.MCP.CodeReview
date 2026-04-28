using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor.Members;

/// <summary>
/// Formats user-visible <see cref="StructuralChange.Description"/> strings for
/// added / removed / signature-changed member events. Owns the per-member-kind
/// switch for the description shape and the small param-list / visibility helpers.
/// Pure functions — no state.
/// </summary>
internal static class MemberChangeFormatter
{
    public static string FormatAdded(MemberDeclarationSyntax member)
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

    public static string FormatRemoved(MemberDeclarationSyntax member)
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

    public static string FormatSignatureChanged(
        MemberDeclarationSyntax before, MemberDeclarationSyntax after, string key)
    {
        return (before, after) switch
        {
            (MethodDeclarationSyntax bm, MethodDeclarationSyntax am) =>
                $"Method signature changed: {bm.Identifier.Text}({FormatParams(bm.ParameterList)}) → {am.Identifier.Text}({FormatParams(am.ParameterList)})",
            (ConstructorDeclarationSyntax bc, ConstructorDeclarationSyntax ac) =>
                $"Constructor changed: ({FormatParams(bc.ParameterList)}) → ({FormatParams(ac.ParameterList)})",
            (PropertyDeclarationSyntax bp, PropertyDeclarationSyntax ap) =>
                $"Property changed: {bp.Identifier.Text} : {bp.Type.ToString().Trim()} → {ap.Type.ToString().Trim()}",
            _ => $"Signature changed: {key.Split(':').Last()}"
        };
    }

    internal static string FormatParams(ParameterListSyntax paramList)
    {
        var parameters = paramList.Parameters;
        if (parameters.Count == 0) return "";
        if (parameters.Count <= 3)
            return string.Join(", ", parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
        return string.Join(", ", parameters.Take(2).Select(p => p.Type?.ToString().Trim() ?? "?"))
               + $", ... +{parameters.Count - 2}";
    }

    internal static string GetVisibility(SyntaxTokenList modifiers)
    {
        if (modifiers.Any(SyntaxKind.PublicKeyword)) return "Public ";
        if (modifiers.Any(SyntaxKind.ProtectedKeyword)) return "Protected ";
        if (modifiers.Any(SyntaxKind.InternalKeyword)) return "Internal ";
        return "";
    }
}
