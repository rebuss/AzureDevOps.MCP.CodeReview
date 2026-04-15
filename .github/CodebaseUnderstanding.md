# CODEBASE CONTEXT PROVIDED

Full codebase context is included below (file-role map, dependency graph, DI registrations, conventions, and current file contents for all files in scope). Skip all exploratory analysis � do not call get_projects_in_solution, get_files_in_project, or read files already provided. Proceed directly to planning and implementation using only the supplied context. If a file outside the provided set might be affected by a model or interface change, read it before editing.

---

# 1. File-Role Map

## Solution structure

| Project | Path | Purpose |
|---|---|---|
| REBUSS.Pure.Core | `REBUSS.Pure.Core\\REBUSS.Pure.Core.csproj` | Domain model library (.NET 10): models, interfaces (`IScmClient`, `IPullRequestDataProvider`, `IRepositoryArchiveProvider`, `IWorkspaceRootProvider`, `IPullRequestDiffCache`), shared diff/classification logic (DiffPlex-based diff algorithm), analysis pipeline, exceptions |
| REBUSS.Pure.AzureDevOps | `REBUSS.Pure.AzureDevOps\REBUSS.Pure.AzureDevOps.csproj` | Azure DevOps provider library (.NET 10): `AzureDevOpsScmClient` facade, fine-grained providers, parsers, API client, configuration/auth; references `REBUSS.Pure.Core` |
| REBUSS.Pure.GitHub | `REBUSS.Pure.GitHub\REBUSS.Pure.GitHub.csproj` | GitHub provider library (.NET 10): `GitHubScmClient` facade, fine-grained providers, parsers, REST API v3 client, configuration/auth (Bearer PAT); references `REBUSS.Pure.Core` |
| REBUSS.Pure.RoslynProcessor | `REBUSS.Pure.RoslynProcessor\REBUSS.Pure.RoslynProcessor.csproj` | Roslyn-based code processor (.NET 10): implements `ICodeProcessor` with Roslyn syntax analysis for before/after context enrichment; references `REBUSS.Pure.Core`, `Microsoft.CodeAnalysis.CSharp` |
| REBUSS.Pure.RoslynProcessor.Tests | `REBUSS.Pure.RoslynProcessor.Tests\REBUSS.Pure.RoslynProcessor.Tests.csproj` | Unit tests for RoslynProcessor (xUnit, NSubstitute); references `REBUSS.Pure.RoslynProcessor`, `REBUSS.Pure.Core` |
| REBUSS.Pure | `REBUSS.Pure\REBUSS.Pure.csproj` | MCP server (console app, .NET 10; NuGet package `CodeReview.MCP`, command `rebuss-pure`); references `REBUSS.Pure.Core`, `REBUSS.Pure.AzureDevOps`, `REBUSS.Pure.GitHub`, and `REBUSS.Pure.RoslynProcessor`; uses `ModelContextProtocol` SDK v1.2.0 + `Microsoft.Extensions.Hosting` v10.0.5 for MCP server hosting; contains tool handlers (attribute-based `[McpServerToolType]`/`[McpServerTool]`), local review pipeline, CLI |
| REBUSS.Pure.Core.Tests | `REBUSS.Pure.Core.Tests\REBUSS.Pure.Core.Tests.csproj` | Unit tests for Core (xUnit, NSubstitute); references `REBUSS.Pure.Core` |
| REBUSS.Pure.AzureDevOps.Tests | `REBUSS.Pure.AzureDevOps.Tests\REBUSS.Pure.AzureDevOps.Tests.csproj` | Unit tests for Azure DevOps provider (xUnit, NSubstitute); references `REBUSS.Pure.AzureDevOps`, `REBUSS.Pure.Core` |
| REBUSS.Pure.GitHub.Tests | `REBUSS.Pure.GitHub.Tests\REBUSS.Pure.GitHub.Tests.csproj` | Unit tests for GitHub provider (xUnit, NSubstitute); references `REBUSS.Pure.GitHub`, `REBUSS.Pure.Core` |
| REBUSS.Pure.Tests | `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj` | Unit + integration tests for MCP server app (xUnit, NSubstitute); references `REBUSS.Pure`, `REBUSS.Pure.Core` |
| REBUSS.Pure.SmokeTests | `REBUSS.Pure.SmokeTests\REBUSS.Pure.SmokeTests.csproj` | Smoke tests + live contract tests (xUnit, Xunit.SkippableFact): runs the compiled binary as a child process; tests `init` command (GitHub + Azure DevOps), MCP stdio protocol, full pack-install pipeline, and live contract tests against real Azure DevOps / GitHub APIs using fixture PRs; build-order dependency on `REBUSS.Pure` |

---

## Source files

### Domain models (REBUSS.Pure.Core\Models)

| File | Role | Key types | Consumed by |
|---|---|---|---|
| `REBUSS.Pure.Core\Models\PullRequestDiff.cs` | Core diff domain model | `PullRequestDiff` (+ `LastSourceCommitId` for cache staleness detection), `FileChange`, `DiffHunk`, `DiffLine` | AzureDevOpsDiffProvider, AzureDevOpsFilesProvider, AzureDevOpsScmClient, LocalReviewProvider, all tool handlers producing diffs, all diff/file-related tests |
| `REBUSS.Pure.Core\Models\PullRequestMetadata.cs` | Parsed PR metadata (lightweight) | `PullRequestMetadata` (record) | AzureDevOpsDiffProvider, PullRequestMetadataParser |
| `REBUSS.Pure.Core\Models\FullPullRequestMetadata.cs` | Rich PR metadata (all fields + `RepositoryFullName`, `WebUrl`) | `FullPullRequestMetadata` | AzureDevOpsMetadataProvider, AzureDevOpsScmClient, GetPullRequestMetadataToolHandler |
| `REBUSS.Pure.Core\Models\IterationInfo.cs` | Iteration commit SHAs | `IterationInfo` (record) | AzureDevOpsDiffProvider, AzureDevOpsMetadataProvider, IterationInfoParser |
| `REBUSS.Pure.Core\Models\FileClassification.cs` | File classification result | `FileClassification`, `FileCategory` (enum) | FileClassifier, AzureDevOpsFilesProvider, AzureDevOpsDiffProvider, LocalReviewProvider |
| `REBUSS.Pure.Core\Models\PullRequestFiles.cs` | File list result models | `PullRequestFiles`, `PullRequestFileInfo`, `PullRequestFilesSummary` | AzureDevOpsFilesProvider, LocalReviewProvider |
| `REBUSS.Pure.Core\Models\BudgetSource.cs` | Budget resolution source enum | `BudgetSource` (enum: `Explicit`, `Registry`, `Default`) | ContextBudgetResolver |
| `REBUSS.Pure.Core\Models\BudgetResolutionResult.cs` | Budget resolution result | `BudgetResolutionResult` (sealed record) | ContextBudgetResolver, ContextBudgetMetadata composition |
| `REBUSS.Pure.Core\Models\TokenEstimationResult.cs` | Token estimation result | `TokenEstimationResult` (sealed record) | TokenEstimator, ContextBudgetMetadata composition |
| `REBUSS.Pure.Core\Models\PackingItemStatus.cs` | Packing item inclusion status enum | `PackingItemStatus` (enum: `Included`, `Partial`, `Deferred`) | ResponsePacker, PackingDecisionItem |
| `REBUSS.Pure.Core\Models\PackingCandidate.cs` | Input item for packing service | `PackingCandidate` (sealed record) | ResponsePacker, tool handlers |
| `REBUSS.Pure.Core\Models\PackingDecisionItem.cs` | Per-item packing decision | `PackingDecisionItem` (sealed record) | ResponsePacker, tool handlers |
| `REBUSS.Pure.Core\Models\ManifestEntry.cs` | Manifest line item | `ManifestEntry` (sealed record) | ContentManifest |
| `REBUSS.Pure.Core\Models\ManifestSummary.cs` | Manifest aggregated stats; +3 optional F004 params: IncludedOnThisPage (int?), RemainingAfterThisPage (int?), TotalPages (int?) | `ManifestSummary` (sealed record) | ContentManifest |
| `REBUSS.Pure.Core\Models\PageReferenceData.cs` | Sealed record (Feature 004): data encoded in page reference tokens (ToolName, RequestParams as JsonElement, SafeBudgetTokens, PageNumber, DataFingerprint?) | `PageReferenceData` (sealed record, namespace `REBUSS.Pure.Core.Models.Pagination`) | PageReferenceCodec, PaginationOrchestrator |
| `REBUSS.Pure.Core\Models\PageSliceItem.cs` | Sealed record (Feature 004): per-item allocation within a page (OriginalIndex, Status, EstimatedTokens, BudgetForPartial?) | `PageSliceItem` (sealed record, namespace `REBUSS.Pure.Core.Models.Pagination`) | PageSlice, PageAllocator |
| `REBUSS.Pure.Core\Models\PageSlice.cs` | Sealed record (Feature 004): one page's boundaries and items (PageNumber, StartIndex, EndIndex, Items, BudgetUsed, BudgetRemaining) | `PageSlice` (sealed record, namespace `REBUSS.Pure.Core.Models.Pagination`) | PageAllocation, PageAllocator |
| `REBUSS.Pure.Core\Models\PageAllocation.cs` | Sealed record (Feature 004): complete allocation across pages (Pages, TotalPages, TotalItems) | `PageAllocation` (sealed record, namespace `REBUSS.Pure.Core.Models.Pagination`) | IPageAllocator return type, tool handlers |
| `REBUSS.Pure.Core\Models\ContentManifest.cs` | Domain manifest | `ContentManifest` (sealed record) | PackingDecision |
| `REBUSS.Pure.Core\Models\PackingDecision.cs` | Full packing result | `PackingDecision` (sealed record) | IResponsePacker return type |
| `REBUSS.Pure.Core\Models\RepositoryDownloadState.cs` | Repository download lifecycle state | `DownloadStatus` (enum), `RepositoryDownloadState` (Status, ExtractedPath, ErrorMessage, PrNumber, CommitRef, DownloadTask) | RepositoryDownloadOrchestrator |

### Core interfaces (REBUSS.Pure.Core)

| File | Role | Key types |
|---|---|---|
| `REBUSS.Pure.Core\IScmClient.cs` | Unified SCM client contract | `IScmClient` (extends `IPullRequestDataProvider` + `IRepositoryArchiveProvider`), `IPullRequestDataProvider` |
| `REBUSS.Pure.Core\IRepositoryArchiveProvider.cs` | Repository archive download contract | `IRepositoryArchiveProvider` — `DownloadRepositoryZipAsync(string commitRef, string destinationPath, CancellationToken ct)` |
| `REBUSS.Pure.Core\IRepositoryDownloadOrchestrator.cs` | Repository download lifecycle contract (moved from REBUSS.Pure to Core to avoid circular reference) | `IRepositoryDownloadOrchestrator` — `TriggerDownloadAsync`, `GetState`, `GetExtractedPathAsync` |
| `REBUSS.Pure.Core\IWorkspaceRootProvider.cs` | Workspace/repository root resolution contract | `IWorkspaceRootProvider` � `SetCliRepositoryPath`, `SetRoots`, `GetRootUris`, `ResolveRepositoryRoot` |
| `REBUSS.Pure.Core\IContextBudgetResolver.cs` | Context window budget resolution contract | `IContextBudgetResolver` — `Resolve(int?, string?)` → `BudgetResolutionResult` |
| `REBUSS.Pure.Core\ITokenEstimator.cs` | Token estimation contract (schema-independent) | `ITokenEstimator` — `Estimate(string, int)` → `TokenEstimationResult`, `EstimateFromStats(int, int)` → `int`, `EstimateTokenCount(string)` → `int` (Feature 005: direct token count from serialized content) |
| `REBUSS.Pure.Core\IPullRequestDiffCache.cs` | Session-scoped PR diff cache contract (Feature 005) | `IPullRequestDiffCache` — `GetOrFetchDiffAsync(int prNumber, string? knownHeadCommitId = null, CancellationToken ct = default)` → `Task<PullRequestDiff>` (caches per PR number; staleness detection via commit SHA comparison; propagates `PullRequestNotFoundException`) |
| `REBUSS.Pure.Core\IResponsePacker.cs` | Response packing contract | `IResponsePacker` — `Pack(IReadOnlyList<PackingCandidate>, int)` → `PackingDecision` |
| `REBUSS.Pure.Core\IPageAllocator.cs` | Deterministic page allocation contract (Feature 004) | `IPageAllocator` — `Allocate(sortedCandidates, safeBudgetTokens)` → `PageAllocation` |
| `REBUSS.Pure.Core\IPageReferenceCodec.cs` | Base64url page reference encode/decode contract (Feature 004) | `IPageReferenceCodec` — `Encode(PageReferenceData)` → `string`, `TryDecode(string)` → `PageReferenceData?` |
| `REBUSS.Pure.Core\IRepositoryReadyHandler.cs` | Post-download notification contract: invoked by `RepositoryDownloadOrchestrator` after successful extraction; fire-and-forget via `Task.Run` | `IRepositoryReadyHandler` — `OnRepositoryReadyAsync(string repoPath, int prNumber, CancellationToken ct)` |

### Core shared logic(REBUSS.Pure.Core\Shared)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.Core\Shared\DiffEdit.cs` | Line-level edit operation | � (`readonly record struct DiffEdit`) |
| `REBUSS.Pure.Core\Shared\IDiffAlgorithm.cs` | Interface: line-level diff algorithm | `DiffEdit` |
| `REBUSS.Pure.Core\Shared\DiffPlexDiffAlgorithm.cs` | Myers-based diff algorithm backed by DiffPlex NuGet library; uses a `private static readonly Differ` instance (thread-safe, shared); throws `InvalidOperationException` for invariant violations (context gap mismatch, trailing line count mismatch) | `IDiffAlgorithm`, `DiffEdit`, `DiffPlex.Differ` |
| `REBUSS.Pure.Core\Shared\IStructuredDiffBuilder.cs` | Interface: produces `List<DiffHunk>` | `DiffHunk` |
| `REBUSS.Pure.Core\Shared\StructuredDiffBuilder.cs` | Builds structured hunks from base/target content | `IStructuredDiffBuilder`, `IDiffAlgorithm`, `DiffHunk`, `DiffLine`, `DiffEdit` |
| `REBUSS.Pure.Core\Shared\ICodeProcessor.cs` | Interface: async diff processing pipeline hook for enriching/transforming diffs at specific stages | `ICodeProcessor` — `AddBeforeAfterContext(string diff, CancellationToken ct) → Task<string>` |
| `REBUSS.Pure.Core\Shared\IDiffEnricher.cs` | Interface: a unit of diff enrichment with ordering and applicability check; chained by `CompositeCodeProcessor` | `IDiffEnricher` — `Order`, `CanEnrich(string)`, `EnrichAsync(string, CancellationToken)` |
| `REBUSS.Pure.Core\Shared\DiffLanguage.cs` | `DiffLanguage` enum (13 languages + Unknown) + `DiffLanguageDetector` static class: centralized language detection from diff headers; used by all enrichers for `CanEnrich`. Also exposes `IsAlreadyEnriched(string)` for the centralized idempotence short-circuit in `CompositeCodeProcessor` (feature 011) | `DiffLanguageDetector` — `Detect(string)`, `IsCSharp(string)`, `IsSkipped(string)`, `IsAlreadyEnriched(string)` |
| `REBUSS.Pure.Core\Shared\IFileClassifier.cs` | Interface: file classifier | `FileClassification` |
| `REBUSS.Pure.Core\Shared\FileClassifier.cs` | Classifies files by path/extension; normalizes paths with leading `/` for pattern matching; review priorities via `ReviewPriorities` constants | `IFileClassifier`, `FileClassification`, `FileCategory`, `ReviewPriorities` |
| `REBUSS.Pure.Core\Shared\IProgressReporter.cs` | MCP-agnostic progress reporting interface; `ReportAsync(object?, int, int?, string, CancellationToken)` accepts SDK `IProgress<ProgressNotificationValue>`, legacy `ProgressToken`, or `null`; stays free of MCP SDK dependency | `IProgressReporter` |

### Core exceptions (REBUSS.Pure.Core\Exceptions)

| File | Role |
|---|---|
| `REBUSS.Pure.Core\Exceptions\PullRequestNotFoundException.cs` | PR not found (404) |
| `REBUSS.Pure.Core\Exceptions\FileNotFoundInPullRequestException.cs` | File not in PR |
| `REBUSS.Pure.Core\Exceptions\BudgetTooSmallException.cs` | Token budget too small for pagination; thrown by `PageAllocator.Allocate` when `safeBudgetTokens` is below `PaginationConstants.MinimumBudgetForPagination` |

### Resources (REBUSS.Pure.Core\Properties)

