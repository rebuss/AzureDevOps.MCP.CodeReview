# Extension Recipes

> Detailed, implementable recipes for extending the codebase.
> For a quick checklist version, see `ProjectConventions.md` §5.
> Update when a new extension pattern emerges or existing patterns change.

## How to use this file

Each recipe provides: **reference file(s)** to study, **steps** with code skeletons,
a **validation checklist**, and **common pitfalls**. Templates use `{Name}` placeholders.

---

## 1. Add a New MCP Tool

### Reference implementations
- **Complex (PR provider):** `GetPullRequestDiffToolHandler.cs` — required int param, domain mapping, custom exception handling
- **Simple (local provider):** `GetLocalChangesFilesToolHandler.cs` — optional string param with default

### Step 1: Output model — `REBUSS.Pure/Tools/Models/{Name}Result.cs`

```csharp
public class {Name}Result
{
    [JsonPropertyName("fieldName")]
    public string FieldName { get; set; } = string.Empty;

    [JsonPropertyName("optionalField")]
    public string? OptionalField { get; set; }  // omitted when null via WhenWritingNull
}
```

### Step 2: Tool handler — `REBUSS.Pure/Tools/{Name}ToolHandler.cs`

Key structural elements (study reference for full pattern):

```csharp
public class {Name}ToolHandler : IMcpToolHandler
{
    private readonly I{Provider} _{provider};
    private readonly ILogger<{Name}ToolHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string ToolName => "get_{snake_name}";

    public McpTool GetToolDefinition() => new()
    {
        Name = ToolName,
        Description = "...",
        InputSchema = new ToolInputSchema
        {
            Type = "object",
            Properties = new Dictionary<string, ToolProperty>
            {
                ["paramName"] = new() { Type = "integer", Description = "..." }
            },
            Required = new List<string> { "paramName" }
        }
    };

    // ExecuteAsync: try/catch -> validate -> call provider -> map -> serialize -> ToolResult
    // Catch domain exceptions (PullRequestNotFoundException etc.) -> CreateErrorResult
    // Catch generic Exception -> CreateErrorResult
}
```

**Critical: integer input extraction** (MCP sends `JsonElement`, not CLR types):

```csharp
prNumber = obj is JsonElement je ? je.GetInt32() : Convert.ToInt32(obj);
```

**String input extraction:**

```csharp
return obj is JsonElement je ? je.GetString() : obj?.ToString();
```

### Step 3: DI — `Program.ConfigureServices`

```csharp
services.AddSingleton<IMcpToolHandler, {Name}ToolHandler>();
```

### Step 4: Tests — `REBUSS.Pure.Tests/Tools/{Name}ToolHandlerTests.cs`

Pattern (see `GetPullRequestDiffToolHandlerTests.cs` for full example):

```csharp
private readonly I{Provider} _provider = Substitute.For<I{Provider}>();
private readonly {Name}ToolHandler _handler;

public {Name}ToolHandlerTests() =>
    _handler = new(_{provider}, NullLogger<{Name}ToolHandler>.Instance);

// Required tests: happy path (JSON structure), null args, missing param,
// invalid param type, JsonElement input, domain exception, generic exception
```

### Step 5: Smoke test — add to `ExpectedTools` in `McpServerSmokeTests.cs`

### Validation checklist
- [ ] Builds, all tests pass
- [ ] `[JsonPropertyName]` on every DTO property
- [ ] `JsonElement` handling in all `TryExtract*` methods
- [ ] `CancellationToken` propagated to all provider calls
- [ ] Tool appears in `tools/list` (smoke test)
- [ ] `CodebaseUnderstanding.md` updated

### Common pitfalls
- **`JsonElement` cast:** `(int)arguments["prNumber"]` throws. Use `je.GetInt32()`.
- **Missing `[JsonPropertyName]`:** serializes as PascalCase, breaking consumers.
- **`Console.Out` usage:** breaks MCP stdio transport. Use `ILogger` (routes to stderr).
- **Forgot DI registration:** handler compiles but never appears in `tools/list`.
- **Stale smoke test:** `ExpectedTools` array not updated causes failure.

---

## 2. Add a New Domain Model

### Reference: `PullRequestDiff.cs` (complex), `FileContent.cs` (simple)

