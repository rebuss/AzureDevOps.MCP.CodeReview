# CODEBASE CONTEXT PROVIDED

Full codebase context is included below (file-role map, dependency graph, DI registrations, conventions, and current file contents for all files in scope). Skip all exploratory analysis — do not call get_projects_in_solution, get_files_in_project, or read files already provided. Proceed directly to planning and implementation using only the supplied context. If a file outside the provided set might be affected by a model or interface change, read it before editing.

---

# 1. File-Role Map

## Solution structure

| Project | Path | Purpose |
|---|---|---|
| REBUSS.Pure | `REBUSS.Pure\REBUSS.Pure.csproj` | MCP server (console app, .NET 8) |
| REBUSS.Pure.Tests | `REBUSS.Pure.Tests\REBUSS.Pure.Tests.csproj` | Unit + integration tests (xUnit, NSubstitute) |

---

## Source files

### Domain models

| File | Role | Key types | Consumed by |
|---|---|---|---|
| `REBUSS.Pure\Services\Common\Models\PullRequestDiff.cs` | Core diff domain model | `PullRequestDiff`, `FileChange`, `DiffHunk`, `DiffLine` | AzureDevOpsDiffProvider, AzureDevOpsFilesProvider, GetPullRequestDiffToolHandler, GetFileDiffToolHandler, StructuredDiffResult mapping, EndToEndTests, all diff/file-related tests |
| `REBUSS.Pure\Services\Common\Models\PullRequestMetadata.cs` | Parsed PR metadata (lightweight) | `PullRequestMetadata` (record) | AzureDevOpsDiffProvider, PullRequestMetadataParser |
| `REBUSS.Pure\Services\Common\Models\FullPullRequestMetadata.cs` | Rich PR metadata (all fields) | `FullPullRequestMetadata` | AzureDevOpsMetadataProvider, GetPullRequestMetadataToolHandler |
| `REBUSS.Pure\Services\Common\Models\IterationInfo.cs` | Iteration commit SHAs | `IterationInfo` (record) | AzureDevOpsDiffProvider, AzureDevOpsMetadataProvider, IterationInfoParser |
| `REBUSS.Pure\Services\Common\DiffEdit.cs` | Line-level edit operation | `DiffEdit` (readonly record struct) | LcsDiffAlgorithm, StructuredDiffBuilder |
| `REBUSS.Pure\Services\FileList\Classification\FileClassification.cs` | File classification result | `FileClassification`, `FileCategory` (enum) | AzureDevOpsFilesProvider, AzureDevOpsDiffProvider |
| `REBUSS.Pure\Services\FileList\Models\PullRequestFiles.cs` | File list result models | `PullRequestFiles`, `PullRequestFileInfo`, `PullRequestFilesSummary` | AzureDevOpsFilesProvider, GetPullRequestFilesToolHandler |
| `REBUSS.Pure\Services\Content\Models\FileContent.cs` | File content result | `FileContent` | AzureDevOpsFileContentProvider, GetFileContentAtRefToolHandler |

### Diff pipeline

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\Common\IDiffAlgorithm.cs` | Interface: line-level diff algorithm | `DiffEdit` |
| `REBUSS.Pure\Services\Common\LcsDiffAlgorithm.cs` | LCS-based O(m×n) diff algorithm | `IDiffAlgorithm`, `DiffEdit` |
| `REBUSS.Pure\Services\Common\IUnifiedDiffBuilder.cs` | Interface: `IStructuredDiffBuilder` — produces `List<DiffHunk>` | `DiffHunk` |
| `REBUSS.Pure\Services\Common\UnifiedDiffBuilder.cs` | Implementation: `StructuredDiffBuilder` — builds structured hunks | `IStructuredDiffBuilder`, `IDiffAlgorithm`, `DiffHunk`, `DiffLine`, `DiffEdit` |
| `REBUSS.Pure\Services\Diff\IPullRequestDiffProvider.cs` | Interface: PR diff provider | `PullRequestDiff` |
| `REBUSS.Pure\Services\Diff\AzureDevOpsDiffProvider.cs` | Orchestrates fetching PR data + building diffs | `IPullRequestDiffProvider`, `IStructuredDiffBuilder`, `IFileClassifier`, `IAzureDevOpsApiClient`, parsers, `PullRequestDiff`, `FileChange`, `DiffHunk` |

### File list pipeline

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\FileList\IPullRequestFilesProvider.cs` | Interface: PR file list provider | `PullRequestFiles` |
| `REBUSS.Pure\Services\FileList\AzureDevOpsFilesProvider.cs` | Builds classified file list from diff provider output | `IPullRequestDiffProvider`, `IFileClassifier`, `FileChange` (uses `.Additions`, `.Deletions`) |
| `REBUSS.Pure\Services\FileList\Classification\IFileClassifier.cs` | Interface: file classifier | `FileClassification` |
| `REBUSS.Pure\Services\FileList\Classification\FileClassifier.cs` | Classifies files by path/extension | `IFileClassifier`, `FileClassification`, `FileCategory` |

### Content pipeline

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\Content\IFileContentProvider.cs` | Interface: file content at ref | `FileContent` |
| `REBUSS.Pure\Services\Content\AzureDevOpsFileContentProvider.cs` | Fetches file content at specific Git ref | `IAzureDevOpsApiClient`, `FileContent` |

### Local review pipeline

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\LocalReview\ILocalGitClient.cs` | Interface: local git operations; defines `LocalFileStatus` record | — |
| `REBUSS.Pure\Services\LocalReview\LocalGitClient.cs` | Runs git child processes; uses `diff --name-status` for all scopes; exposes `WorkingTreeRef` sentinel for filesystem reads | `ILocalGitClient` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewScope.cs` | Value type: `WorkingTree`, `Staged`, `BranchDiff(base)` + `Parse(string?)` | — |
| `REBUSS.Pure\Services\LocalReview\ILocalReviewProvider.cs` | Interface: lists local files + diffs; defines `LocalReviewFiles` model | `PullRequestDiff`, `PullRequestFileInfo`, `PullRequestFilesSummary` |
| `REBUSS.Pure\Services\LocalReview\LocalReviewProvider.cs` | Orchestrates git client + diff builder + file classifier | `IWorkspaceRootProvider`, `ILocalGitClient`, `IStructuredDiffBuilder`, `IFileClassifier`, domain models |
| `REBUSS.Pure\Services\LocalReview\LocalReviewExceptions.cs` | `LocalRepositoryNotFoundException`, `LocalFileNotFoundException`, `GitCommandException` | — |

### Metadata pipeline

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Services\Metadata\IPullRequestMetadataProvider.cs` | Interface: PR metadata provider | `FullPullRequestMetadata` |
| `REBUSS.Pure\Services\Metadata\AzureDevOpsMetadataProvider.cs` | Fetches full PR metadata from multiple endpoints | `IAzureDevOpsApiClient`, parsers, `FullPullRequestMetadata` |

### Parsers

| File | Role |
|---|---|
| `REBUSS.Pure\Services\Common\Parsers\IPullRequestMetadataParser.cs` | Interface: parses PR details JSON |
| `REBUSS.Pure\Services\Common\Parsers\PullRequestMetadataParser.cs` | Parses Azure DevOps PR details JSON → `PullRequestMetadata` / `FullPullRequestMetadata` |
| `REBUSS.Pure\Services\Common\Parsers\IIterationInfoParser.cs` | Interface: parses iterations JSON |
| `REBUSS.Pure\Services\Common\Parsers\IterationInfoParser.cs` | Parses iterations JSON → `IterationInfo` |
| `REBUSS.Pure\Services\Common\Parsers\IFileChangesParser.cs` | Interface: parses file changes JSON |
| `REBUSS.Pure\Services\Common\Parsers\FileChangesParser.cs` | Parses iteration changes JSON → `List<FileChange>` |

### Exceptions

| File | Role |
|---|---|
| `REBUSS.Pure\Services\Common\PullRequestNotFoundException.cs` | PR not found (404) |
| `REBUSS.Pure\Services\Common\FileNotFoundInPullRequestException.cs` | File not in PR |
| `REBUSS.Pure\Services\Common\FileContentNotFoundException.cs` | File content not found at ref |

### MCP tool handlers (business logic)

| File | Role | Depends on |
|---|---|---|
| `REBUSS.Pure\Tools\GetPullRequestDiffToolHandler.cs` | `get_pr_diff` — returns structured JSON with per-file hunks | `IPullRequestDiffProvider`, `StructuredDiffResult` models, `FileChange` → `StructuredFileChange` mapping |
| `REBUSS.Pure\Tools\GetFileDiffToolHandler.cs` | `get_file_diff` — returns structured JSON for a single file | Same as above (uses `GetFileDiffAsync`) |
| `REBUSS.Pure\Tools\GetPullRequestMetadataToolHandler.cs` | `get_pr_metadata` — returns PR metadata JSON | `IPullRequestMetadataProvider`, `PullRequestMetadataResult` models |
| `REBUSS.Pure\Tools\GetPullRequestFilesToolHandler.cs` | `get_pr_files` — returns classified file list JSON | `IPullRequestFilesProvider`, `PullRequestFilesResult` models |
| `REBUSS.Pure\Tools\GetFileContentAtRefToolHandler.cs` | `get_file_content_at_ref` — returns file content JSON | `IFileContentProvider`, `FileContentAtRefResult` model |
| `REBUSS.Pure\Tools\GetLocalChangesFilesToolHandler.cs` | `get_local_files` — lists locally changed files with classification | `ILocalReviewProvider`, `LocalReviewFilesResult` model |
| `REBUSS.Pure\Tools\GetLocalFileDiffToolHandler.cs` | `get_local_file_diff` — returns structured diff for a single local file | `ILocalReviewProvider`, `StructuredDiffResult` models |

### Tool output models (JSON DTOs)

| File | Role |
|---|---|
| `REBUSS.Pure\Tools\Models\StructuredDiffResult.cs` | `StructuredDiffResult`, `StructuredFileChange`, `StructuredHunk`, `StructuredLine` — diff tool JSON output (shared by PR and local tools); `PrNumber` is `int?` (null for local diffs, omitted from JSON) |
| `REBUSS.Pure\Tools\Models\PullRequestMetadataResult.cs` | `PullRequestMetadataResult`, `AuthorInfo`, `RefInfo`, `PrStats`, `DescriptionInfo`, `SourceInfo` |
| `REBUSS.Pure\Tools\Models\PullRequestFilesResult.cs` | `PullRequestFilesResult`, `PullRequestFileItem`, `PullRequestFilesSummaryResult` (also reused by `LocalReviewFilesResult`) |
| `REBUSS.Pure\Tools\Models\FileContentAtRefResult.cs` | `FileContentAtRefResult` |
| `REBUSS.Pure\Tools\Models\LocalReviewFilesResult.cs` | `LocalReviewFilesResult` — JSON output for `get_local_files`; includes `repositoryRoot`, `scope`, `currentBranch` context fields |

### MCP infrastructure (JSON-RPC server)