| File | Role |
|---|---|
| `REBUSS.Pure.Core\Properties\Resources.resx` | Central string resource file for `REBUSS.Pure.Core` literals: `LogReviewContextOrchestratorAnalyzerProducedSection`, `LogReviewContextOrchestratorSkippingAnalyzer`, `LogStructuredDiffBuilderDiffCompleted`, `LogStructuredDiffBuilderSuspiciousDiff`, `ReviewPriorityHigh`, `ReviewPriorityLow`, `ReviewPriorityMedium` |
| `REBUSS.Pure.Core\Properties\Resources.Designer.cs` | Strongly-typed accessor for `Resources.resx`; namespace `REBUSS.Pure.Core.Properties`; manifest `REBUSS.Pure.Core.Properties.Resources`; 7 `internal static string` properties |

### Analysis pipeline (REBUSS.Pure.Core\Analysis)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.Core\Analysis\AnalysisInput.cs` | All data available to analyzers for a single review | `PullRequestDiff`, `FullPullRequestMetadata`, `PullRequestFiles` |
| `REBUSS.Pure.Core\Analysis\AnalysisSection.cs` | One section of review context output | � |
| `REBUSS.Pure.Core\Analysis\ReviewContext.cs` | Aggregated output from all analyzers | `AnalysisSection` |
| `REBUSS.Pure.Core\Analysis\IReviewAnalyzer.cs` | Pluggable analysis feature interface | `AnalysisInput`, `AnalysisSection` |
| `REBUSS.Pure.Core\Analysis\ReviewContextOrchestrator.cs` | Orchestrates SCM data fetch + analyzer pipeline | `IScmClient`, `IReviewAnalyzer`, `AnalysisInput`, `ReviewContext` |

### Azure DevOps provider - Resources (REBUSS.Pure.AzureDevOps\Properties)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Properties\Resources.resx` | Central string resource file for `REBUSS.Pure.AzureDevOps` literals: `AppDataDirectoryName`, `AzCliExecutable`, `AzCliGetTokenArgsTemplate`, `AzureDevOpsConfigFileName`, `ContentTypeHtml`, `ErrorAuthPageInsteadOfApiData`, `ErrorAzureDevOpsAuthRequired`, `ErrorFileNotFoundAtRef`, `ErrorFileNotFoundInPullRequest`, `ErrorPropertyMustNotContainSpaces`, `ErrorPullRequestNotFound`, `GitExecutable`, `GitRemoteGetUrlArgs`, `ProviderDisplayName`, `SkipReasonBinaryFile`, `SkipReasonFileDeleted`, `SkipReasonFileRenamed`, `SkipReasonFullFileRewrite`, `SkipReasonGeneratedFile` |
| `REBUSS.Pure.AzureDevOps\Properties\Resources.Designer.cs` | Strongly-typed accessor for `Resources.resx`; namespace `REBUSS.Pure.AzureDevOps.Properties`; manifest `REBUSS.Pure.AzureDevOps.Properties.Resources`; 19 `internal static string` properties |

### Azure DevOps provider � API client (REBUSS.Pure.AzureDevOps\Api)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Api\IAzureDevOpsApiClient.cs` | Interface: Azure DevOps REST API client (returns raw JSON strings) |
| `REBUSS.Pure.AzureDevOps\Api\AzureDevOpsApiClient.cs` | HTTP client for Azure DevOps; sets BaseAddress lazily in constructor; `GetStringAsync` detects HTML responses on 2xx status codes (e.g. 203) as authentication failures and throws `HttpRequestException` with `Unauthorized` status code |

### Azure DevOps provider � Parsers (REBUSS.Pure.AzureDevOps\Parsers)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Parsers\IPullRequestMetadataParser.cs` | Interface: parses PR details JSON ? `PullRequestMetadata` / `FullPullRequestMetadata` |
| `REBUSS.Pure.AzureDevOps\Parsers\PullRequestMetadataParser.cs` | Parses Azure DevOps PR details JSON |
| `REBUSS.Pure.AzureDevOps\Parsers\IIterationInfoParser.cs` | Interface: parses iterations JSON ? `IterationInfo` |
| `REBUSS.Pure.AzureDevOps\Parsers\IterationInfoParser.cs` | Parses iterations JSON |
| `REBUSS.Pure.AzureDevOps\Parsers\IFileChangesParser.cs` | Interface: parses file changes JSON ? `List<FileChange>` |
| `REBUSS.Pure.AzureDevOps\Parsers\FileChangesParser.cs` | Parses iteration changes JSON; strips leading `/` from Azure DevOps paths during parse |

### Azure DevOps provider � Configuration/Auth (REBUSS.Pure.AzureDevOps\Configuration)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Configuration\AzureDevOpsOptions.cs` | Config model: org, project, repo, PAT, LocalRepoPath (all optional) |
| `REBUSS.Pure.AzureDevOps\Names.cs` | Canonical Azure DevOps provider identifier constants (`Provider = "AzureDevOps"`, `ProviderLower = "azuredevops"`); referenced by `AzureDevOpsOptions.SectionName`, `Program.cs`, `InitCommand.cs`, `McpWorkspaceRootProvider.cs` via `AzureDevOpsNames` alias |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureDevOpsOptionsValidator.cs` | Validates config field format (all fields optional, format-only checks) |
| `REBUSS.Pure.AzureDevOps\Configuration\IGitRemoteDetector.cs` | Interface + `DetectedGitInfo` record: detects Azure DevOps repo from Git remote |
| `REBUSS.Pure.AzureDevOps\Configuration\GitRemoteDetector.cs` | Parses HTTPS/SSH Azure DevOps remote URLs via `git remote get-url origin`; tries current directory and executable location |
| `REBUSS.Pure.AzureDevOps\Configuration\ILocalConfigStore.cs` | Interface + `CachedConfig` model: persists/retrieves cached config |
| `REBUSS.Pure.AzureDevOps\Configuration\LocalConfigStore.cs` | JSON file store under `%LOCALAPPDATA%/REBUSS.Pure/config.json` |
| `REBUSS.Pure.AzureDevOps\Configuration\IAuthenticationProvider.cs` | Interface: provides `AuthenticationHeaderValue`; `InvalidateCachedToken()` clears cache |
| `REBUSS.Pure.AzureDevOps\Configuration\IAzureCliTokenProvider.cs` | Interface: acquires Azure DevOps token via Azure CLI; defines `AzureCliToken` record |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureCliProcessHelper.cs` | `internal static` helper: resolves `ProcessStartInfo` for cross-platform Azure CLI execution |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureCliTokenProvider.cs` | Runs `az account get-access-token` for Bearer tokens; `ParseTokenResponse` is `internal static` |
| `REBUSS.Pure.AzureDevOps\Configuration\ChainedAuthenticationProvider.cs` | Auth chain: PAT ? cached token ? Azure CLI ? error with actionable instructions from `Resources.ErrorAzureDevOpsAuthRequired` |
| `REBUSS.Pure.AzureDevOps\Configuration\AuthenticationDelegatingHandler.cs` | `DelegatingHandler` that lazily sets auth header; retries once on HTTP 203 HTML redirect |
| `REBUSS.Pure.AzureDevOps\Configuration\ConfigurationResolver.cs` | `IPostConfigureOptions<AzureDevOpsOptions>`: merges explicit config, auto-detected (from git remote), and cached values (priority: user > detected > cached) |

### Azure DevOps provider � Providers (REBUSS.Pure.AzureDevOps\Providers)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsDiffProvider.cs` | Orchestrates fetching PR data + building diffs; fetches file diffs in parallel via `Parallel.ForEachAsync` (max 15 concurrent) with per-file base/target content fetched via `Task.WhenAll`; populates `PullRequestDiff.LastSourceCommitId` from iteration's target commit for cache staleness detection | `IAzureDevOpsApiClient`, `IStructuredDiffBuilder`, `IFileClassifier`, parsers, `PullRequestDiff`, `FileChange`, `DiffHunk` |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsMetadataProvider.cs` | Fetches full PR metadata from multiple endpoints | `IAzureDevOpsApiClient`, parsers, `FullPullRequestMetadata` |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsFilesProvider.cs` | Fetches classified file list directly from iteration-changes API (no diff fetching); calls `IAzureDevOpsApiClient` → `IIterationInfoParser.ParseLast` → `IFileChangesParser.Parse` → `IFileClassifier.Classify`; `Additions`/`Deletions`/`Changes` are always 0 because the ADO iteration-changes API does not return line counts — consumers must treat zeros as "unavailable" (see `IPullRequestDataProvider.GetFilesAsync` remarks) | `IAzureDevOpsApiClient`, `IFileChangesParser`, `IIterationInfoParser`, `IFileClassifier` |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsRepositoryArchiveProvider.cs` | Downloads repository as ZIP archive via Azure DevOps Items API | `IAzureDevOpsApiClient`, `IRepositoryArchiveProvider` |

### Azure DevOps provider � SCM facade (REBUSS.Pure.AzureDevOps)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.AzureDevOps\AzureDevOpsScmClient.cs` | `IScmClient` facade: delegates to fine-grained providers, enriches metadata with `WebUrl`/`RepositoryFullName` | `AzureDevOpsDiffProvider`, `AzureDevOpsMetadataProvider`, `AzureDevOpsFilesProvider`, `AzureDevOpsRepositoryArchiveProvider`, `AzureDevOpsOptions` |
| `REBUSS.Pure.AzureDevOps\ServiceCollectionExtensions.cs` | `AddAzureDevOpsProvider(IConfiguration)`: registers all Azure DevOps DI services including interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IRepositoryArchiveProvider` | All Azure DevOps types above |

### GitHub provider - Resources (REBUSS.Pure.GitHub\Properties)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Properties\Resources.resx` | Central string resource file for `REBUSS.Pure.GitHub` literals: `ApiBaseUrl`, `AppDataDirectoryName`, `ErrorFileNotFoundAtRef`, `ErrorFileNotFoundInPullRequest`, `ErrorGitHubAuthRequired`, `ErrorPropertyMustNotContainSpaces`, `ErrorPullRequestNotFound`, `GhCliAuthTokenArgs`, `GhCliExecutable`, `GitExecutable`, `GitHubAcceptHeader`, `GitHubApiVersion`, `GitHubApiVersionHeader`, `GitHubConfigFileName`, `GitHubRateLimitRemainingHeader`, `GitHubRawContentAcceptHeader`, `GitRemoteGetUrlArgs`, `HttpUserAgentProduct`, `SkipReasonBinaryFile`, `SkipReasonFileDeleted`, `SkipReasonFileRenamed`, `SkipReasonFullFileRewrite`, `SkipReasonGeneratedFile` |
| `REBUSS.Pure.GitHub\Properties\Resources.Designer.cs` | Strongly-typed accessor for `Resources.resx`; namespace `REBUSS.Pure.GitHub.Properties`; manifest `REBUSS.Pure.GitHub.Properties.Resources`; 23 `internal static string` properties |

### GitHub provider � API client (REBUSS.Pure.GitHub\Api)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Api\IGitHubApiClient.cs` | Interface: GitHub REST API v3 client (PR details, files, commits, file content at ref); all methods accept optional `CancellationToken` |
| `REBUSS.Pure.GitHub\Api\GitHubApiClient.cs` | HTTP client for GitHub REST API; base URL `https://api.github.com/`; supports pagination (`GetPaginatedArrayAsync`, max 10 pages); raw file content via `Accept: application/vnd.github.raw+json` header; `GetFileContentAtRefAsync` returns `null` only for 404, throws on other non-success; `LogRateLimitHeaders` called from all HTTP paths (`GetStringAsync`, `GetPaginatedArrayAsync`, `GetFileContentAtRefAsync`) — logs warning on `Retry-After` or low remaining quota; all methods propagate `CancellationToken` to `HttpClient` calls |

### GitHub provider � Parsers (REBUSS.Pure.GitHub\Parsers)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Parsers\IGitHubPullRequestParser.cs` | Interface: parses GitHub PR JSON ? `PullRequestMetadata` / `FullPullRequestMetadata`, base/head commit SHAs, combined `ParseWithCommits` |
| `REBUSS.Pure.GitHub\Parsers\GitHubPullRequestParser.cs` | Parses GitHub PR details JSON; maps state (`open`?`active`, `closed`+`merged`?`completed`, `closed`+`!merged`?`abandoned`); `MapState(state, merged)` is `internal static`; `ParseWithCommits` extracts metadata + base/head SHAs in single JSON parse |
| `REBUSS.Pure.GitHub\Parsers\IGitHubFileChangesParser.cs` | Interface: parses GitHub files JSON ? `List<FileChange>` |
| `REBUSS.Pure.GitHub\Parsers\GitHubFileChangesParser.cs` | Parses GitHub PR files JSON array; maps status (`added`?`add`, `removed`?`delete`, `modified`?`edit`, `renamed`?`rename`, `copied`?`add`); `MapStatus` is `internal static` |

### GitHub provider � Configuration/Auth (REBUSS.Pure.GitHub\Configuration)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Configuration\GitHubOptions.cs` | Config model: Owner, RepositoryName, PersonalAccessToken, LocalRepoPath (all optional); `SectionName = Names.Provider` |
| `REBUSS.Pure.GitHub\Names.cs` | Canonical GitHub provider identifier constants (`Provider = "GitHub"`, `ProviderLower = "github"`); referenced by `GitHubOptions.SectionName`, `GitHubScmClient.ProviderName`, `Program.cs`, `InitCommand.cs` via `GitHubNames` alias |
| `REBUSS.Pure.GitHub\Configuration\GitHubOptionsValidator.cs` | Validates config field format (no-spaces format checks, all fields optional) |
| `REBUSS.Pure.GitHub\Configuration\IGitHubRemoteDetector.cs` | Interface + `DetectedGitHubInfo` record: detects GitHub owner/repo from Git remote |
| `REBUSS.Pure.GitHub\Configuration\GitHubRemoteDetector.cs` | Parses HTTPS/SSH GitHub remote URLs via `git remote get-url origin`; supports repo names with dots (e.g. `CodeReview.MCP`); tries current directory and executable location; `ParseRemoteUrl`, `FindGitRepositoryRoot`, `GetCandidateDirectories` are `internal static` |
| `REBUSS.Pure.GitHub\Configuration\IGitHubConfigStore.cs` | Interface + `GitHubCachedConfig` model (Owner, RepositoryName, AccessToken, TokenExpiresOn): persists/retrieves cached GitHub config |
| `REBUSS.Pure.GitHub\Configuration\GitHubConfigStore.cs` | JSON file store under `%LOCALAPPDATA%/REBUSS.Pure/github-config.json` |
| `REBUSS.Pure.GitHub\Configuration\GitHubConfigurationResolver.cs` | `IPostConfigureOptions<GitHubOptions>`: merges explicit config, auto-detected (from git remote), and cached values (priority: user > detected > cached; same pattern as Azure DevOps `ConfigurationResolver`) |
| `REBUSS.Pure.GitHub\Configuration\IGitHubAuthenticationProvider.cs` | Interface: `GetAuthenticationAsync` + `InvalidateCachedToken`; mirrors `IAuthenticationProvider` from AzureDevOps |
| `REBUSS.Pure.GitHub\Configuration\IGitHubCliTokenProvider.cs` | Interface: acquires GitHub token via GitHub CLI; defines `GitHubCliToken` record |
| `REBUSS.Pure.GitHub\Configuration\GitHubCliProcessHelper.cs` | `internal static` helper: resolves `ProcessStartInfo` for cross-platform GitHub CLI execution (Windows `cmd.exe /c gh`, Linux direct `gh`) |
| `REBUSS.Pure.GitHub\Configuration\GitHubCliTokenProvider.cs` | Runs `gh auth token` for Bearer tokens; `ParseTokenResponse` is `internal static`; `DefaultTokenLifetime = 24 hours`; `RunGhCliAsync` creates a linked timeout CTS (`CommandTimeout = 30s`) before reading stdout/stderr to ensure the timeout covers the full operation |
| `REBUSS.Pure.GitHub\Configuration\GitHubChainedAuthenticationProvider.cs` | Auth chain: PAT ? cached token ? GitHub CLI (`gh auth token`) ? error with actionable instructions from `Resources.ErrorGitHubAuthRequired` |
| `REBUSS.Pure.GitHub\Configuration\GitHubAuthenticationHandler.cs` | `DelegatingHandler` that lazily resolves auth via `IGitHubAuthenticationProvider`; sets GitHub API headers (`Accept`, `User-Agent`, `X-GitHub-Api-Version: 2022-11-28`); retries once on HTTP 401 or 403 (excluding rate-limit 403 detected via `X-RateLimit-Remaining: 0`) with token invalidation |