### Steps
1. **Model** in `REBUSS.Pure.Core/Models/{Name}.cs` (plain class, not record)
2. **Interface** — if new capability, extend `IPullRequestDataProvider` or `IFileContentDataProvider` in `IScmClient.cs`
3. **Parser interface + impl** per provider:
   - ADO: `Parsers/I{Name}Parser.cs` + `{Name}Parser.cs` (uses `JsonDocument.Parse`)
   - GitHub: `Parsers/IGitHub{Name}Parser.cs` + `GitHub{Name}Parser.cs`
4. **DI** — register parsers in both `ServiceCollectionExtensions`
5. **Provider + facade** — update fine-grained provider, wire through `ScmClient`

### Validation checklist
- [ ] Model in `Core/Models/`, parsers in provider `Parsers/` folders
- [ ] Parser tests in both `AzureDevOps.Tests/Parsers/` and `GitHub.Tests/Parsers/`
- [ ] DI registrations in both providers

### Common pitfalls
- **Parser in wrong project:** parsers go in provider projects (parse provider-specific JSON), not Core.
- **Missing GitHub variant:** every ADO parser needs a GitHub equivalent (and vice versa).

---

## 3. Modify JSON Output Format

### Reference: `StructuredDiffResult.cs` (DTO), `GetPullRequestDiffToolHandler.BuildStructuredResult` (mapping)

### Steps

| Change type | What to do |
|---|---|
| **Add field** | Add `[JsonPropertyName]` property to DTO + update mapping. Use `string?` for optional. |
| **Rename wire name** | Change `[JsonPropertyName("newName")]` value only. C# name irrelevant. |
| **Restructure** | New DTO + mapping + update **all 3 test layers**: unit, smoke, contract. |

### Common pitfalls
- **Forgot contract tests:** unit tests pass but `SmokeTests/Contracts/` tests fail on live API shape.
- **Non-nullable default:** `string` defaults to `""`, wasting tokens. Use `string?` for truly optional.

---

## 4. Add a New IReviewAnalyzer

### Reference: `IReviewAnalyzer.cs`, `ReviewContextOrchestrator.cs`, `AnalysisInput.cs`

### Analyzer skeleton — `REBUSS.Pure.Core/Analysis/{Name}Analyzer.cs`

```csharp
public class {Name}Analyzer : IReviewAnalyzer
{
    public string SectionKey => "{snake_key}";
    public string DisplayName => "{Display Name}";
    public int Order => 200;  // lower = runs first

    public bool CanAnalyze(AnalysisInput input) => true;

    public async Task<AnalysisSection?> AnalyzeAsync(AnalysisInput input, CancellationToken ct)
    {
        // input.Diff, input.Metadata, input.Files, input.ContentProvider
        // input.PreviousSections["other_key"] -- output from earlier analyzers
        return new AnalysisSection { Key = SectionKey, Title = DisplayName, Content = result };
    }
}
```

**DI:** `services.AddSingleton<IReviewAnalyzer, {Name}Analyzer>();`
Orchestrator auto-discovers via `IEnumerable<IReviewAnalyzer>`.

### Common pitfalls
- **Wrong `Order`:** if B depends on A's output, B.Order must be > A.Order.
- **Duplicate `SectionKey`:** silently overwrites previous section.
- **Blocking `CanAnalyze`:** called synchronously — no I/O here.

---

## 5. Add or Modify a Prompt

### Reference: `Cli/Prompts/review-pr.md`, `InitCommand.PromptFileNames`, `REBUSS.Pure.csproj`

### Steps (new prompt)
1. Create `REBUSS.Pure/Cli/Prompts/{name}.md`
2. Add to csproj: `<EmbeddedResource Include="Cli\Prompts\{name}.md" />`
3. Add to `InitCommand.PromptFileNames` array
4. Rebuild — `rebuss-pure init` copies to `.github/prompts/`

**For modifications:** edit ONLY the source in `Cli/Prompts/`, never the deployed copy.

### Common pitfalls
- **Editing deployed copy:** `.github/prompts/` is overwritten by `init`.
- **Missing csproj entry:** file exists but isn't embedded — silently skipped.

---

## 6. Add a New CLI Command