| File | Role |
|---|---|
| `REBUSS.Pure\Mcp\McpServer.cs` | Main server loop: reads JSON-RPC over stdio, dispatches to method handlers; silently ignores notifications (messages without `id`) for unregistered methods per MCP/JSON-RPC spec — `notifications/initialized` and similar are not errored |
| `REBUSS.Pure\Mcp\IMcpMethodHandler.cs` | Interface: handles one JSON-RPC method |
| `REBUSS.Pure\Mcp\IMcpToolHandler.cs` | Interface: MCP tool (definition + execution) |
| `REBUSS.Pure\Mcp\IWorkspaceRootProvider.cs` | Interface: stores CLI repo path, MCP roots, resolves repository root path |
| `REBUSS.Pure\Mcp\McpWorkspaceRootProvider.cs` | Implementation: resolves repo root from CLI `--repo` (highest priority), MCP roots, or `localRepoPath` config; guards against unexpanded variables (e.g. `${workspaceFolder}` passed literally by Visual Studio); reads `LocalRepoPath` directly from `IConfiguration` to avoid circular dependency with `IPostConfigureOptions<AzureDevOpsOptions>`; `FindGitRepositoryRoot` accepts nullable `string?` and returns `null` for null/empty input |
| `REBUSS.Pure\Mcp\Handlers\InitializeMethodHandler.cs` | `initialize` method handler — extracts MCP roots, stores via `IWorkspaceRootProvider` |
| `REBUSS.Pure\Mcp\Handlers\ToolsListMethodHandler.cs` | `tools/list` method handler |
| `REBUSS.Pure\Mcp\Handlers\ToolsCallMethodHandler.cs` | `tools/call` method handler — resolves tool by name, delegates |
| `REBUSS.Pure\Mcp\IJsonRpcSerializer.cs` | Interface: JSON-RPC serialization |
| `REBUSS.Pure\Mcp\SystemTextJsonSerializer.cs` | System.Text.Json implementation (camelCase, no indent, ignore nulls) |
| `REBUSS.Pure\Mcp\IJsonRpcTransport.cs` | Interface: read/write JSON-RPC messages |
| `REBUSS.Pure\Mcp\StreamJsonRpcTransport.cs` | Newline-delimited stream transport |
| `REBUSS.Pure\Mcp\McpMethodNotFoundException.cs` | Method not found exception |

### MCP models (JSON-RPC protocol)

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

### Azure DevOps integration

| File | Role |
|---|---|
| `REBUSS.Pure\AzureDevOpsIntegration\Services\IAzureDevOpsApiClient.cs` | Interface: Azure DevOps REST API client |
| `REBUSS.Pure\AzureDevOpsIntegration\Services\AzureDevOpsApiClient.cs` | HTTP client for Azure DevOps; sets BaseAddress lazily in constructor from `IOptions<AzureDevOpsOptions>`, auth delegated to `AuthenticationDelegatingHandler`; `GetStringAsync` detects HTML responses on 2xx status codes (e.g. 203) as authentication failures and throws `HttpRequestException` with `Unauthorized` status code and actionable message; `IsHtmlResponse` is `internal static` for testability — checks for `text/html` content type or body starting with `<!doctype`/`<html` (not just any `<`) |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\AzureDevOpsOptions.cs` | Config model: org, project, repo, PAT, LocalRepoPath (all optional) |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\AzureDevOpsOptionsValidator.cs` | Validates config field format (all fields optional) |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\IGitRemoteDetector.cs` | Interface + `DetectedGitInfo` record: detects Azure DevOps repo from Git remote; supports `Detect()` and `Detect(string repositoryPath)` |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\GitRemoteDetector.cs` | Parses HTTPS/SSH Azure DevOps remote URLs via `git remote get-url origin`; tries current directory and walks up from executable location to find repo root; `FindGitRepositoryRoot` accepts nullable `string?` and returns `null` for null/empty input |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\ILocalConfigStore.cs` | Interface + `CachedConfig` model: persists/retrieves cached config |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\LocalConfigStore.cs` | JSON file store under `%LOCALAPPDATA%/REBUSS.Pure/config.json` |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\IAuthenticationProvider.cs` | Interface: provides `AuthenticationHeaderValue` for API calls; `InvalidateCachedToken()` clears the cached token so the next request re-acquires via Azure CLI |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\IAzureCliTokenProvider.cs` | Interface: acquires Azure DevOps token via Azure CLI; defines `AzureCliToken` record |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\AzureCliProcessHelper.cs` | `internal static` helper: resolves the correct `ProcessStartInfo` file name and arguments for running Azure CLI commands cross-platform; on Windows wraps via `cmd.exe /c az ...` (because `az.cmd` is not resolved by `Process.Start` with `UseShellExecute = false`); on Linux/macOS calls `az` directly |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\AzureCliTokenProvider.cs` | Runs `az account get-access-token --resource 499b84ac...` to acquire Bearer tokens via `AzureCliProcessHelper`; `ParseTokenResponse` is `internal static` for testability; uses `CultureInfo.InvariantCulture` for locale-safe date parsing; reads stdout/stderr concurrently to prevent pipe deadlock |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\ChainedAuthenticationProvider.cs` | Auth chain: PAT → cached token (`Basic`/PAT tokens with `null` expiry are always valid since PAT expiry is managed in Azure DevOps; `Bearer` tokens with `null` expiry fall through to Azure CLI refresh; both types respect `TokenExpiresOn` when present) → Azure CLI (`az account get-access-token`) → error with `az login` and PAT instructions; `InvalidateCachedToken()` clears token fields in the config store; `BuildAuthRequiredMessage` produces clear user-facing error with both `az login` and PAT config options |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\AuthenticationDelegatingHandler.cs` | `DelegatingHandler` that lazily sets auth header on each outgoing HTTP request via `IAuthenticationProvider`; when the response is HTTP 203 with `text/html` content-type (Azure DevOps CDN auth redirect), invalidates the cached token via `IAuthenticationProvider.InvalidateCachedToken()` and retries the request once with a fresh token |
| `REBUSS.Pure\AzureDevOpsIntegration\Configuration\ConfigurationResolver.cs` | `IPostConfigureOptions<AzureDevOpsOptions>` impl: merges explicit config, cached, and auto-detected values; returns empty strings for unresolved fields (no throw), skips caching when incomplete; uses `IWorkspaceRootProvider` for repo path resolution |

### CLI infrastructure

| File | Role |
|---|---|
| `REBUSS.Pure\Cli\CliArgumentParser.cs` | Parses CLI args: detects `init` command vs server mode, extracts `--repo`, `--pat`, `--org`, `--project`, `--repository` |
| `REBUSS.Pure\Cli\ICliCommand.cs` | Interface: executable CLI command |
| `REBUSS.Pure\Cli\InitCommand.cs` | `init` command: detects IDE by presence of `.vscode/`, `*.code-workspace` (VS Code) and/or `.vs/`, `*.sln` (Visual Studio) in the git root; target selection: only `.vscode` → VS Code only; only `.vs` → Visual Studio only; both or neither → both; when no `--pat` is provided, attempts Azure CLI authentication: first checks if `az` is installed via `IsAzCliInstalledAsync` (`az --version`); if not installed, prompts user with `[y/N]` to install (on Windows via `winget install -e --id Microsoft.AzureCLI`, on Linux/macOS via `curl \| bash`); after install verification, checks for existing `az` session, runs `az login` **interactively** via `RunAzLoginInteractiveAsync` (inherits parent console so the browser can open), acquires and caches an Azure DevOps token via `AzureCliTokenProvider.ParseTokenResponse`; `CacheAzureCliToken` delegates to `LocalConfigStore` (eliminates duplicated JSON serialization); `RunProcessAsync` reads stdout/stderr concurrently to prevent pipe deadlock; on login failure displays a prominent `AUTHENTICATION NOT CONFIGURED` banner via `WriteAuthFailureBannerAsync` with retry instructions (`rebuss-pure init`), `appsettings.Local.json` path/example, PAT creation link, and `--pat` inline option; if a config file already exists, **merges** the `REBUSS.Pure` server entry into the existing `servers` object (preserving other servers and top-level properties) via `MergeConfigContent`; falls back to full overwrite when existing file is not valid JSON; `MergeConfigContent` carries over existing `--pat` value when no new PAT is provided via `ExtractExistingPat`; copies embedded review prompt files (`review-pr.md`, `self-review.md`) to `.github/prompts/` (skips existing files); accepts `TextReader` for user input and optional `Func<string, CancellationToken, Task<...>>` process runner for testability; exposes `ResolveConfigTargets`, `DetectsVsCode`, `DetectsVisualStudio`, `BuildConfigContent`, `MergeConfigContent`, `RunProcessAsync`, `RunInteractiveProcessAsync` as `internal static` |
| `REBUSS.Pure\Cli\Prompts\review-pr.md` | Embedded resource: PR review prompt template (bundled into assembly, copied by `init`) |
| `REBUSS.Pure\Cli\Prompts\self-review.md` | Embedded resource: self-review prompt template (bundled into assembly, copied by `init`) |

### Logging

| File | Role |
|---|---|
| `REBUSS.Pure\Logging\FileLoggerProvider.cs` | `ILoggerProvider` that writes to daily-rotated files (`server-yyyy-MM-dd.log`) under the log directory; rolls to a new file after midnight; deletes files older than 3 days on startup and on each roll-over; accepts an optional `Func<DateTime>` for testability |

### Entry point

| File | Role |
|---|---|
| `REBUSS.Pure\Program.cs` | DI composition root; dual-mode: CLI commands (`init`) or MCP server; parses `--repo` argument and passes it to `IWorkspaceRootProvider` before server starts |
### Documentation