### GitHub provider � Providers (REBUSS.Pure.GitHub\Providers)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.GitHub\Providers\GitHubDiffProvider.cs` | Orchestrates fetching PR data + building diffs; uses `ParseWithCommits` for single-parse metadata/SHA extraction; fetches file diffs in parallel via `Parallel.ForEachAsync` (max 15 concurrent) with per-file base/head content fetched via `Task.WhenAll`; populates `PullRequestDiff.LastSourceCommitId` from PR head commit for cache staleness detection | `IGitHubApiClient`, `IStructuredDiffBuilder`, `IFileClassifier`, parsers, `PullRequestDiff`, `FileChange`, `DiffHunk` |
| `REBUSS.Pure.GitHub\Providers\GitHubMetadataProvider.cs` | Fetches full PR metadata from PR details + commits endpoints | `IGitHubApiClient`, `IGitHubPullRequestParser`, `FullPullRequestMetadata` |
| `REBUSS.Pure.GitHub\Providers\GitHubFilesProvider.cs` | Fetches classified file list directly from PR files API (no diff fetching); calls `IGitHubApiClient.GetPullRequestFilesAsync` → `IGitHubFileChangesParser.Parse` → `IFileClassifier.Classify`; populates additions/deletions from API response | `IGitHubApiClient`, `IGitHubFileChangesParser`, `IFileClassifier` |
| `REBUSS.Pure.GitHub\Providers\GitHubRepositoryArchiveProvider.cs` | Downloads repository as ZIP archive via GitHub zipball API | `IGitHubApiClient`, `IRepositoryArchiveProvider` |

### GitHub provider � SCM facade (REBUSS.Pure.GitHub)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.GitHub\GitHubScmClient.cs` | `IScmClient` facade: delegates to fine-grained providers, enriches metadata with `WebUrl` (`https://github.com/{owner}/{repo}/pull/{n}`) and `RepositoryFullName` (`{owner}/{repo}`) | `GitHubDiffProvider`, `GitHubMetadataProvider`, `GitHubFilesProvider`, `GitHubRepositoryArchiveProvider`, `GitHubOptions` |
| `REBUSS.Pure.GitHub\ServiceCollectionExtensions.cs` | `AddGitHubProvider(IConfiguration)`: registers all GitHub DI services including options, validator, remote detector, config store, post-configure, auth handler, HTTP client with resilience, parsers, providers, facade + interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IRepositoryArchiveProvider` | All GitHub types above |

### Roslyn code processor (REBUSS.Pure.RoslynProcessor)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.RoslynProcessor\DiffSourceResolver.cs` | Shared source resolver: extracts before/after source code from downloaded repo for a given diff; handles download wait, path resolution, size limits, before-code reconstruction | `IRepositoryDownloadOrchestrator`, `DiffParser`, `RepositoryFileResolver` |
| `REBUSS.Pure.RoslynProcessor\BeforeAfterEnricher.cs` | `IDiffEnricher` (Order=100): enriches C# diffs with before/after context lines using Roslyn syntax analysis | `DiffSourceResolver`, `BeforeAfterAnalyzer`, `DiffParser` |
| `REBUSS.Pure.RoslynProcessor\ScopeAnnotatorEnricher.cs` | `IDiffEnricher` (Order=150): annotates each hunk header with enclosing scope `[scope: Class.Method(Params)]` | `DiffSourceResolver`, `ScopeResolver`, `DiffParser` |
| `REBUSS.Pure.RoslynProcessor\ScopeResolver.cs` | Static scope resolver: finds enclosing member/type/namespace for a line number in a syntax tree | `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\StructuralChangeEnricher.cs` | `IDiffEnricher` (Order=200): detects and annotates structural C# changes (signature changes, added/removed members/types, base type changes). Bails out on zero-hunk inputs (e.g. renames) to avoid spurious blocks (feature 011) | `DiffSourceResolver`, `StructuralChangeDetector`, `DiffParser` |
| `REBUSS.Pure.RoslynProcessor\StructuralChangeDetector.cs` | Static syntax-only detector: compares before/after syntax trees, returns list of structural changes | `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\StructuralChange.cs` | `StructuralChange` record + `StructuralChangeKind` enum: model for detected structural differences | — |
| `REBUSS.Pure.RoslynProcessor\UsingsChangeEnricher.cs` | `IDiffEnricher` (Order=250): detects added/removed `using` directives and inserts `[dependency-changes]` block | `DiffSourceResolver`, `UsingsChangeDetector` |
| `REBUSS.Pure.RoslynProcessor\UsingsChangeDetector.cs` | Static detector: extracts and compares `using` directives from before/after C# source | `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\UsingChange.cs` | `UsingChange` record + `UsingChangeKind` enum: model for using directive changes | — |
| `REBUSS.Pure.RoslynProcessor\CallSiteEnricher.cs` | `IDiffEnricher` (Order=300): discovers where changed members are used in the repo and inserts `[call-sites]` block | `IRepositoryDownloadOrchestrator`, `CallSiteScanner`, `CallSiteTargetExtractor` |
| `REBUSS.Pure.RoslynProcessor\CallSiteScanner.cs` | Scans repo C# files for identifier references using Roslyn syntax-tree matching; pre-filters with string Contains | `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\CallSiteTargetExtractor.cs` | Static: extracts search targets from `[structural-changes]` block or heuristic fallback from diff `+` lines | — |
| `REBUSS.Pure.RoslynProcessor\CallSiteResult.cs` | Records: `CallSiteTarget`, `CallSiteTargetKind`, `CallSiteResult`, `CallSiteLocation` | — |
| `REBUSS.Pure.RoslynProcessor\ContextDecision.cs` | Context level enum: None (cosmetic), Minimal (structural, 3 lines), Full (semantic, 10 lines) | — |
| `REBUSS.Pure.RoslynProcessor\BeforeAfterAnalyzer.cs` | Static Roslyn-based analyzer: compares before/after C# syntax trees, classifies changes into ContextDecision | `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\DiffParser.cs` | Static diff parser: extracts file path and hunk info from formatted diffs, rebuilds diffs with context lines. `RebuildDiffWithContext` uses a three-phase pipeline (cluster adjacent hunks → expand each cluster with shared per-axis-clamped context window → render) to guarantee one merged hunk per non-overlapping range, valid unified-diff syntax, and no duplicated source lines (feature 011) | `ParsedHunk` |
| `REBUSS.Pure.RoslynProcessor\RepositoryFileResolver.cs` | Static file resolver: detects nested ZIP root directory, resolves diff paths to filesystem paths | — |
| `REBUSS.Pure.RoslynProcessor\FindingScopeExtractor.cs` | Feature 021 — extracts the full source body of the enclosing method/property/constructor/type for a given 1-based line number. Delegates the walk + formatted name to `ScopeResolver`. Truncates to `maxLines` window ±`maxLines/2` around the finding line (with `// ... (N lines omitted) ...` markers) when the body exceeds the budget. | `ScopeResolver`, `Microsoft.CodeAnalysis.CSharp` |
| `REBUSS.Pure.RoslynProcessor\FindingLineResolver.cs` | Feature 021 — recovers a usable line number for findings where Copilot omitted one (`(line unknown)`) or wrote an approximation (`~138`, `100-150`, `approx 100`). Extracts backtick-quoted identifiers from the finding description (including qualified names like `Foo.Bar` — uses trailing segment), walks the syntax tree, and scores candidates by declaration kind (method=100, local function=95, property=90, indexer=85, ctor/dtor=80, event=75, type=40, field/variable=20, parameter=10) with a proximity bonus when a hint line is supplied. Returns the highest-scored line, or `null` when no identifier matches. | `Microsoft.CodeAnalysis.CSharp` |

### Repository download (REBUSS.Pure\Services\RepositoryDownload)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\RepositoryDownload\IRepositoryDownloadOrchestrator.cs` | Interface: manages download lifecycle (deduplication, state tracking, background execution) | `RepositoryDownloadState` |
| `REBUSS.Pure\Services\RepositoryDownload\RepositoryDownloadOrchestrator.cs` | Orchestrates background ZIP download, extraction, and cleanup; singleton with mutable state; uses PID-based temp directory naming; registers shutdown cleanup via `IHostApplicationLifetime.ApplicationStopping` | `IRepositoryArchiveProvider`, `IHostApplicationLifetime`, `System.IO.Compression.ZipFile` |
| `REBUSS.Pure\Services\RepositoryDownload\RepositoryCleanupService.cs` | `IHostedService`: scans for orphaned `rebuss-repo-*` temp directories on startup; extracts PID from directory name and checks if process is running; deletes orphaned directories and ZIP files | `RepositoryDownloadOrchestrator.TempRootPrefix` |

### Local review pipeline (REBUSS.Pure\Services\LocalReview)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\LocalReview\ILocalGitClient.cs` | Interface: local git operations; defines `LocalFileStatus` record | � |
| `REBUSS.Pure\Services\LocalReview\LocalGitClient.cs` | Runs git child processes; uses `diff --name-status` for all scopes; exposes `WorkingTreeRef` sentinel for filesystem reads. Handles repos without HEAD (no commits) via optimistic catch-and-retry: tries HEAD-based commands first, verifies HEAD absence with `rev-parse --verify HEAD` only on failure, then falls back to `diff --cached` (staged) or `status --porcelain` (working-tree); BranchDiff throws `GitCommandException` when no HEAD. Eliminates the extra `rev-parse` process in the common case. Redirects stdin on child processes to prevent MCP stdio deadlocks. | `ILocalGitClient` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewScope.cs` | Value type: `WorkingTree`, `Staged`, `BranchDiff(base)` + `Parse(string?)` | � |
| `REBUSS.Pure\Services\LocalReview\ILocalReviewProvider.cs` | Interface: lists local files + diffs; defines `LocalReviewFiles` model | `PullRequestDiff`, `PullRequestFileInfo`, `PullRequestFilesSummary` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewProvider.cs` | Orchestrates git client + diff builder + file classifier | `IWorkspaceRootProvider` (from Core), `ILocalGitClient`, `IStructuredDiffBuilder`, `IFileClassifier`, domain models |
| `REBUSS.Pure\Services\LocalReview\LocalReviewExceptions.cs` | `LocalRepositoryNotFoundException`, `LocalFileNotFoundException`, `GitCommandException` | � |

### Diff enrichment pipeline (REBUSS.Pure\Services)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\CompositeCodeProcessor.cs` | `ICodeProcessor` implementation: chains all registered `IDiffEnricher` implementations in ascending `Order`; logs and skips on failure. Centralized idempotence short-circuit at the top of `AddBeforeAfterContext` returns the input unchanged if `DiffLanguageDetector.IsAlreadyEnriched` returns true (feature 011) | `ICodeProcessor`, `IEnumerable<IDiffEnricher>`, `DiffLanguageDetector` |

### Context Window Awareness (REBUSS.Pure\Services\ContextWindow)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\ContextWindow\ContextWindowOptions.cs` | `IOptions<T>` configuration: safety margin, chars-per-token, default budget, min/max guardrails, gateway cap (`GatewayMaxTokens`), model registry | — |
| `REBUSS.Pure\Services\ContextWindow\ContextBudgetResolver.cs` | Three-tier budget resolution: explicit → registry → default, with gateway cap (`GatewayMaxTokens` — hard platform/proxy limit applied before guardrails and safety margin), min/max guardrails and warnings; registry lookup uses `NormalizeModelId` (lowercase, spaces/dots/underscores/hyphens → single hyphen) for both the incoming identifier and each registry key — tries exact normalized match first, then longest-prefix normalized match (`"Claude Sonnet 4.6"` → `"claude-sonnet-4-6"` prefix-matches `"claude-sonnet-4"` → 200 K) | `IContextBudgetResolver`, `IOptions<ContextWindowOptions>`, `ILogger<ContextBudgetResolver>` |
| `REBUSS.Pure\Services\ContextWindow\TokenEstimator.cs` | Character-count token estimation heuristic, schema-independent; stat-based estimation (`EstimateFromStats`) using `AvgTokensPerLine=15`, `PerFileOverhead=50`; direct token count (`EstimateTokenCount`) using configurable `CharsPerToken` ratio with 4.0 default fallback (Feature 005) | `ITokenEstimator`, `IOptions<ContextWindowOptions>`, `ILogger<TokenEstimator>` |

### Response Packing (REBUSS.Pure\Services)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\PackingPriorityComparer.cs` | Compares `PackingCandidate` items: FileCategory asc, TotalChanges desc, Path asc | `PackingCandidate` |
| `REBUSS.Pure\Services\ResponsePacker.cs` | Greedy priority-based packing: sorts candidates, includes until budget exhausted, first oversized gets Partial; manifest overhead constants sourced from `PaginationConstants` | `IResponsePacker`, `PackingPriorityComparer`, `PaginationConstants`, `ILogger<ResponsePacker>` |

### PR Diff Cache (REBUSS.Pure\Services) — Feature 005

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\PullRequestDiffCache.cs` | Session-scoped cache with in-flight deduplication; concurrent cache misses for the same PR are coalesced via `ConcurrentDictionary<int, Lazy<Task<PullRequestDiff>>>` so only one fetch is issued; completed results stored in a separate `ConcurrentDictionary<int, PullRequestDiff>`; staleness detection — when `knownHeadCommitId` differs from cached `LastSourceCommitId`, both cache and in-flight entries are evicted; failed fetches are not cached (in-flight entry removed via value-aware `TryRemove`) | `IPullRequestDiffCache`, `IPullRequestDataProvider`, `ILogger<PullRequestDiffCache>` |

### Pagination (REBUSS.Pure\Services\Pagination) — Feature 004

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\PageAllocator.cs` | Strict sequential page allocator: fills pages until budget exhausted, oversized items get Partial status; log templates sourced from `Resources` constants | `IPageAllocator`, `PageAllocation`, `PageSlice`, `PageSliceItem`, `PackingItemStatus`, `Resources` |
| `REBUSS.Pure\Services\PageReferenceCodec.cs` | Base64url JSON codec with compact keys (t/r/b/p/f) for page reference tokens | `IPageReferenceCodec`, `PageReferenceData` |
| `REBUSS.Pure\Services\PaginationConstants.cs` | Static constants: PaginationOverhead=150, BaseManifestOverhead=100, PerItemManifestOverhead=15, MinimumBudgetForPagination=265, FallbackEstimateWhenLinecountsUnknown=300 | — |
| `REBUSS.Pure\Services\PaginationOrchestrator.cs` | Internal static helper: validation, page resolution, param matching, staleness detection, metadata building | `PageReferenceData`, `PageAllocation`, `PaginationConstants`, `PaginationMetadataResult`, `StalenessWarningResult` |

### Progressive PR Metadata orchestrator (REBUSS.Pure\Services\PrEnrichment)

Owns background enrichment of pull-request diffs so `get_pr_metadata` can fall back to a basic-summary response on internal timeout while enrichment continues, and `get_pr_content` can later reuse the in-flight or completed work with its own fresh timeout window. Mirrors `RepositoryDownloadOrchestrator` precedent: singleton with mutable per-PR state, background body runs under `IHostApplicationLifetime.ApplicationStopping`, never the caller's `CancellationToken`. Documented Principle VI deviation in plan.md.

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\PrEnrichment\IPrEnrichmentOrchestrator.cs` | Public surface: `TriggerEnrichment(prNumber, headSha, safeBudgetTokens)` (idempotent same-SHA, supersede on different SHA), `WaitForEnrichmentAsync(prNumber, ct)` — caller's ct only governs the wait, never the background body, `TryGetSnapshot(prNumber)` for SHA discovery + Failed-fast-path | — |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentOrchestrator.cs` | Singleton implementation: `ConcurrentDictionary<int, PrEnrichmentJob>` keyed by PR number, `_lock` for state transitions, `_shutdownToken` from `IHostApplicationLifetime.ApplicationStopping`. Background body: `IPullRequestDiffCache.GetOrFetchDiffAsync` → `FileTokenMeasurement.BuildEnrichedCandidatesAsync` → `PackingPriorityComparer.Instance` sort → `IPageAllocator.Allocate`. Lifecycle logging (start / completion / failure / cancellation). Failed jobs are retryable | `IPullRequestDiffCache`, `ITokenEstimator`, `IFileClassifier`, `ICodeProcessor`, `IPageAllocator`, `IHostApplicationLifetime`, `ILogger<>`, `FileTokenMeasurement`, `PackingPriorityComparer` |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentJob.cs` | Internal mutable per-PR record: `PrNumber`, `HeadSha`, `Cts`, `Status` (FetchingDiff/Enriching/Ready/Failed), `ResultTask`, `Result?`, `Failure?`, `StartedAt`. Lives only as values inside the orchestrator's dictionary | `PrEnrichmentStatus`, `PrEnrichmentResult`, `PrEnrichmentFailure` |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentResult.cs` | Immutable record: `SortedCandidates`, `EnrichedByPath`, `Allocation`, `SafeBudgetTokens` (used for budget-mismatch detection in content handler), `CompletedAt`. `HeadSha` field is sourced from `FullPullRequestMetadata.LastMergeSourceCommitId` | `PackingCandidate`, `PageAllocation` |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentFailure.cs` | Immutable record: `ExceptionTypeName` (short name only), `SanitizedMessage` (`%LOCALAPPDATA%` redacted per Principle VIII), `FailedAt`. Static `From(Exception)` factory | — |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentJobSnapshot.cs` | Read-only projection of a job for `TryGetSnapshot` callers; never exposes the live `PrEnrichmentJob` outside the orchestrator | `PrEnrichmentStatus`, `PrEnrichmentResult`, `PrEnrichmentFailure` |
| `REBUSS.Pure\Services\PrEnrichment\PrEnrichmentStatus.cs` | Lifecycle enum: `FetchingDiff`, `Enriching`, `Ready`, `Failed` | — |
| `REBUSS.Pure\Services\PrEnrichment\WorkflowOptions.cs` | `IOptions<T>` configuration: `MetadataInternalTimeoutMs` (default 28 000), `ContentInternalTimeoutMs` (default 28 000). Bound from `Workflow` section in `appsettings.json`. Both values must be strictly less than the host's hard tool-call ceiling (~30 000 ms) so responses serialize in time | — |

