# Project Conventions & Architecture Reference

> Stable reference for coding agents. Complements `CodebaseUnderstanding.md` (inventory).  
> Update this file only when architecture, conventions, or extension patterns change.

## 1. Architecture Overview

```
REBUSS.Pure.Core              (domain models, interfaces, shared logic, analysis pipeline)
    ↑                          ↑
REBUSS.Pure.AzureDevOps    REBUSS.Pure.GitHub       (SCM providers: API client → parsers → providers → facade)
    ↑                          ↑
REBUSS.Pure                                          (MCP server app: tool handlers, local review, CLI, DI root)
```

**Provider pattern:** `IScmClient` facade → fine-grained providers (`DiffProvider`, `MetadataProvider`, `FilesProvider`, `FileContentProvider`) → parsers → API client. Exactly **one provider per process**, selected by `Program.DetectProvider()`. Interface forwarding in DI registers the active facade as `IScmClient`, `IPullRequestDataProvider`, and `IFileContentDataProvider`.

**Analysis pipeline:** `ReviewContextOrchestrator` fetches data via `IScmClient`, then runs all `IReviewAnalyzer` implementations (ordered by `Order`) producing `AnalysisSection`s aggregated into `ReviewContext`. Analyzers receive `AnalysisInput` (a record carrying diff, metadata, files, content provider, and previously generated sections).

## 2. Data Flow

### PR Review: `get_pr_content(prNumber: 42, pageNumber: 1)`
```
MCP stdin → SDK (JSON-RPC dispatch) → [McpServerTool] method on GetPullRequestContentToolHandler
  → ExecuteAsync
    → IPullRequestDataProvider.GetDiffAsync(42)  [DI → AzureDevOpsScmClient or GitHubScmClient]
      → DiffProvider → API client (HTTP) → Parser (JSON → domain) → StructuredDiffBuilder (hunks)
    ← PullRequestDiff
  → FileTokenMeasurement.BuildCandidatesFromDiff → StructuredFileChange models
  → PlainTextFormatter.FormatFileDiff (one block per file) + FormatManifestBlock
  → IEnumerable<ContentBlock> → returned to SDK → JSON-RPC response on stdout
```

### Local Review: `get_local_files(scope)`
```
MCP stdin → SDK → [McpServerTool] method on GetLocalChangesFilesToolHandler
  → ExecuteAsync → ILocalReviewProvider.GetFilesAsync(scope)
    → ILocalGitClient (git child process: diff --name-status) → FileClassifier
  ← LocalReviewFiles → PlainTextFormatter.FormatFileList + FormatManifestBlock
  → IEnumerable<ContentBlock> on stdout
```

### CLI: `rebuss-pure init`
```
args → CliArgumentParser.Parse → Program.RunCliCommandAsync → InitCommand.ExecuteAsync
  → DetectProviderFromGitRemote → CreateAuthFlow (GitHub or AzureDevOps)
  → AuthFlow.RunAsync (gh auth / az login) → write mcp.json + copy prompts & instructions
```

## 3. Coding Conventions

