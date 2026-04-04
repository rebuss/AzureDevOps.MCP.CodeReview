# CLAUDE.md — Starting Point for Claude

This file is the entry point for Claude. Read it before every task.

---

## What this project is

**REBUSS.Pure** is an MCP (Model Context Protocol) server for AI-assisted code review.
It exposes tools that let AI agents fetch PR diffs, metadata, file contents, and local git changes from Azure DevOps and GitHub. It runs as a .NET 10 console app (`rebuss-pure`) using the official `ModelContextProtocol` SDK v1.2.0.

---

## Reference docs — read only what the task requires

| File | Read when you need to… |
|---|---|
| `.github/CodebaseUnderstanding.md` | Locate a file, find which class owns a responsibility, check DI registrations |
| `.github/ProjectConventions.md` | Follow a naming/coding/testing convention, understand data flow |
| `.github/Architecture.md` | Understand MCP protocol layer, provider internals, auth chain, analysis pipeline |
| `.github/Contracts.md` | Work with tool input/output format, add fields, fix contract tests |
| `.github/ExtensionRecipes.md` | Add a new MCP tool, analyzer, CLI command, SCM provider, or config option |

**Rules:**
- Always check `CodebaseUnderstanding.md` (file locations) and `ProjectConventions.md` (conventions) before writing code.
- Read `Architecture.md` only for internals bugs. Read `ExtensionRecipes.md` only for new feature types.
- Trust the reference docs over assumptions; do not guess file paths or patterns.

---

## Solution structure

| Project | Purpose |
|---|---|
| `REBUSS.Pure.Core` | Domain models, interfaces, shared diff/classification logic, analysis pipeline |
| `REBUSS.Pure.AzureDevOps` | Azure DevOps provider: API client → parsers → providers → `AzureDevOpsScmClient` facade |
| `REBUSS.Pure.GitHub` | GitHub provider: API client → parsers → providers → `GitHubScmClient` facade |
| `REBUSS.Pure` | MCP server app: tool handlers, local review, CLI, DI root (`Program.cs`) |
| `REBUSS.Pure.Core.Tests` | Unit tests for Core |
| `REBUSS.Pure.AzureDevOps.Tests` | Unit tests for Azure DevOps provider |
| `REBUSS.Pure.GitHub.Tests` | Unit tests for GitHub provider |
| `REBUSS.Pure.Tests` | Unit + integration tests for MCP server |
| `REBUSS.Pure.SmokeTests` | Smoke + live contract tests (runs compiled binary as child process) |

---

## Architecture at a glance

```
REBUSS.Pure.Core              (domain models, interfaces, analysis pipeline)
    ↑                                    ↑
REBUSS.Pure.AzureDevOps    REBUSS.Pure.GitHub   (SCM providers)
    ↑                                    ↑
REBUSS.Pure                                      (MCP server app)
```

**Provider pattern:** `IScmClient` facade → fine-grained providers (`DiffProvider`, `MetadataProvider`, `FilesProvider`, `FileContentProvider`) → parsers → API client. Exactly **one provider per process**, selected at startup by `Program.DetectProvider()`.

**MCP tool pattern:** plain C# classes with `[McpServerToolType]`/`[McpServerTool]` attributes. Discovered automatically by `WithToolsFromAssembly()` — no manual registration. Return `Task<IEnumerable<ContentBlock>>` (plain text via `PlainTextFormatter`). Throw `McpException` for errors.

**Analysis pipeline:** `ReviewContextOrchestrator` runs all `IReviewAnalyzer` implementations in `Order` sequence, producing `AnalysisSection`s aggregated into `ReviewContext`.

---

## Key conventions (summary)

| Aspect | Rule |
|---|---|
| Framework | .NET 10, C# 14, nullable `enable`, implicit usings `enable` |
| Tool output | Plain text `IEnumerable<ContentBlock>` (`TextContentBlock` per file/section); formatted by `PlainTextFormatter`; no JSON serialization in handler output |
| Naming | `*Provider`, `*Parser`, `*ToolHandler`, `*ScmClient`; `I*` interfaces; `_field` privates |
| Async | `async` all the way; propagate `CancellationToken` to every I/O call |
| DI | Constructor injection; all singletons; interface forwarding for `IScmClient`/`IPullRequestDataProvider`/`IFileContentDataProvider` |
| Errors | Custom exceptions caught in tool handlers → rethrown as `McpException` |
| Logging | `Microsoft.Extensions.Logging`; stderr only — **stdout is reserved for MCP JSON-RPC** |
| Child processes | Must set `RedirectStandardInput = true` + `process.StandardInput.Close()` after `Start()` |
| Testing | xUnit + NSubstitute; mock only API clients; use real parsers/value objects; `MethodName_Scenario_ExpectedResult` naming |

---

## MCP tools (6 total)

| Tool name | Handler | What it does |
|---|---|---|
| `get_pr_metadata` | `GetPullRequestMetadataToolHandler` | Returns PR metadata as plain text; optionally computes content paging info |
| `get_pr_content` | `GetPullRequestContentToolHandler` | Returns one paginated page of PR diff content as plain text |
| `get_pr_files` | `GetPullRequestFilesToolHandler` | Returns classified file list for a PR as plain text; supports pagination (F004) |
| `get_file_content_at_ref` | `GetFileContentAtRefToolHandler` | Returns file content at a specific Git ref as plain text |
| `get_local_files` | `GetLocalChangesFilesToolHandler` | Lists locally changed files with classification as plain text |
| `get_local_content` | `GetLocalContentToolHandler` | Returns one paginated page of local diff content as plain text |

---

## Provider detection order

1. Explicit `--provider` CLI flag
2. `GitHub.Owner` present in config
3. `AzureDevOps.OrganizationName` present in config
4. Git remote URL from `--repo` path or CWD
5. Default: Azure DevOps

## Auth chain (per provider)

PAT → cached token → CLI tool (`az account get-access-token` / `gh auth token`) → error with actionable instructions

## Config priority

CLI args > environment variables > `appsettings.Local.json` > `appsettings.json` > auto-detect from git remote > cached config

---

## After making code changes — always update docs

| Changed | Update |
|---|---|
| Added/removed/modified files | `.github/CodebaseUnderstanding.md` (file-role map, DI registrations) |
| New architectural pattern or convention | `.github/ProjectConventions.md` |
| MCP protocol/provider/auth/pipeline internals | `.github/Architecture.md` |
| Tool input schema, output DTO, error format | `.github/Contracts.md` |
| New extension pattern or recipe step | `.github/ExtensionRecipes.md` |
| New MCP tool, CLI flag, auth change, new provider | `DeveloperGuide.md` |

---

## Build & test

```powershell
# Build
dotnet build REBUSS.Pure.sln

# Run unit tests
dotnet test --filter "FullyQualifiedName!~SmokeTests"

# Run smoke tests (requires compiled binary)
dotnet test REBUSS.Pure.SmokeTests
```