### MCP tool handlers(REBUSS.Pure\Tools)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Tools\GetPullRequestMetadataToolHandler.cs` | `[McpServerToolType]` `get_pr_metadata` — returns plain text `IEnumerable<ContentBlock>`; with optional `modelName`/`maxTokens` it kicks off background enrichment via `IPrEnrichmentOrchestrator` and waits up to `WorkflowOptions.MetadataInternalTimeoutMs` (default 28 000 ms). On internal timeout falls back to a basic-summary response with an explicit "paging not yet available" indicator (FR-002, FR-004); on background failure appends a friendly-status block (FR-017). The host never sees a tool-call timeout regardless of PR size. SDK attribute-based registration | `IPullRequestDataProvider`, `IContextBudgetResolver`, `IRepositoryDownloadOrchestrator`, `IPrEnrichmentOrchestrator`, `IOptions<WorkflowOptions>`, `PlainTextFormatter` |
| `REBUSS.Pure\Tools\GetPullRequestContentToolHandler.cs` | `[McpServerToolType]` `get_pr_content` — returns plain text `IEnumerable<ContentBlock>` (file blocks + pagination footer); reads enriched content from `IPrEnrichmentOrchestrator`'s cached `PrEnrichmentResult`. Cold-start path (no prior snapshot) calls `IPullRequestDataProvider.GetMetadataAsync` for SHA discovery then triggers enrichment. Detects budget mismatch and supersedes with new safeBudget. Runs with its own `WorkflowOptions.ContentInternalTimeoutMs` (default 28 000 ms) — on timeout returns a friendly "still preparing" status block, on Failed snapshot returns a friendly failure block. SDK attribute-based registration | `IPullRequestDataProvider`, `IContextBudgetResolver`, `IPrEnrichmentOrchestrator`, `IOptions<WorkflowOptions>`, `PlainTextFormatter` |
| `REBUSS.Pure\Tools\GetLocalContentToolHandler.cs` | `[McpServerToolType]` `get_local_content` — returns plain text `IEnumerable<ContentBlock>` (file blocks + pagination footer); stat-based page allocation, parallel per-file diff fetch via `Task.WhenAll` (only page files); uses `FileTokenMeasurement.MapToStructured` for file mapping; SDK attribute-based registration | `ILocalReviewProvider`, `IContextBudgetResolver`, `ITokenEstimator`, `IFileClassifier`, `IPageAllocator`, `FileTokenMeasurement`, `PlainTextFormatter`, `PaginationMetadataResult` |
### Tool shared helpers (REBUSS.Pure\Tools\Shared)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Tools\Shared\PlainTextFormatter.cs` | Static internal helper: formats all tool output as plain text strings — `FormatFileDiff`, `FormatHunk`, `FormatFileList`, `FormatFileEntry`, `FormatManifestBlock`, `FormatPaginationBlock`, `FormatSimplePaginationBlock`, `FormatMetadata` (with `pagingDeferred` flag for FR-004 — emits explicit "Content paging: not yet available" indicator when basic-summary fallback is taken), `FormatFriendlyStatus(headline, explanation, suggestedNextAction)` for FR-014/FR-015/FR-016/FR-017 still-preparing and failure responses | `StructuredFileChange`, `ContentManifestResult`, `ManifestEntryResult`, `ManifestSummaryResult`, `PaginationMetadataResult`, `StalenessWarningResult` |
| `REBUSS.Pure\Tools\Shared\FileTokenMeasurement.cs` | Static internal helper (Feature 005): `BuildCandidatesFromDiff` formats each `FileChange` to plain text via `PlainTextFormatter.FormatFileDiff` and measures actual token cost via `ITokenEstimator.EstimateTokenCount`; `MapToStructured` converts domain `FileChange` → `StructuredFileChange` internal model (line op char→string mapping) | `PullRequestDiff`, `ITokenEstimator`, `IFileClassifier`, `PackingCandidate`, `PlainTextFormatter`, `StructuredFileChange` |
| `REBUSS.Pure\Tools\Shared\ToolHandlerHelpers.cs` | Static internal helper: shared pagination/packing methods extracted from tool handlers — `SortCandidates` (priority sort), `BuildCandidates<T>` (generic plain-text format→estimate→classify), `ExtractPageFiles<T>` (simple and partial-aware overloads), `BuildPageManifest` (manifest DTO from page slice) | `PackingPriorityComparer`, `ITokenEstimator`, `IFileClassifier`, `PackingCandidate`, `PageSlice`, `PageAllocation`, `PaginationOrchestrator`, `PlainTextFormatter`, `StructuredFileChange`, `ContentManifestResult`, `ManifestEntryResult` |

### Tool output models (REBUSS.Pure\Tools\Models)

Note: As of the plain-text ContentBlock refactor, most JSON output DTOs have been removed. Handlers now return `IEnumerable<ContentBlock>` (plain text) via `PlainTextFormatter`. The remaining models are internal data structures used by formatters and helpers.

| File | Role |
|---|---|
| `REBUSS.Pure\Tools\Models\StructuredFileChange.cs` | Internal diff data structures (no JSON attributes): `StructuredFileChange` (path, changeType, skipReason?, additions, deletions, hunks), `StructuredHunk` (oldStart, oldCount, newStart, newCount, lines), `StructuredLine` (op, text) — used by `PlainTextFormatter`, `ToolHandlerHelpers`, `FileTokenMeasurement` |
| `REBUSS.Pure\Tools\Models\ManifestEntryResult.cs` | `ManifestEntryResult` — internal DTO for a single manifest item (path, estimatedTokens, status, priorityTier); used by `PlainTextFormatter.FormatManifestBlock` and `ToolHandlerHelpers.BuildPageManifest` |
| `REBUSS.Pure\Tools\Models\ManifestSummaryResult.cs` | `ManifestSummaryResult` — internal DTO for manifest aggregated stats; 3 nullable properties: includedOnThisPage (int?), remainingAfterThisPage (int?), totalPages (int?); used by `PlainTextFormatter.FormatManifestBlock` |
| `REBUSS.Pure\Tools\Models\PaginationMetadataResult.cs` | `PaginationMetadataResult` — pagination navigation DTO: currentPage, totalPages, hasMore, currentPageReference, nextPageReference? (Feature 004); used by `PlainTextFormatter.FormatPaginationBlock` |
| `REBUSS.Pure\Tools\Models\StalenessWarningResult.cs` | `StalenessWarningResult` — staleness warning DTO: message, originalFingerprint, currentFingerprint (Feature 004); used by `PlainTextFormatter.FormatPaginationBlock` and `FormatManifestBlock` |
| `REBUSS.Pure\Tools\Models\ContentManifestResult.cs` | `ContentManifestResult` — internal DTO wrapping `ManifestEntryResult` list + `ManifestSummaryResult`; used by `PlainTextFormatter.FormatManifestBlock` and `ToolHandlerHelpers.BuildPageManifest` |

### Workspace root (REBUSS.Pure\Services)

| File | Role |
|---|---|
| `REBUSS.Pure\Mcp\McpServer.cs` | Main server loop: reads JSON-RPC over stdio, dispatches to method handlers; silently ignores notifications (messages without `id`) for unregistered methods per MCP/JSON-RPC spec |
| `REBUSS.Pure\Mcp\IMcpMethodHandler.cs` | Interface: handles one JSON-RPC method |
| `REBUSS.Pure\Mcp\IMcpToolHandler.cs` | Interface: MCP tool (definition + execution) |
| `REBUSS.Pure\Mcp\McpWorkspaceRootProvider.cs` | Implementation of `IWorkspaceRootProvider` (from Core): resolves repo root from CLI `--repo` (highest priority), MCP roots, or `localRepoPath` config; guards against unexpanded variables; reads `LocalRepoPath` directly from `IConfiguration` to avoid circular dependency with `IPostConfigureOptions<AzureDevOpsOptions>` |
| `REBUSS.Pure\Mcp\Handlers\InitializeMethodHandler.cs` | `initialize` method handler — negotiates protocol version via `IMcpProtocolVersionNegotiator`, extracts MCP roots, stores via `IWorkspaceRootProvider` |
| `REBUSS.Pure\Mcp\Handlers\ToolsListMethodHandler.cs` | `tools/list` method handler |
| `REBUSS.Pure\Mcp\Handlers\ToolsCallMethodHandler.cs` | `tools/call` method handler � resolves tool by name, delegates |
| `REBUSS.Pure\Mcp\IJsonRpcSerializer.cs` | Interface: JSON-RPC serialization |
| `REBUSS.Pure\Mcp\SystemTextJsonSerializer.cs` | System.Text.Json implementation (camelCase, no indent, ignore nulls) |
| `REBUSS.Pure\Mcp\IJsonRpcTransport.cs` | Interface: read/write JSON-RPC messages |
| `REBUSS.Pure\Mcp\StreamJsonRpcTransport.cs` | Newline-delimited stream transport |
| `REBUSS.Pure\Mcp\McpMethodNotFoundException.cs` | Method not found exception |
| `REBUSS.Pure\Mcp\McpProtocolVersionException.cs` | Protocol version negotiation failure exception |
| `REBUSS.Pure\Mcp\IMcpProtocolVersionNegotiator.cs` | Interface: negotiates MCP protocol version between client and server |
| `REBUSS.Pure\Mcp\McpProtocolVersionNegotiator.cs` | Default implementation: maintains supported version list, selects highest compatible version |

### MCP models (REBUSS.Pure\Mcp\Models)

| File | Role |
|---|---|
| `REBUSS.Pure\Mcp\Models\JsonRpcMessage.cs` | Base class (jsonrpc = "2.0") |
| `REBUSS.Pure\Mcp\Models\JsonRpcRequest.cs` | Request: id, method, params |
| `REBUSS.Pure\Mcp\Models\JsonRpcResponse.cs` | Response: id, result, error |
| `REBUSS.Pure\Mcp\Models\JsonRpcError.cs` | Error: code, message, data |
| `REBUSS.Pure\Mcp\Models\McpTool.cs` | Tool definition: name, description, inputSchema |
| `REBUSS.Pure\Mcp\Models\ToolInputSchema.cs` | JSON Schema for tool input |
| `REBUSS.Pure\Mcp\Models\ToolProperty.cs` | Schema property: type, description, enum, default |
| `REBUSS.Pure\Mcp\Models\ToolCallParams.cs` | Tool call params: name, arguments |
| `REBUSS.Pure\Mcp\Models\ToolResult.cs` | Tool result: content items, isError |
| `REBUSS.Pure\Mcp\Models\ContentItem.cs` | Content item: type, text |
| `REBUSS.Pure\Mcp\Models\InitializeResult.cs` | Initialize result: protocol version, capabilities, server info |
| `REBUSS.Pure\Mcp\Models\InitializeParams.cs` | Initialize params: protocol version and roots list from MCP client |
| `REBUSS.Pure\Mcp\Models\McpRoot.cs` | MCP root: uri, name |
| `REBUSS.Pure\Mcp\Models\ServerCapabilities.cs` | Server capabilities |
| `REBUSS.Pure\Mcp\Models\ServerInfo.cs` | Server info: name, version |
| `REBUSS.Pure\Mcp\Models\ToolsCapability.cs` | Tools capability: listChanged |
| `REBUSS.Pure\Mcp\Models\ToolsListResult.cs` | Tools list result |

### CLI infrastructure (REBUSS.Pure\Cli)

