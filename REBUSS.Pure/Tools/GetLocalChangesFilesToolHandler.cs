using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Core;
using REBUSS.Pure.Core.Models.ResponsePacking;
using REBUSS.Pure.Core.Shared;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the <c>get_local_files</c> MCP tool.
    /// Returns a classified list of locally changed files so an AI agent can
    /// decide which files to inspect in detail.
    /// Integrates with response packing to fit results within the context budget.
    /// </summary>
    public class GetLocalChangesFilesToolHandler : IMcpToolHandler
    {
        private readonly ILocalReviewProvider _reviewProvider;
        private readonly IResponsePacker _packer;
        private readonly IContextBudgetResolver _budgetResolver;
        private readonly ITokenEstimator _tokenEstimator;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<GetLocalChangesFilesToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_local_files";

        public GetLocalChangesFilesToolHandler(
            ILocalReviewProvider reviewProvider,
            IResponsePacker packer,
            IContextBudgetResolver budgetResolver,
            ITokenEstimator tokenEstimator,
            IFileClassifier fileClassifier,
            ILogger<GetLocalChangesFilesToolHandler> logger)
        {
            _reviewProvider = reviewProvider;
            _packer = packer;
            _budgetResolver = budgetResolver;
            _tokenEstimator = tokenEstimator;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description =
                "Lists all locally changed files in the git repository with classification metadata " +
                "(status, extension, binary/generated/test flags, review priority) and a summary by category. " +
                "Use this as the first step of a self-review to discover what changed before inspecting diffs. " +
                "Supported scopes: 'working-tree' (default, staged + unstaged vs HEAD), " +
                "'staged' (index vs HEAD only), or any branch/ref name to diff the current branch against it.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["scope"] = new ToolProperty
                    {
                        Type = "string",
                        Description =
                            "The change scope to review. " +
                            "'working-tree' (default): all uncommitted changes vs HEAD. " +
                            "'staged': only staged (indexed) changes vs HEAD. " +
                            "Any other value is treated as a base branch/ref (e.g. 'main', 'origin/main') " +
                            "and returns all commits on the current branch not yet merged into that base."
                    },
                    ["modelName"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "Optional model name (e.g. 'Claude Sonnet') to resolve context window size"
                    },
                    ["maxTokens"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "Optional explicit context window size in tokens"
                    }
                },
                Required = new List<string>()
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var scopeStr = ExtractScope(arguments);
                var scope = LocalReviewScope.Parse(scopeStr);
                var modelName = ExtractOptionalString(arguments, "modelName");
                var maxTokens = ExtractOptionalInt(arguments, "maxTokens");

                _logger.LogInformation("[{ToolName}] Entry: scope={Scope}", ToolName, scope);
                var sw = Stopwatch.StartNew();

                var reviewFiles = await _reviewProvider.GetFilesAsync(scope, cancellationToken);

                var budget = _budgetResolver.Resolve(maxTokens, modelName);

                var fileItems = reviewFiles.Files.Select(f => new PullRequestFileItem
                {
                    Path = f.Path,
                    Status = f.Status,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Changes = f.Changes,
                    Extension = f.Extension,
                    IsBinary = f.IsBinary,
                    IsGenerated = f.IsGenerated,
                    IsTestFile = f.IsTestFile,
                    ReviewPriority = f.ReviewPriority
                }).ToList();

                // Estimate tokens per file item and build candidates
                var candidates = new List<PackingCandidate>(fileItems.Count);
                for (var i = 0; i < fileItems.Count; i++)
                {
                    var fi = fileItems[i];
                    var serialized = JsonSerializer.Serialize(fi, JsonOptions);
                    var estimation = _tokenEstimator.Estimate(serialized, budget.SafeBudgetTokens);
                    var classification = _fileClassifier.Classify(fi.Path);

                    candidates.Add(new PackingCandidate(
                        fi.Path,
                        estimation.EstimatedTokens,
                        classification.Category,
                        fi.Additions + fi.Deletions));
                }

                var decision = _packer.Pack(candidates, budget.SafeBudgetTokens);

                // Filter: include Included and Partial items, defer the rest
                var packedFiles = new List<PullRequestFileItem>();
                for (var i = 0; i < decision.Items.Count; i++)
                {
                    if (decision.Items[i].Status != PackingItemStatus.Deferred)
                        packedFiles.Add(fileItems[i]);
                }

                var result = new LocalReviewFilesResult
                {
                    RepositoryRoot = reviewFiles.RepositoryRoot,
                    Scope = reviewFiles.Scope,
                    CurrentBranch = reviewFiles.CurrentBranch,
                    TotalFiles = reviewFiles.Files.Count,
                    Files = packedFiles,
                    Summary = new PullRequestFilesSummaryResult
                    {
                        SourceFiles = reviewFiles.Summary.SourceFiles,
                        TestFiles = reviewFiles.Summary.TestFiles,
                        ConfigFiles = reviewFiles.Summary.ConfigFiles,
                        DocsFiles = reviewFiles.Summary.DocsFiles,
                        BinaryFiles = reviewFiles.Summary.BinaryFiles,
                        GeneratedFiles = reviewFiles.Summary.GeneratedFiles,
                        HighPriorityFiles = reviewFiles.Summary.HighPriorityFiles
                    },
                    Manifest = MapManifest(decision.Manifest)
                };

                var json = JsonSerializer.Serialize(result, JsonOptions);
                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: scope={Scope}, {FileCount} file(s), {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, scope, reviewFiles.Files.Count, json.Length, sw.ElapsedMilliseconds);

                return CreateSuccessResult(json);
            }
            catch (LocalRepositoryNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Repository not found", ToolName);
                return CreateErrorResult($"Repository not found: {ex.Message}");
            }
            catch (GitCommandException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Git command failed", ToolName);
                return CreateErrorResult($"Git command failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error", ToolName);
                return CreateErrorResult($"Error retrieving local files: {ex.Message}");
            }
        }

        private static string? ExtractScope(Dictionary<string, object>? arguments)
        {
            if (arguments == null || !arguments.TryGetValue("scope", out var scopeObj))
                return null;

            return scopeObj is JsonElement jsonElement
                ? jsonElement.GetString()
                : scopeObj?.ToString();
        }

        private static string? ExtractOptionalString(Dictionary<string, object>? arguments, string key)
        {
            if (arguments == null || !arguments.TryGetValue(key, out var value)) return null;
            return value is JsonElement jsonElement ? jsonElement.GetString() : value?.ToString();
        }

        private static int? ExtractOptionalInt(Dictionary<string, object>? arguments, string key)
        {
            if (arguments == null || !arguments.TryGetValue(key, out var value)) return null;
            try
            {
                return value is JsonElement jsonElement ? jsonElement.GetInt32() : Convert.ToInt32(value);
            }
            catch
            {
                return null;
            }
        }

        private static ContentManifestResult MapManifest(ContentManifest manifest)
        {
            return new ContentManifestResult
            {
                Items = manifest.Items.Select(e => new ManifestEntryResult
                {
                    Path = e.Path,
                    EstimatedTokens = e.EstimatedTokens,
                    Status = e.Status.ToString(),
                    PriorityTier = e.PriorityTier
                }).ToList(),
                Summary = new ManifestSummaryResult
                {
                    TotalItems = manifest.Summary.TotalItems,
                    IncludedCount = manifest.Summary.IncludedCount,
                    PartialCount = manifest.Summary.PartialCount,
                    DeferredCount = manifest.Summary.DeferredCount,
                    TotalBudgetTokens = manifest.Summary.TotalBudgetTokens,
                    BudgetUsed = manifest.Summary.BudgetUsed,
                    BudgetRemaining = manifest.Summary.BudgetRemaining,
                    UtilizationPercent = manifest.Summary.UtilizationPercent
                }
            };
        }

        private static ToolResult CreateSuccessResult(string text) => new()
        {
            Content = new List<ContentItem> { new() { Type = "text", Text = text } },
            IsError = false
        };

        private static ToolResult CreateErrorResult(string errorMessage) => new()
        {
            Content = new List<ContentItem> { new() { Type = "text", Text = $"Error: {errorMessage}" } },
            IsError = true
        };
    }
}
