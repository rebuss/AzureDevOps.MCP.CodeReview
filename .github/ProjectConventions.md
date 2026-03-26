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

### PR Review: `get_pr_diff(prNumber: 42)`
```
MCP stdin → McpServer (JSON-RPC loop) → ToolsCallMethodHandler (resolves by ToolName)
  → GetPullRequestDiffToolHandler.ExecuteAsync
    → IPullRequestDataProvider.GetDiffAsync(42)  [DI → AzureDevOpsScmClient or GitHubScmClient]
      → DiffProvider → API client (HTTP) → Parser (JSON → domain) → StructuredDiffBuilder (hunks)
    ← PullRequestDiff
  → map to StructuredDiffResult → serialize JSON → ToolResult → JSON-RPC response on stdout
```

### Local Review: `get_local_files(scope)`
```
MCP stdin → ToolsCallMethodHandler → GetLocalChangesFilesToolHandler.ExecuteAsync
  → ILocalReviewProvider.GetFilesAsync(scope)
    → ILocalGitClient (git child process: diff --name-status) → FileClassifier
  ← LocalReviewFiles → map to LocalReviewFilesResult → JSON on stdout
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
| **JSON (tool output)** | `camelCase`, `WriteIndented = true`, `WhenWritingNull` ignore; `[JsonPropertyName]` on all DTO properties |
| **JSON (MCP protocol)** | `camelCase`, **no indent**, `WhenWritingNull` ignore (`SystemTextJsonSerializer`) |
| **Naming** | `*Provider`, `*Parser`, `*ToolHandler`, `*ScmClient`; `I*` interfaces; `_field` privates |
| **Pure helpers** | `internal static` methods (e.g. `ParseRemoteUrl`, `MapState`) — unit-testable via `InternalsVisibleTo` |
| **Value types** | `readonly record struct` (e.g. `DiffEdit`, `LocalReviewScope`) |
| **Async** | `async` all the way; propagate `CancellationToken` to every I/O call |
| **DI** | Constructor injection; all singletons; interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` |
| **Errors** | Custom exceptions (`PullRequestNotFoundException`, etc.) caught in tool handlers → `ToolResult { IsError = true }` |
| **Logging** | `Microsoft.Extensions.Logging`; stderr via console provider; file via `FileLoggerProvider` (daily rotation, 3-day retention) |
| **CLI output** | All user output to `Console.Error` — stdout is **reserved** for MCP JSON-RPC stdio transport |

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
1. **Output model** → `REBUSS.Pure/Tools/Models/{Name}Result.cs` (class with `[JsonPropertyName]` on all properties)
2. **Handler** → `REBUSS.Pure/Tools/{Name}ToolHandler.cs` implementing `IMcpToolHandler` (`ToolName`, `GetToolDefinition`, `ExecuteAsync`)
3. **DI** → `Program.ConfigureServices`: `services.AddSingleton<IMcpToolHandler, {Name}ToolHandler>()`
4. **Tests** → `REBUSS.Pure.Tests/Tools/{Name}ToolHandlerTests.cs` (mock provider, verify JSON structure)
5. **Smoke** → update `REBUSS.Pure.SmokeTests/McpProtocol/McpServerSmokeTests.cs` tool count assertion

### 5.2 Add a new domain model
1. **Model** → `REBUSS.Pure.Core/Models/{Name}.cs`
2. **Interface** → extend `IPullRequestDataProvider` or `IFileContentDataProvider` in `REBUSS.Pure.Core/IScmClient.cs` if new capability
3. **Parsers** → add parser interface + implementation in each provider's `Parsers/` folder
4. **Providers** → update relevant fine-grained provider in each provider's `Providers/` folder
5. **Tests** → parser tests + provider tests in both `AzureDevOps.Tests` and `GitHub.Tests`

### 5.3 Modify JSON output format of existing tool
1. **DTO** → edit model in `REBUSS.Pure/Tools/Models/`; preserve or add properties (prefer additive changes)
2. **Handler** → update mapping logic in the tool handler's `Build*Result` method
3. **Tests** → update unit tests (JSON assertions), smoke tests (structure), and contract tests (live expectations)

### 5.4 Add a new `IReviewAnalyzer`
1. **Implementation** → `REBUSS.Pure.Core/Analysis/{Name}Analyzer.cs` implementing `IReviewAnalyzer` (`SectionKey`, `DisplayName`, `Order`, `CanAnalyze`, `AnalyzeAsync`)
2. **DI** → register as `services.AddSingleton<IReviewAnalyzer, {Name}Analyzer>()` (in `Program.ConfigureServices` or provider's `ServiceCollectionExtensions`)
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
4. **Wire up** → add `case` in `Program.DetectProvider` + `ConfigureServices` switch
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
