# Task: Create `.github/ExtensionRecipes.md`

## Goal

Create a **detailed, implementable cookbook** (~250–350 lines) with full recipes for extending the codebase. Each recipe must be complete enough that a coding agent can produce correct, convention-compliant code **by following the recipe alone** — without exploring the codebase from scratch.

## Relationship to existing files

| File | Coverage of "how to extend" | Depth |
|---|---|---|
| `ProjectConventions.md` §5 | 7 recipes, 2–5 steps each, file paths only | **Checklist** — "create file X, register Y" |
| **`ExtensionRecipes.md`** (this file) | Same 7 recipes + additional ones | **Cookbook** — code templates, reference implementations, validation checklists, pitfalls |

**Non-duplication rule:** `ProjectConventions.md` §5 stays as a quick-reference checklist. `ExtensionRecipes.md` goes **deep** — provides the code scaffolding, exact patterns to replicate, and things that can go wrong. An agent doing a simple task reads `ProjectConventions.md`; an agent implementing a new feature type reads `ExtensionRecipes.md`.

## How to build it

### Step 1: Read existing documentation

- `#file:'.github/ProjectConventions.md'` — section 5 (brief recipes to expand), sections 3–4 (conventions and testing patterns to embed in recipes)
- `#file:'.github/CodebaseUnderstanding.md'` — file-role map (section 1) for accurate paths, DI registrations (section 3) for registration patterns

### Step 2: Extract code templates from reference implementations

For each recipe, read the **reference implementation** files listed below **in full**. Extract the structural patterns (not business logic) to create code templates.

**Recipe: New MCP tool — read these reference files:**
- `REBUSS.Pure/Tools/GetPullRequestDiffToolHandler.cs` — full handler: `[McpServerToolType]` class, `[McpServerTool]` method with `[Description]` attributes, `JsonSerializerOptions`, `ExecuteAsync` with try/catch, parameter binding, result building
- `REBUSS.Pure/Tools/GetLocalChangesFilesToolHandler.cs` — simpler handler: different provider type, optional parameters with defaults
- `REBUSS.Pure/Tools/Models/StructuredDiffResult.cs` — output DTO: `[JsonPropertyName]` on every property, nullable for optional fields
- `REBUSS.Pure/Tools/Models/LocalReviewFilesResult.cs` — output DTO: different shape, context fields
- `REBUSS.Pure.Tests/Tools/GetPullRequestDiffToolHandlerTests.cs` — test pattern: mock provider, verify JSON, test validation, test exceptions
- `REBUSS.Pure.Tests/Tools/GetLocalChangesFilesToolHandlerTests.cs` — test pattern: optional params, scope parsing

**Recipe: New domain model — read these:**
- `REBUSS.Pure.Core/Models/PullRequestDiff.cs` — complex model: nested lists
- `REBUSS.Pure.Core/Models/FileContent.cs` — simple model
- `REBUSS.Pure.Core/IScmClient.cs` — how provider interfaces expose new data
- `REBUSS.Pure.AzureDevOps/Parsers/FileChangesParser.cs` + `IFileChangesParser.cs` — parser pattern
- `REBUSS.Pure.GitHub/Parsers/GitHubFileChangesParser.cs` + `IGitHubFileChangesParser.cs` — same pattern, GitHub variant
- `REBUSS.Pure.AzureDevOps.Tests/Parsers/FileChangesParserTests.cs` — parser test pattern
- `REBUSS.Pure.GitHub.Tests/Parsers/GitHubFileChangesParserTests.cs` — parser test pattern

**Recipe: New IReviewAnalyzer — read these:**
- `REBUSS.Pure.Core/Analysis/IReviewAnalyzer.cs` — full interface with XML docs
- `REBUSS.Pure.Core/Analysis/AnalysisInput.cs` — input record: what data is available, `PreviousSections`
- `REBUSS.Pure.Core/Analysis/AnalysisSection.cs` — output: `Key`, `Title`, `Content`
- `REBUSS.Pure.Core/Analysis/ReviewContextOrchestrator.cs` — how orchestrator calls analyzers: order, filter, accumulate

