# Architecture Deep Dive

> Internal architecture reference for coding agents working on cross-cutting changes.
> For quick conventions and extension recipes, see `ProjectConventions.md`.
> For file inventory and DI registrations, see `CodebaseUnderstanding.md`.
> Update this file when internal mechanics, protocol handling, or design patterns change.

## 1. MCP Protocol Layer

### SDK-Based Server Setup (ModelContextProtocol v1.2.0)

The project uses the official `ModelContextProtocol` NuGet package (v1.2.0) instead of
hand-rolled MCP infrastructure. `Program.cs` wires the server with the SDK's fluent
builder pattern on `Host.CreateApplicationBuilder()`:

```
AddMcpServer(options => { ServerInfo = ... })
  → WithStdioServerTransport()
  → WithToolsFromAssembly()
```

1. **`AddMcpServer()`** — registers the SDK's `McpServer` as a DI service. Accepts a
   callback to set `ServerInfo` (name = `"REBUSS.Pure"`, version = `"1.0.0"`).
2. **`WithStdioServerTransport()`** — binds stdin/stdout as the JSON-RPC transport.
   The SDK manages message framing, serialization, and response writing automatically.
3. **`WithToolsFromAssembly()`** — reflectively scans the assembly for classes decorated
   with `[McpServerToolType]` and registers their `[McpServerTool]`-annotated methods
   as MCP tools. No manual tool registration is required.

The application runs via `builder.Build().RunAsync(cts.Token)` using the .NET Generic
Host lifecycle. `CancellationTokenSource` handles Ctrl+C / `SIGTERM` for clean shutdown.

### What the SDK Handles Automatically

The SDK owns the entire JSON-RPC protocol layer. The project contains **no** custom
protocol infrastructure — no server loop, no method handlers, no serializer, no
transport, no protocol negotiation:

| Concern | SDK Responsibility |
|---|---|
| **JSON-RPC framing** | Reads/writes newline-delimited JSON on stdin/stdout |
| **initialize / initialized** | Handshake, protocol version negotiation, capability exchange |
| **tools/list** | Enumerates `[McpServerTool]` methods, auto-generates `inputSchema` from method signatures and `[Description]` attributes |
| **tools/call** | Dispatches by tool name to the matching method, deserializes arguments, returns result |
| **Error codes** | Parse errors (`-32700`), method not found (`-32601`), invalid request (`-32600`), internal error (`-32603`) |
| **Notifications** | Handled per MCP spec (silently consumed or dispatched as appropriate) |

### Tool Handler Pattern (Attribute-Based)

Tool handlers are plain C# classes — no interfaces to implement:

```
[McpServerToolType]                      ← marks class as containing MCP tools
public class SomeToolHandler
{
    public SomeToolHandler(IDep dep) … ← standard constructor injection

    [McpServerTool(Name = "tool_name")]  ← tool name visible to MCP clients
    [Description("Tool description")]    ← used by SDK for tool metadata
    public async Task<string> ExecuteAsync(
        [Description("param help")] int param,  ← parameter descriptions become inputSchema
        CancellationToken ct = default)
    { … }
}
```

**Key characteristics:**
- **`[McpServerToolType]`** on the class — discovered by `WithToolsFromAssembly()`.
- **`[McpServerTool(Name = "...")]`** on the method — defines the MCP tool name.
- **`[Description]`** on method and each parameter — the SDK uses these to generate
  the `inputSchema` JSON automatically. No manual schema definition needed.
- **Constructor injection** — the SDK resolves dependencies from the DI container when
  instantiating tool handler classes. All existing business service registrations work
  unchanged.
- **Return type** — `async Task<string>`. Tool handlers serialize their response to
  JSON (`camelCase`, `WriteIndented = true`) and return the string.
- **Optional parameters** — nullable types with defaults (`int? prNumber = null`,
  `string? scope = null`) map to optional fields in the generated `inputSchema`.

### Transport & stdout Reservation

