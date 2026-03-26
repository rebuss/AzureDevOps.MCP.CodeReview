# Architecture Deep Dive

> Internal architecture reference for coding agents working on cross-cutting changes.
> For quick conventions and extension recipes, see `ProjectConventions.md`.
> For file inventory and DI registrations, see `CodebaseUnderstanding.md`.
> Update this file when internal mechanics, protocol handling, or design patterns change.

## 1. MCP Protocol Layer

### JSON-RPC Server Loop

`McpServer.RunAsync` reads messages in a loop via `IJsonRpcTransport.ReadMessageAsync`
(newline-delimited JSON on stdin). Each message is processed through a pipeline:

1. **Deserialize** — `IJsonRpcSerializer.Deserialize<JsonRpcRequest>`. Parse failures
   return JSON-RPC error `-32700` (parse error).
2. **Validate** — `IsNotification` checks whether the `id` field is absent or null.
   Notifications are **silently discarded** (logged, no response) because MCP does not
   define any server-consumed notifications. This is intentional: returning an error
   for a notification would violate JSON-RPC 2.0 spec.
3. **Dispatch** — `DispatchAsync` resolves the handler from a `Dictionary<string, IMcpMethodHandler>`
   keyed by `MethodName`. Unknown methods return `-32601` (method not found). Missing
   `method` field returns `-32600` (invalid request).
4. **Error wrapping** — unhandled exceptions during handler execution are caught and
   returned as `-32603` (internal error) with the exception message in `error.message`.

**OCP pattern:** new JSON-RPC methods are added by registering a new `IMcpMethodHandler`
in DI. `McpServer` discovers all handlers via `IEnumerable<IMcpMethodHandler>` and builds
the dispatch dictionary in its constructor. No switch statement, no code changes to
`McpServer` needed.

### Message Flow: initialize → tools/list → tools/call

| Phase | Handler | Key Actions |
|---|---|---|
| **Handshake** | `InitializeMethodHandler` | Extracts `roots` array from `params` (MCP workspace roots); stores them in `IWorkspaceRootProvider` for later config resolution. Returns server `capabilities` (tools support) and protocol version `2024-11-05`. |
| **Discovery** | `ToolsListMethodHandler` | Iterates all `IMcpToolHandler` implementations, calls `GetToolDefinition()` on each, returns the aggregated `tools` array. |
| **Execution** | `ToolsCallMethodHandler` | Resolves handler by `params.name` from a `Dictionary<string, IMcpToolHandler>` (keyed by `ToolName`). Passes `params.arguments` to `ExecuteAsync`. Wraps result in JSON-RPC response. Logs timing via `Stopwatch`. |

The session is stateful only in the `IWorkspaceRootProvider` — roots set during
`initialize` influence config resolution for all subsequent tool calls.

### Transport & Serialization

**`StreamJsonRpcTransport`:** wraps stdin/stdout streams. Messages are newline-delimited
UTF-8 JSON (`ReadLineAsync` / `WriteLineAsync`). `leaveOpen: true` in `StreamReader`/
`StreamWriter` to avoid closing stdin/stdout prematurely. Implements `IAsyncDisposable`
for clean shutdown.

**`SystemTextJsonSerializer`:** `camelCase` property naming, `WriteIndented = false`
(compact protocol), `DefaultIgnoreCondition = WhenWritingNull` (omit null fields).
This differs from tool handler serialization which uses `WriteIndented = true` for
human-readable output.

**stdout reservation:** all non-protocol output (logging, errors, user messages) must
go to stderr. stdout is exclusively used by `StreamJsonRpcTransport` for JSON-RPC
responses. Violation breaks the MCP transport.

## 2. Provider Architecture

### The Provider Pattern (detailed)

Each SCM provider follows a layered architecture:

```
IScmClient (facade)
  ├── DiffProvider      → API client → Parser → StructuredDiffBuilder → FileChange[]
  ├── MetadataProvider  → API client → Parser → FullPullRequestMetadata
  ├── FilesProvider     → API client → Parser → PullRequestFiles
  └── FileContentProvider → API client → FileContent
```

**WHY this decomposition:**
- **SRP** — each provider handles one concern (diff, metadata, files, content).
- **Testability** — providers can be tested with real parsers and mocked API client,
  without instantiating the full facade.
- **Data sharing** — `FilesProvider` can reuse file list data already fetched by
  `DiffProvider` (both call the same API client methods, cached by HTTP client).

The facade (`AzureDevOpsScmClient` / `GitHubScmClient`) is a pure delegation layer:
it forwards calls to the appropriate provider and enriches metadata with
provider-agnostic fields (`WebUrl`, `RepositoryFullName`) derived from options.

### Interface Forwarding & Narrow Dependencies

