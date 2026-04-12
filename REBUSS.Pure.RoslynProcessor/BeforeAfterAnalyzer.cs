using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Roslyn-based analyzer that compares before/after C# code and returns a
/// <see cref="ContextDecision"/> indicating how much surrounding context to add.
/// Algorithm ported from beforeafter.txt.
/// </summary>
public static class BeforeAfterAnalyzer
{
    public static ContextDecision Analyze(string beforeCode, string afterCode)
    {
        if (string.IsNullOrWhiteSpace(beforeCode) && string.IsNullOrWhiteSpace(afterCode))
            return ContextDecision.None;

        var beforeTree = CSharpSyntaxTree.ParseText(beforeCode ?? string.Empty);
        var afterTree = CSharpSyntaxTree.ParseText(afterCode ?? string.Empty);

        var beforeRoot = beforeTree.GetRoot();
        var afterRoot = afterTree.GetRoot();

        if (SyntaxFactory.AreEquivalent(beforeRoot, afterRoot))
            return ContextDecision.None;

        // Compare the two syntax trees structurally
        bool foundSemantic = false;
        bool foundStructural = false;

        // Walk the before tree and find nodes that differ from after tree
        var beforeMembers = beforeRoot.DescendantNodes().ToArray();
        var afterMembers = afterRoot.DescendantNodes().ToArray();

        // Use a simple approach: compare top-level members
        var beforeTopLevel = beforeRoot.ChildNodes().ToArray();
        var afterTopLevel = afterRoot.ChildNodes().ToArray();

        for (int i = 0; i < Math.Max(beforeTopLevel.Length, afterTopLevel.Length); i++)
        {
            var beforeNode = i < beforeTopLevel.Length ? beforeTopLevel[i] : null;
            var afterNode = i < afterTopLevel.Length ? afterTopLevel[i] : null;

            if (beforeNode != null && afterNode != null && SyntaxFactory.AreEquivalent(beforeNode, afterNode))
                continue;

            if (IsSemanticChange(beforeNode, afterNode))
            {
                foundSemantic = true;
                break;
            }

            if (IsStructuralChange(beforeNode, afterNode))
                foundStructural = true;
        }

        // If no differences found at top level, check deeper
        if (!foundSemantic && !foundStructural)
        {
            // Check all descendant nodes for differences
            foreach (var afterNode in afterRoot.DescendantNodes())
            {
                // Guard: afterNode.Span may reference positions beyond the before tree's extent
                // (common when after code is longer — new methods, expanded classes).
                if (afterNode.Span.Start >= beforeRoot.FullSpan.Length ||
                    afterNode.Span.End > beforeRoot.FullSpan.Length)
                {
                    foundStructural = true;
                    continue;
                }

                SyntaxNode? correspondingBefore;
                try
                {
                    correspondingBefore = beforeRoot.FindNode(afterNode.Span, getInnermostNodeForTie: true);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Defense-in-depth: Roslyn's internal span validation may have edge cases
                    // not covered by the bounds check above.
                    foundStructural = true;
                    continue;
                }

                if (correspondingBefore != null && SyntaxFactory.AreEquivalent(correspondingBefore, afterNode))
                    continue;

                if (IsSemanticChange(correspondingBefore, afterNode))
                {
                    foundSemantic = true;
                    break;
                }

                if (IsStructuralChange(correspondingBefore, afterNode))
                    foundStructural = true;
            }
        }

        if (foundSemantic)
            return ContextDecision.Full;

        if (foundStructural)
            return ContextDecision.Minimal;

        return ContextDecision.None;
    }

    private static bool IsSemanticChange(SyntaxNode? before, SyntaxNode? after)
    {
        if (before == null && after == null)
            return false;

        // Method signature changes
        var bMethod = FindAncestorOrSelf<MethodDeclarationSyntax>(before);
        var aMethod = FindAncestorOrSelf<MethodDeclarationSyntax>(after);
        if (bMethod != null || aMethod != null)
        {
            if (bMethod == null || aMethod == null)
                return true;
            if (!SyntaxFactory.AreEquivalent(bMethod.ParameterList, aMethod.ParameterList))
                return true;
            if (!SyntaxFactory.AreEquivalent(bMethod.ReturnType, aMethod.ReturnType))
                return true;
            if (bMethod.Modifiers.ToString() != aMethod.Modifiers.ToString())
                return true;
        }

        // Class / interface / record structural changes
        if (before is TypeDeclarationSyntax || after is TypeDeclarationSyntax)
        {
            if (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after))
                return true;
        }

        // Control flow changes
        if (IsControlFlowNode(before) || IsControlFlowNode(after))
        {
            if (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after))
                return true;
        }

        // Expression logic changes — restricted to high-signal subtypes that alter
        // call targets, operators, assignments, or control flow expressions.
        if (IsSemanticExpression(before) || IsSemanticExpression(after))
        {
            if (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after))
                return true;
        }

        // Method body logic
        if (before is BlockSyntax || after is BlockSyntax)
        {
            if (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after))
                return true;
        }

        return false;
    }

    private static bool IsStructuralChange(SyntaxNode? before, SyntaxNode? after)
    {
        if (before == null && after == null)
            return false;

        // New member inside a type
        if ((before?.Parent is TypeDeclarationSyntax || after?.Parent is TypeDeclarationSyntax) &&
            (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after)))
            return true;

        // Identifier rename
        if (before is IdentifierNameSyntax && after is IdentifierNameSyntax &&
            before.ToString() != after.ToString())
            return true;

        // Return statement change
        if ((before is ReturnStatementSyntax || after is ReturnStatementSyntax) &&
            (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after)))
            return true;

        // Minor expression changes
        if ((IsMinorExpression(before) || IsMinorExpression(after)) &&
            (before == null || after == null || !SyntaxFactory.AreEquivalent(before, after)))
            return true;

        return false;
    }

    private static bool IsControlFlowNode(SyntaxNode? node) =>
        node is IfStatementSyntax
            or SwitchStatementSyntax
            or ForStatementSyntax
            or WhileStatementSyntax
            or ForEachStatementSyntax
            or TryStatementSyntax;

    private static bool IsSemanticExpression(SyntaxNode? node) =>
        node is InvocationExpressionSyntax
            or BinaryExpressionSyntax
            or AssignmentExpressionSyntax
            or ConditionalExpressionSyntax
            or CastExpressionSyntax
            or AwaitExpressionSyntax
            or ThrowExpressionSyntax;

    private static bool IsMinorExpression(SyntaxNode? node) =>
        node is ArgumentListSyntax
            or MemberAccessExpressionSyntax
            or LiteralExpressionSyntax
            or InitializerExpressionSyntax
            or ObjectCreationExpressionSyntax;

    private static T? FindAncestorOrSelf<T>(SyntaxNode? node) where T : SyntaxNode =>
        node as T ?? node?.FirstAncestorOrSelf<T>();
}