The SDK's stdio transport owns stdout exclusively for JSON-RPC messages. All
non-protocol output (logging, diagnostics, user messages) **must** go to stderr.
`Program.cs` configures this via `LogToStandardErrorThreshold = LogLevel.Trace` and
the custom `FileLoggerProvider`. Violating the stdout reservation breaks the MCP
transport.

### Child Process stdin Isolation

Because the SDK's stdio transport owns stdin, all child processes spawned by the
server **must** set `RedirectStandardInput = true` and immediately call
`process.StandardInput.Close()` after `process.Start()`. Without this, child
processes (e.g., `git`) inherit the parent's stdin handle, causing a deadlock where
the SDK cannot read from stdin while the child holds the handle. This applies to:
- `LocalGitClient.RunGitAsync` (local review tools)
- `GitRemoteDetector` / `GitHubRemoteDetector` (provider configuration)
- `GetGitRemoteUrl()` in `Program.cs` (startup detection)

### SDK Limitation: No `IMcpServer` Interface

SDK v1.2.0 exposes only the concrete `McpServer` class — there is no `IMcpServer`
abstraction. Components that need `McpServer` (such as `McpWorkspaceRootProvider`)
must resolve it from `IServiceProvider` at runtime. This is a conscious trade-off
documented in §4 (Workspace Root Resolution).

## 2. Provider Architecture

### The Provider Pattern (detailed)

Each SCM provider follows a layered architecture:

```
IScmClient (facade)
  ├── DiffProvider      → API client → Parser → StructuredDiffBuilder → FileChange[]
  ├── MetadataProvider  → API client → Parser → FullPullRequestMetadata
  └── FilesProvider     → API client → Parser → FileClassifier → PullRequestFiles  (no diff fetching)
```

**WHY this decomposition:**
- **SRP** — each provider handles one concern (diff, metadata, files).
- **Testability** — providers can be tested with real parsers and mocked API client,
  without instantiating the full facade.
- **Performance** — `FilesProvider` calls lightweight file-list APIs directly (e.g.,
  GitHub PR files endpoint, Azure DevOps iteration-changes endpoint), avoiding the
  expensive per-file content fetches that `DiffProvider` performs for structured diffs.

The facade (`AzureDevOpsScmClient` / `GitHubScmClient`) is a pure delegation layer:
it forwards calls to the appropriate provider and enriches metadata with
provider-agnostic fields (`WebUrl`, `RepositoryFullName`) derived from options.

### Interface Forwarding & Narrow Dependencies

`IScmClient` extends `IPullRequestDataProvider` + `IRepositoryArchiveProvider`.
`ServiceCollectionExtensions` registers the concrete facade as three interfaces:

```csharp
services.AddSingleton<AzureDevOpsScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<AzureDevOpsScmClient>());
```

**WHY:** tool handlers depend on narrow interfaces (`IPullRequestDataProvider` or
`IRepositoryArchiveProvider`), not the full `IScmClient`. This means:
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

`McpWorkspaceRootProvider` (in `Services/`) resolves the git repository root with
this priority:

1. **CLI `--repo`** — explicitly set via `SetCliRepositoryPath()`. Highest priority
   because the user intentionally specified it.
2. **MCP roots** — lazily fetched from the SDK on first access via
   `McpServer.RequestRootsAsync()`. Converted from `file:///` URI to local path.
   Represents the IDE's workspace folder.
3. **`localRepoPath` config** — read directly from `IConfiguration` (not from
   `IOptions<T>`) to **avoid circular dependency** with `IPostConfigureOptions`
   (which itself needs the workspace root).

**Lazy `McpServer` resolution via `IServiceProvider`:** `McpWorkspaceRootProvider`
cannot take `McpServer` as a constructor dependency because `McpServer` is registered
by `AddMcpServer()` and depends (transitively) on services that depend on workspace
root resolution — creating a circular dependency. Instead, `McpWorkspaceRootProvider`
injects `IServiceProvider` and resolves `McpServer` at runtime in
`EnsureRootsFetched()`:

```csharp
var mcpServer = _serviceProvider.GetService<McpServer>();
// null in CLI mode (no MCP server registered)
var result = Task.Run(async () =>
    await mcpServer.RequestRootsAsync(...)
).GetAwaiter().GetResult();
```

