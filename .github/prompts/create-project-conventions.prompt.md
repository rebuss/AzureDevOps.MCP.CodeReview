# Task: Create `.github/ProjectConventions.md`

## Goal

Create a **stable, concise reference document** (80–150 lines) that tells a coding agent **how this project works and how to extend it** — without needing to explore the codebase from scratch.

This file complements `CodebaseUnderstanding.md` (which is an **inventory** of files, dependencies, and DI registrations). `ProjectConventions.md` is the **blueprint for action** — architecture, conventions, and step-by-step extension recipes.

## Why this file exists

When implementing new features via spec kit, the agent attaches this lightweight file to every task. It provides the "how" and "why" that `CodebaseUnderstanding.md` (the "what" and "where") does not cover.

## How to build it

### Step 1: Understand existing documentation

Read these files first — they contain the raw material:

- `#file:'.github/CodebaseUnderstanding.md'` — file-role map, dependency graph, DI registrations, conventions snapshot (section 4)
- `#file:'DeveloperGuide.md'` — user-facing docs: CLI, MCP tools, auth, testing commands
- `#file:'.github/copilot-instructions.md'` — post-change rules (update docs, testing requirements)

### Step 2: Understand the architecture by reading key structural files

Read these to extract the actual patterns (don't guess from docs — verify from code):

- `REBUSS.Pure/Program.cs` — composition root: `ConfigureBusinessServices`, `DetectProvider`, `BuildCliConfigOverrides`, `ResolvePatTarget`
- `REBUSS.Pure.AzureDevOps/ServiceCollectionExtensions.cs` — DI registration pattern for a provider
- `REBUSS.Pure.GitHub/ServiceCollectionExtensions.cs` — same pattern, GitHub variant
- `REBUSS.Pure.Core/IScmClient.cs` — the core abstraction: `IScmClient`, `IPullRequestDataProvider`, `IFileContentDataProvider`
- `REBUSS.Pure/Tools/GetPullRequestDiffToolHandler.cs` — reference MCP tool: `[McpServerToolType]` class, `[McpServerTool]` method, `[Description]` attributes (the MCP SDK handles tool discovery and JSON-RPC dispatch)
- `REBUSS.Pure.Core/Analysis/IReviewAnalyzer.cs` — pluggable analyzer interface
- `REBUSS.Pure.Core/Analysis/ReviewContextOrchestrator.cs` — analyzer pipeline orchestration
- One test file per pattern to understand testing conventions:
  - `REBUSS.Pure.Tests/Tools/GetPullRequestDiffToolHandlerTests.cs`
  - `REBUSS.Pure.AzureDevOps.Tests/Providers/AzureDevOpsDiffProviderTests.cs`
  - `REBUSS.Pure.GitHub.Tests/Providers/GitHubDiffProviderTests.cs`

### Step 3: Understand data flow by tracing a real request

Trace the path for `get_pr_content(prNumber: 42, pageNumber: 1)` from MCP request to JSON response:
1. MCP SDK receives JSON-RPC via stdin → dispatches to `[McpServerTool]`-annotated method
2. SDK resolves `GetPullRequestContentToolHandler` from DI → calls `ExecuteAsync`
3. Handler calls `IPullRequestDataProvider.GetDiffAsync(42)`
4. DI resolves this to `AzureDevOpsScmClient` (or `GitHubScmClient`) → delegates to `DiffProvider`
5. `DiffProvider` calls API client → parses JSON → builds `PullRequestDiff` via `StructuredDiffBuilder`
6. Handler maps domain model → paginated plain text content → returned to SDK

Do the same for `get_local_files` (local review path without network).

### Step 4: Write the document

Use this **exact structure** (all sections mandatory):

---

```markdown
# Project Conventions & Architecture Reference

> Stable reference for coding agents. Complements `CodebaseUnderstanding.md` (inventory).
> Update this file only when architecture, conventions, or extension patterns change.

## 1. Architecture Overview

[Layered architecture diagram in ASCII/text:
Core → Provider (AzureDevOps / GitHub) → REBUSS.Pure (MCP app)
Direction of dependencies. Why interface forwarding exists.
Provider pattern: IScmClient facade → fine-grained providers → parsers → API client.
One provider per process (selected by DetectProvider).]

## 2. Data Flow

### PR Review flow
[Full pipeline: MCP stdin → SDK JSON-RPC dispatch → [McpServerTool] method → ToolHandler → IPullRequestDataProvider → ScmClient → Provider → Parser → API → back through the chain → JSON on stdout]

### Local Review flow
[Pipeline: MCP stdin → ToolHandler → ILocalReviewProvider → LocalGitClient (git process) → StructuredDiffBuilder → JSON on stdout]

### CLI init flow
[args → CliArgumentParser → InitCommand → DetectProvider → AuthFlow → write mcp.json + copy prompts/instructions]

## 3. Coding Conventions

[Extract and organize from CodebaseUnderstanding.md section 4, PLUS any implicit conventions found in code:
- Target framework, C# version, nullable, implicit usings
- JSON serialization rules (camelCase, ignore nulls, no indent, [JsonPropertyName] on all DTOs)
- Naming: *Provider, *Parser, *ToolHandler, *ScmClient, I* for interfaces, _ for private fields
- internal static for pure helper methods, readonly record struct for value types
- async all the way, CancellationToken propagation
- CLI output to stderr (stdout reserved for MCP stdio JSON-RPC)
- Error handling: custom exceptions → caught in tool handler → ToolResult.IsError = true
- DI: constructor injection, singletons, interface forwarding for IScmClient/IPullRequestDataProvider/IFileContentDataProvider
- Logging: Microsoft.Extensions.Logging, stderr + file]

## 4. Testing Conventions

[Extract from test files:
- xUnit + NSubstitute
- Test project structure mirrors source project (REBUSS.Pure.Tests/Tools/ ↔ REBUSS.Pure/Tools/)
- Test class naming: {ClassUnderTest}Tests
- Arrange/Act/Assert pattern
- What to mock (interfaces) vs what to use real (value objects, models)
- InternalsVisibleTo relationships
- Unit tests vs Smoke tests vs Contract tests — when to write which
- Smoke tests run compiled binary as child process]

## 5. Extension Recipes

### 5.1 Add a new MCP tool
[Step-by-step with exact file paths:
1. Create output model in Tools/Models/ (record with [JsonPropertyName])
2. Create handler class in Tools/ marked with [McpServerToolType], method with [McpServerTool] and [Description]
3. Mark class with [McpServerToolType] and method with [McpServerTool] — SDK discovers automatically via WithToolsFromAssembly()
4. Add unit tests in REBUSS.Pure.Tests/Tools/
5. Add smoke/contract test if applicable
6. Update CodebaseUnderstanding.md]

### 5.2 Add a new domain model
[Steps: file in Core/Models/, update interfaces if needed, update parsers, update tool handlers, update tests]

### 5.3 Modify JSON output format of existing tool
[Steps: which models in Tools/Models/ to change, how to maintain backward compat, which tests to update (unit + contract + smoke)]

### 5.4 Add a new IReviewAnalyzer
[Steps: implement in Core/Analysis/, register in DI, how ReviewContextOrchestrator invokes it]

### 5.5 Add or modify a prompt
[Steps: edit embedded resource in Cli/Prompts/, update InitCommand if new file, rebuild — init always overwrites deployed copies]

### 5.6 Add a new CLI command
[Steps: implement ICliCommand, add case in Program.RunCliCommandAsync, update CliArgumentParser]

### 5.7 Add a new SCM provider
[Steps: new project, implement IScmClient + fine-grained providers + parsers + ServiceCollectionExtensions, update DetectProvider in Program.cs]

## 6. Configuration & Auth Quick Reference

[Compact summary:
- Config hierarchy: CLI args > env vars > appsettings.Local.json > appsettings.json > auto-detect (git remote) > cached
- Auth chain: PAT → cached token → CLI tool (az/gh) → error
- Provider detection: explicit --provider > GitHub.Owner > AzureDevOps.OrganizationName > git remote URL > default AzureDevOps]
```

---

## Quality rules

1. **Concise** — target 80–150 lines. This is a cheat sheet, not a novel. Use tables and bullet points.
2. **Actionable** — every section answers "what do I do?" not just "what exists?"
3. **Accurate** — verify every claim against actual code. Don't copy from docs without checking.
4. **Stable** — don't include volatile details (specific file lists, line counts). Those belong in `CodebaseUnderstanding.md`.
5. **No duplication** — don't repeat the file-role map or DI registrations. Reference `CodebaseUnderstanding.md` for those.
6. **Extension recipes must include exact file paths** — the agent needs to know WHERE to create files, not just what to create.

## After creating the file

1. Update `.github/copilot-instructions.md` to add a rule for maintaining `ProjectConventions.md` (update only when architecture or conventions change, not on every PR).
2. Verify the file is self-consistent and doesn't contradict `CodebaseUnderstanding.md` or `DeveloperGuide.md`.
3. Do NOT update `CodebaseUnderstanding.md` or `README.md` — this task only creates a new file.
