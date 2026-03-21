# CODEBASE CONTEXT PROVIDED

Full codebase context is included below (file-role map, dependency graph, DI registrations, conventions, and current file contents for all files in scope). Skip all exploratory analysis — do not call get_projects_in_solution, get_files_in_project, or read files already provided. Proceed directly to planning and implementation using only the supplied context. If a file outside the provided set might be affected by a model or interface change, read it before editing.

---

# 1. File-Role Map

## Solution structure

| Project | Path | Purpose |
|---|---|---|
| REBUSS.Pure.Core | `REBUSS.Pure.Core\REBUSS.Pure.Core.csproj` | Domain model library (.NET 10): models, interfaces (`IScmClient`, `IPullRequestDataProvider`, `IFileContentDataProvider`, `IWorkspaceRootProvider`), shared diff/classification logic, analysis pipeline, exceptions |
| REBUSS.Pure.AzureDevOps | `REBUSS.Pure.AzureDevOps\REBUSS.Pure.AzureDevOps.csproj` | Azure DevOps provider library (.NET 10): `AzureDevOpsScmClient` facade, fine-grained providers, parsers, API client, configuration/auth; references `REBUSS.Pure.Core` |
| REBUSS.Pure.GitHub | `REBUSS.Pure.GitHub\REBUSS.Pure.GitHub.csproj` | GitHub provider library (.NET 10): `GitHubScmClient` facade, fine-grained providers, parsers, REST API v3 client, configuration/auth (Bearer PAT); references `REBUSS.Pure.Core` |
| REBUSS.Pure | `REBUSS.Pure\REBUSS.Pure.csproj` | MCP server (console app, .NET 10; NuGet package `CodeReview.MCP`, command `rebuss-pure`); references `REBUSS.Pure.Core`, `REBUSS.Pure.AzureDevOps`, and `REBUSS.Pure.GitHub`; contains MCP infrastructure, tool handlers, local review pipeline, CLI |
| REBUSS.Pure.Tests | `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj` | Unit + integration tests (xUnit, NSubstitute); references `REBUSS.Pure`, `REBUSS.Pure.Core`, `REBUSS.Pure.AzureDevOps`, and `REBUSS.Pure.GitHub` |

---

## Source files

### Domain models (REBUSS.Pure.Core\Models)

| File | Role | Key types | Consumed by |
|---|---|---|---|
| `REBUSS.Pure.Core\Models\PullRequestDiff.cs` | Core diff domain model | `PullRequestDiff`, `FileChange`, `DiffHunk`, `DiffLine` | AzureDevOpsDiffProvider, AzureDevOpsFilesProvider, AzureDevOpsScmClient, LocalReviewProvider, all tool handlers producing diffs, all diff/file-related tests |
| `REBUSS.Pure.Core\Models\PullRequestMetadata.cs` | Parsed PR metadata (lightweight) | `PullRequestMetadata` (record) | AzureDevOpsDiffProvider, PullRequestMetadataParser |
| `REBUSS.Pure.Core\Models\FullPullRequestMetadata.cs` | Rich PR metadata (all fields + `RepositoryFullName`, `WebUrl`) | `FullPullRequestMetadata` | AzureDevOpsMetadataProvider, AzureDevOpsScmClient, GetPullRequestMetadataToolHandler |
| `REBUSS.Pure.Core\Models\IterationInfo.cs` | Iteration commit SHAs | `IterationInfo` (record) | AzureDevOpsDiffProvider, AzureDevOpsMetadataProvider, IterationInfoParser |
| `REBUSS.Pure.Core\Models\FileClassification.cs` | File classification result | `FileClassification`, `FileCategory` (enum) | FileClassifier, AzureDevOpsFilesProvider, AzureDevOpsDiffProvider, LocalReviewProvider |
| `REBUSS.Pure.Core\Models\PullRequestFiles.cs` | File list result models | `PullRequestFiles`, `PullRequestFileInfo`, `PullRequestFilesSummary` | AzureDevOpsFilesProvider, GetPullRequestFilesToolHandler, LocalReviewProvider, GetLocalChangesFilesToolHandler |
| `REBUSS.Pure.Core\Models\FileContent.cs` | File content result | `FileContent` | AzureDevOpsFileContentProvider, GitHubFileContentProvider, GetFileContentAtRefToolHandler |

### Core interfaces (REBUSS.Pure.Core)

| File | Role | Key types |
|---|---|---|
| `REBUSS.Pure.Core\IScmClient.cs` | Unified SCM client contract | `IScmClient` (extends `IPullRequestDataProvider` + `IFileContentDataProvider`), `IPullRequestDataProvider`, `IFileContentDataProvider` |
| `REBUSS.Pure.Core\IWorkspaceRootProvider.cs` | Workspace/repository root resolution contract | `IWorkspaceRootProvider` — `SetCliRepositoryPath`, `SetRoots`, `GetRootUris`, `ResolveRepositoryRoot` |

### Core shared logic (REBUSS.Pure.Core\Shared)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.Core\Shared\DiffEdit.cs` | Line-level edit operation | — (`readonly record struct DiffEdit`) |
| `REBUSS.Pure.Core\Shared\IDiffAlgorithm.cs` | Interface: line-level diff algorithm | `DiffEdit` |
| `REBUSS.Pure.Core\Shared\LcsDiffAlgorithm.cs` | LCS-based O(m×n) diff algorithm | `IDiffAlgorithm`, `DiffEdit` |
| `REBUSS.Pure.Core\Shared\IStructuredDiffBuilder.cs` | Interface: produces `List<DiffHunk>` | `DiffHunk` |
| `REBUSS.Pure.Core\Shared\StructuredDiffBuilder.cs` | Builds structured hunks from base/target content | `IStructuredDiffBuilder`, `IDiffAlgorithm`, `DiffHunk`, `DiffLine`, `DiffEdit` |
| `REBUSS.Pure.Core\Shared\IFileClassifier.cs` | Interface: file classifier | `FileClassification` |
| `REBUSS.Pure.Core\Shared\FileClassifier.cs` | Classifies files by path/extension | `IFileClassifier`, `FileClassification`, `FileCategory` |

### Core exceptions (REBUSS.Pure.Core\Exceptions)

| File | Role |
|---|---|
| `REBUSS.Pure.Core\Exceptions\PullRequestNotFoundException.cs` | PR not found (404) |
| `REBUSS.Pure.Core\Exceptions\FileNotFoundInPullRequestException.cs` | File not in PR |
| `REBUSS.Pure.Core\Exceptions\FileContentNotFoundException.cs` | File content not found at ref |