| File | Role |
|---|---|
| `REBUSS.Pure\Cli\CliArgumentParser.cs` | Parses CLI args: detects `init` command vs server mode, extracts `--repo`, `--pat`, `--org`, `--project`, `--repository`, `--provider`, `--owner`; parses `--pat`, `-g`/`--global`, and `--ide` for `init` command; `CliParseResult.IsGlobal` flag, `CliParseResult.Ide` property |
| `REBUSS.Pure\Cli\ICliCommand.cs` | Interface: executable CLI command |
| `REBUSS.Pure\Cli\ICliAuthFlow.cs` | Interface: provider-specific CLI authentication flow during `init` (`RunAsync`) |
| `REBUSS.Pure\Cli\AzureDevOpsCliAuthFlow.cs` | Azure DevOps auth flow: checks for Azure CLI, runs `az login`, caches token; offers to install Azure CLI if not found |
| `REBUSS.Pure\Cli\GitHubCliAuthFlow.cs` | GitHub auth flow: checks for GitHub CLI, runs `gh auth login --web`, caches token; offers to install GitHub CLI if not found. Exposes `GhCliPathOverride` so the downstream Copilot step can reuse a freshly-resolved `gh.exe` path in the same init session. |
| `REBUSS.Pure\Cli\CopilotCliSetupStep.cs` | Feature 012 — optional GitHub Copilot CLI setup step run at the end of `InitCommand.ExecuteAsync` regardless of SCM provider or `--pat`. Detects `gh` / auth / `gh copilot` extension state fresh on every run (no persisted decline), prompts the user, and installs the `github/gh-copilot` extension on consent. When the extension install fails because newer `gh` CLI ships copilot as a built-in command (error: "matches the name of a built-in command"), falls back to running `gh copilot --version` interactively to trigger the built-in installation prompt. Decline, failure, or non-interactive stdin are soft exits with an informational banner; the step never affects init's exit code. Reuses `GitHubCliProcessHelper.TryFindGhCliOnWindows` for fresh-install PATH recovery. |
| `REBUSS.Pure\Services\CopilotReview\CopilotClientProvider.cs` | Feature 013 + 018 — owns the singleton `GitHub.Copilot.SDK.CopilotClient` for the MCP server process. Lazy-started on first call to `TryEnsureStartedAsync` (gate: `SemaphoreSlim`), delegates verification to `CopilotVerificationRunner` and caches the resulting `CopilotVerdict` as `StartupVerdict`. Feature 018: holds the client through an `ICopilotSdkOps` wrapper; cast to real `CopilotClient` is done via `ops.UnderlyingClient` on the happy path. Graceful `StopAsync` with 5s deadline via `IHostedService` shutdown hook. |
| `REBUSS.Pure\Services\CopilotReview\CopilotVerificationRunner.cs` | Feature 018 — runs the 6-step verification sequence (token resolve → client build → `StartAsync` → `GetAuthStatusAsync` → `ListModelsAsync` model-entitlement check → best-effort `GetQuotaAsync` quota check → verdict + log). On success returns `(ICopilotSdkOps, Ok verdict)`; on failure disposes the ops wrapper and returns `(null, failure verdict)`. Also implements `ICopilotVerificationProbe` — a diagnostic entry point used by `CopilotCliSetupStep` that disposes the live client immediately after verification (the init step only needs the verdict). Internal seam: `ICopilotSdkOps` + `CopilotSdkOps` (same file) wrap the real SDK so tests can swap in a stub — `CopilotClient` itself is not unit-testable. |
| `REBUSS.Pure\Services\CopilotReview\CopilotTokenResolver.cs` | Feature 018 — pure function resolving the token source: `REBUSS_COPILOT_TOKEN` env var → `CopilotReview:GitHubToken` config → `(null, LoggedInUser)`. Blank / whitespace treated as unset (FR-012). The token value never touches `ILogger`, `ToString`, or exception messages (FR-013a). Precedence is deliberately the opposite of `GitHubChainedAuthenticationProvider` — env beats config — to support CI / headless operators without editing shipped `appsettings.json`. |
| `REBUSS.Pure\Services\CopilotReview\CopilotUnavailableException.cs` | Feature 018 — thrown from `CopilotAvailabilityDetector.IsAvailableAsync` only when `CopilotReview:StrictMode = true` AND the cached verdict signals a real verification failure (NOT `DisabledByConfig` — FR-016 remediation I1). Carries the full `CopilotVerdict`; `Message` is `verdict.Remediation` (FR-013a-safe by construction). Caught by `GetPullRequestContentToolHandler` and rethrown into the MCP tool-error envelope. |
| `REBUSS.Pure\Services\CopilotReview\CopilotRequestThrottle.cs` | Static throttle ensuring Copilot SDK requests (`CreateSessionAsync`, `SendAsync`) are spaced at least 3 seconds apart. Uses `SemaphoreSlim(1,1)` gate + timestamp tracking. |
| `REBUSS.Pure\Services\CopilotReview\CopilotSessionFactory.cs` | Feature 013 — wraps `CopilotClient.CreateSessionAsync` in the `ICopilotSessionHandle` abstraction so `REBUSS.Pure.Core` never depends on SDK types. Builds `SessionConfig` with `Model`, `PermissionHandler.ApproveAll`, `InfiniteSessions.Enabled = false`, `Streaming = false`. Feature 018 T020: the previous "model not in list" Warning was downgraded to Debug — upstream `CopilotVerificationRunner` already verifies model entitlement, so a duplicate warning here would be redundant. Both `CreateSessionAsync` and `CopilotSessionHandle.SendAsync` call `CopilotRequestThrottle.WaitAsync` before issuing SDK requests. |
| `REBUSS.Pure\Services\CopilotReview\CopilotAvailabilityDetector.cs` | Feature 013 + 018 — reads `CopilotVerdict` from `ICopilotClientProvider.StartupVerdict` and caches it for the process lifetime. `IsAvailableAsync` returns `verdict.IsAvailable` in graceful mode; in strict mode it throws `CopilotUnavailableException` for every failure reason EXCEPT `DisabledByConfig`. `GetVerdictAsync` is the diagnostic entry point that never throws for strict-mode reasons. Short-circuits to a `DisabledByConfig` verdict when `CopilotReview:Enabled = false` and does NOT touch the provider in that case. |
| `REBUSS.Pure\Services\CopilotReview\CopilotPageReviewer.cs` | Feature 013 — sends one page of enriched content to Copilot via a `CopilotSession`, collects the response via event pattern matching (`AssistantMessageEvent`, `SessionIdleEvent`, `SessionErrorEvent`), returns `CopilotPageReviewResult`. Never throws except `OperationCanceledException`. Loads the review prompt template from an embedded resource at `Services/CopilotReview/Prompts/copilot-page-review.md`. |
| `REBUSS.Pure\Services\CopilotReview\CopilotReviewOrchestrator.cs` | Feature 013 + 021 — `PrEnrichmentOrchestrator`-style trigger/wait/snapshot lifecycle with `ConcurrentDictionary<string, CopilotReviewJob>` keyed on review key (string-based, source-agnostic). Re-paginates the enrichment result against `CopilotReview.ReviewBudgetTokens`, dispatches all page reviews **in parallel** via `Task.Run` per page + `Task.WhenAll` — the `CopilotRequestThrottle` (3-second `SemaphoreSlim` gate) serializes outgoing SDK calls while model response wait times overlap. Each page task writes to its own `pageResults[idx]` slot (no contention). Wraps each page in a 3-attempt retry loop that fills in `FailedFilePaths` on exhaustion. `CompletedPages` incremented atomically via `Interlocked.Increment`; `CopilotReviewWaiter` polls this counter for order-agnostic progress notifications (per-page `CurrentActivity` is intentionally **not** published — parallel tasks would overwrite it and cause flip-flop in the IDE feed; the monotonic counter is the sole page-level signal). After all pages complete, `ValidateAllFindingsAsync` runs (when `ValidateFindings == true` and both `FindingValidator` + `FindingScopeResolver` are non-null): parse findings per page → resolve enclosing scopes (truncated when method > `MaxScopeLines`) → delegate to `FindingValidator` (severity-ordering + `IPageAllocator`-based pagination) → map verdicts back per page → rebuild `ReviewText` via `FindingFilterer`. Skips validation entirely when total findings exceed `MaxValidatableFindings`. Narrow exception to Principle VI. |
| `REBUSS.Pure\Services\CopilotReview\CopilotReviewOptions.cs` | Feature 013 + 018 + 021 — `IOptions<T>` binding for `CopilotReview` section: `Enabled`, `ReviewBudgetTokens` (default 128 000), `Model` (default `claude-sonnet-4.6`), `GitHubToken` (feature 018 FR-009), `StrictMode` (feature 018 FR-015), `ValidateFindings` (default `true`), `MaxValidatableFindings` (default 40 — over-threshold skips validation entirely), `MaxScopeLines` (default 150 — truncate enclosing method bodies above this), plus the `GitHubTokenEnvironmentVariable = "REBUSS_COPILOT_TOKEN"` const. |
| `REBUSS.Pure\Services\CopilotReview\Prompts\copilot-page-review.md` | Feature 013 — embedded review instruction template sent to Copilot with each page. `{enrichedPageContent}` placeholder is substituted at call time. |
| `REBUSS.Pure\Services\CopilotReview\Prompts\copilot-finding-validation.md` | Feature 021 — embedded prompt template for the validation pass. Lists each finding numbered `1..N` with severity, file, line, description, and the enclosing scope source (in C# fences). Asks Copilot to reply per finding in `**Finding {N}: VALID|FALSE_POSITIVE|UNCERTAIN** — reason` form. `{findingsWithScopes}` placeholder is substituted by `FindingValidator.BuildPrompt`. |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingParser.cs` | Feature 021 — extracts structured findings (`ParsedFinding`) from the Markdown that `CopilotPageReviewer` returns. Returns the parsed findings plus the unparseable remainder (intros, headings, free-form prose) so `FindingFilterer` can preserve every line of the original output (FR-012). The regex is a tolerant safety net over the strict format demanded by `copilot-page-review.md`: accepts optional leading list markers (`- `, `* `, `1. `) before `**[severity]**`, and for the line expression swallows variants like `(line ~138)`, `(line 100-150)`, `(line approx 42)`, `(line ≈42)` — extracting the first integer via `LineNumberExtractor()`. `(line unknown)` and missing parentheticals yield `LineNumber = null`. |
| `REBUSS.Pure\Services\CopilotReview\Validation\ParsedFinding.cs` | Feature 021 — finding record from the parser: `Index`, `FilePath`, `LineNumber?`, `Severity` (`critical`/`major`/`minor`), `Description`, `OriginalText` (raw block kept verbatim for filtering). |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingScopeResolver.cs` | Feature 021 — resolves the scope context for each finding and gracefully falls back so that every `.cs` finding reaches Copilot validation. Chain (per-finding, per-file source cached once): (1) non-`.cs` → `ScopeResolutionFailure.NotCSharp` (passthrough); (2) missing source → `SourceUnavailable`; (3) `.cs` with source → determine effective line: use `LineNumber` as-is when present, else call `FindingLineResolver.TryResolveLine` over the backtick identifiers in the description; (4) call `FindingScopeExtractor.ExtractScopeBody(line, maxScopeLines)` — truncates around the finding line via `TruncateAroundLine` when the method exceeds `MaxScopeLines` (window ±`MaxScopeLines/2` plus `// ... (X lines omitted) ...` markers); (5) if extraction failed, retry step (3)-(4) passing the original line as a hint to `FindingLineResolver`; (6) still failing → whole-file fallback via `BuildWholeFileFallback` (truncate to `MaxScopeLines × 2` keeping head + tail with `// ... omitted from middle ...` marker) and scope name `<entire file: {path}>` — still `ResolutionFailure.None` so it reaches Copilot. |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingWithScope.cs` | Feature 021 — pairing of `ParsedFinding` with its resolved `ScopeSource`/`ScopeName` and a `ResolutionFailure` enum reason. Consumed by `FindingValidator`. |
| `REBUSS.Pure\Services\CopilotReview\Validation\ScopeResolutionFailure.cs` | Feature 021 — enum: `None`, `NotCSharp`, `SourceUnavailable`, `ScopeNotFound`. Drives the validator's deterministic phase-1 verdict (no Copilot call) for non-resolvable scopes. |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingSeverityOrderer.cs` | Feature 021 — static helper that stable-sorts findings by severity (critical=0, major=1, minor=2, unknown=last). Two methods: `Order<T>` (returns reordered list, generic over selector) and `IsOrderedBySeverity<T>` (sanity check used by `FindingValidator.VerifyResponseOrder` to detect when Copilot scrambles the verdict order — verdicts remain correctly mapped via index-based regex, the check just logs a warning). |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingValidator.cs` | Feature 021 — second-pass false-positive filter. Phase 1 assigns deterministic verdicts for `NotCSharp` (Valid) / `SourceUnavailable`+`ScopeNotFound` (Uncertain) without touching Copilot. Phase 2 orders the remaining findings by severity via `FindingSeverityOrderer`. Phase 3 builds synthetic `PackingCandidate`s (one per finding, `EstimatedTokens` from `ITokenEstimator.EstimateTokenCount` over the per-finding prompt section) and feeds them through `IPageAllocator.Allocate(..., ReviewBudgetTokens − templateOverhead)` so token budget — not a fixed batch count — controls how many findings fit per Copilot SDK call. Phase 4 issues one `ICopilotSessionFactory.CreateSessionAsync` call per allocator page (inspection kind = `validation-{pageNumber}`), parses verdicts via index-based regex (`**Finding {N}: VALID\|FALSE_POSITIVE\|UNCERTAIN**`), runs `VerifyResponseOrder` (warns on severity scramble — does **not** break correctness), and on exception falls through to graceful Valid (FR-012). Optional `pageProgress(pageNumber, totalPages)` callback drives the orchestrator's `CurrentActivity`. |
| `REBUSS.Pure\Services\CopilotReview\Validation\ValidatedFinding.cs` | Feature 021 — output record: `Finding`, `Verdict` (`Valid`/`FalsePositive`/`Uncertain`), optional `Reason` (model rationale or fallback note). |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingVerdict.cs` | Feature 021 — enum: `Valid`, `FalsePositive`, `Uncertain`. Consumed by `FindingFilterer` to decide what makes it into the final review text. |
| `REBUSS.Pure\Services\CopilotReview\Validation\FindingFilterer.cs` | Feature 021 — rebuilds a page's `ReviewText`: emits `Valid` findings verbatim, `Uncertain` with a `[uncertain]` prefix, omits `FalsePositive`, preserves the unparseable remainder. Appends a footer `_Validation: {valid} confirmed, {filtered} filtered, {uncertain} uncertain_` when at least one finding existed. Emits `No issues found.` when every finding was filtered out and there is no remainder. |
| `REBUSS.Pure\Services\CopilotReview\Inspection\ICopilotInspectionWriter.cs` | Feature 022 — internal diagnostic seam capturing prompt+response pairs sent to / received from the Copilot review agent. Two methods: `WritePromptAsync(reviewKey, kind, content, ct)` and `WriteResponseAsync(...)`. Implementations must not throw on IO failure (logged at Warning), must be safe for concurrent use, must sanitize `reviewKey` for filesystem paths, and must not enrich content with auth material. |
| `REBUSS.Pure\Services\CopilotReview\Inspection\FileSystemCopilotInspectionWriter.cs` | Feature 022 — production implementation registered when `REBUSS_COPILOT_INSPECT=1\|true\|True` at DI composition. Writes Markdown files at `%LOCALAPPDATA%\REBUSS.Pure\copilot-inspection\{safeKey}\{yyyyMMdd-HHmmss-fff}-{seq:D3}-{kind}-{role}.md` (falls back to `%TEMP%` when AppData is unavailable). Per-`safeKey` atomic counter ensures parallel writes never collide. Fire-and-forget retention sweep on construction deletes files older than 24h and removes empty per-key dirs. Kinds in use: `page-{N}-review` (from `CopilotPageReviewer`) and `validation-{N}` (from `FindingValidator`). |
| `REBUSS.Pure\Services\CopilotReview\Inspection\NoOpCopilotInspectionWriter.cs` | Feature 022 — default zero-cost implementation registered when the env-var gate is off. Both methods return `Task.CompletedTask` synchronously. |
| `REBUSS.Pure.Core\Services\CopilotReview\*.cs` | Feature 013 + 018 — interfaces (`ICopilotClientProvider` extended with `StartupVerdict`, `ICopilotAvailabilityDetector` extended with `GetVerdictAsync`, `ICopilotSessionFactory`, `ICopilotSessionHandle`, `ICopilotPageReviewer`, `ICopilotReviewOrchestrator`) and DTOs (`CopilotPageReviewResult`, `CopilotReviewResult`, `CopilotReviewSnapshot`, `CopilotReviewStatus`). Feature 018 adds `CopilotVerdict`, `CopilotAuthReason`, `CopilotTokenSource` + `ToLogLabel()` extension. Core still has zero SDK dependency — `CopilotVerdict` is an SDK-free record. |
| `REBUSS.Pure\Cli\InitCommand.cs` | CLI `init` command: generates MCP config files, copies prompt/instruction files, runs auth flow; auto-detects IDEs (`DetectsVsCode`, `DetectsVisualStudio`); supports `--ide vscode`/`--ide vs` explicit targeting; global mode (`-g`) writes to `~/.mcp.json` (VS), `%APPDATA%\Code\User\mcp.json` (VS Code), `~/.copilot/mcp-config.json` (Copilot CLI) via `ResolveGlobalConfigTargets()`; constants: `McpConfigFileName = "mcp.json"`, `VsGlobalMcpConfigFileName = ".mcp.json"`, `CopilotCliMcpConfigFileName = "mcp-config.json"` |
| `REBUSS.Pure\Cli\Prompts\review-pr.prompt.md` | Embedded resource: PR review prompt template; redesigned for `get_pr_metadata`(with budget) + `get_pr_content` page loop workflow; includes strict "No Repository Exploration" constraint forbidding the agent from using any non-MCP tool to access the codebase; legacy tools documented as available |
| `REBUSS.Pure\Cli\Prompts\self-review.prompt.md` | Embedded resource: self-review prompt template; redesigned for `get_local_content` page loop workflow; legacy tools documented as available |
| `REBUSS.Pure\Cli\Prompts\create-pr.md` | Embedded resource: create-PR prompt template (not yet deployed by `init`; reserved for future `#create-pr` command) |

### Logging

| File | Role |
|---|---|
| `REBUSS.Pure\Logging\FileLoggerProvider.cs` | `ILoggerProvider` that writes to daily-rotated files under `%LOCALAPPDATA%\REBUSS.Pure`; 3-day retention |
| `REBUSS.Pure\AppConstants.cs` | Application-level string constants: `ServerName = "REBUSS.Pure"` (MCP server name, log directory name, error prefixes), `ExecutableName = "REBUSS.Pure.exe"` (fallback exe path); `internal static` class |

### Entry point

| File | Role |
|---|---|
| `REBUSS.Pure\Program.cs` | DI composition root using `Host.CreateApplicationBuilder()`; dual-mode: CLI commands (`init`) or MCP server via `RunMcpServerAsync`; `ConfigureBusinessServices(services, configuration, repoPath)` wires shared services (workspace root, context window, response packing, pagination), selects provider via `DetectProvider(configuration, repoPath)` (explicit with case-normalization > GitHub.Owner > AzureDevOps.OrganizationName > git remote URL from `--repo` path or CWD > default AzureDevOps), calls either `services.AddGitHubProvider(configuration)` or `services.AddAzureDevOpsProvider(configuration)`, registers local review pipeline; `parseResult.RepoPath` is forwarded to `DetectProvider`/`GetGitRemoteUrl` so provider auto-detection uses the `--repo` working directory instead of the process CWD; MCP server configured via `AddMcpServer()`/`WithStdioServerTransport()`/`WithToolsFromAssembly()` (SDK attribute-based tool discovery); `BuildCliConfigOverrides` routes PAT to the target provider's config section via `ResolvePatTarget` (explicit provider > --owner > --organization > both); all remaining string literals (file names, config keys, server version, git executable/args, error messages) are resolved via `REBUSS.Pure.Properties.Resources` |

### Resources (REBUSS.Pure\Properties)

| File | Role |
|---|---|
| `REBUSS.Pure\Properties\Resources.resx` | Central string resource file for `Program.cs` literals: `AppSettingsFileName`, `AppSettingsLocalFileName`, `CliCommandInit`, `ConfigKeyProvider`, `ErrorBranchDiffRequiresCommits`, `ErrorUnknownCommand`, `GitExecutable`, `GitRemoteGetUrlArgs`, `GitRevParseVerifyHead`, `ServerVersion` |
| `REBUSS.Pure\Properties\Resources.Designer.cs` | Strongly-typed accessor class (`REBUSS.Pure.Properties.Resources`); `ResourceManager` manifest name `REBUSS.Pure.Properties.Resources`; 8 `internal static string` properties using `GetString()` with null-forgiving operator |

### Documentation

| File | Role |
|---|---|
| `README.md` | Project documentation |
| `install.ps1` | PowerShell installer: downloads latest `.nupkg` from GitHub Releases (`rebuss/CodeReview.MCP`), installs as global tool (`CodeReview.MCP`) |
| `install.sh` | Bash installer: same workflow as `install.ps1` for Linux/macOS |
| `.github\prompts\create-pr.md` | *(Not deployed by `init` — reserved for future release)* GitHub Copilot prompt for creating a pull request; uses `get_local_content` MCP tool and `git`/`gh`/`az` CLI |
| `.github\prompts\create-project-conventions.prompt.md` | Meta-prompt: instructs agent how to create/regenerate `ProjectConventions.md` from codebase analysis |
| `.github\prompts\create-architecture.prompt.md` | Meta-prompt: instructs agent how to create/regenerate `Architecture.md` — deep-dive into MCP protocol, provider internals, auth/config, design rationale |
| `.github\prompts\create-extension-recipes.prompt.md` | Meta-prompt: instructs agent how to create/regenerate `ExtensionRecipes.md` — detailed cookbook with code templates, validation checklists, pitfalls |
| `.github\prompts\create-contracts.prompt.md` | Meta-prompt: instructs agent how to create/regenerate `Contracts.md` — complete MCP tool contract reference with input schemas, output JSON examples, error formats, serialization pipeline |
| `.github\ProjectConventions.md` | Stable architecture & conventions reference for coding agents (complements `CodebaseUnderstanding.md`); extension recipes, data flow, coding/testing conventions |
| `.github\Architecture.md` | Deep-dive architecture reference: MCP protocol, provider internals, auth/config mechanics, analysis pipeline, design rationale |
| `.github\ExtensionRecipes.md` | Detailed extension cookbook: code templates, reference implementations, validation checklists, common pitfalls for each extension pattern |
| `.github\Contracts.md` | Complete MCP tool contract reference: input schemas, output JSON examples, error formats, serialization pipeline, shared DTOs, enum values |

---

## Test files

| File | Tests for |
|---|---|
| `REBUSS.Pure.Core.Tests\Classification\FileClassifierTests.cs` | `FileClassifier` |
| `REBUSS.Pure.Core.Tests\Shared\StructuredDiffBuilderTests.cs` | `StructuredDiffBuilder` — hunk generation, edge cases |
| `REBUSS.Pure.Core.Tests\Shared\DiffPlexDiffAlgorithmTests.cs` | `DiffPlexDiffAlgorithm` — core edit behavior + concurrent-call stability |
| `REBUSS.Pure.AzureDevOps.Tests\Providers\AzureDevOpsDiffProviderTests.cs` | `AzureDevOpsDiffProvider` — full diff/file diff, skip behavior, `IsFullFileRewrite`, `GetSkipReason` |
| `REBUSS.Pure.AzureDevOps.Tests\Providers\AzureDevOpsFilesProviderTests.cs` | `AzureDevOpsFilesProvider` — file list, status mapping, summary, path stripping, last iteration selection, no-iterations edge case; uses mocked `IAzureDevOpsApiClient` + real `FileChangesParser`/`IterationInfoParser` |
| `REBUSS.Pure.AzureDevOps.Tests\Parsers\PullRequestMetadataParserTests.cs` | `PullRequestMetadataParser` |
| `REBUSS.Pure.AzureDevOps.Tests\Parsers\IterationInfoParserTests.cs` | `IterationInfoParser` |
| `REBUSS.Pure.AzureDevOps.Tests\Parsers\FileChangesParserTests.cs` | `FileChangesParser` |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\AzureDevOpsOptionsTests.cs` | Options validation (format-only, all fields optional) |
| `REBUSS.Pure.AzureDevOps.Tests\Api\AzureDevOpsApiClientTests.cs` | API client — URL construction, status codes, file content, version descriptor resolution, HTML response detection |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\GitRemoteDetectorTests.cs` | `GitRemoteDetector.ParseRemoteUrl` — HTTPS, SSH, GitHub, edge cases; `FindGitRepositoryRoot`, `GetCandidateDirectories` |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\ConfigurationResolverTests.cs` | `ConfigurationResolver` — PostConfigure precedence, fallback, caching, mixed sources, Resolve static method |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\ChainedAuthenticationProviderTests.cs` | `ChainedAuthenticationProvider` — PAT precedence, cached tokens, Azure CLI token acquisition and caching, expired token fallback, `InvalidateCachedToken`, `BuildAuthRequiredMessage` tests read `Resources.ErrorAzureDevOpsAuthRequired` directly |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\AzureCliProcessHelperTests.cs` | `AzureCliProcessHelper.GetProcessStartArgs` — Windows `cmd.exe /c az` wrapping, Linux direct `az` invocation, custom `azPath`; `TryFindAzCliOnWindows` |
| `REBUSS.Pure.AzureDevOps.Tests\Configuration\AzureCliTokenProviderTests.cs` | `AzureCliTokenProvider.ParseTokenResponse` — valid JSON, missing/empty token, missing expiry, ISO 8601 dates, resource ID constant |
| `REBUSS.Pure.GitHub.Tests\Providers\GitHubDiffProviderTests.cs` | `GitHubDiffProvider` — metadata extraction, structured diff, additions/deletions, 404 handling, empty files, cancellation, file diff, skip scenarios |
| `REBUSS.Pure.GitHub.Tests\Parsers\GitHubPullRequestParserTests.cs` | `GitHubPullRequestParser` — lightweight parse, full parse, base/head SHA extraction, closed state (merged→completed, not-merged→abandoned), invalid JSON fallbacks, `MapState(state, merged)`, `ParseWithCommits` |
| `REBUSS.Pure.GitHub.Tests\Parsers\GitHubFileChangesParserTests.cs` | `GitHubFileChangesParser` — status mapping (`added`→`add`, `removed`→`delete`, `modified`→`edit`, etc.), empty array, invalid JSON, non-array JSON |
| `REBUSS.Pure.GitHub.Tests\Configuration\GitHubRemoteDetectorTests.cs` | `GitHubRemoteDetector.ParseRemoteUrl` — HTTPS (with/without `.git`), SSH, Azure DevOps/GitLab URLs return null, empty/malformed; `FindGitRepositoryRoot`, `GetCandidateDirectories` |
| `REBUSS.Pure.GitHub.Tests\Configuration\GitHubConfigurationResolverTests.cs` | `GitHubConfigurationResolver` — PostConfigure precedence (user > detected > cached), fallback, caching, mixed sources, Resolve static method, workspace root integration |
| `REBUSS.Pure.GitHub.Tests\GitHubScmClientTests.cs` | `GitHubScmClient` — provider name, facade delegation (diff/metadata/files/archive), metadata enrichment (`WebUrl`, `RepositoryFullName`) |
| `REBUSS.Pure.GitHub.Tests\Providers\GitHubMetadataProviderTests.cs` | `GitHubMetadataProvider` — parsed metadata, commit SHAs, statistics, empty commits, 404 handling, invalid commits JSON |
| `REBUSS.Pure.GitHub.Tests\Providers\GitHubFilesProviderTests.cs` | `GitHubFilesProvider` — classified files, summary, empty files list, line count flow-through, missing line counts default to zero; uses mocked `IGitHubApiClient` + real `GitHubFileChangesParser` |
| `REBUSS.Pure.GitHub.Tests\Configuration\GitHubChainedAuthenticationProviderTests.cs` | `GitHubChainedAuthenticationProvider` — PAT precedence, cached tokens, GitHub CLI token acquisition and caching, expired token fallback, null expiry used as valid, `InvalidateCachedToken`, `BuildAuthRequiredMessage` tests read `Resources.ErrorGitHubAuthRequired` directly |
| `REBUSS.Pure.GitHub.Tests\Configuration\GitHubCliTokenProviderTests.cs` | `GitHubCliTokenProvider.ParseTokenResponse` — valid plain text, whitespace trimming, empty/null/whitespace returns null, `DefaultTokenLifetime` constant (24 hours) |
| `REBUSS.Pure.GitHub.Tests\Configuration\GitHubCliProcessHelperTests.cs` | `GitHubCliProcessHelper.GetProcessStartArgs` — Windows `cmd.exe /c gh` wrapping, Linux direct `gh` invocation, custom `ghPath`; `TryFindGhCliOnWindows` |
| `REBUSS.Pure.Tests\Tools\GetPullRequestMetadataToolHandlerTests.cs` | `GetPullRequestMetadataToolHandler` — plain text output, validation (McpException), provider exceptions, +contentPaging computation (diff-based token measurement via `IPullRequestDiffCache` + `FileTokenMeasurement`, page allocation, backward compatibility without budget params), diff-cache verification test (Feature 005: replaced `_dataProvider.GetFilesAsync` mock with `_diffCache` mock, replaced `EstimateFromStats` with `EstimateTokenCount`) |
| `REBUSS.Pure.Tests\Tools\GetPullRequestContentToolHandlerTests.cs` | `GetPullRequestContentToolHandler` — plain text output, validation (McpException), pagination (page filtering, category breakdown, multi-page, out-of-range), PR not found error; Feature 005: replaced `_dataProvider` with `_diffCache` mock, diff-based token measurement via `EstimateTokenCount`, diff-cache and token-estimation verification tests |
| `REBUSS.Pure.Tests\Tools\GetLocalContentToolHandlerTests.cs` | `GetLocalContentToolHandler` — plain text output, validation (McpException), pagination (per-file diff fetch, category breakdown, scope routing, multi-page, out-of-range) |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalReviewScopeTests.cs` | `LocalReviewScope.Parse` — all scope kinds, ToString |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalGitClientParseTests.cs` | `LocalGitClient` porcelain/name-status parsing, `BuildStatusArgs` HEAD/no-HEAD command selection — via reflection on internal static methods |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalReviewProviderTests.cs` | `LocalReviewProvider` — files listing, status mapping, classification, file diff, skip reasons, exception cases |
| `REBUSS.Pure.Tests\Services\ContextWindow\TokenEstimatorTests.cs` | `TokenEstimator` — token estimation accuracy, null/empty input, boundary conditions, custom ratio, invalid ratio fallback, rounding; +`EstimateFromStats` tests (additions+deletions, zero lines fallback, negative values clamped); +`EstimateTokenCount` tests (null/empty input returns zero, typical content, rounds up, invalid ratio fallback to 4.0, custom ratio) |
| `REBUSS.Pure.Tests\Services\ContextWindow\ContextBudgetResolverTests.cs` | `ContextBudgetResolver` — explicit budget, registry lookup, default fallback, guardrails, safety margin, case-insensitive matching, gateway cap (clamp, disable via null/zero), edge cases |
| `REBUSS.Pure.Tests\Services\PackingPriorityComparerTests.cs` | `PackingPriorityComparer` — FileCategory ordering, TotalChanges secondary sort, Path tiebreaker, TokenCount ignored (negative test), null handling |
| `REBUSS.Pure.Tests\Services\ResponsePackerTests.cs` | `ResponsePacker` — empty candidates, all-fit, budget exceeded, partial assignment, priority ordering, manifest correctness, utilization |
| `REBUSS.Pure.Tests\Services\ResponsePackerAdvancedTests.cs` | `ResponsePacker` advanced — manifest completeness, edge cases, JSON structure, budget compliance, backward compatibility |
| `REBUSS.Pure.Tests\Services\PageAllocatorTests.cs` | `PageAllocator` (Feature 004) — 14 tests: single/multi-page allocation, oversized items, empty input, determinism, scale |
| `REBUSS.Pure.Tests\Services\PageReferenceCodecTests.cs` | `PageReferenceCodec` (Feature 004) — 11 tests: round-trip encoding, null fingerprint, malformed input, safety |
| `REBUSS.Pure.Tests\Services\PaginationOrchestratorTests.cs` | `PaginationOrchestrator` (Feature 004) — 18 tests: validation, page resolution, param matching, staleness detection, metadata building |
| `REBUSS.Pure.Tests\Services\PullRequestDiffCacheTests.cs` | `PullRequestDiffCache` (Feature 005) — 15 tests: cache miss fetches from provider, cache hit returns same instance, different PRs cached independently, provider exceptions propagate (not cached), failed fetches retried on second call, cancellation token passthrough, staleness detection (commit match → cache hit, commit mismatch → evict + re-fetch, null known commit → no eviction, null cached commit → no eviction, case-insensitive comparison), concurrent deduplication (concurrent miss fetches once, failure propagates to all waiters, failure allows retry) |
| `REBUSS.Pure.Tests\Tools\Shared\FileTokenMeasurementTests.cs` | `FileTokenMeasurement` (Feature 005) — 18 tests: `BuildCandidatesFromDiff` (correct count/paths/tokens/classifications/total changes, empty diffs, skipped files), `MapToStructured` (path, changeType, skipReason, additions/deletions, hunks, line op char→string, null skipReason) |
| `REBUSS.Pure.Tests\Logging\FileLoggerProviderTests.cs` | `FileLoggerProvider` — daily rotation, file naming, write content, timestamp, retention/deletion, roll-over, non-log file safety |
| `REBUSS.Pure.Tests\Mcp\McpServerTests.cs` | `McpServer` — initialize, tools/list, tools/call, unknown method, invalid JSON, empty lines, notifications |
| `REBUSS.Pure.Tests\Mcp\InitializeMethodHandlerTests.cs` | `InitializeMethodHandler` — protocol version negotiation, roots extraction, storage, edge cases |
| `REBUSS.Pure.Tests\Mcp\McpProtocolVersionNegotiatorTests.cs` | `McpProtocolVersionNegotiator` — exact match, downward negotiation, future/past versions, null/empty input |
| `REBUSS.Pure.Tests\Mcp\McpWorkspaceRootProviderTests.cs` | `McpWorkspaceRootProvider` — URI conversion, repo root resolution, MCP roots, localRepoPath fallback, CLI `--repo` precedence |
| `REBUSS.Pure.Tests\Cli\CliArgumentParserTests.cs` | `CliArgumentParser` — server mode, `--repo`, `--pat`, `--org`, `--project`, `--repository`, `init` command, `-g`/`--global` flag, `--ide` option, combined args, edge cases |
| `REBUSS.Pure.Tests\Cli\InitCommandTests.cs` | `InitCommand` — generates `mcp.json` (local and global `-g` mode), `--ide vscode`/`--ide vs` explicit IDE targeting, copies prompt and instruction files (always overwrites), error cases, subdirectory support, Azure DevOps CLI login, GitHub CLI login, PAT carry-over, `ResolveGlobalConfigTargets` (2 targets: VS, VS Code); global-mode tests use nested `GlobalTestContext` disposable fixture (creates temp Git repo + simulated global config dirs), inject `globalConfigTargetsResolver` pointing to temp dirs so no real user-home files are touched |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotAvailabilityDetectorTests.cs` | `CopilotAvailabilityDetector` — verdict caching, graceful vs strict-mode behavior, `DisabledByConfig` short-circuit, `GetVerdictAsync` diagnostic path |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotClientProviderTests.cs` | `CopilotClientProvider` — lazy startup gating, verdict surfacing via `StartupVerdict`, hosted service shutdown deadline |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotTokenResolverTests.cs` | `CopilotTokenResolver` — env > config > logged-in-user precedence, blank-value handling, no token leakage to logs/exceptions |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotVerificationRunnerTests.cs` | `CopilotVerificationRunner` — 6-step verification sequence outcomes via `ICopilotSdkOps` stub: success, model-entitlement failure, quota soft failure, token-resolve failure, dispose-on-failure |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotPageReviewerTests.cs` | `CopilotPageReviewer` — `ICopilotSessionFactory` fake driving event sequences (`AssistantMessageEvent`, `SessionIdleEvent`, `SessionErrorEvent`); success/failure/cancellation outcomes; inspection writes (`page-{N}-review` prompt + response) |
| `REBUSS.Pure.Tests\Services\CopilotReview\CopilotReviewOrchestratorTests.cs` | `CopilotReviewOrchestrator` — trigger/wait/snapshot lifecycle, parallel page dispatch via mocked `ICopilotPageReviewer`, retry-loop exhaustion with `FailedFilePaths`, empty-allocation fast path, validation opt-out path (no `FindingValidator`/`FindingScopeResolver`) |
| `REBUSS.Pure.Tests\Services\CopilotReview\Validation\FindingParserTests.cs` | `FindingParser` — extracts severity/file/line/description from Copilot's Markdown, preserves the unparseable remainder, handles missing line numbers, multiple findings per text |
| `REBUSS.Pure.Tests\Services\CopilotReview\Validation\FindingScopeResolverTests.cs` | `FindingScopeResolver` — non-`.cs` passthrough, missing source, truncation around the finding line when method exceeds `MaxScopeLines`, smart-line recovery (backtick identifier in description when `LineNumber` is null), hint-retry (line on a `using` → retry with identifier lookup), whole-file fallback when nothing matches (still `ResolutionFailure.None` with scope name `<entire file: path>`) |
| `REBUSS.Pure.RoslynProcessor.Tests\FindingLineResolverTests.cs` | `FindingLineResolver` (Feature 021) — backtick-identifier extraction (including qualified names), declaration-kind priority (method/property/ctor/type/field/parameter scoring), proximity-bonus tie-breaking for overloads when a hint line is supplied, null returns for empty source/description/no-match |
| `REBUSS.Pure.Tests\Services\CopilotReview\Validation\FindingFiltererTests.cs` | `FindingFilterer` — Valid emitted verbatim, Uncertain prefixed `[uncertain]`, FalsePositive omitted, validation footer with counts, `No issues found.` collapse, remainder preservation |
| `REBUSS.Pure.Tests\Services\CopilotReview\Validation\FindingSeverityOrdererTests.cs` | `FindingSeverityOrderer` (Feature 021) — critical/major/minor ordering, stable within rank, case-insensitive severity, unknown severity sinks to end, `IsOrderedBySeverity` sanity-check helper used by `FindingValidator.VerifyResponseOrder` |
| `REBUSS.Pure.Tests\Services\CopilotReview\Validation\FindingValidatorTests.cs` | `FindingValidator` (Feature 021) — deterministic phase-1 verdicts (NotCSharp → Valid, SourceUnavailable/ScopeNotFound → Uncertain, no Copilot call); resolved findings call Copilot and parse `**Finding N: VERDICT**` lines; multi-page scenario via fake `IPageAllocator` (12 findings × 5/page = 3 SDK calls); session-error graceful degradation (Valid passthrough); empty input no-op; mixed resolved/unresolved sends only resolved; inspection writes use kind `validation-{pageNumber}`; severity ordering observable in prompt; `pageProgress` callback fires once per allocator page |
| `REBUSS.Pure.Tests\Services\CopilotReview\Inspection\CopilotInspectionRegistrationTests.cs` | DI registration — `REBUSS_COPILOT_INSPECT` env-var gate selects `FileSystemCopilotInspectionWriter` vs `NoOpCopilotInspectionWriter` |
| `REBUSS.Pure.Tests\Services\CopilotReview\Inspection\FileSystemCopilotInspectionWriterTests.cs` | `FileSystemCopilotInspectionWriter` — file naming format, sequence counter atomicity, `reviewKey` sanitization, IO failure swallowed (no throw), retention sweep deletes >24h files + empty dirs |
| `REBUSS.Pure.Tests\Services\CopilotReview\Inspection\NoOpCopilotInspectionWriterTests.cs` | `NoOpCopilotInspectionWriter` — both methods return completed task without IO |
| `REBUSS.Pure.SmokeTests\Fixtures\TempGitRepoFixture.cs`
| `REBUSS.Pure.SmokeTests\Fixtures\McpProcessFixture.cs` | Test fixture: starts MCP server as child process, sends JSON-RPC via stdin, reads responses from stdout; async stderr drain prevents pipe deadlocks; uses compile-time `BuildConfiguration` constant (`#if DEBUG`) with `-c {BuildConfiguration} --no-build` to avoid redundant rebuilds during test execution |
| `REBUSS.Pure.SmokeTests\Fixtures\CliProcessHelper.cs` | Test helper: runs `dotnet run --project REBUSS.Pure` with stdin piping, env overrides, and timeout; uses compile-time `BuildConfiguration` constant (`#if DEBUG`) with `-c {BuildConfiguration} --no-build` to avoid redundant rebuilds; wraps stdin write/close in `try/catch (IOException)` for process-exits-before-stdin-consumed race; uses `WaitForExit(TimeSpan)` via `Task.Run` instead of `WaitForExitAsync` to avoid .NET 7+ pipe-EOF-wait hang; separate `pipeCts` for pipe reads with 5 s drain grace period after process exit; `BuildRestrictedPathEnv()` hides auth CLI tools — on Windows filters PATH directories, on Linux/macOS prepends shadow scripts for `gh`/`az` that exit immediately |
| `REBUSS.Pure.SmokeTests\InitCommand\GitHubInitSmokeTests.cs` | Smoke tests for `init` in GitHub repos: PAT, no-PAT, IDE detection, config merge |
| `REBUSS.Pure.SmokeTests\InitCommand\AzureDevOpsInitSmokeTests.cs` | Smoke tests for `init` in Azure DevOps repos: PAT, no-PAT, SSH remote, non-git dir, subdirectory |
| `REBUSS.Pure.SmokeTests\McpProtocol\McpServerSmokeTests.cs` | Smoke tests for MCP protocol: initialize, tools/list, unknown method, graceful shutdown |
| `REBUSS.Pure.SmokeTests\Installation\FullInstallSmokeTests.cs` | Smoke test: pack (`--no-build -c {BuildConfiguration}`) → tool install → init (60 s timeout) → MCP handshake (full user installation flow). Uses `#if DEBUG` compile-time config constant; `IOException` handling on stdin writes/close; uses `WaitForExit(TimeSpan)` via `Task.Run` instead of `WaitForExitAsync` to avoid .NET 7+ pipe-EOF-wait hang; separate `pipeCts` for pipe reads with 5 s drain grace period after process exit; `McpHandshakeAsync` stdin writes wrapped in `try/catch (IOException)`; `RunProcessAsync` accepts `environmentOverrides` parameter; init step uses `CliProcessHelper.BuildRestrictedPathEnv()` to shadow `az`/`gh` CLI tools on CI runners, preventing interactive `az login` from blocking indefinitely when the dotnet-tool shim does not forward `--pat` |
| `REBUSS.Pure.SmokeTests\Infrastructure\TestSettings.cs` | Contract test settings: reads env vars (`REBUSS_ADO_*`, `REBUSS_GH_*`), validates completeness, provides skip reasons |
| `REBUSS.Pure.SmokeTests\Infrastructure\ContractMcpProcessFixture.cs` | Contract test fixture: starts MCP server with provider-specific CLI args (ADO/GitHub/Protocol), performs SDK-compatible handshake (sends `initialize` with `protocolVersion`, `capabilities`, `clientInfo`, then `notifications/initialized`), provides `SendToolCallAsync` |
| `REBUSS.Pure.SmokeTests\Infrastructure\McpProcessFixtureCollections.cs` | xUnit collection definitions: `AdoMcpProcessFixture`, `GitHubMcpProcessFixture`, `ProtocolMcpProcessFixture` — shared process per collection |
| `REBUSS.Pure.SmokeTests\Infrastructure\ToolCallResponseExtensions.cs` | Helper extensions for MCP tool-call responses: `GetToolText` (first content block as plain text), `GetAllToolText` (concatenates all content blocks for multi-block responses), `GetFileBlock` (extracts a file-specific block from diff output), legacy `GetToolContent` (JSON), `IsToolError`, `GetToolErrorMessage` |
| `REBUSS.Pure.SmokeTests\Expectations\AdoTestExpectations.cs` | Expected values from Azure DevOps fixture PR (title, state, file paths, statuses, code fragments) |
| `REBUSS.Pure.SmokeTests\Expectations\GitHubTestExpectations.cs` | Expected values from GitHub fixture PR (title, state, file paths, statuses, code fragments) |
| `REBUSS.Pure.SmokeTests\Protocol\InitializeProtocolTests.cs` | Protocol tests (no credentials): MCP initialize handshake — protocol version, server info, capabilities |
| `REBUSS.Pure.SmokeTests\Protocol\ToolsListProtocolTests.cs` | Protocol tests (no credentials): tools/list — tool count (3), names, schemas; PrNumberTools checks property declaration (not required array) for get_pr_metadata+get_pr_content, +ContentTools_HavePageNumberProperty for get_pr_content/get_local_content |
| `REBUSS.Pure.SmokeTests\Contracts\AzureDevOps\AdoMetadataContractTests.cs` | Contract tests (ADO): `get_pr_metadata` plain-text output — PR header/title, state, branches, author/source, stats, SHA lines, description, draft marker |
| `REBUSS.Pure.SmokeTests\Contracts\AzureDevOps\AdoNegativeContractTests.cs` | Negative tests (ADO): invalid/missing prNumber |
| `REBUSS.Pure.SmokeTests\Contracts\GitHub\GitHubMetadataContractTests.cs` | Contract tests (GitHub): `get_pr_metadata` plain-text output — PR header/title, state, branches, author/source, stats, SHA lines, description, draft marker |
| `REBUSS.Pure.SmokeTests\Contracts\GitHub\GitHubNegativeContractTests.cs` | Negative tests (GitHub): invalid/missing prNumber |

---

# 2. Cross-Cutting Dependency Graph (Models Only)

```
PullRequestDiff (+ FileChange, DiffHunk, DiffLine)
  NOTE: FileChange is NOT thread-safe. Diff providers mutate Hunks/SkipReason/Additions/Deletions
        inside Parallel.ForEachAsync — safe only because each iteration owns a distinct instance.
  ? AzureDevOpsDiffProvider [AzureDevOps]      (produces: populates FileChange.Hunks/Additions/Deletions/SkipReason via Parallel.ForEachAsync)
  ? AzureDevOpsFilesProvider [AzureDevOps]      (consumes: reads FileChange.Path, .ChangeType from iteration-changes API; line counts are zero)
  ? AzureDevOpsScmClient [AzureDevOps]          (delegates: passes through from diff/files providers)
  ? GitHubDiffProvider [GitHub]                 (produces: populates FileChange.Hunks/Additions/Deletions/SkipReason via Parallel.ForEachAsync)
  ? GitHubFilesProvider [GitHub]                (consumes: reads FileChange from PR files API; uses .Additions, .Deletions)
  ? GitHubScmClient [GitHub]                    (delegates: passes through from diff/files providers)
  ? LocalReviewProvider [Pure]                   (produces for GetFileDiffAsync; consumes FileChange for files listing)
  ? GetPullRequestMetadataToolHandler [Pure]     (consumes via IPullRequestDiffCache: diff-based token measurement for contentPaging)
  ? GetPullRequestContentToolHandler [Pure]      (consumes via IPullRequestDiffCache: diff-based page allocation and content rendering)
  ? FileTokenMeasurement [Pure]                  (consumes: serializes FileChange to JSON for token measurement)
  ? StructuredDiffResult [Pure]                  (tool output DTO: mirrors domain hunks)
  ? AnalysisInput [Core]                         (carries PullRequestDiff for analyzer pipeline)

PullRequestMetadata
  ? PullRequestMetadataParser [AzureDevOps]      (produces)
  ? AzureDevOpsDiffProvider [AzureDevOps]        (consumes: BuildDiff)

FullPullRequestMetadata (+ RepositoryFullName, WebUrl)
  ? PullRequestMetadataParser [AzureDevOps]      (produces via ParseFull)
  ? AzureDevOpsMetadataProvider [AzureDevOps]    (produces: populates from API)
  ? AzureDevOpsScmClient [AzureDevOps]           (enriches: sets RepositoryFullName + WebUrl from AzureDevOpsOptions)
  ? GitHubPullRequestParser [GitHub]             (produces via ParseFull)
  ? GitHubMetadataProvider [GitHub]              (produces: populates from API + commits)
  ? GitHubScmClient [GitHub]                     (enriches: sets RepositoryFullName + WebUrl from GitHubOptions)
  ? GetPullRequestMetadataToolHandler [Pure]      (consumes: maps to PullRequestMetadataResult)
  ? AnalysisInput [Core]                          (carries FullPullRequestMetadata for analyzer pipeline)

IterationInfo
  ? IterationInfoParser [AzureDevOps]             (produces)
  ? AzureDevOpsDiffProvider [AzureDevOps]         (consumes: base/target commit SHAs)
  ? AzureDevOpsMetadataProvider [AzureDevOps]     (consumes: fallback commit SHAs)

FileClassification / FileCategory
  ? FileClassifier [Core]                         (produces)
  ? AzureDevOpsFilesProvider [AzureDevOps]        (consumes: Classify → FileClassification for BuildFileInfo, BuildSummary)
  ? AzureDevOpsDiffProvider [AzureDevOps]         (consumes: GetSkipReason)
  ? LocalReviewProvider [Pure]                    (consumes: classifies local files)

DiffEdit
  ? DiffPlexDiffAlgorithm [Core]                 (produces)
  ? StructuredDiffBuilder [Core]                  (consumes: ComputeHunks, FormatHunk)

PullRequestFiles / PullRequestFileInfo / PullRequestFilesSummary
  ? AzureDevOpsFilesProvider [AzureDevOps]        (produces)
  ? AzureDevOpsScmClient [AzureDevOps]            (delegates: passes through from files provider)
  ? GitHubFilesProvider [GitHub]                   (produces)
  ? GitHubScmClient [GitHub]                      (delegates: passes through from files provider)
  ? AnalysisInput [Core]                           (carries PullRequestFiles for analyzer pipeline)

IScmClient / IPullRequestDataProvider / IRepositoryArchiveProvider [Core interfaces]
  ? AzureDevOpsScmClient [AzureDevOps]            (implements IScmClient; registered via interface forwarding in ServiceCollectionExtensions)
  ? GitHubScmClient [GitHub]                      (implements IScmClient; registered via interface forwarding in ServiceCollectionExtensions)
  ? ReviewContextOrchestrator [Core]               (consumes IScmClient: fetches all data for analysis)
  ? GetPullRequestMetadataToolHandler [Pure]       (consumes IPullRequestDataProvider for metadata; diff via IPullRequestDiffCache)

IPullRequestDiffCache [Core interface]
  ? PullRequestDiffCache [Pure]                    (implements: ConcurrentDictionary cache + Lazy<Task> in-flight dedup keyed by PR number, delegates to IPullRequestDataProvider on miss; concurrent misses coalesced; staleness detection via LastSourceCommitId comparison when knownHeadCommitId is provided)
  ? GetPullRequestMetadataToolHandler [Pure]       (consumes: fetches diff for diff-based token measurement in contentPaging; passes metadata.LastMergeSourceCommitId for staleness detection)
  ? GetPullRequestContentToolHandler [Pure]        (consumes: fetches diff for page allocation and content rendering; no staleness check — trusts prior metadata validation)

IWorkspaceRootProvider [Core interface]
  ? McpWorkspaceRootProvider [Pure]                (implements: resolves repo root from CLI --repo, MCP roots, or localRepoPath)
  ? LocalReviewProvider [Pure]                     (consumes: resolves git repo root for local review)
  ? ConfigurationResolver [AzureDevOps]            (consumes: workspace root for git detection)

AnalysisInput / AnalysisSection / ReviewContext [Core]
  ? ReviewContextOrchestrator [Core]               (produces AnalysisInput, aggregates AnalysisSections into ReviewContext)
  ? IReviewAnalyzer implementations                (consume AnalysisInput, produce AnalysisSections)

LocalReviewScope / LocalFileStatus
  ? LocalGitClient [Pure]                          (produces LocalFileStatus)
  ? LocalReviewProvider [Pure]                     (consumes: orchestrates git client + diff builder)
  ? GetLocalContentToolHandler [Pure]               (consumes scope string + pageNumber ? pass to provider)

LocalReviewFiles
  ? LocalReviewProvider [Pure]                     (produces)
  ? GetLocalContentToolHandler [Pure]               (consumes: maps to LocalContentPageResult)

AzureDevOpsOptions (+ LocalRepoPath) [AzureDevOps]
  ? ConfigurationResolver [AzureDevOps]            (IPostConfigureOptions: merges user > detected > cached values into options)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes via IOptions<AzureDevOpsOptions>: reads PersonalAccessToken lazily)
  ? AzureDevOpsApiClient [AzureDevOps]             (consumes via IOptions<AzureDevOpsOptions>: reads ProjectName, RepositoryName, sets BaseAddress lazily)
  ? AzureDevOpsScmClient [AzureDevOps]             (consumes via IOptions<AzureDevOpsOptions>: reads org/project/repo for WebUrl/RepositoryFullName)
  ? AuthenticationDelegatingHandler [AzureDevOps]  (consumes IAuthenticationProvider: sets auth header lazily on each request)

IConfiguration
  ? McpWorkspaceRootProvider [Pure]                (consumes: reads AzureDevOps:LocalRepoPath directly to avoid circular dependency)

CliParseResult (+ Pat, Organization, Project, Repository, Ide)
  ? Program.Main [Pure]                            (produces via CliArgumentParser.Parse)
  ? Program.RunMcpServerAsync [Pure]               (consumes: reads RepoPath, passes to IWorkspaceRootProvider;
                                                     reads Pat/Organization/Project/Repository, adds as in-memory config overrides)
  ? Program.RunCliCommandAsync [Pure]              (consumes: reads CommandName, dispatches to ICliCommand)

McpRoot / InitializeParams
  → InitializeMethodHandler [Pure]                 (consumes: extracts protocolVersion + roots from initialize request)
  → IWorkspaceRootProvider [Core]                  (stores: root URIs from MCP client)
  → McpWorkspaceRootProvider [Pure]                (resolves: repo root from CLI --repo, MCP roots, or localRepoPath)
  → ConfigurationResolver [AzureDevOps]            (consumes: workspace root for git detection)

IMcpProtocolVersionNegotiator [Pure interface]
  → McpProtocolVersionNegotiator [Pure]            (implements: maintains supported version list, selects highest compatible)
  → InitializeMethodHandler [Pure]                 (consumes: negotiates version during initialize handshake)

DetectedGitInfo
  ? GitRemoteDetector [AzureDevOps]                (produces via synchronous Detect())
  ? ConfigurationResolver [AzureDevOps]            (consumes: fallback for org/project/repo)

CachedConfig
  ? LocalConfigStore [AzureDevOps]                 (produces/consumes: file I/O)
  ? ConfigurationResolver [AzureDevOps]            (consumes: fallback for org/project/repo)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes: cached token; produces: saves new token)

AzureCliProcessHelper [AzureDevOps]
  ? AzureCliTokenProvider [AzureDevOps]            (consumes: resolves az process start args)
  ? AzureDevOpsCliAuthFlow [Pure]                  (consumes: resolves az process start args for both captured and interactive execution)

AzureCliToken
  ? AzureCliTokenProvider [AzureDevOps]            (produces via GetTokenAsync / ParseTokenResponse)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes: acquires token, caches via ILocalConfigStore)
  ? AzureDevOpsCliAuthFlow [Pure]                  (consumes: uses ParseTokenResponse during init to cache token via LocalConfigStore)

GitHubOptions (+ LocalRepoPath) [GitHub]
  ? GitHubConfigurationResolver [GitHub]           (IPostConfigureOptions: merges user > detected > cached values into options)
  ? GitHubChainedAuthenticationProvider [GitHub]   (consumes via IOptions<GitHubOptions>: reads PersonalAccessToken lazily)
  ? GitHubAuthenticationHandler [GitHub]           (consumes IGitHubAuthenticationProvider: sets auth header lazily; retries on 401/403 excluding rate-limit)
  ? GitHubApiClient [GitHub]                       (consumes via constructor: reads Owner/RepositoryName for API URLs)
  ? GitHubScmClient [GitHub]                       (consumes via IOptions<GitHubOptions>: reads Owner/RepositoryName for WebUrl/RepositoryFullName)

GitHubCliProcessHelper [GitHub]
  ? GitHubCliTokenProvider [GitHub]                (consumes: resolves gh process start args)
  ? GitHubCliAuthFlow [Pure]                       (consumes: resolves gh process start args for both captured and interactive execution)

GitHubCliToken
  ? GitHubCliTokenProvider [GitHub]                (produces via GetTokenAsync / ParseTokenResponse)
  ? GitHubChainedAuthenticationProvider [GitHub]   (consumes: acquires token, caches via IGitHubConfigStore)
  ? GitHubCliAuthFlow [Pure]                       (consumes: uses ParseTokenResponse during init to cache token via GitHubConfigStore)

DetectedGitHubInfo
  ? GitHubRemoteDetector [GitHub]                  (produces via synchronous Detect())
  ? GitHubConfigurationResolver [GitHub]           (consumes: fallback for owner/repo)

GitHubCachedConfig (+ AccessToken, TokenExpiresOn)
  ? GitHubConfigStore [GitHub]                     (produces/consumes: file I/O)
  ? GitHubConfigurationResolver [GitHub]           (consumes: fallback for owner/repo)
  ? GitHubChainedAuthenticationProvider [GitHub]   (consumes: cached token; produces: saves new token)
  ? GitHubCliAuthFlow [Pure]                       (produces: caches token after gh auth login)
```

---

# 3. DI Registration Summary

## `REBUSS.Pure.AzureDevOps\ServiceCollectionExtensions.cs` ? `AddAzureDevOpsProvider(IConfiguration)`

```csharp
// Options + validation + post-configure resolution
services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
services.AddSingleton<IValidateOptions<AzureDevOpsOptions>, AzureDevOpsOptionsValidator>();
services.AddSingleton<IGitRemoteDetector, GitRemoteDetector>();
services.AddSingleton<ILocalConfigStore, LocalConfigStore>();
services.AddSingleton<IPostConfigureOptions<AzureDevOpsOptions>, ConfigurationResolver>();

// Authentication provider (chained: PAT ? cached token ? Azure CLI ? error)
services.AddSingleton<IAzureCliTokenProvider, AzureCliTokenProvider>();
services.AddSingleton<IAuthenticationProvider, ChainedAuthenticationProvider>();
services.AddTransient<AuthenticationDelegatingHandler>();

// Typed HTTP client with auth handler + resilience
services.AddHttpClient<IAzureDevOpsApiClient, AzureDevOpsApiClient>()
    .AddHttpMessageHandler<AuthenticationDelegatingHandler>()
    .AddStandardResilienceHandler();

// Azure DevOps JSON parsers
services.AddSingleton<IPullRequestMetadataParser, PullRequestMetadataParser>();
services.AddSingleton<IIterationInfoParser, IterationInfoParser>();
services.AddSingleton<IFileChangesParser, FileChangesParser>();

// Azure DevOps fine-grained providers (internal implementation details)
services.AddSingleton<AzureDevOpsDiffProvider>();
services.AddSingleton<AzureDevOpsMetadataProvider>();
services.AddSingleton<AzureDevOpsFilesProvider>();
services.AddSingleton<AzureDevOpsRepositoryArchiveProvider>();

// Unified SCM client facade + interface forwarding
services.AddSingleton<AzureDevOpsScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
```

## `REBUSS.Pure.GitHub\ServiceCollectionExtensions.cs` ? `AddGitHubProvider(IConfiguration)`

```csharp
// Options + validation + post-configure resolution
services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
services.AddSingleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>();
services.AddSingleton<IGitHubRemoteDetector, GitHubRemoteDetector>();
services.AddSingleton<IGitHubConfigStore, GitHubConfigStore>();
services.AddSingleton<IPostConfigureOptions<GitHubOptions>, GitHubConfigurationResolver>();

// Authentication provider (chained: PAT ? cached token ? GitHub CLI ? error)
services.AddSingleton<IGitHubCliTokenProvider, GitHubCliTokenProvider>();
services.AddSingleton<IGitHubAuthenticationProvider, GitHubChainedAuthenticationProvider>();

// Authentication handler (resolves auth via IGitHubAuthenticationProvider + required GitHub headers)
services.AddTransient<GitHubAuthenticationHandler>();

// Typed HTTP client with auth handler + resilience
services.AddHttpClient<IGitHubApiClient, GitHubApiClient>()
    .AddHttpMessageHandler<GitHubAuthenticationHandler>()
    .AddStandardResilienceHandler();

// GitHub JSON parsers
services.AddSingleton<IGitHubPullRequestParser, GitHubPullRequestParser>();
services.AddSingleton<IGitHubFileChangesParser, GitHubFileChangesParser>();

// GitHub fine-grained providers (internal implementation details)
services.AddSingleton<GitHubDiffProvider>();
services.AddSingleton<GitHubMetadataProvider>();
services.AddSingleton<GitHubFilesProvider>();
services.AddSingleton<GitHubRepositoryArchiveProvider>();

// Unified SCM client facade + interface forwarding
services.AddSingleton<GitHubScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<GitHubScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<GitHubScmClient>());
services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<GitHubScmClient>());
```

## `REBUSS.Pure\Program.cs` — `RunMcpServerAsync` + `ConfigureBusinessServices`

```csharp
// RunMcpServerAsync: uses Host.CreateApplicationBuilder() for MCP server hosting
var builder = Host.CreateApplicationBuilder();

// Replace host's default configuration with pre-built configuration
builder.Configuration.Sources.Clear();
builder.Configuration.AddConfiguration(configuration);

// Logging — stderr output + file logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.AddProvider(new FileLoggerProvider(GetLogDirectory()));
builder.Logging.SetMinimumLevel(LogLevel.Debug);

// Business services (extracted into ConfigureBusinessServices)
ConfigureBusinessServices(builder.Services, configuration);

// MCP server with stdio transport and attribute-based tool discovery
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new() { Name = "REBUSS.Pure", Version = "1.0.0" };
    })
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

// ConfigureBusinessServices(IServiceCollection, IConfiguration):

// Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

// Shared services (provider-agnostic)
services.AddSingleton<IDiffAlgorithm, DiffPlexDiffAlgorithm>();
services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
services.AddSingleton<IFileClassifier, FileClassifier>();
services.AddSingleton<DiffSourceResolver>();
services.AddSingleton<IDiffEnricher, BeforeAfterEnricher>();         // Order=100
services.AddSingleton<IDiffEnricher, ScopeAnnotatorEnricher>();      // Order=150
services.AddSingleton<IDiffEnricher, StructuralChangeEnricher>();    // Order=200
services.AddSingleton<IDiffEnricher, UsingsChangeEnricher>();        // Order=250
services.AddSingleton<CallSiteScanner>();
services.AddSingleton<IDiffEnricher, CallSiteEnricher>();            // Order=300
services.AddSingleton<ICodeProcessor, CompositeCodeProcessor>();

// Context Window Awareness
services.Configure<ContextWindowOptions>(configuration.GetSection(ContextWindowOptions.SectionName));
services.AddSingleton<IContextBudgetResolver, ContextBudgetResolver>();
services.AddSingleton<ITokenEstimator, TokenEstimator>();

// Response Packing
services.AddSingleton<IResponsePacker, ResponsePacking.ResponsePacker>();

// Pagination (Feature 004)
services.AddSingleton<IPageAllocator, Pagination.PageAllocator>();
services.AddSingleton<IPageReferenceCodec, Pagination.PageReferenceCodec>();

// PR diff cache — eliminates duplicate API calls between metadata and content handlers (Feature 005)
services.AddSingleton<IPullRequestDiffCache, PullRequestDiffCache>();

// Provider selection: explicit config > GitHub.Owner > AzureDevOps.OrganizationName > git remote URL > default AzureDevOps
var provider = DetectProvider(configuration);
switch (provider)
{
    case "GitHub":
        services.AddGitHubProvider(configuration);
        break;
    case "AzureDevOps":
    default:
        services.AddAzureDevOpsProvider(configuration);
        break;
}

// MCP tool handlers
// SDK-migrated handlers (registered via [McpServerToolType] attribute discovery):
// GetPullRequestMetadataToolHandler, GetPullRequestContentToolHandler,
// GetLocalContentToolHandler

// Repository download orchestrator + startup cleanup
services.AddSingleton<IRepositoryDownloadOrchestrator, RepositoryDownloadOrchestrator>();
services.AddHostedService<RepositoryCleanupService>();

// Local self-review pipeline
services.AddSingleton<ILocalGitClient, LocalGitClient>();
services.AddSingleton<ILocalReviewProvider, LocalReviewProvider>();

// Copilot review layer (features 013 + 018 + 021 + 022)
services.Configure<CopilotReviewOptions>(configuration.GetSection(CopilotReviewOptions.SectionName));
services.AddSingleton<ICopilotTokenResolver, CopilotTokenResolver>();
services.AddSingleton<CopilotVerificationRunner>();
services.AddSingleton<ICopilotVerificationProbe>(sp => sp.GetRequiredService<CopilotVerificationRunner>());
services.AddSingleton<CopilotClientProvider>();
services.AddSingleton<ICopilotClientProvider>(sp => sp.GetRequiredService<CopilotClientProvider>());
services.AddHostedService(sp => sp.GetRequiredService<CopilotClientProvider>()); // graceful StopAsync on shutdown
services.AddSingleton<ICopilotSessionFactory, CopilotSessionFactory>();
services.AddSingleton<ICopilotAvailabilityDetector, CopilotAvailabilityDetector>();
services.AddSingleton<ICopilotPageReviewer, CopilotPageReviewer>();

// Feature 022 — env-var gate (REBUSS_COPILOT_INSPECT=1|true|True) chooses the writer
// at DI composition time. Toggling requires a process restart.
if (Environment.GetEnvironmentVariable("REBUSS_COPILOT_INSPECT") is "1" or "true" or "True")
    services.AddSingleton<ICopilotInspectionWriter, FileSystemCopilotInspectionWriter>();
else
    services.AddSingleton<ICopilotInspectionWriter, NoOpCopilotInspectionWriter>();

// Feature 021 — finding validation always wired (orchestrator decides at runtime per
// CopilotReviewOptions.ValidateFindings, read lazily per Principle V).
services.AddSingleton<FindingScopeResolver>();
services.AddSingleton<FindingValidator>(); // ctor: ICopilotSessionFactory + IOptions<CopilotReviewOptions> + ICopilotInspectionWriter + IPageAllocator + ITokenEstimator + ILogger

services.AddSingleton<ICopilotReviewOrchestrator, CopilotReviewOrchestrator>();
services.AddSingleton<CopilotReviewWaiter>();

// JSON-RPC infrastructure
services.AddSingleton<IJsonRpcSerializer, SystemTextJsonSerializer>();
services.AddSingleton<IJsonRpcTransport>(_ =>
    new StreamJsonRpcTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()));
services.AddSingleton<IMcpProtocolVersionNegotiator, McpProtocolVersionNegotiator>();

// Method handlers
services.AddSingleton<IMcpMethodHandler, InitializeMethodHandler>();
services.AddSingleton<IMcpMethodHandler, ToolsListMethodHandler>();
services.AddSingleton<IMcpMethodHandler, ToolsCallMethodHandler>();

// Server
services.AddSingleton<McpServer>(...);

// In RunMcpServerAsync: CLI arguments (--pat, --org, --project, --repository) are
// collected into a Dictionary and added via AddInMemoryCollection to the configuration
// builder AFTER environment variables, giving them highest priority.
// CLI --repo is applied after building the service provider via IWorkspaceRootProvider.SetCliRepositoryPath.
```

---

# 4. Conventions Snapshot

| Aspect | Value |
|---|---|
| **Target framework** | .NET 10 (`net10.0`) |
| **C# version** | 14.0 (implicit via .NET 10 SDK) |
| **Nullable context** | `enable` (project-wide) |
| **Implicit usings** | `enable` |
| **Test framework** | xUnit 2.9.3 |
| **Mocking library** | NSubstitute 5.3.0 |
| **JSON library** | System.Text.Json 10.0.5 |
| **JSON naming policy** | `camelCase` via `JsonNamingPolicy.CamelCase` in tool handlers; explicit `[JsonPropertyName]` on all DTO properties |
| **JSON null handling** | `JsonIgnoreCondition.WhenWritingNull` |
| **Internal access** | `InternalsVisibleTo("REBUSS.Pure.Tests")` on `REBUSS.Pure`; `InternalsVisibleTo("REBUSS.Pure.Core.Tests")` on `REBUSS.Pure.Core`; `InternalsVisibleTo("REBUSS.Pure")` and `InternalsVisibleTo("REBUSS.Pure.AzureDevOps.Tests")` on `REBUSS.Pure.AzureDevOps`; `InternalsVisibleTo("REBUSS.Pure")` and `InternalsVisibleTo("REBUSS.Pure.GitHub.Tests")` on `REBUSS.Pure.GitHub` |
| **DI pattern** | Constructor injection; all registered as singletons; interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IRepositoryArchiveProvider` through the active provider's facade (`AzureDevOpsScmClient` or `GitHubScmClient`); provider-specific registrations via `ServiceCollectionExtensions.AddAzureDevOpsProvider()` or `ServiceCollectionExtensions.AddGitHubProvider()`; exactly one provider is registered per process based on `DetectProvider()` |
| **Architecture** | 8-project solution: `REBUSS.Pure.Core` (domain models, interfaces, shared logic, analysis pipeline) ? `REBUSS.Pure.AzureDevOps` (Azure DevOps provider) / `REBUSS.Pure.GitHub` (GitHub provider) ? `REBUSS.Pure` (MCP server, tool handlers, local review pipeline, CLI); test projects: `REBUSS.Pure.Core.Tests`, `REBUSS.Pure.AzureDevOps.Tests`, `REBUSS.Pure.GitHub.Tests`, `REBUSS.Pure.Tests` |
| **Error handling** | Custom exceptions in `REBUSS.Pure.Core.Exceptions` (`PullRequestNotFoundException`, `FileNotFoundInPullRequestException`), caught in tool handlers, returned as `ToolResult.IsError = true` |
| **Logging** | `Microsoft.Extensions.Logging`, stderr output via console provider, file logging via `FileLoggerProvider` |
| **Comments** | XML doc on public types/methods; no inline comments unless complex |
| **Naming** | Standard C# conventions; private fields prefixed with `_`; interfaces prefixed with `I` |
| **File naming** | Interface and class in same-named files (e.g., `IUnifiedDiffBuilder.cs` contains `IStructuredDiffBuilder`) � note: file names may not match type names after refactoring |
| **CLI pattern** | `CliArgumentParser` for parsing, `ICliCommand` for commands; CLI output goes to `Console.Error` (stdout reserved for MCP stdio) |
| **Provider pattern** | Fine-grained providers (e.g. `AzureDevOpsDiffProvider`, `GitHubDiffProvider`) are concrete classes registered directly; the SCM facade (`AzureDevOpsScmClient` or `GitHubScmClient`) implements `IScmClient` and delegates to them; tool handlers depend on narrow `IPullRequestDataProvider` interface from Core |
| **Provider registration** | Each provider encapsulates its DI registrations as an extension method: `AddAzureDevOpsProvider(IConfiguration)` in `REBUSS.Pure.AzureDevOps` and `AddGitHubProvider(IConfiguration)` in `REBUSS.Pure.GitHub`; `Program.cs` calls exactly one based on `DetectProvider()` |

---

# 5. Current File Contents

> This section contains the exact current state of files that are in scope for modification.
> When no code refactoring is in progress, this section is intentionally left empty.
> Populate it with the relevant file contents before starting a modification task.

*(No files currently in scope for modification.)*

---

# 6. Context Self-Diagnostic

Before using this document, verify:

- [ ] File-role map matches actual file listing (run `get_files_in_project` if unsure)
- [ ] DI registration summary matches `Program.cs` `ConfigureBusinessServices` + `RunMcpServerAsync` (MCP SDK builder), `ServiceCollectionExtensions.AddAzureDevOpsProvider`, and `ServiceCollectionExtensions.AddGitHubProvider`
- [ ] All model types mentioned in the dependency graph exist in the listed paths
- [ ] Section 5 file contents are current (re-read if stale)
- [ ] No legacy duplicate files exist in `REBUSS.Pure` (Azure DevOps code lives exclusively in `REBUSS.Pure.AzureDevOps`)