This is a justified service-locator pattern — the alternative (constructor injection)
would create an unresolvable DI cycle. The `Task.Run` + `GetResult()` blocking call
is safe because console apps have no `SynchronizationContext`.

**Thread safety:** all shared state (`_rootUris`, `_cliRepositoryPath`, `_rootsFetched`)
is guarded by a `lock` object. `EnsureRootsFetched()` runs at most once per process
lifetime.

**Note:** SDK v1.2.0 provides only the concrete `McpServer` class — no `IMcpServer`
interface. `GetService<McpServer>()` returns `null` in CLI mode (where `AddMcpServer()`
is not called), which `EnsureRootsFetched()` handles gracefully by skipping MCP root
resolution.

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

## 6. Repository Download Pipeline

### Trigger

When `get_pr_metadata` is called, the handler fires a background download via
`IRepositoryDownloadOrchestrator.TriggerDownloadAsync(prNumber, commitRef)`.
This is fire-and-forget — the metadata response returns immediately without waiting.

### Download Flow

```
get_pr_metadata → MetadataHandler
  → _downloadOrchestrator.TriggerDownloadAsync(prNumber, commitRef)
    → Background Task.Run:
      1. IRepositoryArchiveProvider.DownloadRepositoryZipAsync(commitRef, zipPath)
         (Azure DevOps: Items API ?$format=zip | GitHub: /zipball/{ref})
      2. ZipFile.ExtractToDirectory(zipPath, extractDir)
      3. Delete ZIP immediately
      4. State = Ready, ExtractedPath = extractDir
```

### Lifecycle

- **Deduplication**: Same PR + same commit = no-op. Different PR = cancel old, start new.
- **Shutdown**: `IHostApplicationLifetime.ApplicationStopping` triggers cleanup of
  `rebuss-repo-{pid}/` directory.
- **Startup**: `RepositoryCleanupService` (`IHostedService`) scans for orphaned
  `rebuss-repo-*` directories, extracts PID from name, deletes if process not running.
- **Temporary storage**: `{Path.GetTempPath()}/rebuss-repo-{Environment.ProcessId}/`

## 6a. Progressive PR Metadata (PrEnrichmentOrchestrator)

### Why this exists

The MCP host enforces a hard ~30 s tool-call timeout. For large PRs, computing
content paging requires fetching the diff *and* running the enrichment pipeline
(`CompositeCodeProcessor` → Roslyn-backed enrichers → per-file before/after
windows + structural change blocks), which on big repos can exceed that ceiling.

> **Note (feature 011)**: `CompositeCodeProcessor.AddBeforeAfterContext` short-circuits
> at the top via `DiffLanguageDetector.IsAlreadyEnriched`, returning the input
> unchanged if it already carries any of the enricher-emitted markers (`[scope:`,
> `[structural-changes]`, `[dependency-changes]`, `[call-sites]`). This makes the
> chain idempotent and is the single point of policy that covers all five enrichers.
> The hunk-rebuild logic in `DiffParser.RebuildDiffWithContext` is now a three-phase
> pipeline (cluster adjacent hunks → expand each cluster with shared per-axis-clamped
> context → render) that guarantees one merged hunk per non-overlapping range, valid
> unified-diff syntax, and no duplicated source lines across adjacent hunks.

Without the orchestrator, `get_pr_metadata` for an oversized PR would return a
raw timeout error and the review could not start.

### Flow