### Analysis pipeline (REBUSS.Pure.Core\Analysis)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.Core\Analysis\AnalysisInput.cs` | All data available to analyzers for a single review | `PullRequestDiff`, `FullPullRequestMetadata`, `PullRequestFiles`, `IFileContentDataProvider` |
| `REBUSS.Pure.Core\Analysis\AnalysisSection.cs` | One section of review context output | — |
| `REBUSS.Pure.Core\Analysis\ReviewContext.cs` | Aggregated output from all analyzers | `AnalysisSection` |
| `REBUSS.Pure.Core\Analysis\IReviewAnalyzer.cs` | Pluggable analysis feature interface | `AnalysisInput`, `AnalysisSection` |
| `REBUSS.Pure.Core\Analysis\ReviewContextOrchestrator.cs` | Orchestrates SCM data fetch + analyzer pipeline | `IScmClient`, `IReviewAnalyzer`, `AnalysisInput`, `ReviewContext` |

### Azure DevOps provider — API client (REBUSS.Pure.AzureDevOps\Api)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Api\IAzureDevOpsApiClient.cs` | Interface: Azure DevOps REST API client (returns raw JSON strings) |
| `REBUSS.Pure.AzureDevOps\Api\AzureDevOpsApiClient.cs` | HTTP client for Azure DevOps; sets BaseAddress lazily in constructor; `GetStringAsync` detects HTML responses on 2xx status codes (e.g. 203) as authentication failures and throws `HttpRequestException` with `Unauthorized` status code |

### Azure DevOps provider — Parsers (REBUSS.Pure.AzureDevOps\Parsers)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Parsers\IPullRequestMetadataParser.cs` | Interface: parses PR details JSON ? `PullRequestMetadata` / `FullPullRequestMetadata` |
| `REBUSS.Pure.AzureDevOps\Parsers\PullRequestMetadataParser.cs` | Parses Azure DevOps PR details JSON |
| `REBUSS.Pure.AzureDevOps\Parsers\IIterationInfoParser.cs` | Interface: parses iterations JSON ? `IterationInfo` |
| `REBUSS.Pure.AzureDevOps\Parsers\IterationInfoParser.cs` | Parses iterations JSON |
| `REBUSS.Pure.AzureDevOps\Parsers\IFileChangesParser.cs` | Interface: parses file changes JSON ? `List<FileChange>` |
| `REBUSS.Pure.AzureDevOps\Parsers\FileChangesParser.cs` | Parses iteration changes JSON |

### Azure DevOps provider — Configuration/Auth (REBUSS.Pure.AzureDevOps\Configuration)

| File | Role |
|---|---|
| `REBUSS.Pure.AzureDevOps\Configuration\AzureDevOpsOptions.cs` | Config model: org, project, repo, PAT, LocalRepoPath (all optional) |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureDevOpsOptionsValidator.cs` | Validates config field format (all fields optional, format-only checks) |
| `REBUSS.Pure.AzureDevOps\Configuration\IGitRemoteDetector.cs` | Interface + `DetectedGitInfo` record: detects Azure DevOps repo from Git remote |
| `REBUSS.Pure.AzureDevOps\Configuration\GitRemoteDetector.cs` | Parses HTTPS/SSH Azure DevOps remote URLs via `git remote get-url origin`; tries current directory and executable location |
| `REBUSS.Pure.AzureDevOps\Configuration\ILocalConfigStore.cs` | Interface + `CachedConfig` model: persists/retrieves cached config |
| `REBUSS.Pure.AzureDevOps\Configuration\LocalConfigStore.cs` | JSON file store under `%LOCALAPPDATA%/REBUSS.Pure/config.json` |
| `REBUSS.Pure.AzureDevOps\Configuration\IAuthenticationProvider.cs` | Interface: provides `AuthenticationHeaderValue`; `InvalidateCachedToken()` clears cache |
| `REBUSS.Pure.AzureDevOps\Configuration\IAzureCliTokenProvider.cs` | Interface: acquires Azure DevOps token via Azure CLI; defines `AzureCliToken` record |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureCliProcessHelper.cs` | `internal static` helper: resolves `ProcessStartInfo` for cross-platform Azure CLI execution |
| `REBUSS.Pure.AzureDevOps\Configuration\AzureCliTokenProvider.cs` | Runs `az account get-access-token` for Bearer tokens; `ParseTokenResponse` is `internal static` |
| `REBUSS.Pure.AzureDevOps\Configuration\ChainedAuthenticationProvider.cs` | Auth chain: PAT ? cached token ? Azure CLI ? error with actionable instructions |
| `REBUSS.Pure.AzureDevOps\Configuration\AuthenticationDelegatingHandler.cs` | `DelegatingHandler` that lazily sets auth header; retries once on HTTP 203 HTML redirect |
| `REBUSS.Pure.AzureDevOps\Configuration\ConfigurationResolver.cs` | `IPostConfigureOptions<AzureDevOpsOptions>`: merges explicit config, cached, and auto-detected values |

### Azure DevOps provider — Providers (REBUSS.Pure.AzureDevOps\Providers)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsDiffProvider.cs` | Orchestrates fetching PR data + building diffs | `IAzureDevOpsApiClient`, `IStructuredDiffBuilder`, `IFileClassifier`, parsers, `PullRequestDiff`, `FileChange`, `DiffHunk` |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsMetadataProvider.cs` | Fetches full PR metadata from multiple endpoints | `IAzureDevOpsApiClient`, parsers, `FullPullRequestMetadata` |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsFilesProvider.cs` | Builds classified file list from diff provider output | `AzureDevOpsDiffProvider`, `IFileClassifier`, `FileChange` (uses `.Additions`, `.Deletions`) |
| `REBUSS.Pure.AzureDevOps\Providers\AzureDevOpsFileContentProvider.cs` | Fetches file content at specific Git ref | `IAzureDevOpsApiClient`, `FileContent` |

