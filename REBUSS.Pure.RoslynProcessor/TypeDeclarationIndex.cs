using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Indexes the top-level type declarations in a syntax tree by namespace-prefixed
/// qualified name. "Top-level" means types declared directly under a
/// <see cref="BaseNamespaceDeclarationSyntax"/> or under a
/// <see cref="CompilationUnitSyntax"/> — nested types (declared inside another type)
/// are intentionally excluded; structural change detection treats them as part of
/// their parent's body.
/// <para>
/// On duplicate qualified names (e.g. partial classes split across files in the
/// same logical "before" or "after" snapshot) the first declaration wins —
/// <see cref="Dictionary{TKey,TValue}.TryAdd"/> semantics — preserving prior behaviour.
/// </para>
/// </summary>
internal sealed class TypeDeclarationIndex
{
    private readonly Dictionary<string, TypeDeclarationSyntax> _byQualifiedName;

    private TypeDeclarationIndex(Dictionary<string, TypeDeclarationSyntax> byQualifiedName)
    {
        _byQualifiedName = byQualifiedName;
    }

    public static TypeDeclarationIndex BuildFrom(SyntaxNode root)
    {
        var map = new Dictionary<string, TypeDeclarationSyntax>();
        foreach (var type in GetTypeDeclarations(root))
            map.TryAdd(GetQualifiedName(type), type);
        return new TypeDeclarationIndex(map);
    }

    public IEnumerable<string> Names => _byQualifiedName.Keys;

    public TypeDeclarationSyntax this[string qualifiedName] => _byQualifiedName[qualifiedName];

    /// <summary>
    /// Renders <c>"class"</c> / <c>"struct"</c> / <c>"record"</c> / <c>"record struct"</c> /
    /// <c>"interface"</c> for descriptions of added/removed types. Falls back to
    /// <c>"type"</c> for kinds we do not special-case.
    /// </summary>
    public static string GetKindName(TypeDeclarationSyntax type)
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

    public static string GetQualifiedName(TypeDeclarationSyntax type)
    {
        var namespaceParts = new List<string>();
        foreach (var ns in type.Ancestors().OfType<BaseNamespaceDeclarationSyntax>())
            namespaceParts.Insert(0, ns.Name.ToString());

        var qualifier = string.Join(".", namespaceParts);
        return string.IsNullOrEmpty(qualifier)
            ? type.Identifier.Text
            : $"{qualifier}.{type.Identifier.Text}";
    }

    private static IEnumerable<TypeDeclarationSyntax> GetTypeDeclarations(SyntaxNode root)
    {
        // Top-level types only (under a namespace or compilation unit).
        // Excludes nested types (declared inside another type).
        return root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .Where(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax);
    }
}
