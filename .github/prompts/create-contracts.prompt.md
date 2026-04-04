# Task: Create `.github/Contracts.md`

## Goal

Create a **complete MCP tool contract reference** (~200–300 lines) that documents the **exact JSON shapes** flowing between the MCP server and AI agents. This is the single source of truth for input schemas, output examples, error formats, and serialization rules.

This file is critical when:
- Changing JSON output formats of existing tools
- Adding new MCP tools (knowing the exact contract pattern)
- Debugging contract test failures
- Understanding what the consuming AI agent actually sees

## Relationship to existing files

| File | What it says about contracts | Depth |
|---|---|---|
| `ProjectConventions.md` §3 | "JSON: camelCase, WriteIndented, WhenWritingNull" | One-line rule |
| `CodebaseUnderstanding.md` | Lists tool handler files and DTO files | File paths only |
| `DeveloperGuide.md` | Tool names and one-line descriptions | User-facing summary |
| **`Contracts.md`** (this file) | Full input schemas, output JSON examples, error format, serialization pipeline | **Complete contract specification** |

**Non-duplication rule:** Don't repeat the tool descriptions from `DeveloperGuide.md`. Focus on **structural contracts** — the exact JSON shapes, field types, nullability rules, and serialization mechanics that an agent needs to produce correct code.

## How to build it

### Step 1: Read existing documentation for orientation

- `#file:'.github/ProjectConventions.md'` — section 3 (serialization rules to reference, not repeat)
- `#file:'DeveloperGuide.md'` — tool names and descriptions (for cross-reference, not duplication)

### Step 2: Extract input schemas from tool handler `GetToolDefinition()` methods

Read the `GetToolDefinition()` method from **every** tool handler. Extract the exact `ToolInputSchema`: property names, types, descriptions, required/optional, default values, enum values.

**Read these files — focus on `GetToolDefinition()` and `McpTool` return value:**
- `REBUSS.Pure/Tools/GetPullRequestMetadataToolHandler.cs`
- `REBUSS.Pure/Tools/GetPullRequestFilesToolHandler.cs`
- `REBUSS.Pure/Tools/GetPullRequestContentToolHandler.cs`
- `REBUSS.Pure/Tools/GetFileContentAtRefToolHandler.cs`
- `REBUSS.Pure/Tools/GetLocalChangesFilesToolHandler.cs`
- `REBUSS.Pure/Tools/GetLocalContentToolHandler.cs`

### Step 3: Extract output schemas from DTO models

Read **every** output model file. Document every property: name (as it appears in JSON via `[JsonPropertyName]`), C# type, nullable or not, and semantic meaning.

**Read these files in full:**
- `REBUSS.Pure/Tools/Models/StructuredDiffResult.cs` — used by `get_pr_content`, `get_local_content`
- `REBUSS.Pure/Tools/Models/PullRequestMetadataResult.cs` — used by `get_pr_metadata`
- `REBUSS.Pure/Tools/Models/PullRequestFilesResult.cs` — used by `get_pr_files` (also reused by `get_local_files`)
- `REBUSS.Pure/Tools/Models/FileContentAtRefResult.cs` — used by `get_file_content_at_ref`
- `REBUSS.Pure/Tools/Models/LocalReviewFilesResult.cs` — used by `get_local_files`

### Step 4: Understand the serialization pipeline (dual-config)

The server uses **two different serialization configs**. Read these to understand the full pipeline:

- The MCP SDK handles transport-layer serialization internally (compact JSON-RPC envelope)
- `REBUSS.Pure/Tools/GetPullRequestContentToolHandler.cs` — tool handler layer: `camelCase`, `WriteIndented = true`, `WhenWritingNull`

**Key insight to document:** Tool handlers serialize their output DTOs to a JSON **string** (indented, human-readable). This string is returned from the `[McpServerTool]` method. The MCP SDK then wraps it in the JSON-RPC response envelope with compact serialization. The result is: compact JSON-RPC envelope wrapping a `content[0].text` field that contains a pretty-printed JSON string.

### Step 5: Extract error contract from tool handlers