**Recipe: New CLI command — read these:**
- `REBUSS.Pure/Cli/ICliCommand.cs` — interface: `Name`, `ExecuteAsync`
- `REBUSS.Pure/Cli/InitCommand.cs` — reference implementation (read first ~80 lines for structure)
- `REBUSS.Pure/Cli/CliArgumentParser.cs` — how commands are recognized and args extracted
- `REBUSS.Pure/Program.cs` — `RunCliCommandAsync` dispatch switch
- `REBUSS.Pure.Tests/Cli/InitCommandTests.cs` — first ~50 lines for test setup pattern
- `REBUSS.Pure.Tests/Cli/CliArgumentParserTests.cs` — first ~50 lines for parser test pattern

**Recipe: New/modified prompt — read these:**
- `REBUSS.Pure/Cli/Deployment/PromptDeployer.cs` — `PromptFileNames` array + `DeployAsync` method (the deployer that `InitCommand` invokes for prompt deployment)
- `REBUSS.Pure/Cli/Prompts/review-pr.md` — embedded resource example
- `REBUSS.Pure/REBUSS.Pure.csproj` — verify `<EmbeddedResource>` includes

**Recipe: Modified JSON output — read these:**
- `REBUSS.Pure/Tools/Models/StructuredDiffResult.cs` — current shape
- `REBUSS.Pure/Tools/GetPullRequestDiffToolHandler.cs` — `BuildStructuredResult` method (mapping logic)
- `REBUSS.Pure.Tests/Tools/GetPullRequestDiffToolHandlerTests.cs` — JSON assertions to update
- `REBUSS.Pure.SmokeTests/Contracts/GitHub/GitHubDiffContractTests.cs` — contract assertions to update
- `REBUSS.Pure.SmokeTests/Contracts/AzureDevOps/AdoDiffContractTests.cs` — contract assertions to update

**Recipe: New SCM provider — read these:**
- `REBUSS.Pure.GitHub/ServiceCollectionExtensions.cs` — full DI registration as template
- `REBUSS.Pure.GitHub/GitHubScmClient.cs` — facade pattern: delegation + enrichment
- `REBUSS.Pure.GitHub/Api/IGitHubApiClient.cs` — API client interface
- `REBUSS.Pure.GitHub/Providers/GitHubDiffProvider.cs` — first ~40 lines for constructor pattern
- `REBUSS.Pure/Program.cs` — `DetectProvider` and `ConfigureBusinessServices` switch

### Step 3: Identify common pitfalls by reading test files