### Reference: `ICliCommand.cs`, `InitCommand.cs`, `CliArgumentParser.cs`, `Program.RunCliCommandAsync`

### Steps
1. **Command** in `REBUSS.Pure/Cli/{Name}Command.cs` implementing `ICliCommand`
   - `Name` property, `ExecuteAsync` returns exit code (0 = success)
   - All output to `Console.Error` (injected `TextWriter`)
2. **Parser** — add `if` block in `CliArgumentParser.Parse` for the new command name
3. **Dispatch** — add case in `Program.RunCliCommandAsync`:
   ```csharp
   "{name}" => new {Name}Command(Console.Error),
   ```
4. **Tests** — `CliArgumentParserTests` (recognition) + `{Name}CommandTests` (behavior)

### Common pitfalls
- **stdout usage:** all CLI output goes to `Console.Error` for MCP transport safety.
- **Missing dispatch:** parser recognizes command but `RunCliCommandAsync` throws on default branch.

---

## 7. Add a New SCM Provider

### Reference: **GitHub project** (newer, cleaner) — `ServiceCollectionExtensions.cs`, `GitHubScmClient.cs`

### Required folder structure (mirror GitHub):

```
REBUSS.Pure.{Provider}/
  Api/           -- I{Provider}ApiClient + implementation (HttpClient, raw JSON)
  Parsers/       -- one interface + impl per data type
  Providers/     -- DiffProvider, MetadataProvider, FilesProvider, FileContentProvider
  Configuration/ -- Options, Validator, RemoteDetector, ConfigStore, AuthProvider,
                    AuthHandler (DelegatingHandler), ConfigResolver (IPostConfigureOptions)
  {Provider}ScmClient.cs           -- IScmClient facade (delegation + enrichment)
  ServiceCollectionExtensions.cs   -- Add{Provider}Provider(IConfiguration)
```

### Critical DI pattern (interface forwarding):

```csharp
services.AddSingleton<{Provider}ScmClient>();
services.AddSingleton<IScmClient>(sp => sp.GetRequiredService<{Provider}ScmClient>());
services.AddSingleton<IPullRequestDataProvider>(sp => sp.GetRequiredService<{Provider}ScmClient>());
services.AddSingleton<IFileContentDataProvider>(sp => sp.GetRequiredService<{Provider}ScmClient>());
```

### Wire into `Program.cs`:
- `DetectProvider`: add git remote URL detection
- `ConfigureServices`: add `case "{Provider}"` in switch

### Common pitfalls
- **Missing interface forwarding:** tool handlers resolve `IPullRequestDataProvider`, not `IScmClient`. All three registrations required.
- **Forgot `DetectProvider`:** provider only selectable via `--provider` flag, not auto-detected.

---

## 8. Add a New Configuration Option

### Reference: `AzureDevOpsOptions.cs`, `ConfigurationResolver.cs`

### Steps
1. Add property to Options class
2. Add validation in `OptionsValidator.Validate` (if required)
3. Add resolution in `ConfigurationResolver.PostConfigure` (if auto-detectable):
   ```csharp
   options.NewField = Resolve(options.NewField, cached?.NewField, detected?.NewField, nameof(options.NewField));
   ```
4. Add CLI arg (if needed): `CliArgumentParser` parsing + `Program.BuildCliConfigOverrides` override
5. Consume via `IOptions<T>` in the target service

### Common pitfalls
- **Config key mismatch:** `nameof()` must match JSON key in `appsettings.json`.
- **Circular dependency:** do NOT inject `IOptions<T>` into `IPostConfigureOptions<T>`. Read raw `IConfiguration` keys.

---

## 9. Add a New MCP Method Handler

### Reference: `InitializeMethodHandler.cs`, `ToolsCallMethodHandler.cs`

### Steps
1. Implement `IMcpMethodHandler`: `MethodName` string, `HandleAsync(JsonElement?, CancellationToken)`
2. Register: `services.AddSingleton<IMcpMethodHandler, {Name}MethodHandler>()`
3. `McpServer` auto-discovers via `IEnumerable<IMcpMethodHandler>`

### Common pitfalls
- **Duplicate `MethodName`:** `McpServer` builds dictionary at startup — duplicates throw.
- **Wrong return type:** return value becomes `result` field of JSON-RPC response. Must match MCP spec.