Read the error handling pattern from any tool handler (they all follow the same pattern):
- `REBUSS.Pure/Tools/GetPullRequestContentToolHandler.cs` — `CreateErrorResult` method

And the models (provided by the MCP SDK — `ModelContextProtocol` NuGet package):
- The SDK defines `ToolResult`, `ContentItem`, `JsonRpcResponse`, and error types internally
- Tool handlers return `Task<string>` — the SDK wraps the return value in the appropriate response structure
- For protocol-level errors, the SDK generates standard JSON-RPC error responses

**Document both levels:**
1. **Tool-level error:** `ToolResult { isError: true, content: [{ type: "text", text: "error message" }] }` — returned for business errors (PR not found, file not found, validation errors)
2. **JSON-RPC level error:** `JsonRpcResponse { error: { code: -32601, message: "..." } }` — returned for protocol errors (unknown method, missing params)

### Step 6: Extract semantic rules from handler code and tests

Read the mapping/result-building methods in tool handlers and the assertions in tests to discover semantic rules that aren't visible from DTOs alone:

- `REBUSS.Pure/Tools/GetPullRequestContentToolHandler.cs` — `BuildStructuredResult` method: how domain model maps to DTO
- `REBUSS.Pure/Tools/GetPullRequestMetadataToolHandler.cs` — `BuildMetadataResult` method: `MaxDescriptionLength` truncation, `isDraft` mapping
- `REBUSS.Pure/Tools/GetPullRequestFilesToolHandler.cs` — `BuildResult` method: how file classification maps to output
- `REBUSS.Pure/Tools/GetFileContentAtRefToolHandler.cs` — `ExecuteAsync`: direct mapping, `encoding` field ("utf-8" vs "base64")
- `REBUSS.Pure.Tests/Tools/GetPullRequestContentToolHandlerTests.cs` — JSON assertions: what fields are expected, what's omitted
- `REBUSS.Pure.SmokeTests/Expectations/GitHubTestExpectations.cs` — expected values for contract tests
- `REBUSS.Pure.SmokeTests/Expectations/AdoTestExpectations.cs` — expected values for contract tests

### Step 7: Write the document

Use this **exact structure**:

---