| Aspect | Convention |
|---|---|
| **Framework** | .NET 10 (`net10.0`), C# 14, nullable `enable`, implicit usings `enable` |
| **Tool output** | Plain text `IEnumerable<ContentBlock>` (`TextContentBlock` per file/section); formatted by `PlainTextFormatter`; no JSON serialization in handler output |
| **JSON (MCP protocol)** | Handled by MCP SDK internally (compact JSON-RPC envelope) |
| **Naming** | `*Provider`, `*Parser`, `*ToolHandler`, `*ScmClient`; `I*` interfaces; `_field` privates |
| **Pure helpers** | `internal static` methods (e.g. `ParseRemoteUrl`, `MapState`) — unit-testable via `InternalsVisibleTo` |
| **Value types** | `readonly record struct` (e.g. `DiffEdit`, `LocalReviewScope`) |
| **Async** | `async` all the way; propagate `CancellationToken` to every I/O call |
| **DI** | Constructor injection; all singletons; interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` |
| **Errors** | Custom exceptions (`PullRequestNotFoundException`, etc.) caught in tool handlers → rethrown as `McpException` (SDK converts to error response) |
| **Logging** | `Microsoft.Extensions.Logging`; stderr via console provider; file via `FileLoggerProvider` (daily rotation, 3-day retention) |
| **CLI output** | All user output to `Console.Error` — stdout is **reserved** for MCP JSON-RPC stdio transport |
| **Child processes** | Must set `RedirectStandardInput = true` + `process.StandardInput.Close()` after `Start()` to prevent stdin handle inheritance deadlocks with SDK's stdio transport |

## 4. Testing Conventions

| Aspect | Convention |
|---|---|
| **Framework** | xUnit 2.9.3 + NSubstitute 5.3.0 |
| **Structure** | Test project mirrors source: `REBUSS.Pure.Tests/Tools/` ↔ `REBUSS.Pure/Tools/` |
| **Naming** | `{ClassUnderTest}Tests`, method: `MethodName_Scenario_ExpectedResult` |
| **Pattern** | Arrange/Act/Assert; mock interfaces (`Substitute.For<T>()`), use real value objects and models |
| **Test setup** | Mocks as `readonly` fields; SUT created in constructor; `NullLogger<T>.Instance` for loggers |
| **Provider tests** | Use real parsers + real `StructuredDiffBuilder`/`LcsDiffAlgorithm`; mock only API client |
| **Unit vs Smoke vs Contract** | Unit: isolated class. Smoke: compiled binary as child process (`McpProcessFixture`). Contract: real APIs with fixture PRs (env-var gated, auto-skip) |
| **`InternalsVisibleTo`** | Each project grants access to its test project + `REBUSS.Pure` (for composition) |

## 5. Extension Recipes

### 5.1 Add a new MCP tool
1. **Handler** → `REBUSS.Pure/Tools/{Name}ToolHandler.cs`: public class with `[McpServerToolType]`, public method with `[McpServerTool(Name = "tool_name")]` and `[Description]` on method + parameters. Returns `Task<IEnumerable<ContentBlock>>`. Inject dependencies via constructor.
2. **Formatter** → add formatter methods to `PlainTextFormatter` if new output format is needed; otherwise reuse existing methods
3. **Discovery** → automatic via `WithToolsFromAssembly()` in `Program.cs` — no manual DI registration needed for tools
4. **Tests** → `REBUSS.Pure.Tests/Tools/{Name}ToolHandlerTests.cs` (mock provider, call `ExecuteAsync` directly, use `AllText(blocks)` helper to verify text content)
5. **Smoke** → update `REBUSS.Pure.SmokeTests/McpProtocol/McpServerSmokeTests.cs` tool count assertion

### 5.2 Add a new domain model
1. **Model** → `REBUSS.Pure.Core/Models/{Name}.cs`
2. **Interface** → extend `IPullRequestDataProvider` or `IFileContentDataProvider` in `REBUSS.Pure.Core/IScmClient.cs` if new capability
3. **Parsers** → add parser interface + implementation in each provider's `Parsers/` folder
4. **Providers** → update relevant fine-grained provider in each provider's `Providers/` folder
5. **Tests** → parser tests + provider tests in both `AzureDevOps.Tests` and `GitHub.Tests`

### 5.3 Modify plain text output of existing tool
1. **Formatter** → edit `PlainTextFormatter` method for this tool's output section
2. **Handler** → update call site in the tool handler if the formatter signature changes
3. **Tests** → update unit tests (text content assertions in `AllText(blocks)`) and smoke tests

> **Note:** JSON output DTOs (`*Result.cs` with `[JsonPropertyName]`) have been removed. Output is produced entirely by `PlainTextFormatter` methods.

### 5.4 Add a new `IReviewAnalyzer`
1. **Implementation** → `REBUSS.Pure.Core/Analysis/{Name}Analyzer.cs` implementing `IReviewAnalyzer` (`SectionKey`, `DisplayName`, `Order`, `CanAnalyze`, `AnalyzeAsync`)
2. **DI** → register as `services.AddSingleton<IReviewAnalyzer, {Name}Analyzer>()` (in `Program.ConfigureBusinessServices` or provider's `ServiceCollectionExtensions`)
3. `ReviewContextOrchestrator` discovers and invokes it automatically via `IEnumerable<IReviewAnalyzer>`

### 5.5 Add or modify a prompt
1. **Source** → edit/create embedded resource in `REBUSS.Pure/Cli/Prompts/{name}.md`
2. **Deploy** → if new file, update `InitCommand.cs` to include it in the copy list
3. Rebuild — `rebuss-pure init` **always overwrites** deployed copies in `.github/prompts/` and `.github/instructions/`

### 5.6 Add a new CLI command
1. **Command** → `REBUSS.Pure/Cli/{Name}Command.cs` implementing `ICliCommand`
2. **Parser** → add recognition in `CliArgumentParser.Parse`
3. **Dispatch** → add `case` in `Program.RunCliCommandAsync`
4. **Tests** → `REBUSS.Pure.Tests/Cli/{Name}CommandTests.cs` + update `CliArgumentParserTests.cs`

### 5.7 Add a new SCM provider
1. **Project** → new `REBUSS.Pure.{Provider}` with reference to `REBUSS.Pure.Core`
2. **Structure** → `Api/`, `Parsers/`, `Providers/`, `Configuration/` folders (mirror AzureDevOps/GitHub)
3. **Facade** → implement `IScmClient` + `ServiceCollectionExtensions.Add{Provider}Provider(IConfiguration)`
4. **Wire up** → add `case` in `Program.DetectProvider` + `ConfigureBusinessServices` switch
5. **Tests** → new `REBUSS.Pure.{Provider}.Tests` project

## 6. Configuration & Auth Quick Reference

| Priority | Config source |
|---|---|
| 1 (highest) | CLI args (`--pat`, `--org`, `--project`, `--repository`, `--owner`, `--provider`) |
| 2 | Environment variables (`AzureDevOps__*`, `GitHub__*`) |
| 3 | `appsettings.Local.json` (gitignored, for secrets) |
| 4 | `appsettings.json` |
| 5 | Auto-detect from `git remote get-url origin` |
| 6 (lowest) | Cached config (`%LOCALAPPDATA%/REBUSS.Pure/config.json` or `github-config.json`) |

**Auth chain (per provider):** PAT → cached token → CLI tool (`az account get-access-token` / `gh auth token`) → error with actionable instructions.

**Provider detection:** explicit `--provider` → `GitHub.Owner` present → `AzureDevOps.OrganizationName` present → git remote URL → default `AzureDevOps`.
