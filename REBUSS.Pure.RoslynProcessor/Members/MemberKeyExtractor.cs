using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor.Members;

/// <summary>
/// Identity key generators for members. Two flavours:
/// <list type="bullet">
///   <item><see cref="Key"/> — full identity including the parameter-type list, so
///         method overloads get distinct keys (e.g. <c>method:Foo(int)</c> vs
///         <c>method:Foo(string)</c>). Used to match a before-member against an
///         after-member of identical signature.</item>
///   <item><see cref="GroupKey"/> — name-and-kind only (no parameters), used by the
///         orphan-pairing heuristic to recognise that a sole overload had its
///         parameter list edited and should be reported as a single
///         <see cref="StructuralChangeKind.SignatureChanged"/> rather than
///         remove + add.</item>
/// </list>
/// Returns <c>null</c> from <see cref="Key"/> for unsupported member kinds (the
/// orchestrator filters those out before they reach the comparison passes).
/// </summary>
internal static class MemberKeyExtractor
{
    public static string? Key(MemberDeclarationSyntax member)
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

    public static string GroupKey(MemberDeclarationSyntax member)
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

    private static string FormatParamTypeKey(ParameterListSyntax paramList)
    {
        return string.Join(",", paramList.Parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
    }

    private static string FormatIndexerParamTypeKey(BracketedParameterListSyntax paramList)
    {
        return string.Join(",", paramList.Parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
    }
}