### Azure DevOps provider — SCM facade (REBUSS.Pure.AzureDevOps)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.AzureDevOps\AzureDevOpsScmClient.cs` | `IScmClient` facade: delegates to fine-grained providers, enriches metadata with `WebUrl`/`RepositoryFullName` | `AzureDevOpsDiffProvider`, `AzureDevOpsMetadataProvider`, `AzureDevOpsFilesProvider`, `AzureDevOpsFileContentProvider`, `AzureDevOpsOptions` |
| `REBUSS.Pure.AzureDevOps\ServiceCollectionExtensions.cs` | `AddAzureDevOpsProvider(IConfiguration)`: registers all Azure DevOps DI services including interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` | All Azure DevOps types above |

### GitHub provider — API client (REBUSS.Pure.GitHub\Api)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Api\IGitHubApiClient.cs` | Interface: GitHub REST API v3 client (PR details, files, commits, file content at ref) |
| `REBUSS.Pure.GitHub\Api\GitHubApiClient.cs` | HTTP client for GitHub REST API; base URL `https://api.github.com/`; supports pagination (`GetPaginatedArrayAsync`, max 10 pages); raw file content via `Accept: application/vnd.github.raw+json` header |

### GitHub provider — Parsers (REBUSS.Pure.GitHub\Parsers)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Parsers\IGitHubPullRequestParser.cs` | Interface: parses GitHub PR JSON ? `PullRequestMetadata` / `FullPullRequestMetadata`, base/head commit SHAs |
| `REBUSS.Pure.GitHub\Parsers\GitHubPullRequestParser.cs` | Parses GitHub PR details JSON; maps state (`open`?`active`, `closed`?`completed`); `MapState` is `internal static` |
| `REBUSS.Pure.GitHub\Parsers\IGitHubFileChangesParser.cs` | Interface: parses GitHub files JSON ? `List<FileChange>` |
| `REBUSS.Pure.GitHub\Parsers\GitHubFileChangesParser.cs` | Parses GitHub PR files JSON array; maps status (`added`?`add`, `removed`?`delete`, `modified`?`edit`, `renamed`?`rename`, `copied`?`add`); `MapStatus` is `internal static` |

### GitHub provider — Configuration/Auth (REBUSS.Pure.GitHub\Configuration)

| File | Role |
|---|---|
| `REBUSS.Pure.GitHub\Configuration\GitHubOptions.cs` | Config model: Owner, RepositoryName, PersonalAccessToken, LocalRepoPath (all optional); `SectionName = "GitHub"` |
| `REBUSS.Pure.GitHub\Configuration\GitHubOptionsValidator.cs` | Validates config field format (no-spaces format checks, all fields optional) |
| `REBUSS.Pure.GitHub\Configuration\IGitHubRemoteDetector.cs` | Interface + `DetectedGitHubInfo` record: detects GitHub owner/repo from Git remote |
| `REBUSS.Pure.GitHub\Configuration\GitHubRemoteDetector.cs` | Parses HTTPS/SSH GitHub remote URLs via `git remote get-url origin`; tries current directory and executable location; `ParseRemoteUrl`, `FindGitRepositoryRoot`, `GetCandidateDirectories` are `internal static` |
| `REBUSS.Pure.GitHub\Configuration\IGitHubConfigStore.cs` | Interface + `GitHubCachedConfig` model: persists/retrieves cached GitHub config |
| `REBUSS.Pure.GitHub\Configuration\GitHubConfigStore.cs` | JSON file store under `%LOCALAPPDATA%/REBUSS.Pure/github-config.json` |
| `REBUSS.Pure.GitHub\Configuration\GitHubConfigurationResolver.cs` | `IPostConfigureOptions<GitHubOptions>`: merges explicit config, cached, and auto-detected values (same pattern as Azure DevOps `ConfigurationResolver`) |
| `REBUSS.Pure.GitHub\Configuration\GitHubAuthenticationHandler.cs` | `DelegatingHandler` that sets Bearer PAT + GitHub API headers (`Accept`, `User-Agent`, `X-GitHub-Api-Version: 2022-11-28`) |

### GitHub provider — Providers (REBUSS.Pure.GitHub\Providers)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.GitHub\Providers\GitHubDiffProvider.cs` | Orchestrates fetching PR data + building diffs (no iteration concept; base/head SHAs from PR endpoint directly) | `IGitHubApiClient`, `IStructuredDiffBuilder`, `IFileClassifier`, parsers, `PullRequestDiff`, `FileChange`, `DiffHunk` |
| `REBUSS.Pure.GitHub\Providers\GitHubMetadataProvider.cs` | Fetches full PR metadata from PR details + commits endpoints | `IGitHubApiClient`, `IGitHubPullRequestParser`, `FullPullRequestMetadata` |
| `REBUSS.Pure.GitHub\Providers\GitHubFilesProvider.cs` | Builds classified file list from diff provider output | `GitHubDiffProvider`, `IFileClassifier`, `FileChange` |
| `REBUSS.Pure.GitHub\Providers\GitHubFileContentProvider.cs` | Fetches file content at specific Git ref; detects binary via null byte | `IGitHubApiClient`, `FileContent` |

### GitHub provider — SCM facade (REBUSS.Pure.GitHub)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure.GitHub\GitHubScmClient.cs` | `IScmClient` facade: delegates to fine-grained providers, enriches metadata with `WebUrl` (`https://github.com/{owner}/{repo}/pull/{n}`) and `RepositoryFullName` (`{owner}/{repo}`) | `GitHubDiffProvider`, `GitHubMetadataProvider`, `GitHubFilesProvider`, `GitHubFileContentProvider`, `GitHubOptions` |
| `REBUSS.Pure.GitHub\ServiceCollectionExtensions.cs` | `AddGitHubProvider(IConfiguration)`: registers all GitHub DI services including options, validator, remote detector, config store, post-configure, auth handler, HTTP client with resilience, parsers, providers, facade + interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` | All GitHub types above |

### Local review pipeline (REBUSS.Pure\Services\LocalReview)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\LocalReview\ILocalGitClient.cs` | Interface: local git operations; defines `LocalFileStatus` record | — |
| `REBUSS.Pure\Services\LocalReview\LocalGitClient.cs` | Runs git child processes; uses `diff --name-status` for all scopes; exposes `WorkingTreeRef` sentinel for filesystem reads | `ILocalGitClient` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewScope.cs` | Value type: `WorkingTree`, `Staged`, `BranchDiff(base)` + `Parse(string?)` | — |
| `REBUSS.Pure\Services\LocalReview\ILocalReviewProvider.cs` | Interface: lists local files + diffs; defines `LocalReviewFiles` model | `PullRequestDiff`, `PullRequestFileInfo`, `PullRequestFilesSummary` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewProvider.cs` | Orchestrates git client + diff builder + file classifier | `IWorkspaceRootProvider` (from Core), `ILocalGitClient`, `IStructuredDiffBuilder`, `IFileClassifier`, domain models |
| `REBUSS.Pure\Services\LocalReview\LocalReviewExceptions.cs` | `LocalRepositoryNotFoundException`, `LocalFileNotFoundException`, `GitCommandException` | — |