| File | Role |
|---|---|
| `README.md` | Project documentation |
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
| `REBUSS.Pure.Tests\Integration\EndToEndTests.cs` | Full JSON-RPC pipeline: request → McpServer → handler → response |
| `REBUSS.Pure.Tests\Mcp\McpServerTests.cs` | `McpServer` — initialize, tools/list, tools/call, unknown method, invalid JSON, empty lines, notifications without id silently ignored, notifications don’t affect subsequent requests, unknown method with id still returns error |
| `REBUSS.Pure.Tests\Mcp\InitializeMethodHandlerTests.cs` | `InitializeMethodHandler` — roots extraction, storage, edge cases |
| `REBUSS.Pure.Tests\Mcp\McpWorkspaceRootProviderTests.cs` | `McpWorkspaceRootProvider` — URI conversion, repo root resolution, MCP roots, localRepoPath fallback, CLI `--repo` precedence, unexpanded variable guard |
| `REBUSS.Pure.Tests\Cli\CliArgumentParserTests.cs` | `CliArgumentParser` — server mode, `--repo`, `--pat`, `--org`, `--project`, `--repository`, `init` command, combined args, edge cases |
| `REBUSS.Pure.Tests\Cli\InitCommandTests.cs` | `InitCommand` — generates `mcp.json` for correct IDE target(s), copies prompt files to `.github/prompts/`, error cases, subdirectory support, skip-existing prompts, PAT carry-over in `MergeConfigContent`, Azure CLI login during init (existing session reuse, interactive login, login failure displays auth banner with retry/PAT instructions, PAT skips login), Azure CLI installation prompt (detects missing az, prompts user y/n, installs on confirm, shows manual install hint on failure, skips prompt when az is installed) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureDevOpsOptionsTests.cs` | Options validation (format-only, all fields optional) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureDevOpsApiClientTests.cs` | API client — URL construction, status codes, file content, version descriptor resolution, HTML response detection (203 with HTML content type, HTML body without content type, valid JSON not flagged, XML body not flagged as HTML) |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\GitRemoteDetectorTests.cs` | `GitRemoteDetector.ParseRemoteUrl` — HTTPS, SSH, GitHub, edge cases; `FindGitRepositoryRoot`, `GetCandidateDirectories` |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\ConfigurationResolverTests.cs` | `ConfigurationResolver` — PostConfigure precedence, fallback, caching, mixed sources, Resolve static method |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\ChainedAuthenticationProviderTests.cs` | `ChainedAuthenticationProvider` — PAT precedence, cached tokens, Azure CLI token acquisition and caching, expired token fallback to Azure CLI, null expiry falls through to Azure CLI refresh (not used as valid), `InvalidateCachedToken` clears cache fields, `BuildAuthRequiredMessage` content validation |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureCliProcessHelperTests.cs` | `AzureCliProcessHelper.GetProcessStartArgs` — Windows `cmd.exe /c az` wrapping, Linux direct `az` invocation |
| `REBUSS.Pure.Tests\AzureDevOpsIntegration\AzureCliTokenProviderTests.cs` | `AzureCliTokenProvider.ParseTokenResponse` — valid JSON, missing/empty token, missing expiry, ISO 8601 dates, resource ID constant |

---

# 2. Cross-Cutting Dependency Graph (Models Only)

```
PullRequestDiff (+ FileChange, DiffHunk, DiffLine)
  → AzureDevOpsDiffProvider         (produces: populates FileChange.Hunks/Additions/Deletions/SkipReason)
  → AzureDevOpsFilesProvider        (consumes: reads FileChange.Additions, .Deletions, .Path, .ChangeType)
  → GetPullRequestDiffToolHandler   (consumes: maps FileChange → StructuredFileChange)
  → GetFileDiffToolHandler          (consumes: maps FileChange → StructuredFileChange)
  → StructuredDiffResult            (tool output DTO: mirrors domain hunks)
  → EndToEndTests                   (creates test data)
  → AzureDevOpsDiffProviderTests    (creates test data, asserts on Hunks/SkipReason/Additions/Deletions)
  → AzureDevOpsFilesProviderTests   (creates test data with Additions/Deletions)
  → GetPullRequestDiffToolHandlerTests  (creates SampleDiff)
  → GetFileDiffToolHandlerTests         (creates SampleFileDiff)

PullRequestMetadata
  → PullRequestMetadataParser       (produces)
  → AzureDevOpsDiffProvider         (consumes: BuildDiff)

FullPullRequestMetadata
  → PullRequestMetadataParser       (produces via ParseFull)
  → AzureDevOpsMetadataProvider     (consumes + enriches)
  → GetPullRequestMetadataToolHandler (consumes: maps to PullRequestMetadataResult)

IterationInfo
  → IterationInfoParser             (produces)
  → AzureDevOpsDiffProvider         (consumes: base/target commit SHAs)
  → AzureDevOpsMetadataProvider     (consumes: fallback commit SHAs)

FileClassification / FileCategory
  → FileClassifier                  (produces)
  → AzureDevOpsFilesProvider        (consumes: BuildFileInfo, BuildSummary)
  → AzureDevOpsDiffProvider         (consumes: GetSkipReason)

DiffEdit
  → LcsDiffAlgorithm               (produces)
  → StructuredDiffBuilder           (consumes: ComputeHunks, FormatHunk)

FileContent
  → AzureDevOpsFileContentProvider  (produces)
  → GetFileContentAtRefToolHandler  (consumes)

LocalReviewScope / LocalFileStatus
  → LocalGitClient                  (produces LocalFileStatus)
  → LocalReviewProvider             (consumes: orchestrates git client + diff builder)
  → GetLocalChangesFilesToolHandler (consumes scope string → parse → pass to provider)
  → GetLocalFileDiffToolHandler     (consumes scope string + path → pass to provider)

LocalReviewFiles
  → LocalReviewProvider             (produces)
  → GetLocalChangesFilesToolHandler (consumes: maps to LocalReviewFilesResult)

PullRequestDiff (reused for local diffs)
  → LocalReviewProvider             (produces for GetFileDiffAsync)
  → GetLocalFileDiffToolHandler     (consumes: maps to StructuredDiffResult)

PullRequestFiles / PullRequestFileInfo / PullRequestFilesSummary
  → AzureDevOpsFilesProvider        (produces)
  → GetPullRequestFilesToolHandler  (consumes)

AzureDevOpsOptions (+ LocalRepoPath)
→ ConfigurationResolver           (IPostConfigureOptions: merges cached + detected values into options)
→ ChainedAuthenticationProvider   (consumes via IOptions<AzureDevOpsOptions>: reads PersonalAccessToken lazily)
→ AzureDevOpsApiClient            (consumes via IOptions<AzureDevOpsOptions>: reads ProjectName, RepositoryName, sets BaseAddress lazily)
→ AuthenticationDelegatingHandler (consumes IAuthenticationProvider: sets auth header lazily on each request)
→ Program.cs                      (lazy resolution via IOptions<AzureDevOpsOptions>.Value)

IConfiguration
  → McpWorkspaceRootProvider        (consumes: reads AzureDevOps:LocalRepoPath directly to avoid circular dependency)

CliParseResult (+ Pat, Organization, Project, Repository)
→ Program.Main                    (produces via CliArgumentParser.Parse)
→ Program.RunMcpServerAsync       (consumes: reads RepoPath, passes to IWorkspaceRootProvider;
                                    reads Pat/Organization/Project/Repository, adds as in-memory config overrides)
→ Program.RunCliCommandAsync      (consumes: reads CommandName, dispatches to ICliCommand)

McpRoot / InitializeParams
  → InitializeMethodHandler         (consumes: extracts roots from initialize request)
  → IWorkspaceRootProvider          (stores: root URIs from MCP client)
  → McpWorkspaceRootProvider        (resolves: repo root from CLI --repo, MCP roots, or localRepoPath)
  → ConfigurationResolver           (consumes: workspace root for git detection)

DetectedGitInfo
  → GitRemoteDetector               (produces via synchronous Detect())
  → ConfigurationResolver           (consumes: fallback for org/project/repo)

CachedConfig
  → LocalConfigStore                (produces/consumes: file I/O)
  → ConfigurationResolver           (consumes: fallback for org/project/repo)
  → ChainedAuthenticationProvider   (consumes: cached token; produces: saves new token)

AzureCliProcessHelper
  → AzureCliTokenProvider           (consumes: resolves az process start args)
  → InitCommand                     (consumes: resolves az process start args for both captured and interactive execution)

AzureCliToken
→ AzureCliTokenProvider           (produces via GetTokenAsync / ParseTokenResponse)
→ ChainedAuthenticationProvider   (consumes: acquires token, caches via ILocalConfigStore)
→ InitCommand                     (consumes: uses ParseTokenResponse during init to cache token via LocalConfigStore)
```

---

# 3. DI Registration Summary

From `REBUSS.Pure\Program.cs` → `ConfigureServices` and `RunMcpServerAsync`:

```csharp
// IConfiguration — registered so McpWorkspaceRootProvider can read LocalRepoPath
// directly without going through IOptions<AzureDevOpsOptions> (avoids circular dependency)
services.AddSingleton<IConfiguration>(configuration);

// Logging — also writes to %LOCALAPPDATA%\REBUSS.Pure\server.log for clients that don't expose stderr
services.AddLogging(builder =>
{
    builder.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
    builder.AddProvider(new FileLoggerProvider(GetLogFilePath()));
    builder.SetMinimumLevel(LogLevel.Debug);
});

// Configuration (all fields optional)
services.Configure<AzureDevOpsOptions>(configuration.GetSection(AzureDevOpsOptions.SectionName));
services.AddSingleton<IValidateOptions<AzureDevOpsOptions>, AzureDevOpsOptionsValidator>();

// Workspace root provider: resolves repository path from CLI --repo, MCP roots, or localRepoPath
services.AddSingleton<IWorkspaceRootProvider, McpWorkspaceRootProvider>();

// Configuration resolution: merges explicit config, cached, and auto-detected values
// via IPostConfigureOptions — runs automatically on first IOptions<AzureDevOpsOptions>.Value access
services.AddSingleton<IGitRemoteDetector, GitRemoteDetector>();
services.AddSingleton<ILocalConfigStore, LocalConfigStore>();
services.AddSingleton<IPostConfigureOptions<AzureDevOpsOptions>, ConfigurationResolver>();

// Authentication provider (chained: PAT → cached token → Azure CLI → error)
services.AddSingleton<IAzureCliTokenProvider, AzureCliTokenProvider>();
services.AddSingleton<IAuthenticationProvider, ChainedAuthenticationProvider>();
services.AddTransient<AuthenticationDelegatingHandler>();

// HTTP client — BaseAddress set lazily in AzureDevOpsApiClient constructor,
// auth header set lazily per-request by AuthenticationDelegatingHandler
services.AddHttpClient<IAzureDevOpsApiClient, AzureDevOpsApiClient>()
    .AddHttpMessageHandler<AuthenticationDelegatingHandler>()
    .AddStandardResilienceHandler();

// Parsers
services.AddSingleton<IPullRequestMetadataParser, PullRequestMetadataParser>();
services.AddSingleton<IIterationInfoParser, IterationInfoParser>();
services.AddSingleton<IFileChangesParser, FileChangesParser>();

// Diff pipeline
services.AddSingleton<IDiffAlgorithm, LcsDiffAlgorithm>();
services.AddSingleton<IStructuredDiffBuilder, StructuredDiffBuilder>();
services.AddSingleton<IPullRequestDiffProvider, AzureDevOpsDiffProvider>();

// Metadata pipeline
services.AddSingleton<IPullRequestMetadataProvider, AzureDevOpsMetadataProvider>();

// File list pipeline
services.AddSingleton<IFileClassifier, FileClassifier>();
services.AddSingleton<IPullRequestFilesProvider, AzureDevOpsFilesProvider>();

// Content pipeline
services.AddSingleton<IFileContentProvider, AzureDevOpsFileContentProvider>();

// MCP tool handlers
services.AddSingleton<IMcpToolHandler, GetPullRequestDiffToolHandler>();
services.AddSingleton<IMcpToolHandler, GetFileDiffToolHandler>();
services.AddSingleton<IMcpToolHandler, GetPullRequestMetadataToolHandler>();
services.AddSingleton<IMcpToolHandler, GetPullRequestFilesToolHandler>();
services.AddSingleton<IMcpToolHandler, GetFileContentAtRefToolHandler>();

// Local self-review pipeline (no Azure DevOps required)
services.AddSingleton<ILocalGitClient, LocalGitClient>();
services.AddSingleton<ILocalReviewProvider, LocalReviewProvider>();
services.AddSingleton<IMcpToolHandler, GetLocalChangesFilesToolHandler>();
services.AddSingleton<IMcpToolHandler, GetLocalFileDiffToolHandler>();

// JSON-RPC infrastructure
services.AddSingleton<IJsonRpcSerializer, SystemTextJsonSerializer>();
services.AddSingleton<IJsonRpcTransport>(_ =>
    new StreamJsonRpcTransport(Console.OpenStandardInput(), Console.OpenStandardOutput()));

// Method handlers
services.AddSingleton<IMcpMethodHandler, InitializeMethodHandler>();
services.AddSingleton<IMcpMethodHandler, ToolsListMethodHandler>();
services.AddSingleton<IMcpMethodHandler, ToolsCallMethodHandler>();

// Server
services.AddSingleton<McpServer>(...);

// In RunMcpServerAsync: CLI arguments (--pat, --org, --project, --repository) are
// collected into a Dictionary and added via AddInMemoryCollection to the configuration
// builder AFTER environment variables, giving them highest priority.
// CLI --repo is applied after building the service provider:
// if parseResult.RepoPath is not null, it's set via IWorkspaceRootProvider.SetCliRepositoryPath
// before server.RunAsync is called.
```

