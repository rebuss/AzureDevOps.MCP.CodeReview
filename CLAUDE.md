# REBUSS.Pure — Project Guide for Claude Code

This file mirrors `.github/copilot-instructions.md`. Both point Claude/Copilot at the
same set of reference docs maintained under `.github/`. Keep this file in sync if the
navigation rules change.

## Project reference docs — when to read which file

Before exploring the codebase from scratch, consult the reference files below.
Each file answers a specific class of questions. **Read only what the task requires** — do not read all files for every task.

| File | Read when you need to… | What you will find |
|---|---|---|
| `.github/CodebaseUnderstanding.md` | **Locate a file**, understand project structure, find which class owns a responsibility, check DI registrations, see what tests exist | Complete file-role inventory, model dependency graph, DI registration table, test-file map. This is the **index** — start here to find where things are. |
| `.github/ProjectConventions.md` | **Follow a convention**, check naming rules, verify the correct pattern for JSON serialization, testing, DI, or error handling | Architecture overview, data-flow diagrams, coding conventions table, testing conventions table, quick-reference extension checklists (§5). This is the **cheat sheet**. |
| `.github/Architecture.md` | **Understand how a subsystem works internally** — MCP protocol loop, provider architecture, auth chain, config resolution, analysis pipeline, Copilot review orchestration | Deep-dive mechanics: request lifecycle, serialization pipeline, provider selection, token caching, `IPostConfigureOptions` lazy resolution, orchestrator sequencing, finding validation pipeline. Read this when debugging or modifying internals, **not** for routine feature work. |
| `.github/Contracts.md` | **Work with tool input/output JSON** — add a field to a DTO, add a new tool, fix a contract test, understand what the consuming AI agent sees | Exact JSON examples for all MCP tools, input parameter tables, output field tables with types and nullability, error format, serialization pipeline, shared DTO reference, enum/constant value catalog. |
| `.github/ExtensionRecipes.md` | **Implement a new feature type** — new MCP tool, new domain model, new analyzer, new CLI command, new SCM provider, new config option | Step-by-step recipes with C# code templates (`{Name}` placeholders), validation checklists, and common pitfalls. |

### Decision guide

```
Task: "Add a new MCP tool"
  1. ExtensionRecipes.md §1  → step-by-step recipe with code templates
  2. Contracts.md §3         → study an existing tool's contract as reference
  3. CodebaseUnderstanding.md → find exact file paths for DI registration, test location
  4. ProjectConventions.md §3-4 → verify serialization and testing conventions

Task: "Fix a bug in the diff provider"
  1. CodebaseUnderstanding.md → locate the provider file, its tests, its dependencies
  2. ProjectConventions.md §2 → understand data flow through the provider
  3. Architecture.md §2       → if the bug involves provider internals/facade pattern

Task: "Change JSON output of an existing tool"
  1. Contracts.md §3+§4       → current contract (fields, types, nullability)
  2. ExtensionRecipes.md §3   → recipe for modifying JSON output (which test layers to update)
  3. CodebaseUnderstanding.md → find DTO file, handler file, all 3 test layers

Task: "Understand how authentication works"
  1. Architecture.md §3       → auth chain, token caching, DelegatingHandler retry
  2. ProjectConventions.md §6 → config priority table, auth quick reference

Task: "Modify the Copilot review or finding validation pipeline"
  1. CodebaseUnderstanding.md → locate orchestrator, validator, scope resolver, inspection writer
  2. Architecture.md          → orchestrator sequencing (parallel page review, severity-ordered validation pagination)
  3. ProjectConventions.md    → DI conventions for optional services (FindingValidator/Resolver are nullable)

Task: "General code change / bug fix"
  1. CodebaseUnderstanding.md → locate files involved
  2. ProjectConventions.md    → verify conventions
  (Architecture.md, Contracts.md, ExtensionRecipes.md usually not needed)
```

### Rules
- **Do not skip the reference docs.** If a task involves creating or modifying code, check at least `CodebaseUnderstanding.md` (for file locations) and `ProjectConventions.md` (for conventions) before writing code.
- **Do not read files you don't need.** `Architecture.md` is for internals — skip it for routine feature work. `ExtensionRecipes.md` is for new feature types — skip it for bug fixes.
- **Trust the reference docs over assumptions.** If a file path, convention, or contract is documented in these files, use it. Do not guess.