### MCP tool handlers (REBUSS.Pure\Tools)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Tools\GetPullRequestDiffToolHandler.cs` | `get_pr_diff` — returns structured JSON with per-file hunks | `IPullRequestDataProvider`, `StructuredDiffResult` models |
| `REBUSS.Pure\Tools\GetFileDiffToolHandler.cs` | `get_file_diff` — returns structured JSON for a single file | `IPullRequestDataProvider`, `StructuredDiffResult` models |
| `REBUSS.Pure\Tools\GetPullRequestMetadataToolHandler.cs` | `get_pr_metadata` — returns PR metadata JSON | `IPullRequestDataProvider`, `PullRequestMetadataResult` models |
| `REBUSS.Pure\Tools\GetPullRequestFilesToolHandler.cs` | `get_pr_files` — returns classified file list JSON | `IPullRequestDataProvider`, `PullRequestFilesResult` models |
| `REBUSS.Pure\Tools\GetFileContentAtRefToolHandler.cs` | `get_file_content_at_ref` — returns file content JSON | `IFileContentDataProvider`, `FileContentAtRefResult` model |
| `REBUSS.Pure\Tools\GetLocalChangesFilesToolHandler.cs` | `get_local_files` — lists locally changed files with classification | `ILocalReviewProvider`, `LocalReviewFilesResult` model |
| `REBUSS.Pure\Tools\GetLocalFileDiffToolHandler.cs` | `get_local_file_diff` — returns structured diff for a single local file | `ILocalReviewProvider`, `StructuredDiffResult` models |

### Tool output models (REBUSS.Pure\Tools\Models)

| File | Role |
|---|---|
| `REBUSS.Pure\Tools\Models\StructuredDiffResult.cs` | `StructuredDiffResult`, `StructuredFileChange`, `StructuredHunk`, `StructuredLine` — diff tool JSON output (shared by PR and local tools); `PrNumber` is `int?` (null for local diffs, omitted from JSON) |
| `REBUSS.Pure\Tools\Models\PullRequestMetadataResult.cs` | `PullRequestMetadataResult`, `AuthorInfo`, `RefInfo`, `PrStats`, `DescriptionInfo`, `SourceInfo` |
| `REBUSS.Pure\Tools\Models\PullRequestFilesResult.cs` | `PullRequestFilesResult`, `PullRequestFileItem`, `PullRequestFilesSummaryResult` (also reused by `LocalReviewFilesResult`) |
| `REBUSS.Pure\Tools\Models\FileContentAtRefResult.cs` | `FileContentAtRefResult` |
| `REBUSS.Pure\Tools\Models\LocalReviewFilesResult.cs` | `LocalReviewFilesResult` — JSON output for `get_local_files`; includes `repositoryRoot`, `scope`, `currentBranch` context fields |

### MCP infrastructure (REBUSS.Pure\Mcp)

| File | Role |
|---|---|
| `REBUSS.Pure\Mcp\McpServer.cs` | Main server loop: reads JSON-RPC over stdio, dispatches to method handlers; silently ignores notifications (messages without `id`) for unregistered methods per MCP/JSON-RPC spec |
| `REBUSS.Pure\Mcp\IMcpMethodHandler.cs` | Interface: handles one JSON-RPC method |
| `REBUSS.Pure\Mcp\IMcpToolHandler.cs` | Interface: MCP tool (definition + execution) |
| `REBUSS.Pure\Mcp\McpWorkspaceRootProvider.cs` | Implementation of `IWorkspaceRootProvider` (from Core): resolves repo root from CLI `--repo` (highest priority), MCP roots, or `localRepoPath` config; guards against unexpanded variables; reads `LocalRepoPath` directly from `IConfiguration` to avoid circular dependency with `IPostConfigureOptions<AzureDevOpsOptions>` |
| `REBUSS.Pure\Mcp\Handlers\InitializeMethodHandler.cs` | `initialize` method handler — extracts MCP roots, stores via `IWorkspaceRootProvider` |
| `REBUSS.Pure\Mcp\Handlers\ToolsListMethodHandler.cs` | `tools/list` method handler |
| `REBUSS.Pure\Mcp\Handlers\ToolsCallMethodHandler.cs` | `tools/call` method handler — resolves tool by name, delegates |
| `REBUSS.Pure\Mcp\IJsonRpcSerializer.cs` | Interface: JSON-RPC serialization |
| `REBUSS.Pure\Mcp\SystemTextJsonSerializer.cs` | System.Text.Json implementation (camelCase, no indent, ignore nulls) |
| `REBUSS.Pure\Mcp\IJsonRpcTransport.cs` | Interface: read/write JSON-RPC messages |
| `REBUSS.Pure\Mcp\StreamJsonRpcTransport.cs` | Newline-delimited stream transport |
| `REBUSS.Pure\Mcp\McpMethodNotFoundException.cs` | Method not found exception |

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
| `REBUSS.Pure\Mcp\Models\InitializeParams.cs` | Initialize params: roots list from MCP client |
| `REBUSS.Pure\Mcp\Models\McpRoot.cs` | MCP root: uri, name |
| `REBUSS.Pure\Mcp\Models\ServerCapabilities.cs` | Server capabilities |
| `REBUSS.Pure\Mcp\Models\ServerInfo.cs` | Server info: name, version |
| `REBUSS.Pure\Mcp\Models\ToolsCapability.cs` | Tools capability: listChanged |
| `REBUSS.Pure\Mcp\Models\ToolsListResult.cs` | Tools list result |

### CLI infrastructure (REBUSS.Pure\Cli)

| File | Role |
|---|---|
| `REBUSS.Pure\Cli\CliArgumentParser.cs` | Parses CLI args: detects `init` command vs server mode, extracts `--repo`, `--pat`, `--org`, `--project`, `--repository`, `--provider`, `--owner` |
| `REBUSS.Pure\Cli\ICliCommand.cs` | Interface: executable CLI command |
| `REBUSS.Pure\Cli\InitCommand.cs` | `init` command: generates MCP config files, copies prompt files, handles Azure CLI authentication flow |
| `REBUSS.Pure\Cli\Prompts\review-pr.md` | Embedded resource: PR review prompt template |
| `REBUSS.Pure\Cli\Prompts\self-review.md` | Embedded resource: self-review prompt template |