`IScmClient` extends `IPullRequestDataProvider` + `IFileContentDataProvider`.
`ServiceCollectionExtensions` registers the concrete facade as three interfaces:

```csharp
services.AddSingleton<AzureDevOpsScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IFileContentDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
```

**WHY:** tool handlers depend on narrow interfaces (`IPullRequestDataProvider` or
`IFileContentDataProvider`), not the full `IScmClient`. This means:
- Tool handlers don't know which SCM provider is active
- They can be tested by mocking only the narrow interface
- Adding a new provider doesn't affect tool handler code at all

### Provider Selection (DetectProvider)

`Program.DetectProvider` uses a priority chain (checked at startup, once):

1. Explicit `--provider` CLI arg (via `Provider` config key)
2. `GitHub.Owner` config key present → GitHub
3. `AzureDevOps.OrganizationName` config key present → AzureDevOps
4. Git remote URL contains `github.com` → GitHub
5. Git remote URL contains `dev.azure.com` / `visualstudio.com` → AzureDevOps
6. Default → AzureDevOps

**WHY one provider per process:** simplicity. MCP servers are long-lived processes
bound to one IDE workspace. A workspace targets one repository on one platform.
Multi-provider support would complicate DI (conditional resolution), options
validation, and auth chains — with no real use case.

### Azure DevOps vs GitHub: Key Implementation Differences

| Aspect | Azure DevOps | GitHub |
|---|---|---|
| **Base/head commit resolution** | Iterations API: `GetPullRequestIterationsAsync` → `ParseLast` → `BaseCommit` / `TargetCommit` | PR details JSON: `ParseWithCommits` extracts `base.sha` / `head.sha` directly |
| **File content fetch** | `GetFileContentAtCommitAsync(commitId, path)` — by commit SHA | `GetFileContentAtRefAsync(ref, path)` — by any git ref (SHA, branch) |
| **File content parallelism** | Sequential: `await base`, then `await target` | Parallel: `Task.WhenAll(baseTask, headTask)` per file |
| **Auth failure detection** | `AuthenticationDelegatingHandler` checks `Content-Type` for HTML on 2xx | `GitHubAuthenticationHandler` checks HTTP 401 or 403 status codes |
| **Parser count** | 3 parsers (metadata, iteration, file changes) | 2 parsers (PR details with commits, file changes) |
| **Auth token format** | PAT → Basic (`base64(:pat)`), CLI token → Bearer | All tokens → Bearer |
| **GitHub-specific headers** | N/A | `Accept: application/vnd.github+json`, `X-GitHub-Api-Version: 2022-11-28`, `User-Agent: REBUSS-Pure/1.0` |

## 3. Authentication & Token Management

### Chained Authentication Pattern

Both providers implement identical chain-of-responsibility logic:

1. **PAT from config** — highest priority. If `PersonalAccessToken` is set in options,
   use it immediately. ADO wraps as `Basic` (base64 of `:pat`); GitHub sends as `Bearer`.
2. **Cached token** — loaded from `ILocalConfigStore` / `IGitHubConfigStore`
   (`%LOCALAPPDATA%/REBUSS.Pure/config.json` or `github-config.json`). Validity check:
   - ADO: `Basic` tokens have no expiry check; `Bearer` tokens must have `ExpiresOn`
     with 5-minute buffer (`TokenExpiresOn > UtcNow + 5min`).
   - GitHub: simple expiry check with same 5-minute buffer, no token type distinction.
3. **CLI tool** — `az account get-access-token` (ADO) or `gh auth token` (GitHub).
   Acquired token is cached for subsequent requests.
4. **Error** — throws `InvalidOperationException` with actionable multi-line message
   instructing user to run CLI login or configure a PAT.

**WHY chain, not strategy:** the chain evaluates lazily and short-circuits. Most
requests use the cached token (step 2) — the CLI tool (step 3) is expensive
(process spawn) and only runs when cache is empty or expired.

### Token Caching & Invalidation

Tokens are cached in JSON files under `%LOCALAPPDATA%/REBUSS.Pure/`:
- `config.json` — ADO: stores `AccessToken`, `TokenType`, `TokenExpiresOn`,
  plus `OrganizationName` / `ProjectName` / `RepositoryName`
- `github-config.json` — GitHub: stores `AccessToken`, `TokenExpiresOn`,
  plus `Owner` / `RepositoryName`

**Invalidation** is triggered by the `DelegatingHandler` on auth failure:
it calls `InvalidateCachedToken()` which nulls the `AccessToken` and `TokenExpiresOn`
fields in the cached config, then re-acquires via the chain (typically falls through
to CLI tool).

### DelegatingHandler Retry Logic

