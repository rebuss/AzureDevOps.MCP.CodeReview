using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace REBUSS.Pure.RoslynProcessor;

/// <summary>
/// Scans C# files in a repository for identifier references to target members.
/// Uses Roslyn syntax-tree-based matching (not text search) to avoid false positives.
/// </summary>
public class CallSiteScanner
{
    private const int MaxFileSizeBytes = 100_000;
    private const int MaxCallSitesPerTarget = 10;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "obj", "bin", ".git"
    };

    private static readonly HashSet<string> ExcludedSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".Designer.cs", ".g.cs", ".AssemblyInfo.cs"
    };

    private readonly ILogger<CallSiteScanner> _logger;

    public CallSiteScanner(ILogger<CallSiteScanner> logger)
    {
        _logger = logger;
    }

    public async Task<IReadOnlyList<CallSiteResult>> ScanAsync(
        string repoRoot,
        IReadOnlyList<CallSiteTarget> targets,
        string? excludeFilePath,
        CancellationToken ct)
    {
        if (targets.Count == 0)
            return [];

        var targetNames = targets.Select(t => t.Name).ToHashSet();

        if (targetNames.Count == 0)
            return [];

        var results = new Dictionary<string, List<CallSiteLocation>>();
        var totalCounts = new Dictionary<string, int>();
        foreach (var target in targets)
        {
            results[target.Name] = [];
            totalCounts[target.Name] = 0;
        }

        var resolvedExclude = excludeFilePath?.Replace('/', Path.DirectorySeparatorChar);

        foreach (var filePath in EnumerateCsFiles(repoRoot, resolvedExclude))
        {
            ct.ThrowIfCancellationRequested();

            string sourceText;
            try
            {
                sourceText = await File.ReadAllTextAsync(filePath, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue;
            }

            // Pre-filter: skip files not containing any target name
            if (!targetNames.Any(name => sourceText.Contains(name, StringComparison.Ordinal)))
                continue;

            var relativePath = Path.GetRelativePath(repoRoot, filePath).Replace('\\', '/');

            try
            {
                var tree = CSharpSyntaxTree.ParseText(sourceText, path: relativePath, cancellationToken: ct);
                var root = tree.GetRoot(ct);

                foreach (var identifier in root.DescendantNodes().OfType<IdentifierNameSyntax>())
                {
                    var name = identifier.Identifier.Text;
                    if (!targetNames.Contains(name))
                        continue;

                    if (!IsCallSiteContext(identifier))
                        continue;

                    totalCounts[name]++;

                    if (results[name].Count < MaxCallSitesPerTarget)
                    {
                        var lineSpan = tree.GetLineSpan(identifier.Span);
                        results[name].Add(new CallSiteLocation
                        {
                            FilePath = relativePath,
                            Line = lineSpan.StartLinePosition.Line + 1
                        });
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Failed to parse {FilePath} for call site scan", relativePath);
            }
        }

        return targets.Select(t => new CallSiteResult
        {
            TargetName = t.Name,
            Reason = t.Reason,
            Locations = results[t.Name],
            TotalCount = totalCounts[t.Name]
        }).ToList();
    }

    private static bool IsCallSiteContext(IdentifierNameSyntax identifier)
    {
        var parent = identifier.Parent;
        return parent is InvocationExpressionSyntax
            or MemberAccessExpressionSyntax
            or ObjectCreationExpressionSyntax
            or BaseListSyntax
            or SimpleBaseTypeSyntax;
    }

    private static IEnumerable<string> EnumerateCsFiles(string repoRoot, string? resolvedExclude)
    {
        foreach (var filePath in Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories))
        {
            // Exclude by directory
            var relativePath = Path.GetRelativePath(repoRoot, filePath);
            var parts = relativePath.Split(Path.DirectorySeparatorChar);
            if (parts.Any(p => ExcludedDirectories.Contains(p)))
                continue;

            // Exclude current file
            if (resolvedExclude != null && relativePath.EndsWith(resolvedExclude, StringComparison.OrdinalIgnoreCase))
                continue;

            // Exclude generated files
            if (ExcludedSuffixes.Any(suffix => filePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Exclude large files
            try
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length > MaxFileSizeBytes)
                    continue;
            }
            catch
            {
                continue;
            }

            yield return filePath;
        }
    }
}