---

# 4. Conventions Snapshot

| Aspect | Value |
|---|---|
| **Target framework** | .NET 8 (`net8.0`) |
| **C# version** | 12.0 (implicit via .NET 8 SDK) |
| **Nullable context** | `enable` (project-wide) |
| **Implicit usings** | `enable` |
| **Test framework** | xUnit 2.9.3 |
| **Mocking library** | NSubstitute 5.3.0 |
| **JSON library** | System.Text.Json 9.0.0 |
| **JSON naming policy** | `camelCase` via `JsonNamingPolicy.CamelCase` in tool handlers; explicit `[JsonPropertyName]` on all DTO properties |
| **JSON null handling** | `JsonIgnoreCondition.WhenWritingNull` |
| **Internal access** | `InternalsVisibleTo("REBUSS.Pure.Tests")` |
| **DI pattern** | Constructor injection, all registered as singletons |
| **Architecture** | Layered: Tools → Services → Azure DevOps API; domain models separate from tool output DTOs |
| **Error handling** | Custom exceptions (`PullRequestNotFoundException`, etc.), caught in tool handlers, returned as `ToolResult.IsError = true` |
| **Logging** | `Microsoft.Extensions.Logging`, stderr output via console provider |
| **Comments** | XML doc on public types/methods; no inline comments unless complex |
| **Naming** | Standard C# conventions; private fields prefixed with `_`; interfaces prefixed with `I` |
| **File naming** | Interface and class in same-named files (e.g., `IUnifiedDiffBuilder.cs` contains `IStructuredDiffBuilder`) — note: file names may not match type names after refactoring |
| **CLI pattern** | `CliArgumentParser` for parsing, `ICliCommand` for commands; CLI output goes to `Console.Error` (stdout reserved for MCP stdio) |

---

# 5. Current File Contents

## Domain Models

### `REBUSS.Pure\Services\Common\Models\PullRequestDiff.cs`

```csharp
namespace REBUSS.Pure.Services.Common.Models
{
    /// <summary>
    /// Represents a Pull Request diff with all relevant information.
    /// </summary>
    public class PullRequestDiff
    {
        public string Title { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string SourceBranch { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public string SourceRefName { get; set; } = string.Empty;
        public string TargetRefName { get; set; } = string.Empty;
        public List<FileChange> Files { get; set; } = new();
    }

    /// <summary>
    /// Represents a single file change in a PR.
    /// </summary>
    public class FileChange
    {
        public string Path { get; set; } = string.Empty;
        public string ChangeType { get; set; } = string.Empty;
        public List<DiffHunk> Hunks { get; set; } = new();

        /// <summary>
        /// When set, indicates that diff generation was skipped and explains why
        /// (e.g. "file deleted", "binary file", "generated file", "file renamed", "full file rewrite").
        /// <c>null</c> means the diff was computed normally.
        /// </summary>
        public string? SkipReason { get; set; }

        public int Additions { get; set; }
        public int Deletions { get; set; }
    }

    /// <summary>
    /// Represents a single hunk in a structured diff.
    /// </summary>
    public class DiffHunk
    {
        public int OldStart { get; set; }
        public int OldCount { get; set; }
        public int NewStart { get; set; }
        public int NewCount { get; set; }
        public List<DiffLine> Lines { get; set; } = new();
    }

    /// <summary>
    /// Represents a single line operation within a diff hunk.
    /// </summary>
    public class DiffLine
    {
        /// <summary>
        /// ' ' = unchanged context, '-' = removed, '+' = added.
        /// </summary>
        public char Op { get; set; }

        public string Text { get; set; } = string.Empty;
    }
}
```

### `REBUSS.Pure\Services\Common\DiffEdit.cs`

```csharp
namespace REBUSS.Pure.Services.Common;

/// <summary>
/// Represents a single edit operation produced by a diff algorithm.
/// Kind: ' ' = unchanged context, '-' = removed from old, '+' = added in new.
/// </summary>
public readonly record struct DiffEdit(char Kind, int OldIdx, int NewIdx);
```

### `REBUSS.Pure\Services\Common\Models\PullRequestMetadata.cs`

```csharp
namespace REBUSS.Pure.Services.Common.Models
{
    /// <summary>
    /// Parsed metadata from the Azure DevOps pull request details endpoint.
    /// </summary>
    public sealed record PullRequestMetadata(
        string Title,
        string Status,
        string SourceBranch,
        string TargetBranch,
        string SourceRefName,
        string TargetRefName);
}
```

### `REBUSS.Pure\Services\Common\Models\FullPullRequestMetadata.cs`

```csharp
namespace REBUSS.Pure.Services.Common.Models
{
    /// <summary>
    /// Rich metadata model for a pull request, populated from multiple API endpoints.
    /// </summary>
    public sealed class FullPullRequestMetadata
    {
        public int PullRequestId { get; set; }
        public int CodeReviewId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public bool IsDraft { get; set; }
        public string AuthorLogin { get; set; } = string.Empty;
        public string AuthorDisplayName { get; set; } = string.Empty;
        public DateTime CreatedDate { get; set; }
        public DateTime? ClosedDate { get; set; }
        public string SourceBranch { get; set; } = string.Empty;
        public string TargetBranch { get; set; } = string.Empty;
        public string SourceRefName { get; set; } = string.Empty;
        public string TargetRefName { get; set; } = string.Empty;
        public string LastMergeSourceCommitId { get; set; } = string.Empty;
        public string LastMergeTargetCommitId { get; set; } = string.Empty;
        public string RepositoryName { get; set; } = string.Empty;
        public string ProjectName { get; set; } = string.Empty;
        public List<string> CommitShas { get; set; } = new();
        public int ChangedFilesCount { get; set; }
        public int Additions { get; set; }
        public int Deletions { get; set; }
    }
}
```

### `REBUSS.Pure\Services\Common\Models\IterationInfo.cs`

```csharp
namespace REBUSS.Pure.Services.Common.Models
{
    /// <summary>
    /// Information extracted from the last PR iteration:
    /// the iteration ID and the two commit SHAs used to build the diff.
    /// </summary>
    public sealed record IterationInfo(int Id, string BaseCommit, string TargetCommit)
    {
        public static readonly IterationInfo Empty = new(0, string.Empty, string.Empty);
    }
}
```

---

## Diff Pipeline

### `REBUSS.Pure\Services\Common\IDiffAlgorithm.cs`

```csharp
namespace REBUSS.Pure.Services.Common;

/// <summary>
/// Computes a line-level edit list between two versions of a file.
/// </summary>
public interface IDiffAlgorithm
{
    IReadOnlyList<DiffEdit> ComputeEdits(string[] oldLines, string[] newLines);
}
```

### `REBUSS.Pure\Services\Common\LcsDiffAlgorithm.cs`

```csharp
namespace REBUSS.Pure.Services.Common;

/// <summary>
/// Classic O(m*n) LCS-based diff algorithm.
/// Produces a minimal edit list by computing the longest common subsequence.
/// </summary>
public class LcsDiffAlgorithm : IDiffAlgorithm
{
    public IReadOnlyList<DiffEdit> ComputeEdits(string[] oldLines, string[] newLines)
    {
        var dp = BuildLcsTable(oldLines, newLines);
        return TraceEdits(oldLines, newLines, dp);
    }

    private static int[,] BuildLcsTable(string[] a, string[] b)
    {
        int m = a.Length, n = b.Length;
        var dp = new int[m + 1, n + 1];

        for (int i = m - 1; i >= 0; i--)
        for (int j = n - 1; j >= 0; j--)
            dp[i, j] = a[i] == b[j]
                ? dp[i + 1, j + 1] + 1
                : Math.Max(dp[i + 1, j], dp[i, j + 1]);

        return dp;
    }

    private static List<DiffEdit> TraceEdits(string[] a, string[] b, int[,] dp)
    {
        int m = a.Length, n = b.Length;
        var result = new List<DiffEdit>();
        int ai = 0, bi = 0;

        while (ai < m && bi < n)
        {
            if (a[ai] == b[bi])
                result.Add(new DiffEdit(' ', ai++, bi++));
            else if (dp[ai + 1, bi] >= dp[ai, bi + 1])
                result.Add(new DiffEdit('-', ai++, bi));
            else
                result.Add(new DiffEdit('+', ai, bi++));
        }

        while (ai < m) result.Add(new DiffEdit('-', ai++, bi));
        while (bi < n) result.Add(new DiffEdit('+', ai, bi++));

        return result;
    }
}
```

### `REBUSS.Pure\Services\Common\IUnifiedDiffBuilder.cs`

```csharp
using REBUSS.Pure.Services.Common.Models;

namespace REBUSS.Pure.Services.Common;

/// <summary>
/// Produces structured diff hunks for a single file given base and target content.
/// </summary>
public interface IStructuredDiffBuilder
{
    /// <summary>
    /// Produces a list of structured diff hunks for a single file.
    /// <c>null</c> content means the file did not exist at that commit (add/delete).
    /// Returns an empty list when both sides are identical.
    /// </summary>
    List<DiffHunk> Build(string filePath, string? baseContent, string? targetContent);
}
```

### `REBUSS.Pure\Services\Common\UnifiedDiffBuilder.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Services.Common.Models;

namespace REBUSS.Pure.Services.Common;

/// <summary>
/// Produces structured diff hunks for a single file given base and target content.
/// Depends on <see cref="IDiffAlgorithm"/> for the line-level edit computation (DIP).
/// </summary>
public class StructuredDiffBuilder : IStructuredDiffBuilder
{
    private const int DefaultContextLines = 3;

    private readonly IDiffAlgorithm _diffAlgorithm;
    private readonly ILogger<StructuredDiffBuilder> _logger;

    public StructuredDiffBuilder(IDiffAlgorithm diffAlgorithm, ILogger<StructuredDiffBuilder> logger)
    {
        _diffAlgorithm = diffAlgorithm;
        _logger = logger;
    }