**ADO — `AuthenticationDelegatingHandler`:**
- Lazily resolves auth header per request via `IAuthenticationProvider.GetAuthenticationAsync`
- **HTML 203 detection:** Azure DevOps returns HTTP 2xx with HTML content when tokens
  expire (instead of 401). The handler checks `Content-Type` header for `text/html`:
  if an HTML response is received on a 2xx status code, it treats this as auth failure.
- Clones the request (reads content bytes, copies headers), invalidates token,
  re-acquires, retries **once**.

**GitHub — `GitHubAuthenticationHandler`:**
- Sets required GitHub headers on every request (`Accept`, `X-GitHub-Api-Version`, `User-Agent`)
- Retries on HTTP 401 (Unauthorized) or 403 (Forbidden)
- **Rate-limit exclusion:** 403 with `X-RateLimit-Remaining: 0` is NOT retried —
  the header value `"0"` indicates rate limiting, not auth failure. Re-authenticating
  would not help.
- Same clone-invalidate-reacquire-retry pattern, one attempt.

**WHY different detection:** ADO's load balancer returns 203 HTML for expired tokens
(a known behavior). GitHub returns proper 401/403 status codes. Each handler is
adapted to its platform's failure mode.

## 4. Configuration Resolution

### IPostConfigureOptions Pattern

Both providers register a `ConfigurationResolver` / `GitHubConfigurationResolver`
as `IPostConfigureOptions<TOptions>`. This runs lazily on the **first**
`IOptions<T>.Value` access.

**WHY not resolve in constructor:** configuration resolution depends on
`IWorkspaceRootProvider.ResolveRepositoryRoot()`, which needs MCP roots
(set during `initialize` handshake). At DI construction time, the MCP handshake
hasn't happened yet. `IPostConfigureOptions` defers resolution until the first
tool call actually needs the options.

### Merge Priority

The `Resolve` method applies a three-tier priority for each field:

```
User config (appsettings / env vars)  →  takes precedence if non-empty
  ↓ fallback
Auto-detected (git remote URL)         →  current workspace reality
  ↓ fallback
Cached (local config store)            →  last known good values
```

**Note:** CLI args are injected via `AddInMemoryCollection(cliOverrides)` in the
`ConfigurationBuilder` chain **before** options binding. Since `IConfiguration`
is hierarchical (last source wins), CLI args override appsettings and env vars.
When `PostConfigure` reads `options.OrganizationName`, CLI values are already
the "user config" and take top priority.

After successful resolution, the merged config is saved back to the local config
store for future runs.

### Workspace Root Resolution

`McpWorkspaceRootProvider` resolves the git repository root with this priority:

1. **CLI `--repo`** — explicitly set via `SetCliRepositoryPath()`. Highest priority
   because the user intentionally specified it.
2. **MCP roots** — extracted from `initialize` handshake `roots[].uri`. Converted
   from `file:///` URI to local path. Represents the IDE's workspace folder.
3. **`localRepoPath` config** — read directly from `IConfiguration` (not from
   `IOptions<T>`) to **avoid circular dependency** with `IPostConfigureOptions`
   (which itself needs the workspace root).

All candidate paths are validated:
- **Unexpanded variable guard:** `IsUnexpandedVariable` checks for `${` or `$(`
  prefixes — if present, the path is treated as unresolved and skipped.
- **`FindGitRepositoryRoot`** walks up the directory tree looking for a `.git`
  folder, returning the repository root rather than a subdirectory.

## 5. Analysis Pipeline

### Orchestrator Pattern

`ReviewContextOrchestrator` drives the review analysis:

1. **Fetch** — retrieves diff, metadata, and files via `IScmClient` (three parallel-ready
   calls, though currently sequential).
2. **Sort** — orders `IReviewAnalyzer[]` by their `Order` property (ascending).
3. **Filter** — calls `CanAnalyze(input)` on each analyzer to skip irrelevant ones.
4. **Run** — executes analyzers **sequentially** (not parallel) via `AnalyzeAsync`.
5. **Accumulate** — collects `AnalysisSection` results into `ReviewContext`.

**WHY sequential:** analyzers can depend on previous sections' output. Parallel
execution would break the `PreviousSections` data-sharing contract.

### Inter-Analyzer Data Sharing

`AnalysisInput` is an immutable record carrying diff, metadata, files, content
provider, and a `PreviousSections` dictionary. Before each analyzer runs, the
orchestrator creates a **new** `AnalysisInput` with the accumulated sections so far:

```
Analyzer A (Order=1)  →  receives PreviousSections = {}
  produces SectionA
Analyzer B (Order=2)  →  receives PreviousSections = { "sectionA": content }
  can read SectionA's output to inform its own analysis
```