---

## Reference docs maintenance

### Codebase Understanding Updates
After applying all code changes and before finishing your response, update
`.github/CodebaseUnderstanding.md` to reflect every modification you made:
add or remove files in the file-role map, update the model dependency graph,
adjust DI registrations, and replace the current file contents for every
modified in-scope file.

### Project Conventions Updates
Update `.github/ProjectConventions.md` **only if** the change introduces a new
architectural pattern, convention, or extension point not yet documented.

### Architecture Updates
Update `.github/Architecture.md` **only if** the change modifies internal mechanics
of the MCP protocol layer (server loop, JSON-RPC dispatch, transport), provider
architecture (facade pattern, interface forwarding, provider selection),
authentication/configuration resolution (auth chain, token caching,
`IPostConfigureOptions` pattern, workspace root resolution), the analysis
pipeline (orchestrator, inter-analyzer data sharing), or the Copilot review
pipeline (parallel page review, finding parsing/scoping/validation, severity
ordering, page allocation for SDK calls). Do **not** update for routine
feature additions, new tools, new analyzers, or prompt changes.

### Contract Updates
Update `.github/Contracts.md` whenever a tool's input schema, output JSON shape,
error format, or serialization behavior changes. This includes: adding, removing,
or renaming fields in tool output DTOs (`Tools/Models/`); adding a new MCP tool;
changing parameter types or required/optional status in `GetToolDefinition()`;
and modifying enum/constant values (state, changeType, op, reviewPriority, scope).
Do **not** update for internal refactoring that preserves the wire format.

### Extension Recipes Updates
Update `.github/ExtensionRecipes.md` **only if** an extension pattern itself changes —
e.g. a new required step is added to the "add MCP tool" recipe, the DI registration
pattern changes, a new type of extensible component is introduced, or test
scaffolding conventions change. Do **not** update when merely implementing a feature
using an existing recipe.

### Update README and DeveloperGuide
After applying changes to the project, update `README.md` and `DeveloperGuide.md` **only if** the modifications provide meaningful value to the documentation.
Skip the update if the changes are too minor, overly detailed, or not relevant for end-users.

**`DeveloperGuide.md` must be updated when any of the following change:**

- A new MCP tool is added or an existing tool's parameters change (update the MCP Tools Reference tables, including primary vs. legacy classification)
- The recommended review workflow changes (update the Review Workflows section)
- A new CLI flag or server argument is added to `CliArgumentParser` (update the CLI Commands section)
- Authentication or configuration handling changes for any provider — Azure DevOps or GitHub (update Authentication and Configuration sections)
- A new SCM provider is introduced (add corresponding auth, config, and troubleshooting sub-sections)
- A new troubleshooting scenario is identified (add to the Troubleshooting section)

Do **not** update `DeveloperGuide.md` for internal refactoring, test changes, or implementation details that have no end-user impact.

### Code Quality and Testing Requirements
After modifying or generating code:

- Ensure that existing unit tests still pass and correctly cover the updated behavior.
- If tests fail due to legitimate logic changes, update or extend them accordingly.
- Add new unit tests when new functionality requires coverage.
- Follow Clean Code principles and strictly adhere to SOLID design guidelines in all code you produce or modify.

## Active Technologies
- C# 14, .NET 10 (`net10.0`), nullable + implicit usings enabled (per Constitution §Technology). + `System.Text.Json`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options`. No new third-party packages. (023-local-review-finding-validation)
- Filesystem reads via `ILocalGitClient` (git child process) for index/HEAD content, direct `File.ReadAllTextAsync` for working tree (already used inside `LocalGitClient.GetFileContentAtRefAsync`). (023-local-review-finding-validation)

## Recent Changes
- 023-local-review-finding-validation: Added C# 14, .NET 10 (`net10.0`), nullable + implicit usings enabled (per Constitution §Technology). + `System.Text.Json`, `Microsoft.Extensions.Logging`, `Microsoft.Extensions.Options`. No new third-party packages.