```markdown
# MCP Tool Contracts

> Complete JSON contract reference for all MCP tools.
> For serialization conventions, see `ProjectConventions.md` §3.
> Update this file whenever a tool's input schema, output shape, or error format changes.

## 1. Serialization Pipeline

[Explain the dual-config serialization:
1. Tool handler serializes DTO → JSON string (WriteIndented=true, camelCase, WhenWritingNull)
2. String placed in ToolResult.Content[0].Text
3. MCP transport serializes JsonRpcResponse (WriteIndented=false, camelCase, WhenWritingNull)
4. Result: compact envelope, pretty-printed payload

Include a minimal example showing the layering:
```json
{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\n  \"prNumber\": 42,\n  ...}"}]}}
```
]

## 2. Error Contracts

### Tool-level errors (business logic)
[ToolResult with isError=true. When: validation failure, PR not found, file not found.
JSON example. Note: error message is human-readable, not structured.]

### JSON-RPC errors (protocol level)
[JsonRpcResponse with error object. When: unknown method, invalid params.
JSON example with error code and message.]

## 3. Tool Contracts

### 3.1 `get_pr_metadata`

#### Input
[Table: parameter | type | required | description]

#### Output
[Full JSON example with all fields populated. Then a table documenting each field:
field path | type | nullable | semantic notes (e.g. "state: active|completed|abandoned",
"description.text truncated to 800 chars", "isDraft: boolean")]

#### Shared DTOs
[List which sub-objects (AuthorInfo, RefInfo, PrStats, etc.) are unique to this tool]

---

### 3.2 `get_pr_files`

#### Input
[Table]

#### Output
[Full JSON example. Field table. Note: PullRequestFileItem and PullRequestFilesSummaryResult
are REUSED by get_local_files.]

#### Semantic rules
[reviewPriority values: "high"/"medium"/"low". How classification works.
Binary/generated/test detection logic (reference FileClassifier).]

---

### 3.3 `get_pr_content`

#### Input
[Table: prNumber (required) + pageNumber (required)]

#### Output
[Full plain text example showing paginated diff content.
Note: returns paginated unified-diff content as plain text ContentBlocks.]

#### Semantic rules
[Pagination: splits diff content across pages. Each page contains complete file diffs.
skipReason: when set (binary, generated, full-rewrite), hunks array is empty.
Line op values: "+", "-", " " (context). Additions/deletions counts.]

---

### 3.4 `get_file_content_at_ref`

#### Input
[Table: path (required) + ref (required)]

#### Output
[Full JSON example. encoding: "utf-8" or "base64". 
content: null when binary+base64 not supported (check actual behavior).
size: byte count of content.]

---

### 3.5 `get_local_files`

#### Input
[Table: scope (optional, default "working-tree"). Document all scope values.]

#### Output
[Full JSON example. Note: EXTENDS PullRequestFilesResult shape with
repositoryRoot, scope, currentBranch context fields.
Reuses PullRequestFileItem and PullRequestFilesSummaryResult.]

---

### 3.6 `get_local_content`

#### Input
[Table: pageNumber (required) + scope (optional, default "working-tree")]

#### Output
[Paginated plain text diff content for local changes.
Returns unified-diff content as plain text ContentBlocks.]

---

## 4. Shared DTO Reference

[Table of all shared DTOs across tools:
- StructuredDiffResult — used by: get_pr_content, get_local_content
- StructuredFileChange — nested in StructuredDiffResult
- StructuredHunk — nested in StructuredFileChange
- StructuredLine — nested in StructuredHunk
- PullRequestFileItem — used by: get_pr_files, get_local_files
- PullRequestFilesSummaryResult — used by: get_pr_files, get_local_files

For each DTO: list all JSON fields with types. This is the authoritative field list
that must be updated when DTOs change.]

## 5. Enum & Constant Values

[Collect all constrained string values used across tools:
- state: "active", "completed", "abandoned"
- changeType / status: "add", "edit", "delete", "rename"
- op (line operation): "+", "-", " "
- encoding: "utf-8", "base64"
- reviewPriority: "high", "medium", "low"
- scope: "working-tree", "staged", "<branch-name>"
- fileCategory (from FileClassifier): source, test, config, docs, binary, generated

Source these from: handler code, parser MapStatus/MapState methods, FileClassifier,
LocalReviewScope, and contract test expectations.]
```

---

## Quality rules

1. **JSON examples must be realistic** — use plausible values (real-looking paths, SHAs, branch names), not "string" or "xxx". Generate them based on contract test expectations and unit test fixtures.
2. **Every field documented** — no field in a DTO can be missing from the contract. Cross-check against the C# model files.
3. **Nullable fields explicitly marked** — document which fields can be null/omitted and under what conditions.
4. **Shared DTOs called out** — when multiple tools share the same output shape, document it once in §4 and reference it from each tool section.
5. **Semantic rules are contract-level** — don't explain implementation details (that's `Architecture.md`). Document the observable behavior: what values a field can have, when it's omitted, what it means.
6. **Target 200–300 lines** — use compact JSON examples (not full pretty-printed, just enough to show structure), tables for field docs, bullets for rules.
7. **Stable** — if a tool's schema or output shape changes, this file MUST be updated. Include a note at the top about this maintenance rule.

## After creating the file

1. Add to `CodebaseUnderstanding.md` Documentation section:
   ```
   | `.github\Contracts.md` | Complete MCP tool contract reference: input schemas, output JSON examples, error formats, serialization pipeline, shared DTOs, enum values |
   ```
2. Update `.github/copilot-instructions.md` to add:
   ```markdown
   ### Contract Updates
   Update `.github/Contracts.md` whenever a tool's input schema, output JSON shape,
   error format, or serialization behavior changes. This includes adding/removing/renaming
   fields in tool output DTOs, changing enum values, or modifying error messages.
   ```
3. Do NOT update `ProjectConventions.md`, `README.md`, or `DeveloperGuide.md`.