For each recipe, scan the corresponding test files for edge cases that reveal common mistakes:
- Missing `CancellationToken` propagation
- Missing `[JsonPropertyName]` on new DTO properties (serializes as PascalCase instead of camelCase)
- Tool class not public or missing `[McpServerToolType]` attribute (SDK can't discover it)
- Output to `Console.Out` instead of `Console.Error` (breaks MCP stdio)
- Missing `[Description]` on tool parameters (tool definition incomplete for AI agents)
- Not updating smoke test tool count after adding a tool

### Step 4: Write the document

Use this **exact structure**:

---

```markdown
# Extension Recipes

> Detailed, implementable recipes for extending the codebase.
> For a quick checklist version, see `ProjectConventions.md` §5.
> Update when a new extension pattern emerges or existing patterns change.

## How to use this file

Each recipe contains:
- **Reference implementation** — existing file(s) to study as a pattern
- **Steps** — what to create/modify, with code templates showing the structural pattern
- **Validation checklist** — what to verify after implementing
- **Common pitfalls** — mistakes the agent is likely to make

Code templates use `{Name}` placeholders. Replace with actual names.

---

## 1. Add a New MCP Tool

### Reference implementation
[Point to GetPullRequestDiffToolHandler (complex, with PR provider) and 
GetLocalChangesFilesToolHandler (simpler, with local provider) as the two 
patterns to choose from based on the new tool's data source.]

### Step 1: Output model
[Code template for a new result class in REBUSS.Pure/Tools/Models/{Name}Result.cs:
- Class with [JsonPropertyName] on EVERY property
- Nullable properties for optional fields (omitted from JSON via WhenWritingNull)
- Pattern: public class, not record (consistency with existing models)]

### Step 2: Tool handler
[Code template for REBUSS.Pure/Tools/{Name}ToolHandler.cs:
- Class marked with [McpServerToolType]
- Static readonly JsonSerializerOptions (camelCase, WriteIndented=true, WhenWritingNull)
- Constructor: inject provider interface + ILogger<T>
- Public async method marked with [McpServerTool(Name = "tool_name")], [Description("...")]
- Parameters with [Description] attributes, optional params have defaults
- Returns Task<string> (JSON-serialized result)
- try/catch structure: McpException for validation, custom exception catch, generic Exception catch
- Result building: domain model → DTO mapping → JSON serialization → return string]

### Step 3: Tool discovery
[No manual DI registration needed. The MCP SDK discovers tools automatically via 
`WithToolsFromAssembly()` in Program.cs, scanning for `[McpServerToolType]` classes.
The tool class must be public. Constructor dependencies are resolved from the DI container.]

### Step 4: Unit tests
[Code template for REBUSS.Pure.Tests/Tools/{Name}ToolHandlerTests.cs:
- Mock provider as readonly field
- SUT created in constructor with NullLogger<T>.Instance and other dependencies
- Happy path: call ExecuteAsync directly, verify JSON structure with JsonDocument.Parse
- Validation tests: missing params, invalid params → McpException
- Exception tests: custom exceptions → error JSON, generic → error JSON
- Pattern: direct method invocation (no MCP protocol layer in unit tests)]

### Step 5: Smoke test update
[Which assertion in McpServerSmokeTests to update (tool count)]

### Validation checklist
[Bulleted list: builds, tests pass, tool appears in tools/list, 
JSON output is camelCase, optional fields omitted when null, 
CodebaseUnderstanding.md updated]

### Common pitfalls
[List from Step 3 analysis: [JsonPropertyName] missing on DTO properties, 
Console.Out usage (breaks MCP stdio), CancellationToken not propagated, 
tool class not public (SDK can't discover it), missing [McpServerToolType] attribute,
[Description] missing on parameters (tool definition incomplete)]

---

## 2. Add a New Domain Model

### Reference implementation
[PullRequestDiff (complex) and FileContent (simple)]

### Steps
[Step-by-step: model creation in Core/Models, interface extension in IScmClient if needed,
parser interface + implementation per provider, provider update, tool handler mapping update.
Include code templates for: model class, parser interface, parser implementation skeleton.]

### Validation checklist
### Common pitfalls

---

## 3. Modify JSON Output Format of Existing Tool

### Reference implementation
[StructuredDiffResult + GetPullRequestDiffToolHandler BuildStructuredResult method]

### Steps
[Additive vs breaking changes. How to add a property (add to DTO + mapping).
How to rename a property ([JsonPropertyName] controls wire name independently).
How to restructure (new DTO + update mapping + update ALL test layers).
Emphasize: three test layers must be updated — unit, smoke, contract.]

### Validation checklist
### Common pitfalls

---

## 4. Add a New IReviewAnalyzer

### Reference implementation
[IReviewAnalyzer interface, ReviewContextOrchestrator, AnalysisInput, AnalysisSection]

### Steps
[Code template for analyzer: SectionKey, DisplayName, Order, CanAnalyze, AnalyzeAsync.
How to access diff/metadata/files from AnalysisInput.
How to consume previous analyzer output via PreviousSections.
DI registration: services.AddSingleton<IReviewAnalyzer, {Name}Analyzer>().
Test template: mock IRepositoryArchiveProvider if content fetch needed.]

### Validation checklist
### Common pitfalls

---

## 5. Add or Modify a Prompt

### Reference implementation
[Cli/Prompts/*.prompt.md files, PromptDeployer.PromptFileNames array, REBUSS.Pure.csproj EmbeddedResource]

### Steps
[How embedded resources work. Where to add new file. How InitCommand discovers 
and copies them. Why deployed copies are always overwritten.
For modifications: edit ONLY the embedded source, not the deployed copy.]

### Validation checklist
### Common pitfalls

---

## 6. Add a New CLI Command

### Reference implementation
[ICliCommand interface, InitCommand, CliArgumentParser, Program.RunCliCommandAsync]

### Steps
[Code template for command class. How to add argument parsing in CliArgumentParser.
How to add dispatch case in Program. Output convention: Console.Error only.
Test template: command test + parser test.]

### Validation checklist
### Common pitfalls

---

## 7. Add a New SCM Provider

### Reference implementation
[GitHub project structure as complete template — it's the newer, cleaner implementation]

### Steps
[Project creation. Required folder structure. Required implementations:
- Api/I{Provider}ApiClient.cs + {Provider}ApiClient.cs
- Parsers/ (one per data type)
- Providers/ (DiffProvider, MetadataProvider, FilesProvider, FileContentProvider)
- Configuration/ (Options, Validator, RemoteDetector, ConfigStore, AuthProvider, Handler, Resolver)
- {Provider}ScmClient.cs (facade)
- ServiceCollectionExtensions.cs (full DI)
Update in REBUSS.Pure: Program.DetectProvider + ConfigureBusinessServices switch.
Test project creation.]

### Validation checklist
### Common pitfalls

---

## 8. Add a New Configuration Option

### Reference implementation
[AzureDevOpsOptions, AzureDevOpsOptionsValidator, ConfigurationResolver]

### Steps
[How to add a field to Options class. How to add validation in Validator.
How to add resolution in ConfigurationResolver (merge priority).
How to add CLI arg in CliArgumentParser + BuildCliConfigOverrides.
How to read in consumer via IOptions<T>.]

### Validation checklist
### Common pitfalls

---

## 9. Extend MCP Server Capabilities

### Reference implementation
[Program.cs MCP SDK bootstrap: `AddMcpServer`, `WithStdioServerTransport`, `WithToolsFromAssembly`]

### Steps
[The MCP SDK handles all protocol methods (initialize, tools/list, tools/call) internally.
To add new capabilities, use SDK extension points:
- New tools: add `[McpServerToolType]` class (see Recipe 1)
- Server info: modify `options.ServerInfo` in Program.cs
- Resources/prompts: use SDK's `WithResources`/`WithPrompts` if supported]

### Validation checklist
### Common pitfalls
```

---

## Quality rules

1. **Implementable** — code templates must be syntactically valid C# (with `{Name}` placeholders). An agent should be able to copy-paste and fill in.
2. **Reference-first** — every recipe starts with "study this existing file." The agent reads the reference, then follows the template.
3. **Complete** — each recipe covers ALL files to create/modify, including DI registration, tests, and doc updates. No implicit "you should also..." steps.
4. **Convention-embedded** — templates already include `[JsonPropertyName]`, `CancellationToken`, `ILogger<T>`, etc. The agent doesn't need to remember conventions separately.
5. **Pitfalls are specific** — not generic advice like "don't forget tests." Each pitfall references a concrete mistake (e.g. "JsonElement cast fails — use `jsonElement.GetInt32()` not `Convert.ToInt32()`").
6. **Target 250–350 lines** — comprehensive but scannable. Use code blocks for templates, tables for comparisons, bullets for checklists.
7. **Stable** — templates reference patterns, not specific line numbers. Update only when patterns change.

## After creating the file

1. Add to `CodebaseUnderstanding.md` Documentation section:
   ```
   | `.github\ExtensionRecipes.md` | Detailed extension cookbook: code templates, reference implementations, validation checklists, common pitfalls for each extension pattern |
   ```
2. Do NOT modify `ProjectConventions.md` §5 — it stays as the brief checklist.
3. Do NOT update `README.md` or `DeveloperGuide.md` — this is an internal agent reference.
4. Do NOT update `.github/copilot-instructions.md` — `ExtensionRecipes.md` follows the same update rule as `ProjectConventions.md` (update only when patterns change).