    public List<DiffHunk> Build(string filePath, string? baseContent, string? targetContent)
    {
        if (baseContent == targetContent)
            return new List<DiffHunk>();

        var sw = Stopwatch.StartNew();

        var aPath = filePath.TrimStart('/');
        var baseLines = SplitLines(baseContent);
        var targetLines = SplitLines(targetContent);

        var hunks = ComputeHunks(baseLines, targetLines, DefaultContextLines);

        sw.Stop();

        _logger.LogDebug(
            "Diff for '{FilePath}': {HunkCount} hunk(s), old={OldLineCount} lines, new={NewLineCount} lines, {ElapsedMs}ms",
            aPath, hunks.Count, baseLines.Length, targetLines.Length, sw.ElapsedMilliseconds);

        if (hunks.Count > 50)
        {
            _logger.LogWarning(
                "Suspicious diff for '{FilePath}': {HunkCount} hunks (possible generated/binary file)",
                aPath, hunks.Count);
        }

        return hunks;
    }

    internal static string[] SplitLines(string? content)
    {
        if (string.IsNullOrEmpty(content))
            return Array.Empty<string>();
        return content.Replace("\r\n", "\n").Split('\n');
    }

    // --- Hunk computation --------------------------------------------------------

    private List<DiffHunk> ComputeHunks(string[] oldLines, string[] newLines, int contextLines)
    {
        var edits = _diffAlgorithm.ComputeEdits(oldLines, newLines);
        var hunks = new List<DiffHunk>();
        int i = 0;

        while (i < edits.Count)
        {
            if (edits[i].Kind == ' ') { i++; continue; }

            int hunkStart = Math.Max(0, i - contextLines);
            var (hunkEdits, nextI) = CollectHunkEdits(edits, hunkStart, contextLines);
            i = nextI;

            if (hunkEdits.Count > 0)
                hunks.Add(FormatHunk(hunkEdits, oldLines, newLines));
        }

        return hunks;
    }

    private static (List<DiffEdit> HunkEdits, int NextI) CollectHunkEdits(
        IReadOnlyList<DiffEdit> edits, int hunkStart, int contextLines)
    {
        var hunkEdits = new List<DiffEdit>();
        int j = hunkStart;
        int nextI = hunkStart;

        while (j < edits.Count)
        {
            hunkEdits.Add(edits[j]);

            if (edits[j].Kind == ' ')
            {
                var (trailingLen, moreChanges) = CountTrailingContext(edits, j);

                if (!moreChanges || trailingLen > contextLines)
                {
                    int keep = Math.Min(contextLines, trailingLen);
                    for (int x = 1; x < keep && j + x < edits.Count; x++)
                        hunkEdits.Add(edits[j + x]);
                    return (hunkEdits, j + keep);
                }
            }

            j++;
            nextI = j;
        }

        return (hunkEdits, nextI);
    }

    private static (int TrailingLen, bool MoreChanges) CountTrailingContext(
        IReadOnlyList<DiffEdit> edits, int j)
    {
        int trailingEnd = j;
        while (trailingEnd + 1 < edits.Count && edits[trailingEnd + 1].Kind == ' ')
            trailingEnd++;

        return (trailingEnd - j + 1, trailingEnd + 1 < edits.Count);
    }

    // --- Hunk formatting ---------------------------------------------------------

    private static DiffHunk FormatHunk(List<DiffEdit> hunkEdits, string[] oldLines, string[] newLines)
    {
        int oldStart = hunkEdits.Where(e => e.Kind != '+').Select(e => e.OldIdx + 1).DefaultIfEmpty(1).First();
        int newStart = hunkEdits.Where(e => e.Kind != '-').Select(e => e.NewIdx + 1).DefaultIfEmpty(1).First();
        int oldCount = hunkEdits.Count(e => e.Kind != '+');
        int newCount = hunkEdits.Count(e => e.Kind != '-');

        var lines = new List<DiffLine>();
        foreach (var edit in hunkEdits)
        {
            var lineText = edit.Kind == '+' ? newLines[edit.NewIdx] : oldLines[edit.OldIdx];
            lines.Add(new DiffLine { Op = edit.Kind, Text = lineText });
        }

        return new DiffHunk
        {
            OldStart = oldStart,
            OldCount = oldCount,
            NewStart = newStart,
            NewCount = newCount,
            Lines = lines
        };
    }
}
```

### `REBUSS.Pure\Services\Diff\IPullRequestDiffProvider.cs`

```csharp
using REBUSS.Pure.Services.Common.Models;

namespace REBUSS.Pure.Services.Diff
{
    /// <summary>
    /// Interface for retrieving Pull Request diffs.
    /// </summary>
    public interface IPullRequestDiffProvider
    {
        /// <exception cref="Common.PullRequestNotFoundException">Thrown when PR is not found.</exception>
        Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken cancellationToken = default);

        /// <exception cref="Common.PullRequestNotFoundException">Thrown when PR is not found.</exception>
        /// <exception cref="Common.FileNotFoundInPullRequestException">Thrown when the file does not exist in the PR.</exception>
        Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken cancellationToken = default);
    }
}
```

### `REBUSS.Pure\Services\Diff\AzureDevOpsDiffProvider.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.AzureDevOpsIntegration.Services;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Models;
using REBUSS.Pure.Services.Common.Parsers;
using REBUSS.Pure.Services.FileList.Classification;

namespace REBUSS.Pure.Services.Diff
{
    /// <summary>
    /// Fetches structured diff content from Azure DevOps by:
    /// 1. Reading PR details (title, status, refs).
    /// 2. Reading the last iteration to get the base and target commit SHAs.
    /// 3. Enumerating changed files from the iteration changes endpoint.
    /// 4. For each file, fetching raw content at both commits.
    /// 5. Producing structured diff hunks via <see cref="StructuredDiffBuilder"/>.
    /// </summary>
    public class AzureDevOpsDiffProvider : IPullRequestDiffProvider
    {
        private const int FullRewriteMinLineCount = 10;

        private readonly IAzureDevOpsApiClient _apiClient;
        private readonly IPullRequestMetadataParser _metadataParser;
        private readonly IIterationInfoParser _iterationParser;
        private readonly IFileChangesParser _changesParser;
        private readonly IStructuredDiffBuilder _diffBuilder;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<AzureDevOpsDiffProvider> _logger;

        public AzureDevOpsDiffProvider(
            IAzureDevOpsApiClient apiClient,
            IPullRequestMetadataParser metadataParser,
            IIterationInfoParser iterationParser,
            IFileChangesParser changesParser,
            IStructuredDiffBuilder diffBuilder,
            IFileClassifier fileClassifier,
            ILogger<AzureDevOpsDiffProvider> logger)
        {
            _apiClient = apiClient;
            _metadataParser = metadataParser;
            _iterationParser = iterationParser;
            _changesParser = changesParser;
            _diffBuilder = diffBuilder;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public async Task<PullRequestDiff> GetDiffAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Fetching diff for PR #{PrNumber}", prNumber);
                var sw = Stopwatch.StartNew();

                var (metadata, files, baseCommit, targetCommit) = await FetchPullRequestDataAsync(prNumber);

                _logger.LogInformation(
                    "PR #{PrNumber}: {FileCount} file(s) changed, building diffs (base={BaseCommit}, target={TargetCommit})",
                    prNumber, files.Count,
                    baseCommit?.Length > 7 ? baseCommit[..7] : baseCommit,
                    targetCommit?.Length > 7 ? targetCommit[..7] : targetCommit);

                await BuildFileDiffsAsync(files, baseCommit, targetCommit, cancellationToken);

                var result = BuildDiff(metadata, files);
                sw.Stop();

                var totalHunks = result.Files.Sum(f => f.Hunks.Count);
                _logger.LogInformation(
                    "Diff for PR #{PrNumber} completed: {FileCount} file(s), {TotalHunks} hunk(s), {ElapsedMs}ms",
                    prNumber, files.Count, totalHunks, sw.ElapsedMilliseconds);

                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Pull Request #{PrNumber} not found", prNumber);
                throw new PullRequestNotFoundException($"Pull Request #{prNumber} not found", ex);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching diff for PR #{PrNumber}", prNumber);
                throw;
            }
        }

        public async Task<PullRequestDiff> GetFileDiffAsync(int prNumber, string path, CancellationToken cancellationToken = default)
        {
            try
            {
                _logger.LogInformation("Fetching diff for file '{Path}' in PR #{PrNumber}", path, prNumber);
                var sw = Stopwatch.StartNew();

                var (metadata, files, baseCommit, targetCommit) = await FetchPullRequestDataAsync(prNumber);

                var normalizedPath = NormalizePath(path);
                var matchingFiles = files
                    .Where(f => NormalizePath(f.Path).Equals(normalizedPath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingFiles.Count == 0)
                {
                    _logger.LogWarning("File '{Path}' not found in PR #{PrNumber}", path, prNumber);
                    throw new FileNotFoundInPullRequestException(
                        $"File '{path}' not found in Pull Request #{prNumber}");
                }

                await BuildFileDiffsAsync(matchingFiles, baseCommit, targetCommit, cancellationToken);

                var result = BuildDiff(metadata, matchingFiles);
                sw.Stop();

                var totalHunks = result.Files.Sum(f => f.Hunks.Count);
                _logger.LogInformation(
                    "File diff for '{Path}' in PR #{PrNumber} completed: {TotalHunks} hunk(s), {ElapsedMs}ms",
                    path, prNumber, totalHunks, sw.ElapsedMilliseconds);

                return result;
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _logger.LogWarning("Pull Request #{PrNumber} not found", prNumber);
                throw new PullRequestNotFoundException($"Pull Request #{prNumber} not found", ex);
            }
            catch (FileNotFoundInPullRequestException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching diff for file '{Path}' in PR #{PrNumber}", path, prNumber);
                throw;
            }
        }

        private async Task<(PullRequestMetadata metadata, List<FileChange> files, string baseCommit, string targetCommit)>
            FetchPullRequestDataAsync(int prNumber)
        {
            var metadata  = _metadataParser.Parse(await _apiClient.GetPullRequestDetailsAsync(prNumber));
            var iteration = _iterationParser.ParseLast(await _apiClient.GetPullRequestIterationsAsync(prNumber));
            var files     = await FetchFileChangesAsync(prNumber, iteration.Id);

            return (metadata, files, iteration.BaseCommit, iteration.TargetCommit);
        }

        private static string NormalizePath(string path) => path.TrimStart('/');

        private async Task<List<FileChange>> FetchFileChangesAsync(int prNumber, int iterationId)
        {
            var changesJson = iterationId > 0
                ? await _apiClient.GetPullRequestIterationChangesAsync(prNumber, iterationId)
                : "{}";

            return _changesParser.Parse(changesJson);
        }

        private async Task BuildFileDiffsAsync(
            List<FileChange> files,
            string baseCommit,
            string targetCommit,
            CancellationToken cancellationToken)
        {
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrEmpty(baseCommit) || string.IsNullOrEmpty(targetCommit))
                {
                    _logger.LogDebug(
                        "Skipping diff for '{FilePath}': commit SHAs not resolved (base={BaseCommit}, target={TargetCommit})",
                        file.Path, baseCommit ?? "<null>", targetCommit ?? "<null>");
                    continue;
                }

                var skipReason = GetSkipReason(file);
                if (skipReason is not null)
                {
                    file.SkipReason = skipReason;
                    _logger.LogDebug(
                        "Skipping diff for '{FilePath}': {SkipReason}",
                        file.Path, skipReason);
                    continue;
                }

                var fileSw = Stopwatch.StartNew();

                var baseContent   = await _apiClient.GetFileContentAtCommitAsync(baseCommit,   file.Path);
                var targetContent = await _apiClient.GetFileContentAtCommitAsync(targetCommit, file.Path);
                file.Hunks = _diffBuilder.Build(file.Path, baseContent, targetContent);

                if (IsFullFileRewrite(baseContent, targetContent, file.Hunks))
                {
                    file.SkipReason = "full file rewrite";
                    file.Hunks = new List<DiffHunk>();
                    _logger.LogDebug(
                        "Replaced diff for '{FilePath}': detected full file rewrite",
                        file.Path);
                }
                else
                {
                    file.Additions = file.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '+');
                    file.Deletions = file.Hunks.SelectMany(h => h.Lines).Count(l => l.Op == '-');
                }