### Logging

| File | Role |
|---|---|
| `REBUSS.Pure\Logging\FileLoggerProvider.cs` | `ILoggerProvider` that writes to daily-rotated files under `%LOCALAPPDATA%\REBUSS.Pure`; 3-day retention |

### Entry point

| File | Role |
|---|---|
| `REBUSS.Pure\Program.cs` | DI composition root; dual-mode: CLI commands (`init`) or MCP server; `ConfigureServices` wires shared services, selects provider via `DetectProvider(configuration)` (explicit > GitHub.Owner > AzureDevOps.OrganizationName > git remote URL > default AzureDevOps), calls either `services.AddGitHubProvider(configuration)` or `services.AddAzureDevOpsProvider(configuration)`, registers MCP tool handlers, local review pipeline, JSON-RPC infrastructure, and method handlers |

### Documentation

| File | Role |
|---|---|
| `README.md` | Project documentation |
| `install.ps1` | PowerShell installer: downloads latest `.nupkg` from GitHub Releases (`rebuss/CodeReview.MCP`), installs as global tool (`CodeReview.MCP`) |
| `install.sh` | Bash installer: same workflow as `install.ps1` for Linux/macOS |
| `.github\prompts\review-pr.md` | GitHub Copilot prompt for Azure DevOps PR review |
| `.github\prompts\self-review.md` | GitHub Copilot prompt for local self-review (no Azure DevOps required) |

---

## Test files

