using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Resolves the enclosing scope (method, property, constructor, class, namespace)
/// for a given line number in a C# syntax tree. Pure logic, no I/O.
/// </summary>
public static class ScopeResolver
{
    private const int MaxDisplayParams = 4;

    /// <summary>
    /// Finds the most specific enclosing member for the given 1-based line number.
    /// Returns a formatted scope string, or <c>null</c> if the line is out of range or unresolvable.
    /// </summary>
    public static string? Resolve(SyntaxNode root, int lineNumber)
    {
        var text = root.SyntaxTree.GetText();
        if (lineNumber < 1 || lineNumber > text.Lines.Count)
            return null;

        var lineSpan = text.Lines[lineNumber - 1].Span;
        var node = root.FindNode(lineSpan, getInnermostNodeForTie: true);
        if (node == null)
            return null;

        var enclosing = FindEnclosingMember(node);
        if (enclosing == null)
            return null;

        return FormatScope(enclosing);
    }

    /// <summary>
    /// Walks the parent chain to find the innermost enclosing member (method, property,
    /// constructor, type, namespace, etc.) for the given node. <c>internal</c> so
    /// <c>FindingScopeExtractor</c> (feature 021) can reuse the walk.
    /// </summary>
    internal static SyntaxNode? FindEnclosingMember(SyntaxNode node)
    {
        var current = node;
        while (current != null)
        {
            switch (current)
            {
                case MethodDeclarationSyntax:
                case ConstructorDeclarationSyntax:
                case DestructorDeclarationSyntax:
                case PropertyDeclarationSyntax:
                case IndexerDeclarationSyntax:
                case EventDeclarationSyntax:
                case OperatorDeclarationSyntax:
                case LocalFunctionStatementSyntax:
                    return current;
                case TypeDeclarationSyntax:
                    return current;
                case BaseNamespaceDeclarationSyntax:
                    return current;
            }
            current = current.Parent;
        }
        return null;
    }

    private static string FormatScope(SyntaxNode member)
    {
        return member switch
        {
            MethodDeclarationSyntax m => FormatMethodScope(m),
            ConstructorDeclarationSyntax c => FormatConstructorScope(c),
            DestructorDeclarationSyntax d => $"{GetEnclosingTypeName(d)}.~{GetEnclosingTypeSimpleName(d)}",
            PropertyDeclarationSyntax p => $"{GetEnclosingTypeName(p)}.{p.Identifier.Text}",
            IndexerDeclarationSyntax ix => $"{GetEnclosingTypeName(ix)}.this[{FormatParams(ix.ParameterList)}]",
            EventDeclarationSyntax e => $"{GetEnclosingTypeName(e)}.{e.Identifier.Text}",
            OperatorDeclarationSyntax o => $"{GetEnclosingTypeName(o)}.operator {o.OperatorToken.Text}",
            LocalFunctionStatementSyntax lf => FormatLocalFunctionScope(lf),
            TypeDeclarationSyntax t => GetFullTypeName(t),
            BaseNamespaceDeclarationSyntax ns => ns.Name.ToString(),
            _ => null!
        };
    }

    private static string FormatMethodScope(MethodDeclarationSyntax method)
    {
        var typeName = GetEnclosingTypeName(method);
        var methodName = method.Identifier.Text;
        var typeParams = method.TypeParameterList != null
            ? $"<{string.Join(", ", method.TypeParameterList.Parameters.Select(p => p.Identifier.Text))}>"
            : "";
        var parameters = FormatParams(method.ParameterList);
        return $"{typeName}.{methodName}{typeParams}({parameters})";
    }

    private static string FormatConstructorScope(ConstructorDeclarationSyntax ctor)
    {
        var typeName = GetEnclosingTypeName(ctor);
        var parameters = FormatParams(ctor.ParameterList);
        return $"{typeName}.ctor({parameters})";
    }

    private static string FormatLocalFunctionScope(LocalFunctionStatementSyntax localFunc)
    {
        var chain = new List<string> { $"{localFunc.Identifier.Text}({FormatParams(localFunc.ParameterList)})" };

        var current = localFunc.Parent;
        while (current != null)
        {
            switch (current)
            {
                case MethodDeclarationSyntax m:
                    chain.Add(m.Identifier.Text);
                    chain.Add(GetEnclosingTypeName(m));
                    chain.Reverse();
                    return string.Join(".", chain);
                case LocalFunctionStatementSyntax outerLf:
                    chain.Add(outerLf.Identifier.Text);
                    break;
                case ConstructorDeclarationSyntax c:
                    chain.Add("ctor");
                    chain.Add(GetEnclosingTypeName(c));
                    chain.Reverse();
                    return string.Join(".", chain);
            }
            current = current.Parent;
        }

        // Fallback: local function not inside a method (shouldn't happen in valid C#)
        chain.Reverse();
        return string.Join(".", chain);
    }

    private static string FormatParams(BaseParameterListSyntax paramList)
    {
        var parameters = paramList.Parameters;
        if (parameters.Count == 0) return "";
        if (parameters.Count <= MaxDisplayParams)
            return string.Join(", ", parameters.Select(p => p.Type?.ToString().Trim() ?? "?"));
        return string.Join(", ", parameters.Take(MaxDisplayParams).Select(p => p.Type?.ToString().Trim() ?? "?"))
               + $", ... +{parameters.Count - MaxDisplayParams}";
    }

    private static string GetEnclosingTypeName(SyntaxNode member)
    {
        var type = member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type != null ? GetFullTypeName(type) : "?";
    }

    private static string GetEnclosingTypeSimpleName(SyntaxNode member)
    {
        var type = member.Ancestors().OfType<TypeDeclarationSyntax>().FirstOrDefault();
        return type?.Identifier.Text ?? "?";
    }

    private static string GetFullTypeName(TypeDeclarationSyntax type)
    {
        var names = new List<string> { type.Identifier.Text };
        var parent = type.Parent;
        while (parent is TypeDeclarationSyntax outerType)
        {
            names.Add(outerType.Identifier.Text);
            parent = outerType.Parent;
        }
        names.Reverse();
        return string.Join(".", names);
    }
}