                fileSw.Stop();

                _logger.LogDebug(
                    "Built diff for '{FilePath}' ({ChangeType}): {HunkCount} hunk(s), {ElapsedMs}ms",
                    file.Path, file.ChangeType, file.Hunks.Count, fileSw.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Returns a skip reason if the file should not have its diff computed,
        /// or <c>null</c> if normal diff logic should proceed.
        /// </summary>
        internal string? GetSkipReason(FileChange file)
        {
            if (string.Equals(file.ChangeType, "delete", StringComparison.OrdinalIgnoreCase))
                return "file deleted";

            if (string.Equals(file.ChangeType, "rename", StringComparison.OrdinalIgnoreCase))
                return "file renamed";

            var classification = _fileClassifier.Classify(file.Path);

            if (classification.IsBinary)
                return "binary file";

            if (classification.IsGenerated)
                return "generated file";

            return null;
        }

        /// <summary>
        /// Detects a full-file rewrite: both contents are non-trivial but the diff
        /// contains zero context (unchanged) lines, indicating every line changed.
        /// </summary>
        internal static bool IsFullFileRewrite(string? baseContent, string? targetContent, List<DiffHunk> hunks)
        {
            if (string.IsNullOrEmpty(baseContent) || string.IsNullOrEmpty(targetContent))
                return false;

            if (hunks.Count == 0)
                return false;

            var oldLineCount = baseContent.Replace("\r\n", "\n").Split('\n').Length;
            var newLineCount = targetContent.Replace("\r\n", "\n").Split('\n').Length;

            if (oldLineCount < FullRewriteMinLineCount && newLineCount < FullRewriteMinLineCount)
                return false;

            return !hunks.SelectMany(h => h.Lines).Any(l => l.Op == ' ');
        }

        private static PullRequestDiff BuildDiff(
            PullRequestMetadata metadata,
            List<FileChange> files)
        {
            return new PullRequestDiff
            {
                Title         = metadata.Title,
                Status        = metadata.Status,
                SourceBranch  = metadata.SourceBranch,
                TargetBranch  = metadata.TargetBranch,
                SourceRefName = metadata.SourceRefName,
                TargetRefName = metadata.TargetRefName,
                Files         = files
            };
        }
    }
}
```

---

## File List Pipeline

### `REBUSS.Pure\Services\FileList\AzureDevOpsFilesProvider.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Services.Common.Models;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Services.FileList.Classification;
using REBUSS.Pure.Services.FileList.Models;

namespace REBUSS.Pure.Services.FileList
{
    /// <summary>
    /// Fetches the list of changed files for a pull request, classifies each file,
    /// computes per-file line stats from the diff content, and builds a category summary.
    /// Delegates to <see cref="IPullRequestDiffProvider"/> for the raw file data and diffs.
    /// </summary>
    public class AzureDevOpsFilesProvider : IPullRequestFilesProvider
    {
        private readonly IPullRequestDiffProvider _diffProvider;
        private readonly IFileClassifier _fileClassifier;
        private readonly ILogger<AzureDevOpsFilesProvider> _logger;

        public AzureDevOpsFilesProvider(
            IPullRequestDiffProvider diffProvider,
            IFileClassifier fileClassifier,
            ILogger<AzureDevOpsFilesProvider> logger)
        {
            _diffProvider = diffProvider;
            _fileClassifier = fileClassifier;
            _logger = logger;
        }

        public async Task<PullRequestFiles> GetFilesAsync(int prNumber, CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("Fetching files for PR #{PrNumber}", prNumber);
            var sw = Stopwatch.StartNew();

            var diff = await _diffProvider.GetDiffAsync(prNumber, cancellationToken);

            var classified = diff.Files
                .Select(f => (fileChange: f, classification: _fileClassifier.Classify(f.Path)))
                .ToList();

            var files = classified.Select(x => BuildFileInfo(x.fileChange, x.classification)).ToList();
            var summary = BuildSummary(classified.Select(x => x.classification).ToList(), files);

            sw.Stop();

            _logger.LogInformation(
                "Files for PR #{PrNumber} completed: {TotalFiles} file(s) " +
                "(source={SourceFiles}, test={TestFiles}, config={ConfigFiles}, docs={DocsFiles}, " +
                "binary={BinaryFiles}, generated={GeneratedFiles}, highPriority={HighPriority}), {ElapsedMs}ms",
                prNumber, files.Count,
                summary.SourceFiles, summary.TestFiles, summary.ConfigFiles, summary.DocsFiles,
                summary.BinaryFiles, summary.GeneratedFiles, summary.HighPriorityFiles,
                sw.ElapsedMilliseconds);

            return new PullRequestFiles { Files = files, Summary = summary };
        }

        private static PullRequestFileInfo BuildFileInfo(FileChange fileChange, FileClassification classification)
        {
            return new PullRequestFileInfo
            {
                Path = fileChange.Path.TrimStart('/'),
                Status = MapStatus(fileChange.ChangeType),
                Additions = fileChange.Additions,
                Deletions = fileChange.Deletions,
                Changes = fileChange.Additions + fileChange.Deletions,
                Extension = classification.Extension,
                IsBinary = classification.IsBinary,
                IsGenerated = classification.IsGenerated,
                IsTestFile = classification.IsTestFile,
                ReviewPriority = classification.ReviewPriority
            };
        }

        private static string MapStatus(string changeType) => changeType.ToLowerInvariant() switch
        {
            "add" => "added",
            "edit" => "modified",
            "delete" => "removed",
            "rename" => "renamed",
            _ => changeType
        };

        private static PullRequestFilesSummary BuildSummary(
            List<FileClassification> classifications, List<PullRequestFileInfo> files)
        {
            return new PullRequestFilesSummary
            {
                SourceFiles = classifications.Count(c => c.Category == FileCategory.Source),
                TestFiles = classifications.Count(c => c.Category == FileCategory.Test),
                ConfigFiles = classifications.Count(c => c.Category == FileCategory.Config),
                DocsFiles = classifications.Count(c => c.Category == FileCategory.Docs),
                BinaryFiles = classifications.Count(c => c.Category == FileCategory.Binary),
                GeneratedFiles = classifications.Count(c => c.Category == FileCategory.Generated),
                HighPriorityFiles = files.Count(f => f.ReviewPriority == "high")
            };
        }
    }
}
```

---

## Tool Handlers

### `REBUSS.Pure\Tools\GetPullRequestDiffToolHandler.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Models;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_pr_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks.
    /// </summary>
    public class GetPullRequestDiffToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDiffProvider _diffProvider;
        private readonly ILogger<GetPullRequestDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_pr_diff";

        public GetPullRequestDiffToolHandler(
            IPullRequestDiffProvider diffProvider,
            ILogger<GetPullRequestDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves the diff (file changes) for a specific Pull Request from Azure DevOps. " +
                          "Returns a structured JSON object with per-file hunks optimized for AI code review.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve the diff for"
                    }
                },
                Required = new List<string> { "prNumber" }
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!TryExtractPrNumber(arguments, out var prNumber, out var error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}", ToolName, prNumber);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetDiffAsync(prNumber, cancellationToken);

                var result = BuildStructuredResult(prNumber, diff);

                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: PR #{PrNumber}, {FileCount} file(s), {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, diff.Files.Count, result.Content[0].Text.Length, sw.ElapsedMilliseconds);

                return result;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found (prNumber={PrNumber})", ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (prNumber={PrNumber})",
                    ToolName, arguments?.GetValueOrDefault("prNumber"));
                return CreateErrorResult($"Error retrieving PR diff: {ex.Message}");
            }
        }

        // --- Input extraction -----------------------------------------------------

        private bool TryExtractPrNumber(
            Dictionary<string, object>? arguments,
            out int prNumber,
            out string errorMessage)
        {
            prNumber = 0;
            errorMessage = string.Empty;

            if (arguments == null || !arguments.TryGetValue("prNumber", out var prNumberObj))
            {
                errorMessage = "Missing required parameter: prNumber";
                return false;
            }

            try
            {
                prNumber = prNumberObj is JsonElement jsonElement
                    ? jsonElement.GetInt32()
                    : Convert.ToInt32(prNumberObj);
            }
            catch
            {
                errorMessage = "Invalid prNumber parameter: must be an integer";
                return false;
            }

            if (prNumber <= 0)
            {
                errorMessage = "prNumber must be greater than 0";
                return false;
            }

            return true;
        }

        // --- Result builders ------------------------------------------------------

        private static ToolResult BuildStructuredResult(int prNumber, PullRequestDiff diff)
        {
            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = diff.Files.Select(f => new StructuredFileChange
                {
                    Path = f.Path,
                    ChangeType = f.ChangeType,
                    SkipReason = f.SkipReason,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Hunks = f.Hunks.Select(h => new StructuredHunk
                    {
                        OldStart = h.OldStart,
                        OldCount = h.OldCount,
                        NewStart = h.NewStart,
                        NewCount = h.NewCount,
                        Lines = h.Lines.Select(l => new StructuredLine
                        {
                            Op = l.Op.ToString(),
                            Text = l.Text
                        }).ToList()
                    }).ToList()
                }).ToList()
            };

            return CreateSuccessResult(JsonSerializer.Serialize(structured, JsonOptions));
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
```

### `REBUSS.Pure\Tools\GetFileDiffToolHandler.cs`

```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Models;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Models;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Tools.Models;
using System.Text.Json;

namespace REBUSS.Pure.Tools
{
    /// <summary>
    /// Handles the execution of the get_file_diff MCP tool.
    /// Validates input, delegates to <see cref="IPullRequestDiffProvider"/>,
    /// and returns a structured JSON result with per-file hunks for a single file.
    /// </summary>
    public class GetFileDiffToolHandler : IMcpToolHandler
    {
        private readonly IPullRequestDiffProvider _diffProvider;
        private readonly ILogger<GetFileDiffToolHandler> _logger;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
        };

        public string ToolName => "get_file_diff";