| File | Tests for |
|---|---|
| `REBUSS.Pure.Tests\Services\UnifiedDiffBuilderTests.cs` | `StructuredDiffBuilder` — hunk generation, edge cases |
| `REBUSS.Pure.Tests\Services\AzureDevOpsDiffProviderTests.cs` | `AzureDevOpsDiffProvider` — full diff/file diff, skip behavior, `IsFullFileRewrite`, `GetSkipReason` |
| `REBUSS.Pure.Tests\Services\AzureDevOpsFilesProviderTests.cs` | `AzureDevOpsFilesProvider` — file list, status mapping, summary |
| `REBUSS.Pure.Tests\Services\AzureDevOpsFileContentProviderTests.cs` | `AzureDevOpsFileContentProvider` |
| `REBUSS.Pure.Tests\Services\LcsDiffAlgorithmTests.cs` | `LcsDiffAlgorithm` |
| `REBUSS.Pure.Tests\Services\Classification\FileClassifierTests.cs` | `FileClassifier` |
| `REBUSS.Pure.Tests\Services\Parsers\PullRequestMetadataParserTests.cs` | `PullRequestMetadataParser` |
| `REBUSS.Pure.Tests\Services\Parsers\IterationInfoParserTests.cs` | `IterationInfoParser` |
| `REBUSS.Pure.Tests\Services\Parsers\FileChangesParserTests.cs` | `FileChangesParser` |
| `REBUSS.Pure.Tests\Tools\GetPullRequestDiffToolHandlerTests.cs` | `GetPullRequestDiffToolHandler` — structured JSON output, validation, exceptions |
| `REBUSS.Pure.Tests\Tools\GetFileDiffToolHandlerTests.cs` | `GetFileDiffToolHandler` — structured JSON output, validation, schema, exceptions |
| `REBUSS.Pure.Tests\Tools\GetPullRequestFilesToolHandlerTests.cs` | `GetPullRequestFilesToolHandler` |
| `REBUSS.Pure.Tests\Tools\GetFileContentAtRefToolHandlerTests.cs` | `GetFileContentAtRefToolHandler` |
| `REBUSS.Pure.Tests\Tools\GetLocalChangesFilesToolHandlerTests.cs` | `GetLocalChangesFilesToolHandler` — scope parsing, JSON output, error handling |
| `REBUSS.Pure.Tests\Tools\GetLocalFileDiffToolHandlerTests.cs` | `GetLocalFileDiffToolHandler` — validation, scope routing, error handling, tool definition |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalReviewScopeTests.cs` | `LocalReviewScope.Parse` — all scope kinds, ToString |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalGitClientParseTests.cs` | `LocalGitClient` porcelain/name-status parsing — via reflection on internal static methods |
| `REBUSS.Pure.Tests\Services\LocalReview\LocalReviewProviderTests.cs` | `LocalReviewProvider` — files listing, status mapping, classification, file diff, skip reasons, exception cases |
| `REBUSS.Pure.Tests\Logging\FileLoggerProviderTests.cs` | `FileLoggerProvider` — daily rotation, file naming, write content, timestamp, retention/deletion, roll-over, non-log file safety |
| `REBUSS.Pure.Tests\Integration\EndToEndTests.cs` | Full JSON-RPC pipeline: request ? McpServer ? handler ? response |
| `REBUSS.Pure.Tests\Mcp\McpServerTests.cs` | `McpServer` — initialize, tools/list, tools/call, unknown method, invalid JSON, empty lines, notifications without id silently ignored, notifications don't affect subsequent requests, unknown method with id still returns error |
| `REBUSS.Pure.Tests\Mcp\InitializeMethodHandlerTests.cs` | `InitializeMethodHandler` — roots extraction, storage, edge cases |
| `REBUSS.Pure.Tests\Mcp\McpWorkspaceRootProviderTests.cs` | `McpWorkspaceRootProvider` — URI conversion, repo root resolution, MCP roots, localRepoPath fallback, CLI `--repo` precedence, unexpanded variable guard |
| `REBUSS.Pure.Tests\Cli\CliArgumentParserTests.cs` | `CliArgumentParser` — server mode, `--repo`, `--pat`, `--org`, `--project`, `--repository`, `init` command, combined args, edge cases |
| `REBUSS.Pure.Tests\Cli\InitCommandTests.cs` | `InitCommand` — generates `mcp.json` for correct IDE target(s), copies prompt files to `.github/prompts/`, error cases, subdirectory support, skip-existing prompts, PAT carry-over in `MergeConfigContent`, Azure CLI login during init (existing session reuse, interactive login, login failure displays auth banner with retry/PAT instructions, PAT skips login), Azure CLI installation prompt (detects missing az, prompts user y/n, installs on confirm, shows manual install hint on failure, skips prompt when az is installed) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureDevOpsOptionsTests.cs` | Options validation (format-only, all fields optional) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureDevOpsApiClientTests.cs` | API client — URL construction, status codes, file content, version descriptor resolution, HTML response detection (203 with HTML content type, HTML body without content type, valid JSON not flagged, XML body not flagged as HTML) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\GitRemoteDetectorTests.cs` | `GitRemoteDetector.ParseRemoteUrl` — HTTPS, SSH, GitHub, edge cases; `FindGitRepositoryRoot`, `GetCandidateDirectories` |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\ConfigurationResolverTests.cs` | `ConfigurationResolver` — PostConfigure precedence, fallback, caching, mixed sources, Resolve static method |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\ChainedAuthenticationProviderTests.cs` | `ChainedAuthenticationProvider` — PAT precedence, cached tokens, Azure CLI token acquisition and caching, expired token fallback to Azure CLI, null expiry falls through to Azure CLI refresh (not used as valid), `InvalidateCachedToken` clears cache fields, `BuildAuthRequiredMessage` content validation |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureCliProcessHelperTests.cs` | `AzureCliProcessHelper.GetProcessStartArgs` — Windows `cmd.exe /c az` wrapping, Linux direct `az` invocation, custom `azPath` produces quoted full-path command; `TryFindAzCliOnWindows` returns `null` on non-Windows |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureCliTokenProviderTests.cs` | `AzureCliTokenProvider.ParseTokenResponse` — valid JSON, missing/empty token, missing expiry, ISO 8601 dates, resource ID constant |
| `REBUSS.Pure.Tests\GitHub\GitHubDiffProviderTests.cs` | `GitHubDiffProvider` — metadata extraction, structured diff, additions/deletions, 404 handling, empty files, cancellation, file diff, case insensitivity, skip scenarios (delete/rename/binary/full-rewrite) |
| `REBUSS.Pure.Tests\GitHub\GitHubPullRequestParserTests.cs` | `GitHubPullRequestParser` — lightweight parse, full parse, base/head SHA extraction, closed state, invalid JSON fallbacks, `MapState` unit tests |
| `REBUSS.Pure.Tests\GitHub\GitHubFileChangesParserTests.cs` | `GitHubFileChangesParser` — status mapping (`added`?`add`, `removed`?`delete`, `modified`?`edit`, etc.), empty array, invalid JSON, non-array JSON |
| `REBUSS.Pure.Tests\GitHub\GitHubRemoteDetectorTests.cs` | `GitHubRemoteDetector.ParseRemoteUrl` — HTTPS (with/without `.git`), SSH, Azure DevOps/GitLab URLs return null, empty/malformed; `FindGitRepositoryRoot`, `GetCandidateDirectories` |
| `REBUSS.Pure.Tests\GitHub\GitHubScmClientTests.cs` | `GitHubScmClient` — provider name, facade delegation (diff/metadata/files/content), metadata enrichment (`WebUrl`, `RepositoryFullName`) |
| `REBUSS.Pure.Tests\GitHub\GitHubMetadataProviderTests.cs` | `GitHubMetadataProvider` — parsed metadata, commit SHAs, statistics, empty commits, 404 handling, invalid commits JSON |
| `REBUSS.Pure.Tests\GitHub\GitHubFileContentProviderTests.cs` | `GitHubFileContentProvider` — content retrieval, leading slash trim, binary detection, file not found, size calculation, cancellation |
| `REBUSS.Pure.Tests\GitHub\GitHubFilesProviderTests.cs` | `GitHubFilesProvider` — classified files, summary, empty files list |

---

# 2. Cross-Cutting Dependency Graph (Models Only)

```
PullRequestDiff (+ FileChange, DiffHunk, DiffLine)
  ? AzureDevOpsDiffProvider [AzureDevOps]      (produces: populates FileChange.Hunks/Additions/Deletions/SkipReason)
  ? AzureDevOpsFilesProvider [AzureDevOps]      (consumes: reads FileChange.Additions, .Deletions, .Path, .ChangeType)
  ? AzureDevOpsScmClient [AzureDevOps]          (delegates: passes through from diff/files providers)
  ? GitHubDiffProvider [GitHub]                 (produces: populates FileChange.Hunks/Additions/Deletions/SkipReason)
  ? GitHubFilesProvider [GitHub]                (consumes: reads FileChange from diff provider output)
  ? GitHubScmClient [GitHub]                    (delegates: passes through from diff/files providers)
  ? LocalReviewProvider [Pure]                   (produces for GetFileDiffAsync; consumes FileChange for files listing)
  ? GetPullRequestDiffToolHandler [Pure]         (consumes: maps FileChange ? StructuredFileChange)
  ? GetFileDiffToolHandler [Pure]                (consumes: maps FileChange ? StructuredFileChange)
  ? GetLocalFileDiffToolHandler [Pure]           (consumes: maps FileChange ? StructuredFileChange)
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
  ? AzureDevOpsFilesProvider [AzureDevOps]        (consumes: BuildFileInfo, BuildSummary)
  ? AzureDevOpsDiffProvider [AzureDevOps]         (consumes: GetSkipReason)
  ? LocalReviewProvider [Pure]                    (consumes: classifies local files)

DiffEdit
  ? LcsDiffAlgorithm [Core]                      (produces)
  ? StructuredDiffBuilder [Core]                  (consumes: ComputeHunks, FormatHunk)

FileContent
  ? AzureDevOpsFileContentProvider [AzureDevOps]  (produces)
  ? AzureDevOpsScmClient [AzureDevOps]            (delegates: passes through from content provider)
  ? GitHubFileContentProvider [GitHub]             (produces)
  ? GitHubScmClient [GitHub]                      (delegates: passes through from content provider)
  ? GetFileContentAtRefToolHandler [Pure]          (consumes)

PullRequestFiles / PullRequestFileInfo / PullRequestFilesSummary
  ? AzureDevOpsFilesProvider [AzureDevOps]        (produces)
  ? AzureDevOpsScmClient [AzureDevOps]            (delegates: passes through from files provider)
  ? GitHubFilesProvider [GitHub]                   (produces)
  ? GitHubScmClient [GitHub]                      (delegates: passes through from files provider)
  ? GetPullRequestFilesToolHandler [Pure]          (consumes)
  ? AnalysisInput [Core]                           (carries PullRequestFiles for analyzer pipeline)