```
get_pr_metadata(prNumber, modelName)
  ├─> _metadataProvider.GetMetadataAsync (fast)
  ├─> _orchestrator.TriggerEnrichment(prNumber, headSha, safeBudget)   ── fire-and-forget
  │     └─> Task.Run(BackgroundBodyAsync) under _shutdownToken         ── runs to completion
  │           ├─> _diffCache.GetOrFetchDiffAsync
  │           ├─> FileTokenMeasurement.BuildEnrichedCandidatesAsync
  │           ├─> PackingPriorityComparer.Instance.Sort
  │           └─> _pageAllocator.Allocate → PrEnrichmentResult cached
  └─> WaitForEnrichmentAsync(prNumber, linkedCts.Token)                ── linkedCts.CancelAfter(28 000 ms)
        ├─> Ready in time:  emit "Content paging:" block
        ├─> OperationCanceledException (internal timeout, !callerCt):
        │     return basic summary + "Content paging: not yet available" (FR-004)
        └─> Failed snapshot or wait threw:
              return basic summary + FormatFriendlyStatus failure block (FR-017)

(later)

get_pr_content(prNumber, pageNumber)
  ├─> snapshot = _orchestrator.TryGetSnapshot(prNumber)
  ├─> Failed?            return FormatFriendlyStatus failure block (no retrigger)
  ├─> null?              cold-start: GetMetadataAsync → TriggerEnrichment
  ├─> Ready w/ stale     supersede: TriggerEnrichment with new safeBudget
  │   safeBudget?
  ├─> WaitForEnrichmentAsync(linkedCts.Token, 28 000 ms)
  ├─> success:           BuildPageBlocks from result.Allocation + result.EnrichedByPath
  └─> internal timeout:  return FormatFriendlyStatus "still preparing" (FR-014/FR-016)
```

### Load-bearing semantic: caller cancellation never cancels background body

The orchestrator captures `IHostApplicationLifetime.ApplicationStopping` once at
construction and uses *only* that token (or a CTS linked to it) for the
background body. The caller's `CancellationToken` is passed exclusively to
`Task.WaitAsync(ct)` inside `WaitForEnrichmentAsync`, which governs the *wait*
but **never** the work itself. This is what enables a metadata call to return
a basic summary at second 28, leave the background body running, and a
follow-up content call ten seconds later to find the result in cache.

This semantic is asserted by `PrEnrichmentOrchestratorTests
.CallerCancellation_DoesNotCancelBackgroundBody` — do not regress it.

### Job lifecycle

| Status | Transition |
|---|---|
| `FetchingDiff` | Set when `BackgroundBodyAsync` enters; about to await `_diffCache.GetOrFetchDiffAsync` |
| `Enriching` | Set after diff fetch returns; about to call `BuildEnrichedCandidatesAsync` |
| `Ready` | Set after `_pageAllocator.Allocate` succeeds; `Result` populated |
| `Failed` | Set on any exception other than shutdown-derived `OperationCanceledException`; `Failure` populated via `PrEnrichmentFailure.From(ex)` (sanitized; no stack trace; `%LOCALAPPDATA%` redacted per Principle VIII) |

Idempotency:
- Same `(prNumber, headSha)` re-trigger → no-op (existing job reused)
- Different `headSha` for same `prNumber` → old job's CTS cancelled, new job started
- `Failed` state for same SHA → next `TriggerEnrichment` starts a fresh job (FR-018)

### Configuration

`appsettings.json` `Workflow` section:
- `MetadataInternalTimeoutMs` (default `28000`)
- `ContentInternalTimeoutMs` (default `28000`)

Both must be strictly less than the host's hard tool-call ceiling (typically
30 000 ms) to leave a serialization margin. Tunable per environment.

### Constitution deviation

Mutable per-PR state on a singleton instance (Principle VI says services must
be stateless). This deviation mirrors `RepositoryDownloadOrchestrator` exactly
and is documented in `specs/net-specific/plan.md` *Complexity Tracking*. Both
orchestrators exist because `IOptions<T>` cannot express in-flight `Task`
lifecycles, and a static class would make tests impossible without process
restart.

## 6b. Copilot Review Pipeline (CopilotReviewOrchestrator)

### Why parallel dispatch

`CopilotReviewOrchestrator` coordinates server-side Copilot review of every page
of enriched content. Pages are dispatched **concurrently** via `Task.Run` per page
plus `Task.WhenAll`. The `CopilotRequestThrottle` (a static `SemaphoreSlim(1,1)`
with a 3-second minimum spacing) serializes the actual SDK calls, but model
response wait times overlap across pages — yielding wall-time reduction of roughly
`3s × (N-1) + max(response_time)` vs `N × response_time` sequentially.