        public GetFileDiffToolHandler(
            IPullRequestDiffProvider diffProvider,
            ILogger<GetFileDiffToolHandler> logger)
        {
            _diffProvider = diffProvider;
            _logger = logger;
        }

        public McpTool GetToolDefinition() => new()
        {
            Name = ToolName,
            Description = "Retrieves the diff for a single file in a specific Pull Request from Azure DevOps. " +
                          "Returns a structured JSON object with the file diff including hunks optimized for AI code review.",
            InputSchema = new ToolInputSchema
            {
                Type = "object",
                Properties = new Dictionary<string, ToolProperty>
                {
                    ["prNumber"] = new ToolProperty
                    {
                        Type = "integer",
                        Description = "The Pull Request number/ID to retrieve the diff for"
                    },
                    ["path"] = new ToolProperty
                    {
                        Type = "string",
                        Description = "The repository-relative path of the file (e.g. 'src/Cache/CacheService.cs')"
                    }
                },
                Required = new List<string> { "prNumber", "path" }
            }
        };

        public async Task<ToolResult> ExecuteAsync(
            Dictionary<string, object>? arguments,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (!TryExtractPrNumber(arguments, out var prNumber, out var error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                if (!TryExtractPath(arguments!, out var path, out error))
                {
                    _logger.LogWarning("[{ToolName}] Validation failed: {Error}", ToolName, error);
                    return CreateErrorResult(error);
                }

                _logger.LogInformation("[{ToolName}] Entry: PR #{PrNumber}, path='{Path}'",
                    ToolName, prNumber, path);
                var sw = Stopwatch.StartNew();

                var diff = await _diffProvider.GetFileDiffAsync(prNumber, path, cancellationToken);

                var result = BuildStructuredResult(prNumber, diff);

                sw.Stop();

                _logger.LogInformation(
                    "[{ToolName}] Completed: PR #{PrNumber}, path='{Path}', {ResponseLength} chars, {ElapsedMs}ms",
                    ToolName, prNumber, path, result.Content[0].Text.Length, sw.ElapsedMilliseconds);

                return result;
            }
            catch (PullRequestNotFoundException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] Pull request not found (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Pull Request not found: {ex.Message}");
            }
            catch (FileNotFoundInPullRequestException ex)
            {
                _logger.LogWarning(ex, "[{ToolName}] File not found in pull request (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"File not found in Pull Request: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{ToolName}] Error (prNumber={PrNumber}, path='{Path}')",
                    ToolName, arguments?.GetValueOrDefault("prNumber"), arguments?.GetValueOrDefault("path"));
                return CreateErrorResult($"Error retrieving file diff: {ex.Message}");
            }
        }

        // --- Input extraction -----------------------------------------------------

        private bool TryExtractPrNumber(
            Dictionary<string, object>? arguments,
            out int prNumber,
            out string errorMessage)
        {
            prNumber = 0;
            errorMessage = string.Empty;

            if (arguments == null || !arguments.TryGetValue("prNumber", out var prNumberObj))
            {
                errorMessage = "Missing required parameter: prNumber";
                return false;
            }

            try
            {
                prNumber = prNumberObj is JsonElement jsonElement
                    ? jsonElement.GetInt32()
                    : Convert.ToInt32(prNumberObj);
            }
            catch
            {
                errorMessage = "Invalid prNumber parameter: must be an integer";
                return false;
            }

            if (prNumber <= 0)
            {
                errorMessage = "prNumber must be greater than 0";
                return false;
            }

            return true;
        }

        private static bool TryExtractPath(
            Dictionary<string, object> arguments,
            out string path,
            out string errorMessage)
        {
            path = string.Empty;
            errorMessage = string.Empty;

            if (!arguments.TryGetValue("path", out var pathObj))
            {
                errorMessage = "Missing required parameter: path";
                return false;
            }

            path = pathObj is JsonElement jsonElement
                ? jsonElement.GetString() ?? string.Empty
                : pathObj?.ToString() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                errorMessage = "path parameter must not be empty";
                return false;
            }

            return true;
        }

        // --- Result builders ------------------------------------------------------

        private static ToolResult BuildStructuredResult(int prNumber, PullRequestDiff diff)
        {
            var structured = new StructuredDiffResult
            {
                PrNumber = prNumber,
                Files = diff.Files.Select(f => new StructuredFileChange
                {
                    Path = f.Path,
                    ChangeType = f.ChangeType,
                    SkipReason = f.SkipReason,
                    Additions = f.Additions,
                    Deletions = f.Deletions,
                    Hunks = f.Hunks.Select(h => new StructuredHunk
                    {
                        OldStart = h.OldStart,
                        OldCount = h.OldCount,
                        NewStart = h.NewStart,
                        NewCount = h.NewCount,
                        Lines = h.Lines.Select(l => new StructuredLine
                        {
                            Op = l.Op.ToString(),
                            Text = l.Text
                        }).ToList()
                    }).ToList()
                }).ToList()
            };

            return CreateSuccessResult(JsonSerializer.Serialize(structured, JsonOptions));
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
```

---

## Tool Output Models

### `REBUSS.Pure\Tools\Models\StructuredDiffResult.cs`

```csharp
using System.Text.Json.Serialization;

namespace REBUSS.Pure.Tools.Models
{
    /// <summary>
    /// Structured JSON response model for diff tools.
    /// Contains hunk-level diff data optimized for AI code review.
    /// </summary>
    public class StructuredDiffResult
    {
        [JsonPropertyName("prNumber")]
        public int? PrNumber { get; set; }

        [JsonPropertyName("files")]
        public List<StructuredFileChange> Files { get; set; } = new();
    }

    public class StructuredFileChange
    {
        [JsonPropertyName("path")]
        public string Path { get; set; } = string.Empty;

        [JsonPropertyName("changeType")]
        public string ChangeType { get; set; } = string.Empty;

        [JsonPropertyName("skipReason")]
        public string? SkipReason { get; set; }

        [JsonPropertyName("additions")]
        public int Additions { get; set; }

        [JsonPropertyName("deletions")]
        public int Deletions { get; set; }

        [JsonPropertyName("hunks")]
        public List<StructuredHunk> Hunks { get; set; } = new();
    }

    public class StructuredHunk
    {
        [JsonPropertyName("oldStart")]
        public int OldStart { get; set; }

        [JsonPropertyName("oldCount")]
        public int OldCount { get; set; }

        [JsonPropertyName("newStart")]
        public int NewStart { get; set; }

        [JsonPropertyName("newCount")]
        public int NewCount { get; set; }

        [JsonPropertyName("lines")]
        public List<StructuredLine> Lines { get; set; } = new();
    }

    public class StructuredLine
    {
        [JsonPropertyName("op")]
        public string Op { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;
    }
}
```

---

## Entry Point

### `REBUSS.Pure\Program.cs`

```csharp
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using REBUSS.Pure.AzureDevOpsIntegration.Configuration;
using REBUSS.Pure.AzureDevOpsIntegration.Services;
using REBUSS.Pure.Cli;
using REBUSS.Pure.Logging;
using REBUSS.Pure.Mcp;
using REBUSS.Pure.Mcp.Handlers;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Parsers;
using REBUSS.Pure.Services.Content;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Services.FileList;
using REBUSS.Pure.Services.FileList.Classification;
using REBUSS.Pure.Services.LocalReview;
using REBUSS.Pure.Services.Metadata;
using REBUSS.Pure.Tools;

namespace REBUSS.Pure
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            var parseResult = CliArgumentParser.Parse(args);

            if (!parseResult.IsServerMode)
                return await RunCliCommandAsync(parseResult);

            await RunMcpServerAsync(parseResult);
            return 0;
        }

        private static async Task<int> RunCliCommandAsync(CliParseResult parseResult)
        {
            ICliCommand command = parseResult.CommandName switch
            {
                "init" => new InitCommand(
                    Console.Error,
                    Environment.CurrentDirectory,
                    GetExecutablePath(),
                    parseResult.Pat),
                _ => throw new InvalidOperationException($"Unknown command: {parseResult.CommandName}")
            };

            return await command.ExecuteAsync();
        }

        private static string GetExecutablePath()
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(processPath))
                return processPath;

            return Path.Combine(AppContext.BaseDirectory, "REBUSS.Pure.exe");
        }

        private static string GetLogFilePath()
        {
            var logDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "REBUSS.Pure");
            Directory.CreateDirectory(logDir);
            return Path.Combine(logDir, "server.log");
        }

        private static async Task RunMcpServerAsync(CliParseResult parseResult)
        {
            try
            {
                var cliOverrides = BuildCliConfigOverrides(parseResult);

                var configuration = new ConfigurationBuilder()
                    .SetBasePath(AppContext.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                    .AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true)
                    .AddEnvironmentVariables()
                    .AddInMemoryCollection(cliOverrides)
                    .Build();

                var services = new ServiceCollection();
                ConfigureServices(services, configuration);
                await using var serviceProvider = services.BuildServiceProvider();

                // Apply CLI --repo argument if provided
                if (!string.IsNullOrWhiteSpace(parseResult.RepoPath))
                {
                    var workspaceRootProvider = serviceProvider.GetRequiredService<IWorkspaceRootProvider>();
                    workspaceRootProvider.SetCliRepositoryPath(parseResult.RepoPath);
                }

                var server = serviceProvider.GetRequiredService<McpServer>();
                using var cts = new CancellationTokenSource();

                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };

                await server.RunAsync(cts.Token);
            }
            catch (Exception ex)
            {
                await Console.Error.WriteLineAsync($"[REBUSS.Pure] FATAL: {ex.GetType().FullName}: {ex.Message}");
                if (ex.InnerException is not null)
                    await Console.Error.WriteLineAsync($"[REBUSS.Pure] INNER: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                await Console.Error.WriteLineAsync(ex.StackTrace ?? string.Empty);
                Environment.Exit(1);
            }
        }

        private static Dictionary<string, string?> BuildCliConfigOverrides(CliParseResult parseResult)
        {
            var overrides = new Dictionary<string, string?>();

            if (!string.IsNullOrWhiteSpace(parseResult.Pat))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.PersonalAccessToken)}"] = parseResult.Pat;

            if (!string.IsNullOrWhiteSpace(parseResult.Organization))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.OrganizationName)}"] = parseResult.Organization;

            if (!string.IsNullOrWhiteSpace(parseResult.Project))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.ProjectName)}"] = parseResult.Project;

            if (!string.IsNullOrWhiteSpace(parseResult.Repository))
                overrides[$"{AzureDevOpsOptions.SectionName}:{nameof(AzureDevOpsOptions.RepositoryName)}"] = parseResult.Repository;

            return overrides;
        }

        private static void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
            // ... same DI registrations as in Section 3 ...
        }
    }
}
```

---

## Test Files

### `REBUSS.Pure.Tests\Services\UnifiedDiffBuilderTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using REBUSS.Pure.Services.Common;
using REBUSS.Pure.Services.Common.Models;

namespace REBUSS.Pure.Tests.Services;

public class StructuredDiffBuilderTests
{
    private readonly IStructuredDiffBuilder _builder =
        new StructuredDiffBuilder(new LcsDiffAlgorithm(), NullLogger<StructuredDiffBuilder>.Instance);