IScmClient / IPullRequestDataProvider / IFileContentDataProvider [Core interfaces]
  ? AzureDevOpsScmClient [AzureDevOps]            (implements IScmClient; registered via interface forwarding in ServiceCollectionExtensions)
  ? GitHubScmClient [GitHub]                      (implements IScmClient; registered via interface forwarding in ServiceCollectionExtensions)
  ? ReviewContextOrchestrator [Core]               (consumes IScmClient: fetches all data for analysis)
  ? GetPullRequestDiffToolHandler [Pure]           (consumes IPullRequestDataProvider)
  ? GetFileDiffToolHandler [Pure]                  (consumes IPullRequestDataProvider)
  ? GetPullRequestMetadataToolHandler [Pure]       (consumes IPullRequestDataProvider)
  ? GetPullRequestFilesToolHandler [Pure]          (consumes IPullRequestDataProvider)
  ? GetFileContentAtRefToolHandler [Pure]          (consumes IFileContentDataProvider)
  ? AnalysisInput [Core]                           (carries IFileContentDataProvider for on-demand content fetching)

IWorkspaceRootProvider [Core interface]
  ? McpWorkspaceRootProvider [Pure]                (implements: resolves repo root from CLI --repo, MCP roots, or localRepoPath)
  ? LocalReviewProvider [Pure]                     (consumes: resolves git repo root for local review)
  ? InitializeMethodHandler [Pure]                 (consumes: stores MCP roots)
  ? ConfigurationResolver [AzureDevOps]            (consumes: workspace root for git detection)

AnalysisInput / AnalysisSection / ReviewContext [Core]
  ? ReviewContextOrchestrator [Core]               (produces AnalysisInput, aggregates AnalysisSections into ReviewContext)
  ? IReviewAnalyzer implementations                (consume AnalysisInput, produce AnalysisSections)

LocalReviewScope / LocalFileStatus
  ? LocalGitClient [Pure]                          (produces LocalFileStatus)
  ? LocalReviewProvider [Pure]                     (consumes: orchestrates git client + diff builder)
  ? GetLocalChangesFilesToolHandler [Pure]          (consumes scope string ? parse ? pass to provider)
  ? GetLocalFileDiffToolHandler [Pure]              (consumes scope string + path ? pass to provider)

LocalReviewFiles
  ? LocalReviewProvider [Pure]                     (produces)
  ? GetLocalChangesFilesToolHandler [Pure]          (consumes: maps to LocalReviewFilesResult)

AzureDevOpsOptions (+ LocalRepoPath) [AzureDevOps]
  ? ConfigurationResolver [AzureDevOps]            (IPostConfigureOptions: merges cached + detected values into options)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes via IOptions<AzureDevOpsOptions>: reads PersonalAccessToken lazily)
  ? AzureDevOpsApiClient [AzureDevOps]             (consumes via IOptions<AzureDevOpsOptions>: reads ProjectName, RepositoryName, sets BaseAddress lazily)
  ? AzureDevOpsScmClient [AzureDevOps]             (consumes via IOptions<AzureDevOpsOptions>: reads org/project/repo for WebUrl/RepositoryFullName)
  ? AuthenticationDelegatingHandler [AzureDevOps]  (consumes IAuthenticationProvider: sets auth header lazily on each request)

IConfiguration
  ? McpWorkspaceRootProvider [Pure]                (consumes: reads AzureDevOps:LocalRepoPath directly to avoid circular dependency)

CliParseResult (+ Pat, Organization, Project, Repository)
  ? Program.Main [Pure]                            (produces via CliArgumentParser.Parse)
  ? Program.RunMcpServerAsync [Pure]               (consumes: reads RepoPath, passes to IWorkspaceRootProvider;
                                                     reads Pat/Organization/Project/Repository, adds as in-memory config overrides)
  ? Program.RunCliCommandAsync [Pure]              (consumes: reads CommandName, dispatches to ICliCommand)

McpRoot / InitializeParams
  ? InitializeMethodHandler [Pure]                 (consumes: extracts roots from initialize request)
  ? IWorkspaceRootProvider [Core]                  (stores: root URIs from MCP client)
  ? McpWorkspaceRootProvider [Pure]                (resolves: repo root from CLI --repo, MCP roots, or localRepoPath)
  ? ConfigurationResolver [AzureDevOps]            (consumes: workspace root for git detection)

DetectedGitInfo
  ? GitRemoteDetector [AzureDevOps]                (produces via synchronous Detect())
  ? ConfigurationResolver [AzureDevOps]            (consumes: fallback for org/project/repo)

CachedConfig
  ? LocalConfigStore [AzureDevOps]                 (produces/consumes: file I/O)
  ? ConfigurationResolver [AzureDevOps]            (consumes: fallback for org/project/repo)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes: cached token; produces: saves new token)

AzureCliProcessHelper [AzureDevOps]
  ? AzureCliTokenProvider [AzureDevOps]            (consumes: resolves az process start args)
  ? InitCommand [Pure]                             (consumes: resolves az process start args for both captured and interactive execution)

AzureCliToken
  ? AzureCliTokenProvider [AzureDevOps]            (produces via GetTokenAsync / ParseTokenResponse)
  ? ChainedAuthenticationProvider [AzureDevOps]    (consumes: acquires token, caches via ILocalConfigStore)
  ? InitCommand [Pure]                             (consumes: uses ParseTokenResponse during init to cache token via LocalConfigStore)

GitHubOptions (+ LocalRepoPath) [GitHub]
  ? GitHubConfigurationResolver [GitHub]           (IPostConfigureOptions: merges cached + detected values into options)
  ? GitHubAuthenticationHandler [GitHub]           (consumes via IOptions<GitHubOptions>: reads PersonalAccessToken lazily)
  ? GitHubApiClient [GitHub]                       (consumes via constructor: reads Owner/RepositoryName for API URLs)
  ? GitHubScmClient [GitHub]                       (consumes via IOptions<GitHubOptions>: reads Owner/RepositoryName for WebUrl/RepositoryFullName)

DetectedGitHubInfo
  ? GitHubRemoteDetector [GitHub]                  (produces via synchronous Detect())
  ? GitHubConfigurationResolver [GitHub]           (consumes: fallback for owner/repo)

GitHubCachedConfig
  ? GitHubConfigStore [GitHub]                     (produces/consumes: file I/O)
  ? GitHubConfigurationResolver [GitHub]           (consumes: fallback for owner/repo)
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
services.AddSingleton<AzureDevOpsFileContentProvider>();