### Thread safety

| Concern | Mechanism |
|---|---|
| `pageResults[idx]` array | Each task writes to a unique index — no contention |
| `CompletedPages` counter | `Interlocked.Increment` — lock-free atomic |
| `CurrentActivity` | `volatile string?` — last writer wins (cosmetic) |
| `Status`, `Result`, `ErrorMessage` | Guarded by `_lock` |
| Job dictionary | `ConcurrentDictionary<string, CopilotReviewJob>` |

### Progress observation

`CopilotReviewWaiter` polls `TryGetSnapshot()` at a configurable interval
(`CopilotReviewProgressPollingIntervalMs`, default 2000ms) and emits two kinds of
IDE notifications:

1. **`"Copilot review — X/N pages complete"`** whenever `CompletedPages` increases.
   This message is inherently order-agnostic — pages may complete out of order
   (e.g. page 3 before page 2), but the notification only shows the aggregate count.
2. **Phase activities** read from `CurrentActivity` whenever it changes —
   `"Allocated N pages — reviewing in parallel"`, `"Resolving N scopes for validation"`,
   `"Validating findings: page X/Y"`, and `"Review complete — X/N pages succeeded"`.

**Per-page `CurrentActivity` is intentionally not published.** Pages run in parallel
and each concurrent task would overwrite `CurrentActivity` with its own "waiting"
message, causing the IDE feed to flip-flop between pages. The monotonic
`CompletedPages` counter is the sole page-level progress signal; per-attempt retries
are visible in the log only. The waiter also intentionally does **not** reset its
"last reported activity" tracker on page-completion milestones so a stale phase
message can never be re-emitted after a counter tick.

### Retry loop

Each page gets up to 3 attempts (`MaxAttemptsPerPage = 3`) inside
`ReviewPageWithRetryAsync`. No backoff — retries fire immediately. On exhaustion,
the orchestrator fills in `FailedFilePaths` (only it knows which files were on
the page) and returns a `CopilotPageReviewResult.Failure`.

### Finding validation pipeline (Feature 021)

After all page reviews complete (and provided `CopilotReviewOptions.ValidateFindings`
is `true` and both `FindingValidator` + `FindingScopeResolver` resolved through DI),
`ValidateAllFindingsAsync` runs as a fifth phase before the orchestrator surfaces
the result. Its job is to filter false positives produced by the page-review pass:

```
parse → resolve scopes → severity-order → token-budget pages → SDK call(s) → map back → filter
```