**WHY immutable record + copy:** no shared mutable state between analyzers. Each
analyzer gets a snapshot of all prior results, preventing race conditions and
ordering bugs.

### Extension Point

Register `IReviewAnalyzer` in DI. `ReviewContextOrchestrator` discovers all
implementations via `IEnumerable<IReviewAnalyzer>` — no changes to the orchestrator
needed. See recipe 5.4 in `ProjectConventions.md`.

## 6. Local Review Pipeline

### Scope Model

`LocalReviewScope` is a sealed class with private constructor and static factory methods:

| Scope | Factory | Git Command | Base Ref | Target Ref |
|---|---|---|---|---|
| **WorkingTree** | `WorkingTree()` | `diff --name-status HEAD` | `HEAD` | `WORKING_TREE` (filesystem) |
| **Staged** | `Staged()` | `diff --name-status --cached HEAD` | `HEAD` | `:0` (index) |
| **BranchDiff** | `BranchDiff(base)` | `diff --name-status {base}...HEAD` | `{base}` | `HEAD` |

`Parse(string?)` maps user input: `null`/empty/`"working-tree"` → WorkingTree,
`"staged"` → Staged, anything else → BranchDiff with that value as base.

### Git Process Spawning

`LocalGitClient` runs git as a child process (`Process.Start` with
`UseShellExecute = false`, `CreateNoWindow = true`, `RedirectStandardOutput/Error`).
Parsing is minimal — only `--name-status` output (tab-delimited: `STATUS\tpath`)
and `status --porcelain` output are supported.

**`WorkingTreeRef` sentinel:** the constant `"WORKING_TREE"` is a synthetic git ref
that signals `GetFileContentAtRefAsync` to read the file directly from the filesystem
(`File.ReadAllTextAsync`) instead of running `git show`. This exists because
working-tree changes (unstaged edits) aren't addressable by any real git ref —
they exist only on disk.

For all other refs (`HEAD`, `:0`, commit SHAs, branch names), the method runs
`git show {ref}:{path}`. If the file doesn't exist at that ref (new file,
deleted file), the `GitCommandException` is caught and `null` is returned.

### File Content Resolution

`LocalReviewProvider.GetDiffRefs` determines base and target refs per scope:

- **WorkingTree:** base = `HEAD` (last commit), target = `WORKING_TREE` (filesystem).
  Captures staged + unstaged changes together.
- **Staged:** base = `HEAD`, target = `:0` (git index). `:0` is git's staging area
  ref, showing exactly what `git add` has staged.
- **BranchDiff:** base = `{baseBranch}` (the merge base), target = `HEAD`.
  Uses `{base}...HEAD` syntax in diff which finds the merge-base automatically.

The resolved content strings are fed to `IStructuredDiffBuilder.Build` which
produces `DiffHunk[]` using `LcsDiffAlgorithm` (LCS-based line diff).

## 7. Design Decisions & Rationale

- **Singletons everywhere:** services are stateless (state lives in options and
  config stores). `HttpClient` instances are expensive to create and should be
  reused. Singleton lifetime aligns with the long-lived MCP server process.

- **No abstract base class for providers:** `AzureDevOpsScmClient` and
  `GitHubScmClient` don't share a base class despite similar structure. Composition
  over inheritance — each facade delegates to its own set of providers with
  platform-specific logic. Forced inheritance would create coupling and empty
  virtual methods.

- **Embedded resources for prompts:** prompt markdown files are embedded in the
  assembly as resources. This ensures a single deployment artifact (the `.nupkg`)
  with version-locked prompts — no external files to manage or version separately.

- **Daily log rotation with 3-day retention:** `FileLoggerProvider` creates a new
  log file per day and deletes files older than 3 days. MCP servers are long-lived
  processes (run for the duration of an IDE session). Without rotation, log files
  would grow unbounded. 3-day retention balances debuggability with disk usage.

- **Direct `IConfiguration` read in `McpWorkspaceRootProvider`:** reads
  `AzureDevOps:LocalRepoPath` directly from `IConfiguration` instead of
  `IOptions<AzureDevOpsOptions>` to avoid a circular dependency. `IPostConfigureOptions`
  (the config resolver) depends on `IWorkspaceRootProvider`, and `IOptions<T>.Value`
  triggers `IPostConfigureOptions`. Reading the raw config key breaks the cycle.

- **`DelegatingHandler` registered as `Transient`:** HTTP message handlers must be
  transient because `IHttpClientFactory` manages their lifetime and pools them.
  The auth provider they depend on is singleton — thread-safe by design.

- **`ToolsCallMethodHandler` uses `Dictionary<string, IMcpToolHandler>`:** O(1)
  tool resolution by name. The dictionary is built once from
  `IEnumerable<IMcpToolHandler>` in the constructor, keyed by `ToolName`.