    [Fact]
    public void Build_ReturnsEmpty_WhenBothContentIdentical()
    {
        var result = _builder.Build("/src/File.cs", "hello", "hello");
        Assert.Empty(result);
    }

    [Fact]
    public void Build_ReturnsEmpty_WhenBothNull()
    {
        var result = _builder.Build("/src/File.cs", null, null);
        Assert.Empty(result);
    }

    [Fact]
    public void Build_NewFile_ContainsAddedLines()
    {
        var result = _builder.Build("/src/New.cs", null, "line1\nline2");

        Assert.Single(result);
        var hunk = result[0];
        Assert.Equal(2, hunk.Lines.Count);
        Assert.All(hunk.Lines, l => Assert.Equal('+', l.Op));
        Assert.Equal("line1", hunk.Lines[0].Text);
        Assert.Equal("line2", hunk.Lines[1].Text);
    }

    [Fact]
    public void Build_DeletedFile_ContainsRemovedLines()
    {
        var result = _builder.Build("/src/Old.cs", "line1\nline2", null);

        Assert.Single(result);
        var hunk = result[0];
        Assert.Equal(2, hunk.Lines.Count);
        Assert.All(hunk.Lines, l => Assert.Equal('-', l.Op));
        Assert.Equal("line1", hunk.Lines[0].Text);
        Assert.Equal("line2", hunk.Lines[1].Text);
    }

    [Fact]
    public void Build_ModifiedFile_ContainsMinusAndPlusLines()
    {
        var result = _builder.Build("src/File.cs", "aaa\nbbb\nccc", "aaa\nBBB\nccc");

        Assert.Single(result);
        var hunk = result[0];
        Assert.Contains(hunk.Lines, l => l.Op == '-' && l.Text == "bbb");
        Assert.Contains(hunk.Lines, l => l.Op == '+' && l.Text == "BBB");
        Assert.Contains(hunk.Lines, l => l.Op == ' ');
    }

    [Fact]
    public void Build_ContainsHunkMetadata()
    {
        var result = _builder.Build("a.txt", "old", "new");

        Assert.Single(result);
        var hunk = result[0];
        Assert.True(hunk.OldStart > 0);
        Assert.True(hunk.NewStart > 0);
        Assert.True(hunk.OldCount > 0);
        Assert.True(hunk.NewCount > 0);
    }

    [Fact]
    public void Build_HandlesCrlf()
    {
        var result = _builder.Build("a.txt", "aaa\r\nbbb", "aaa\r\nccc");

        Assert.Single(result);
        Assert.Contains(result[0].Lines, l => l.Op == '-' && l.Text == "bbb");
        Assert.Contains(result[0].Lines, l => l.Op == '+' && l.Text == "ccc");
    }

    [Fact]
    public void Build_NewFile_HunkMetadata_CorrectCounts()
    {
        var result = _builder.Build("new.txt", null, "a\nb\nc");

        Assert.Single(result);
        Assert.Equal(0, result[0].OldCount);
        Assert.Equal(3, result[0].NewCount);
    }

    [Fact]
    public void Build_DeletedFile_HunkMetadata_CorrectCounts()
    {
        var result = _builder.Build("old.txt", "a\nb\nc", null);

        Assert.Single(result);
        Assert.Equal(3, result[0].OldCount);
        Assert.Equal(0, result[0].NewCount);
    }
}
```

### `REBUSS.Pure.Tests\Services\AzureDevOpsFilesProviderTests.cs`

```csharp
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using REBUSS.Pure.Services.Common.Models;
using REBUSS.Pure.Services.Diff;
using REBUSS.Pure.Services.FileList;
using REBUSS.Pure.Services.FileList.Classification;
using REBUSS.Pure.Services.FileList.Models;

namespace REBUSS.Pure.Tests.Services;

public class AzureDevOpsFilesProviderTests
{
    private readonly IPullRequestDiffProvider _diffProvider = Substitute.For<IPullRequestDiffProvider>();
    private readonly AzureDevOpsFilesProvider _provider;

    public AzureDevOpsFilesProviderTests()
    {
        _provider = new AzureDevOpsFilesProvider(
            _diffProvider,
            new FileClassifier(),
            NullLogger<AzureDevOpsFilesProvider>.Instance);
    }

    [Fact]
    public async Task GetFilesAsync_MapsFileInfoCorrectly()
    {
        _diffProvider.GetDiffAsync(42, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new()
                {
                    Path = "/src/Service.cs", ChangeType = "edit",
                    Additions = 2, Deletions = 1,
                    Hunks = new List<DiffHunk>
                    {
                        new()
                        {
                            OldStart = 1, OldCount = 1, NewStart = 1, NewCount = 2,
                            Lines = new List<DiffLine>
                            {
                                new() { Op = '-', Text = "old" },
                                new() { Op = '+', Text = "new" },
                                new() { Op = '+', Text = "extra" }
                            }
                        }
                    }
                }
            }
        });

        var result = await _provider.GetFilesAsync(42);

        Assert.Single(result.Files);
        var file = result.Files[0];
        Assert.Equal("src/Service.cs", file.Path);
        Assert.Equal("modified", file.Status);
        Assert.Equal(2, file.Additions);
        Assert.Equal(1, file.Deletions);
        Assert.Equal(3, file.Changes);
        Assert.Equal(".cs", file.Extension);
        Assert.False(file.IsBinary);
        Assert.False(file.IsGenerated);
        Assert.False(file.IsTestFile);
        Assert.Equal("high", file.ReviewPriority);
    }

    [Fact]
    public async Task GetFilesAsync_MapsStatusCorrectly()
    {
        _diffProvider.GetDiffAsync(1, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/a.cs", ChangeType = "add" },
                new() { Path = "/b.cs", ChangeType = "edit" },
                new() { Path = "/c.cs", ChangeType = "delete" },
                new() { Path = "/d.cs", ChangeType = "rename" }
            }
        });

        var result = await _provider.GetFilesAsync(1);

        Assert.Equal("added", result.Files[0].Status);
        Assert.Equal("modified", result.Files[1].Status);
        Assert.Equal("removed", result.Files[2].Status);
        Assert.Equal("renamed", result.Files[3].Status);
    }

    [Fact]
    public async Task GetFilesAsync_BuildsSummaryCorrectly()
    {
        _diffProvider.GetDiffAsync(10, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/App.cs", ChangeType = "edit", Additions = 2 },
                new() { Path = "/tests/AppTests.cs", ChangeType = "edit", Additions = 1 },
                new() { Path = "/appsettings.json", ChangeType = "edit" },
                new() { Path = "/docs/readme.md", ChangeType = "edit" },
                new() { Path = "/lib/tool.dll", ChangeType = "add" },
                new() { Path = "/obj/Debug/net8.0/out.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(10);

        Assert.Equal(6, result.Files.Count);
        Assert.Equal(1, result.Summary.SourceFiles);
        Assert.Equal(1, result.Summary.TestFiles);
        Assert.Equal(1, result.Summary.ConfigFiles);
        Assert.Equal(1, result.Summary.DocsFiles);
        Assert.Equal(1, result.Summary.BinaryFiles);
        Assert.Equal(1, result.Summary.GeneratedFiles);
        Assert.Equal(1, result.Summary.HighPriorityFiles);
    }

    [Fact]
    public async Task GetFilesAsync_HandlesEmptyFileList()
    {
        _diffProvider.GetDiffAsync(5, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>()
        });

        var result = await _provider.GetFilesAsync(5);

        Assert.Empty(result.Files);
        Assert.Equal(0, result.Summary.SourceFiles);
        Assert.Equal(0, result.Summary.HighPriorityFiles);
    }

    [Fact]
    public async Task GetFilesAsync_HandlesFileWithNoHunks()
    {
        _diffProvider.GetDiffAsync(6, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/Empty.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(6);

        var file = Assert.Single(result.Files);
        Assert.Equal(0, file.Additions);
        Assert.Equal(0, file.Deletions);
        Assert.Equal(0, file.Changes);
    }

    [Fact]
    public async Task GetFilesAsync_StripsLeadingSlashFromPath()
    {
        _diffProvider.GetDiffAsync(7, Arg.Any<CancellationToken>()).Returns(new PullRequestDiff
        {
            Files = new List<FileChange>
            {
                new() { Path = "/src/A.cs", ChangeType = "edit" }
            }
        });

        var result = await _provider.GetFilesAsync(7);

        Assert.Equal("src/A.cs", result.Files[0].Path);
    }
}
```

### `REBUSS.Pure.Tests\Tools\GetPullRequestDiffToolHandlerTests.cs`

(See full content in section 5 tool handler tests — file contains: `SampleDiff` with hunks, `ExecuteAsync_ReturnsStructuredJson_ByDefault`, validation tests, exception tests, `CreateArgs` helper.)

### `REBUSS.Pure.Tests\Tools\GetFileDiffToolHandlerTests.cs`

(See full content in section 5 tool handler tests — file contains: `SampleFileDiff` with hunks, structured JSON tests, validation tests, schema test asserting `format` NOT in properties, exception tests.)

### `REBUSS.Pure.Tests\Services\AzureDevOpsDiffProviderTests.cs`

(See full content in section 5 — file contains: standard mock setup, metadata/diff/additions/deletions tests, 404 tests, cancellation, file diff tests, all skip behavior tests (delete/rename/binary/generated/full-rewrite), `IsFullFileRewrite` unit tests, `GetSkipReason` unit tests.)

### `REBUSS.Pure.Tests\Integration\EndToEndTests.cs`

(See full content in section 5 — file contains: `FullPipeline_ReturnsStructuredJson_ByDefault`, `FullPipeline_PrNotFound_ReturnsToolError`, `FullPipeline_InitializeThenToolsList_ReturnsToolWithSchema` asserting `format` NOT in properties.)

---

# 6. Context Self-Diagnostic

Before starting any task, evaluate whether the context in this file is **sufficient and current**. Apply the following rules:

### If the context is insufficient

Stop and tell me explicitly:

> **⚠️ The codebase context in `CodebaseUnderstanding.md` is insufficient for this task.**

Then list **exactly** what is missing (e.g., "Section 5 is missing the contents of `FooService.cs`", "The dependency graph does not include the new `BarProvider` → `BazModel` relationship", "DI registrations are missing the recently added `IQuuxHandler`"). For each missing item, state the specific addition needed so I can update the file.

### If the context is excessive or outdated

Stop and tell me explicitly:

> **⚠️ The codebase context in `CodebaseUnderstanding.md` contains outdated or unnecessary information.**

Then list **exactly** which parts are stale or excessive (e.g., "Section 5 still contains the old `FormatMode` enum that was removed", "The file-role map lists `LegacyHandler.cs` which no longer exists", "The `FooBar` DTO in section 5 has a property `Baz` that was renamed to `Qux`"). For each item, state whether it should be removed, renamed, or updated, and what the correct content should be.

### General rule

Always provide **explicit, actionable descriptions** of what to change in this file. Never say "the context might be outdated" without specifying exactly what is wrong and how to fix it.
