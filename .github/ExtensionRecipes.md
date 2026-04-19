# Extension Recipes

> Detailed, implementable recipes for extending the codebase.
> For a quick checklist version, see `ProjectConventions.md` §5.
> Update when a new extension pattern emerges or existing patterns change.

## How to use this file

Each recipe provides: **reference file(s)** to study, **steps** with code skeletons,
a **validation checklist**, and **common pitfalls**. Templates use `{Name}` placeholders.

---

## 1. Add a New MCP Tool

> Uses the **ModelContextProtocol SDK v1.2.0** attribute-based tool pattern.
> The old `IMcpToolHandler` interface has been removed.

### Reference implementations
- **Complex (PR provider, pagination):** `GetPullRequestDiffToolHandler.cs` — required/optional params, domain exception mapping, pagination support
- **Simple (single file):** `GetFileDiffToolHandler.cs` — required typed params (`int`, `string`), minimal dependencies
- **Local provider:** `GetLocalChangesFilesToolHandler.cs` — optional string param with default, local git operations

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
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

[McpServerToolType]
public class {Name}ToolHandler
{
    private readonly I{Provider} _{provider};
    private readonly ILogger<{Name}ToolHandler> _logger;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public {Name}ToolHandler(I{Provider} {provider}, ILogger<{Name}ToolHandler> logger)
    {
        _{provider} = {provider};
        _logger = logger;
    }

    [McpServerTool(Name = "get_{snake_name}"), Description("Tool description here")]
    public async Task<string> ExecuteAsync(
        [Description("Required param description")] int requiredParam,
        [Description("Optional param description")] string? optionalParam = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Validation
            if (requiredParam <= 0)
                throw new McpException("requiredParam must be greater than 0");

            // Business logic
            var result = await _{provider}.GetDataAsync(requiredParam, cancellationToken);

            // Map to output DTO and serialize
            var output = new {Name}Result { /* mapping */ };
            return JsonSerializer.Serialize(output, JsonOptions);
        }
        catch ({DomainException} ex)
        {
            _logger.LogWarning(ex, "[get_{snake_name}] Domain error");
            throw new McpException($"Domain error: {ex.Message}");
        }
        catch (McpException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[get_{snake_name}] Unexpected error");
            throw new McpException($"Error in get_{snake_name}: {ex.Message}");
        }
    }
}
```

**Key SDK conventions:**
- `[McpServerToolType]` on the class — marks it for auto-discovery
- `[McpServerTool(Name = "...")]` on the method — defines the tool name
- `[Description("...")]` on the method — becomes the tool description in `tools/list`
- `[Description("...")]` on each parameter — populates `inputSchema` descriptions
- **Non-nullable** parameters → `required` in the generated `inputSchema`
- **Nullable** parameters with defaults → optional in `inputSchema`
- Return `Task<string>` — SDK wraps the string as `TextContent` in the response
- `throw new McpException("message")` — SDK sets `isError=true` on the response
- For C# keywords as parameter names (e.g., `ref`), use `@ref` in C#

### Step 3: DI — auto-discovered (no manual registration needed)

`WithToolsFromAssembly()` in `Program.cs` discovers all `[McpServerToolType]` classes
automatically at startup. **No DI registration line is needed for tool handlers.**

Register any new business services the handler depends on in `Program.ConfigureBusinessServices()`:

```csharp
services.AddSingleton<I{NewService}, {NewService}>();
```

### Step 4: Tests — `REBUSS.Pure.Tests/Tools/{Name}ToolHandlerTests.cs`

Pattern (see `GetLocalChangesFilesToolHandlerTests.cs` for full example):

```csharp
using ModelContextProtocol;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

private readonly I{Provider} _provider = Substitute.For<I{Provider}>();
private readonly {Name}ToolHandler _handler;

public {Name}ToolHandlerTests()
{
    _handler = new {Name}ToolHandler(
        _provider,
        NullLogger<{Name}ToolHandler>.Instance);
}

// Required tests:
// - Happy path: call ExecuteAsync with valid args, parse returned JSON, assert structure
// - Parameter defaults: verify default behavior when optional params omitted
// - Validation: invalid param values throw McpException
// - Domain exceptions: provider throws domain exception → McpException with message
// - Unexpected exceptions: provider throws generic Exception → McpException
```

**Test assertions use direct method calls** (no `Dictionary<string, object>` arguments):

```csharp
[Fact]
public async Task ExecuteAsync_ReturnsStructuredJson()
{
    _provider.GetDataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
        .Returns(sampleData);

    var text = await _handler.ExecuteAsync(requiredParam: 42);

    var doc = JsonDocument.Parse(text);
    Assert.Equal("expected", doc.RootElement.GetProperty("fieldName").GetString());
}

[Fact]
public async Task ExecuteAsync_ThrowsMcpException_OnDomainError()
{
    _provider.GetDataAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
        .ThrowsAsync(new {DomainException}("not found"));

    var ex = await Assert.ThrowsAsync<McpException>(
        () => _handler.ExecuteAsync(requiredParam: 1));
    Assert.Contains("not found", ex.Message);
}
```

### Step 5: Smoke test — add to `ExpectedTools` in `McpServerSmokeTests.cs`

### Validation checklist
- [ ] Builds, all tests pass
- [ ] `[McpServerToolType]` on handler class
- [ ] `[McpServerTool(Name = "...")]` and `[Description]` on `ExecuteAsync` method
- [ ] `[Description]` on every parameter
- [ ] `[JsonPropertyName]` on every DTO property
- [ ] `CancellationToken` propagated to all async calls
- [ ] Error handling: domain exceptions → `McpException`, re-throw `McpException`, catch-all → `McpException`
- [ ] Tool appears in `tools/list` (smoke test)
- [ ] `CodebaseUnderstanding.md` updated

### Common pitfalls
- **Missing `[McpServerToolType]`:** class compiles but is never discovered — tool won't appear in `tools/list`.
- **Missing `[JsonPropertyName]`:** serializes as PascalCase, breaking consumers.
- **`Console.Out` usage:** breaks MCP stdio transport. Use `ILogger` (routes to stderr).
- **Stale smoke test:** `ExpectedTools` array not updated causes failure.
- **Returning error JSON instead of throwing:** use `throw new McpException(...)` — SDK handles `isError=true`.
- **Non-nullable param without validation:** SDK enforces `required` in schema, but validate domain constraints (e.g., `> 0`) in the method body.

---

## 2. Add a New Domain Model

### Reference: `PullRequestDiff.cs` (complex), `FullPullRequestMetadata.cs` (simple)

### Steps
1. **Model** in `REBUSS.Pure.Core/Models/{Name}.cs` (plain class, not record)
2. **Interface** — if new capability, extend `IPullRequestDataProvider` or `IRepositoryArchiveProvider` in `IScmClient.cs`
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
services.AddSingleton<IRepositoryArchiveProvider>(sp => sp.GetRequiredService<{Provider}ScmClient>());
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

## 9. MCP Method Handling (SDK-managed)

> **Note:** With SDK v1.2.0, MCP method dispatch (`initialize`, `tools/list`,
> `tools/call`, etc.) is handled internally by the `ModelContextProtocol` SDK.
> The old `IMcpMethodHandler` interface and custom `McpServer` dispatcher have
> been removed. There is no need to implement custom method handlers for
> standard MCP protocol methods.
>
> If you need to customize server behavior, use the SDK's configuration options
> in `Program.cs` (e.g., `AddMcpServer(options => { ... })`).