// Unified SCM client facade + interface forwarding
services.AddSingleton<AzureDevOpsScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IFileContentDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
```

## `REBUSS.Pure.GitHub\ServiceCollectionExtensions.cs` ? `AddGitHubProvider(IConfiguration)`

```csharp
// Options + validation + post-configure resolution
services.Configure<GitHubOptions>(configuration.GetSection(GitHubOptions.SectionName));
services.AddSingleton<IValidateOptions<GitHubOptions>, GitHubOptionsValidator>();
services.AddSingleton<IGitHubRemoteDetector, GitHubRemoteDetector>();
services.AddSingleton<IGitHubConfigStore, GitHubConfigStore>();
services.AddSingleton<IPostConfigureOptions<GitHubOptions>, GitHubConfigurationResolver>();

// Authentication handler (Bearer PAT + required GitHub headers)
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
services.AddSingleton<GitHubFileContentProvider>();

// Unified SCM client facade + interface forwarding
services.AddSingleton<GitHubScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<GitHubScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<GitHubScmClient>());
services.AddSingleton<IFileContentDataProvider>(sp => sp.GetRequiredService<GitHubScmClient>());
```

## `REBUSS.Pure\Program.cs` ? `ConfigureServices(IServiceCollection, IConfiguration)`

```csharp
// IConfiguration — registered so McpWorkspaceRootProvider can read LocalRepoPath
services.AddSingleton<IConfiguration>(configuration);

// Logging — also writes to %LOCALAPPDATA%\REBUSS.Pure\server.log
services.AddLogging(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.AddProvider(new FileLoggerProvider(GetLogDirectory()));
    builder.SetMinimumLevel(LogLevel.Debug);
});

// Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

// Shared services (provider-agnostic)
services.AddSingleton<IDiffAlgorithm, LcsDiffAlgorithm>();
services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
services.AddSingleton<IFileClassifier, FileClassifier>();

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
services.AddSingleton<IMcpToolHandler, GetPullRequestDiffToolHandler>();
services.AddSingleton<IMcpToolHandler, GetFileDiffToolHandler>();
services.AddSingleton<IMcpToolHandler, GetPullRequestMetadataToolHandler>();
services.AddSingleton<IMcpToolHandler, GetPullRequestFilesToolHandler>();
services.AddSingleton<IMcpToolHandler, GetFileContentAtRefToolHandler>();

// Local self-review pipeline
services.AddSingleton<ILocalGitClient, LocalGitClient>();
services.AddSingleton<ILocalReviewProvider, LocalReviewProvider>();
services.AddSingleton<IMcpToolHandler, GetLocalChangesFilesToolHandler>();
services.AddSingleton<IMcpToolHandler, GetLocalFileDiffToolHandler>();

// JSON-RPC infrastructure
services.AddSingleton<IJsonRpcSerializer, SystemTextJsonSerializer>();
services.AddSingleton<IJsonRpcTransport>(_ =>
    new StreamJsonRpcTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()));

// Method handlers — each handles one JSON-RPC method (OCP: add new methods without changing McpServer)
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
| **Internal access** | `InternalsVisibleTo("REBUSS.Pure.Tests")` on `REBUSS.Pure`; `InternalsVisibleTo("REBUSS.Pure.Core.Tests")` on `REBUSS.Pure.Core`; `InternalsVisibleTo("REBUSS.Pure")`, `InternalsVisibleTo("REBUSS.Pure.Tests")`, and `InternalsVisibleTo("REBUSS.Pure.AzureDevOps.Tests")` on `REBUSS.Pure.AzureDevOps`; `InternalsVisibleTo("REBUSS.Pure")` and `InternalsVisibleTo("REBUSS.Pure.Tests")` on `REBUSS.Pure.GitHub` |
| **DI pattern** | Constructor injection; all registered as singletons; interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` through the active provider's facade (`AzureDevOpsScmClient` or `GitHubScmClient`); provider-specific registrations via `ServiceCollectionExtensions.AddAzureDevOpsProvider()` or `ServiceCollectionExtensions.AddGitHubProvider()`; exactly one provider is registered per process based on `DetectProvider()` |
| **Architecture** | 5-project: `REBUSS.Pure.Core` (domain models, interfaces, shared logic, analysis pipeline) ? `REBUSS.Pure.AzureDevOps` (Azure DevOps provider) / `REBUSS.Pure.GitHub` (GitHub provider) ? `REBUSS.Pure` (MCP server, tool handlers, local review pipeline, CLI) ? `REBUSS.Pure.Tests` |
| **Error handling** | Custom exceptions in `REBUSS.Pure.Core.Exceptions` (`PullRequestNotFoundException`, `FileNotFoundInPullRequestException`, `FileContentNotFoundException`), caught in tool handlers, returned as `ToolResult.IsError = true` |
| **Logging** | `Microsoft.Extensions.Logging`, stderr output via console provider, file logging via `FileLoggerProvider` |
| **Comments** | XML doc on public types/methods; no inline comments unless complex |
| **Naming** | Standard C# conventions; private fields prefixed with `_`; interfaces prefixed with `I` |
| **File naming** | Interface and class in same-named files (e.g., `IUnifiedDiffBuilder.cs` contains `IStructuredDiffBuilder`) — note: file names may not match type names after refactoring |
| **CLI pattern** | `CliArgumentParser` for parsing, `ICliCommand` for commands; CLI output goes to `Console.Error` (stdout reserved for MCP stdio) |
| **Provider pattern** | Fine-grained providers (e.g. `AzureDevOpsDiffProvider`, `GitHubDiffProvider`) are concrete classes registered directly; the SCM facade (`AzureDevOpsScmClient` or `GitHubScmClient`) implements `IScmClient` and delegates to them; tool handlers depend on narrow `IPullRequestDataProvider`/`IFileContentDataProvider` interfaces from Core |
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
- [ ] DI registration summary matches `Program.cs` `ConfigureServices`, `ServiceCollectionExtensions.AddAzureDevOpsProvider`, and `ServiceCollectionExtensions.AddGitHubProvider`
- [ ] All model types mentioned in the dependency graph exist in the listed paths
- [ ] Section 5 file contents are current (re-read if stale)
- [ ] No legacy duplicate files exist in `REBUSS.Pure` (Azure DevOps code lives exclusively in `REBUSS.Pure.AzureDevOps`)