| Phase | Component | Detail |
|---|---|---|
| Parse | `FindingParser.Parse` | Per-page Markdown → `ParsedFinding[]` + the unparseable remainder (free-form prose preserved verbatim, FR-012). The regex is a tolerant safety net over what `copilot-page-review.md` demands: swallows leading list markers (`- `, `* `, `1. `), and for the line expression accepts `42`, `~42`, `≈42`, `42-50`, `approx 42`, `(line unknown)` — the first integer is extracted; "unknown" yields `LineNumber == null` |
| Threshold | orchestrator | If total findings > `MaxValidatableFindings` (default 40) → skip validation entirely (likely a systemic issue) |
| Resolve scopes | `FindingScopeResolver` | **Non-`.cs`** ⇒ `NotCSharp` (passthrough, no Copilot call). **Missing source** ⇒ `SourceUnavailable`. **`.cs` with source** — smart-line + fallback chain: (1) if `LineNumber == null`, ask `FindingLineResolver` (in `RoslynProcessor`) to locate a line from identifiers cited in backticks in the description; (2) run `FindingScopeExtractor` at that line — when the method body exceeds `MaxScopeLines` (default 150), truncate with a window ±`MaxScopeLines/2` plus `// ... (X lines omitted) ...` markers; (3) if (2) fails (line on a `using`/top-level/malformed syntax), retry `FindingLineResolver` with the original line as a hint and re-run (2); (4) still failing ⇒ whole-file fallback: truncate source to `MaxScopeLines × 2` centred on the middle with a `// ... (X lines omitted from middle) ...` marker, scope name tagged `<entire file: {path}>`, **ResolutionFailure.None** so it still reaches Copilot. This chain was added after observing that Copilot frequently omits line numbers or writes approximations for file-level findings — previously those would silently short-circuit to `ScopeNotFound → Uncertain` and skip validation |
| Phase 1 verdicts | `FindingValidator` | Deterministic, no Copilot call: `NotCSharp` → `Valid` (passthrough), `SourceUnavailable` → `Uncertain`. Note: `ScopeNotFound` is no longer reachable for `.cs` findings — the whole-file fallback keeps them in the Copilot-bound set |
| Severity order | `FindingSeverityOrderer.Order` | Stable sort by severity rank: `critical=0`, `major=1`, `minor=2`, unknown last. Ensures the most important issues land in the first SDK call's prompt |
| Token budgeting | `IPageAllocator` + `ITokenEstimator` | Build one synthetic `PackingCandidate` per finding+scope pair, with `EstimatedTokens` from `EstimateTokenCount` over the per-finding prompt section. Allocate against `ReviewBudgetTokens − templateOverhead` so the wrapping prompt template (loaded once and measured) is accounted for. **Token budget — not a fixed batch size — controls how many findings fit per Copilot SDK call.** This is the same mechanism used to paginate the original review pass |
| SDK calls | `FindingValidator.ValidatePageAsync` | One `ICopilotSessionFactory.CreateSessionAsync` call per allocator page. Inspection writer kind = `validation-{pageNumber}`. Response parsed via index-based regex (`**Finding {N}: VALID\|FALSE_POSITIVE\|UNCERTAIN**`) so verdict mapping is order-independent |
| Order check | `FindingValidator.VerifyResponseOrder` | Re-runs `IsOrderedBySeverity` on the response sequence; if Copilot scrambled severities, log a warning. Verdicts remain correctly mapped via the index regex — the warning is informational |
| Map back | orchestrator | Walk per-page findings in original order, slice the validator's flat result array per page, hand to `FindingFilterer.Apply(remainder, pageValidated)` |
| Rebuild | `FindingFilterer.Apply` | Emit `Valid` verbatim, `Uncertain` prefixed with `[uncertain]`, omit `FalsePositive`. Append `_Validation: V confirmed, F filtered, U uncertain_` footer; collapse to `No issues found.` when everything was filtered and no remainder existed |

Graceful-degradation policy (FR-012) is preserved at every step: validator failure
keeps original findings as `Valid` so a flaky validation pass never breaks the
review output.

#### What gets logged where

- `_inspection.WritePromptAsync` / `WriteResponseAsync` capture each Copilot SDK
  exchange. Kinds in use: `page-{N}-review` from `CopilotPageReviewer`,
  `validation-{N}` from `FindingValidator`. There is no separate "summary" file —
  the validation prompt is itself the consolidated dump of every finding (with
  scope source) and the response carries every verdict, so they double as the
  end-of-review summary.
- `ILogger` carries phase transitions, retries, and validation warnings (skipped
  due to threshold, page failure with graceful degradation, scrambled response order).

## 7. Local Review Pipeline

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

## 8. Design Decisions & Rationale

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

- **Service-locator for `McpServer` in `McpWorkspaceRootProvider`:** `McpServer` is
  resolved from `IServiceProvider` at runtime instead of constructor injection. This
  breaks the normal DI pattern but is necessary to avoid a circular dependency:
  `McpServer` → tool handlers → options → `IPostConfigureOptions` →
  `IWorkspaceRootProvider` → `McpServer`. The service-locator use is isolated to a
  single call site (`EnsureRootsFetched`) and returns `null` gracefully in CLI mode.

- **Attribute-based tool discovery over interface-based:** the SDK's
  `[McpServerToolType]` / `[McpServerTool]` pattern replaces the previous
  `IMcpToolHandler` interface. New tools are added by creating a class with the
  correct attributes — no DI registration, no handler dictionary, no changes to any
  existing code. The SDK generates `inputSchema` from method signatures and
  `[Description]` attributes, eliminating manual schema maintenance.
