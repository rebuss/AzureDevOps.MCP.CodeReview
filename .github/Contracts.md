# MCP Tool Contracts

> Complete JSON contract reference for all MCP tools.
> For serialization conventions, see `ProjectConventions.md` §3.
> Update this file whenever a tool's input schema, output shape, or error format changes.

## 1. Serialization Pipeline

All tool handlers return **`Task<IEnumerable<ContentBlock>>`** where each `ContentBlock` is a `TextContentBlock` containing a plain text string. The SDK wraps these in a standard MCP `content` array.

Output pipeline:

1. **Tool handler** → `PlainTextFormatter` → one `TextContentBlock` per file/section (plain text, no JSON)
2. Handler returns `IEnumerable<ContentBlock>`. The **SDK** wraps the collection directly in the MCP `content` array
3. **SDK transport** serializes the JSON-RPC envelope (outer framing is fully SDK-managed)

Wire format example (diff tool, 2 files + manifest):

```json
{
  "jsonrpc": "2.0", "id": 1,
  "result": {
    "content": [
      {"type":"text","text":"=== src/Foo.cs (edit: +10 -3) ===\n@@ -1,5 +1,7 @@\n +added line\n-removed line\n context"},
      {"type":"text","text":"=== src/Bar.cs (edit: +2 -0) ===\n@@ -1,3 +1,5 @@\n +another line"},
      {"type":"text","text":"Manifest:\n  src/Foo.cs                                                    ~  150 tokens  Included   Source\nBudget: 300/140000 tokens (0%)"}
    ]
  }
}
```

**Block structure by tool:**

| Tool | Blocks returned |
|---|---|
| `get_pr_diff` / `get_file_diff` / `get_local_file_diff` (F003) | `[file_block..., manifest_block]` |
| `get_pr_diff` / `get_pr_files` (F004, paginated) | `[file_block..., manifest_block, pagination_block]` |
| `get_pr_content` / `get_local_content` | `[file_block..., simple_pagination_block]` |
| `get_pr_files` (F003, non-paginated) | `[file_list_block, manifest_block]` |
| `get_local_files` (F004, paginated) | `[file_list_block, manifest_block, pagination_block]` |
| `get_pr_metadata` | `[metadata_block]` (single block) |
| `get_file_content_at_ref` | `[content_block]` (single block) |

> **Refactor note:** Before this change, handlers serialized output DTOs to JSON inside the `text` field. Now all output is human-readable plain text. The `[JsonPropertyName]` output DTOs (`PullRequestMetadataResult`, `PullRequestFilesResult`, `FileContentAtRefResult`, etc.) have been removed. Internal data structures (`StructuredFileChange`, `StructuredHunk`, `StructuredLine`) remain as plain C# classes used by `PlainTextFormatter`.

## 2. Error Contracts

### Tool-level errors (business logic)

Returned when: validation failure, PR not found, file not found, git command failure.

Since the SDK migration, errors are thrown as **`McpException`** from tool handlers. The SDK catches these and produces the standard MCP error response with `isError: true`:

```json
{"content":[{"type":"text","text":"Pull Request not found: PR #999 does not exist"}],"isError":true}
```

Error handling pattern in each handler:

```csharp
catch (PullRequestNotFoundException ex)
{
    throw new McpException($"Pull Request not found: {ex.Message}");
}
catch (McpException) { throw; }
catch (Exception ex)
{
    throw new McpException($"Error retrieving PR files: {ex.Message}");
}
```

> **Migration note:** Previously, errors were returned as `ToolResult { isError = true, content = [...] }` with an `"Error: "` prefix in the message text. Now errors are thrown as `McpException` and the SDK produces the `isError` response. The message text **no longer** carries the `"Error: "` prefix — the `McpException` message is used directly.

Error messages by tool:
- `get_pr_diff` / `get_pr_files` / `get_pr_metadata`: `"Pull Request not found: ..."`, `"Error retrieving PR ..."`
- `get_file_diff`: `"File not found in Pull Request: ..."`, `"Pull Request not found: ..."`
- `get_file_content_at_ref`: `"File not found: ..."`, `"Error retrieving file content: ..."`
- `get_local_files`: `"Repository not found: ..."`, `"Git command failed: ..."`
- `get_local_file_diff`: `"File not found in local changes: ..."`, `"Repository not found: ..."`, `"Git command failed: ..."`
- `get_pr_content`: `"Missing required parameter: prNumber"`, `"Missing required parameter: pageNumber"`, `"pageNumber N exceeds total pages M"`, `"Pull Request not found: ..."`, `"Error retrieving PR content: ..."`
- `get_local_content`: `"Missing required parameter: pageNumber"`, `"pageNumber N exceeds total pages M"`, `"Error retrieving local content: ..."`

### JSON-RPC errors (protocol level)

Returned when: unknown method, malformed request, protocol version negotiation failure. Uses standard JSON-RPC error codes. These are now fully handled by the SDK.

```json
{"jsonrpc":"2.0","id":"err-1","error":{"code":-32601,"message":"Method not found","data":"Method not found: nonexistent/method"}}
```

### Protocol version negotiation

Protocol version negotiation is **fully handled by the SDK**. The server advertises the latest protocol version it supports, and the SDK performs the negotiation automatically during the `initialize` handshake.

### Initialize response

The `initialize` response is **auto-generated by the SDK**. It includes:

- **`serverInfo`** — name and version, configured via `AddMcpServer(options => { options.ServerInfo = new() { Name = "REBUSS.Pure", Version = "1.0.0" }; })`
- **`capabilities`** — auto-populated based on registered tool types and transport
- **`protocolVersion`** — negotiated automatically by the SDK

```json
{
  "jsonrpc": "2.0",
  "id": 1,
  "result": {
    "protocolVersion": "2025-03-26",
    "capabilities": { "tools": { "listChanged": false } },
    "serverInfo": { "name": "REBUSS.Pure", "version": "1.0.0" }
  }
}
```

> **Migration note:** Previously, the server had custom `initialize` handling with explicit protocol version negotiation across multiple supported versions. Now the SDK manages the full handshake. The exact `protocolVersion` and `capabilities` shape may vary with SDK version updates.

## 3. Tool Input Schema Generation

Since the migration to the **ModelContextProtocol SDK v1.2.0**, tool input schemas are **auto-generated by the SDK** from the C# method signatures of `[McpServerTool]`-decorated methods. Hand-crafted `GetToolDefinition()` methods are no longer used.

### Schema generation rules

| C# signature pattern | JSON Schema result | Example |
|---|---|---|
| Non-nullable value type (`int prNumber`) | Required parameter | `"prNumber": { "type": "integer" }` in `required` array |
| Non-nullable reference type (`string path`) | Required parameter | `"path": { "type": "string" }` in `required` array |
| Nullable with default (`int? prNumber = null`) | Optional parameter | `"prNumber": { "type": "integer" }` — **not** in `required` |
| Nullable with default (`string? scope = null`) | Optional parameter | `"scope": { "type": "string" }` — **not** in `required` |
| `[Description("...")]` on parameter | `"description"` field | `"prNumber": { "type": "integer", "description": "The PR number/ID" }` |
| `CancellationToken` parameter | **Not exposed** | Auto-injected by SDK, invisible in schema |
| `@ref` (C# keyword escape) | `"ref"` in JSON | C# `string @ref` → schema property `"ref"` |

### Example: auto-generated schema for `get_file_content_at_ref`

C# signature:
```csharp
[McpServerTool(Name = "get_file_content_at_ref")]
public async Task<string> ExecuteAsync(
    [Description("The repository-relative path of the file")] string path,
    [Description("The Git ref to fetch the file at: a commit SHA, branch, or tag")] string @ref,
    CancellationToken cancellationToken = default)
```

Auto-generated JSON Schema:
```json
{
  "name": "get_file_content_at_ref",
  "inputSchema": {
    "type": "object",
    "properties": {
      "path": { "type": "string", "description": "The repository-relative path of the file" },
      "ref": { "type": "string", "description": "The Git ref to fetch the file at: a commit SHA, branch, or tag" }
    },
    "required": ["path", "ref"]
  }
}
```

> **Migration note:** Previously, each tool handler implemented a `GetToolDefinition()` method that returned a hand-crafted `Tool` object with the schema. Now the SDK infers the schema from the method signature. The contract is equivalent, but the source of truth is the C# method signature rather than a manual JSON definition.

## 4. Tool Contracts

### 4.1 `get_pr_metadata`

#### Input

Auto-generated from `GetPullRequestMetadataToolHandler.ExecuteAsync` signature. `prNumber` is logically required (validated at runtime); budget parameters are optional and trigger `contentPaging` computation.

| Parameter | C# Type | Required | Default | Description |
|---|---|---|---|---|
| `prNumber` | `int?` | ✅ (runtime) | `null` | The Pull Request number/ID to retrieve metadata for |
| `modelName` | `string?` | ❌ | `null` | Model name for context budget resolution (e.g. `"gpt-4o"`). Triggers pagination info. |
| `maxTokens` | `int?` | ❌ | `null` | Explicit token budget override. Triggers pagination info. |

> **Feature 005 note:** When `modelName` or `maxTokens` is provided, the response includes content paging info in plain text for use with `get_pr_content`.

#### Output — plain text (single `TextContentBlock`)

```
PR #42: Add caching support
State:    active
Author:   jdoe (Jane Doe)
Branch:   refs/heads/feature/cache → refs/heads/main
Stats:    5 file(s) | +120 -40 | 3 commit(s)
URL:      https://dev.azure.com/org/project/_git/repo/pullrequest/42
Head SHA: def456
Base SHA: abc123
Description:
  PR description...

Content paging: 5 page(s) | 286 file(s) | 89600 tokens/page
Files per page: p1:12f, p2:15f, p3:18f, p4:14f, p5:8f
```

| Line | Condition |
|---|---|
| `URL:` | Only present when `WebUrl` is non-empty |
| `Head SHA:` / `Base SHA:` | Only present when commit SHAs are available |
| `Description:` block | Only present when PR has a description; truncated to **800 chars** |
| `Content paging:` block | Only present when `modelName` or `maxTokens` was provided |
| Author display name | Only appended when display name differs from login |

---

### 4.2 `get_pr_files`

#### Input

Auto-generated from `GetPullRequestFilesToolHandler.ExecuteAsync` signature. All parameters are nullable with defaults, making them optional in the schema.

| Parameter | C# Type | Required | Description |
|---|---|---|---|
| `prNumber` | `int?` | ❌ | The Pull Request number/ID to retrieve the file list for |
| `modelName` | `string?` | ❌ | Optional model name (e.g. 'Claude Sonnet') to resolve context window size |
| `maxTokens` | `int?` | ❌ | Optional explicit context window size in tokens |
| `pageReference` | `string?` | ❌ | Opaque page reference from a previous response. Encodes all context needed to re-derive the page. |
| `pageNumber` | `int?` | ❌ | Page number for direct access (requires original params + budget) |

> **Feature 004 note:** `prNumber` is **optional** when `pageReference` is provided (the page reference encodes the original request parameters). `pageReference` and `pageNumber` are mutually exclusive.

#### Output — plain text blocks

**Block 1:** file list table

```
Changed files: PR #42 (2 file(s))
  Path                                                           Status       +Add    -Del  Priority Flags
  ----------------------------------------------------------------------------------------------------
  src/Cache/CacheService.cs                                      edit          +45     -12  high
  tests/CacheServiceTests.cs                                     edit          +30      -0  medium [test]

Summary: 1 source, 1 test | High priority: 1
```

**Block 2:** manifest

```
Manifest:
  src/Cache/CacheService.cs                                          ~  450 tokens  Included   Source
  tests/CacheServiceTests.cs                                         ~  300 tokens  Included   Test
Budget: 750/140000 tokens (1%)
```

**Block 3 (paginated only — Feature 004):** pagination footer

```
--- Page 1 of 3 | hasMore: true | next: eyJ0IjoiZ2V0X3ByX2ZpbGVzIi... | STALE: head changed (abc123… → def456…) ---
```

| Part | Condition |
|---|---|
| `next: <ref>` | Only present when `hasMore: true` |
| `STALE: ...` | Only present when staleness detected (head SHA changed between pages) |

---

### 4.3 `get_pr_diff`

#### Input

Auto-generated from `GetPullRequestDiffToolHandler.ExecuteAsync` signature. All parameters are nullable with defaults, making them optional in the schema.

| Parameter | C# Type | Required | Description |
|---|---|---|---|
| `prNumber` | `int?` | ❌ | The Pull Request number/ID to retrieve the diff for |
| `modelName` | `string?` | ❌ | Optional model name (e.g. 'Claude Sonnet') to resolve context window size |
| `maxTokens` | `int?` | ❌ | Optional explicit context window size in tokens |
| `pageReference` | `string?` | ❌ | Opaque page reference from a previous response. Encodes all context needed to re-derive the page. |
| `pageNumber` | `int?` | ❌ | Page number for direct access (requires original params + budget) |

> **Feature 004 note:** `prNumber` is **optional** when `pageReference` is provided (the page reference encodes the original request parameters). `pageReference` and `pageNumber` are mutually exclusive.

#### Output — plain text blocks

**Block 1..N:** one `TextContentBlock` per file

Each file block starts with a `=== path (changeType: +additions -deletions) ===` header, followed by one or more hunks. Each hunk begins with a unified-diff `@@ -oldStart,oldCount +newStart,newCount @@` header line, then `+`/`-`/` `-prefixed content lines:

```
=== src/Cache/CacheService.cs (edit: +5 -2) ===
@@ -10,4 +10,5 @@
 public class CacheService
-    private int _ttl = 60;
+    private int _ttl = 300;
```

For skipped files (binary, generated, etc.):

```
=== docs/logo.png (add: skipped) ===
Reason: Binary file
```

**Last block:** manifest

```
Manifest:
  src/Cache/CacheService.cs                                          ~  150 tokens  Included   Source
  docs/logo.png                                                      ~    5 tokens  Included   Docs
Budget: 155/140000 tokens (0%)
```

**Extra block (paginated only — Feature 004):** pagination footer (appended after manifest)

```
--- Page 1 of 3 | hasMore: true | next: eyJ0IjoiZ2V0X3ByX2RpZmYiL... | STALE: head changed (abc123… → def456…) ---
```

**Shared by:** `get_pr_diff`, `get_file_diff`, `get_local_file_diff`. For `get_file_diff`, exactly 1 file block is returned.

---

### 4.4 `get_file_diff`

#### Input

Auto-generated from `GetFileDiffToolHandler.ExecuteAsync` signature. Both parameters are non-nullable, making them required.

| Parameter | C# Type | Required | Description |
|---|---|---|---|
| `prNumber` | `int` | ✅ | The Pull Request number/ID |
| `path` | `string` | ✅ | Repository-relative file path |

#### Output

Same plain text format as `get_pr_diff`. Returns exactly 1 file block + manifest block. Tool-level error if file not in PR.

---

### 4.5 `get_file_content_at_ref`

#### Input

Auto-generated from `GetFileContentAtRefToolHandler.ExecuteAsync` signature. Both parameters are non-nullable, making them required. Note: `@ref` (C# keyword escape) maps to `ref` in the JSON schema.

| Parameter | C# Type | Required | Description |
|---|---|---|---|
| `path` | `string` | ✅ | The repository-relative path of the file |
| `ref` | `string` | ✅ | The Git ref to fetch the file at: a commit SHA, branch name, or tag |

#### Output — plain text (single `TextContentBlock`)

```
=== src/Cache/CacheService.cs @ abc123def456 ===
using System;

namespace Cache...
```

For binary files:

```
=== docs/logo.png @ abc123def456 ===
[binary file, size: 4096 bytes, encoding: base64]
```

| Part | Notes |
|---|---|
| Header line | `=== {path} @ {ref} ===` |
| Body | Raw file content for text files; `[binary file, ...]` for binary; `[file content not available]` if content is null |

---

### 4.6 `get_local_files`

#### Input

Auto-generated from `GetLocalChangesFilesToolHandler.ExecuteAsync` signature. All parameters are nullable with defaults, making them optional in the schema.

| Parameter | C# Type | Required | Default | Description |
|---|---|---|---|---|
| `scope` | `string?` | ❌ | `null` (resolved to `"working-tree"`) | Scope of local changes: `"working-tree"`, `"staged"`, or any branch/ref name |
| `modelName` | `string?` | ❌ | `null` | Optional model name to resolve context window size |
| `maxTokens` | `int?` | ❌ | `null` | Optional explicit context window size in tokens |
| `pageReference` | `string?` | ❌ | `null` | Opaque page reference from a previous response. Mutually exclusive with `pageNumber`. |
| `pageNumber` | `int?` | ❌ | `null` | Page number for direct access. Mutually exclusive with `pageReference`. |

#### Output — plain text blocks

Same block structure as `get_pr_files`. Context string in the file list header uses the local scope, e.g. `"working-tree (3 file(s))"`.

**Block 1:** file list table (same format as `get_pr_files`, context = `"{scope} (N file(s))"` for non-paginated or `"{scope} (page P/T)"` for paginated)

```
Changed files: working-tree (2 file(s))
  Path                                                           Status       +Add    -Del  Priority Flags
  ----------------------------------------------------------------------------------------------------
  src/Cache/CacheService.cs                                      modified        +5      -2  high
  tests/CacheServiceTests.cs                                     modified        +3      -0  medium [test]

Summary: 1 source, 1 test | High priority: 1
```

**Block 2:** manifest (same format as `get_pr_files`)

**Block 3 (paginated only — Feature 004):** pagination footer (no staleness warning for local tools)

```
--- Page 1 of 2 | hasMore: true | next: eyJ0IjoiZ2V0X2xvY2FsX2Zp... ---
```

> **Note:** `get_local_files` does **not** include `stalenessWarning` (local tools use null fingerprint).

**Local`status` values differ from PR tools:** `"added"`, `"modified"`, `"removed"`, `"renamed"` (git status codes).

---

### 4.7 `get_local_file_diff`

#### Input

Auto-generated from `GetLocalFileDiffToolHandler.ExecuteAsync` signature.

| Parameter | C# Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | `string` | ✅ | — | Repository-relative file path |
| `scope` | `string?` | ❌ | `null` (resolved to `"working-tree"`) | Same scope values as `get_local_files` |
| `modelName` | `string?` | ❌ | `null` | Optional model name to resolve context window size |
| `maxTokens` | `int?` | ❌ | `null` | Optional explicit context window size in tokens |

#### Output

Same plain text format as `get_pr_diff`. Returns exactly 1 file block + manifest block.

---

### 4.8 `get_pr_content`

#### Input

Auto-generated from `GetPullRequestContentToolHandler.ExecuteAsync` signature. All parameters are nullable with defaults, making them optional in the schema. `prNumber` and `pageNumber` are logically required (validated at runtime).

| Parameter | C# Type | Required | Default | Description |
|---|---|---|---|---|
| `prNumber` | `int?` | ✅ (runtime) | `null` | The Pull Request number/ID |
| `pageNumber` | `int?` | ✅ (runtime) | `null` | Page number to retrieve (1-based) |
| `modelName` | `string?` | ❌ | `null` | Model name for context budget resolution |
| `maxTokens` | `int?` | ❌ | `null` | Explicit token budget override |

#### Output — plain text blocks

**Block 1..N:** one `TextContentBlock` per file (same diff format as `get_pr_diff`, with `@@ -oldStart,oldCount +newStart,newCount @@` hunk headers)

```
=== src/Cache/CacheService.cs (edit: +5 -2) ===
@@ -10,4 +10,5 @@
 public class CacheService
-    private int _ttl = 60;
+    private int _ttl = 300;
```

**Last block:** simple pagination footer

```
--- Page 1 of 5 | hasMore: true | 12/286 files | ~85000 tokens | 10 Source, 2 Config ---
```

| Part | Notes |
|---|---|
| `hasMore` | `true` when `pageNumber < totalPages` |
| `N/M files` | Files on this page / total files |
| `~X tokens` | Estimated token usage for this page |
| Category breakdown | Optional; shows count per `FileCategory` on this page (e.g. `3 Source, 1 Test`). Omitted when empty. |

Error messages: `"Missing required parameter: prNumber"`, `"prNumber must be greater than 0"`, `"Missing required parameter: pageNumber"`, `"pageNumber must be >= 1"`, `"pageNumber N exceeds total pages M"`, `"Pull Request not found: ..."`, `"Error retrieving PR content: ..."`.

---

### 4.9 `get_local_content`

#### Input

Auto-generated from `GetLocalContentToolHandler.ExecuteAsync` signature. All parameters are nullable with defaults, making them optional in the schema. `pageNumber` is logically required (validated at runtime).

| Parameter | C# Type | Required | Default | Description |
|---|---|---|---|---|
| `pageNumber` | `int?` | ✅ (runtime) | `null` | Page number to retrieve (1-based) |
| `scope` | `string?` | ❌ | `null` (resolved to `"working-tree"`) | Review scope: `"working-tree"`, `"staged"`, or a branch/ref name |
| `modelName` | `string?` | ❌ | `null` | Model name for context budget resolution |
| `maxTokens` | `int?` | ❌ | `null` | Explicit token budget override |

#### Output — plain text blocks

Same structure as `get_pr_content`. One `TextContentBlock` per file on the page, followed by a simple pagination footer block (including category breakdown).

```
--- Page 1 of 2 | hasMore: true | 5/8 files | ~12000 tokens | 4 Source, 1 Test ---
```

Error messages: `"Missing required parameter: pageNumber"`, `"pageNumber must be >= 1"`, `"pageNumber N exceeds total pages M"`, `"Error retrieving local content: ..."`.

---

## 5. Internal Data Structures

These C# classes are used internally by `PlainTextFormatter`, `ToolHandlerHelpers`, and `FileTokenMeasurement`. They are **not** JSON output DTOs — they have no `[JsonPropertyName]` attributes.

| Type | Used by | Fields |
|---|---|---|
| **StructuredFileChange** | `PlainTextFormatter.FormatFileDiff`, `ToolHandlerHelpers.TruncateHunks`, `FileTokenMeasurement` | `path`, `changeType`, `skipReason`?, `additions`, `deletions`, `hunks` |
| **StructuredHunk** | nested in `StructuredFileChange` | `oldStart`, `oldCount`, `newStart`, `newCount`, `lines` |
| **StructuredLine** | nested in `StructuredHunk` | `op` (`"+"`, `"-"`, `" "`), `text` |
| **ContentManifestResult** | `PlainTextFormatter.FormatManifestBlock`, `ToolHandlerHelpers.BuildPageManifest` | `items` (ManifestEntryResult[]), `summary` (ManifestSummaryResult) |
| **ManifestEntryResult** | nested in ContentManifestResult | `path`, `estimatedTokens`, `status`, `priorityTier` |
| **ManifestSummaryResult** | nested in ContentManifestResult | `totalItems`, `includedCount`, `partialCount`, `deferredCount`, `totalBudgetTokens`, `budgetUsed`, `budgetRemaining`, `utilizationPercent`, `includedOnThisPage`? (int?), `remainingAfterThisPage`? (int?), `totalPages`? (int?) |
| **PaginationMetadataResult** | `PlainTextFormatter.FormatPaginationBlock` | `currentPage`, `totalPages`, `hasMore`, `currentPageReference`, `nextPageReference`? |
| **StalenessWarningResult** | `PlainTextFormatter.FormatPaginationBlock`, `FormatManifestBlock` | `message`, `originalFingerprint`, `currentFingerprint` |

## 6. Enum & Constant Values

| Value set | Values | Source |
|---|---|---|
| **state** (PR metadata) | `"active"`, `"completed"`, `"abandoned"` | ADO: native. GitHub: `MapState(state, merged)` |
| **changeType** (diff) | `"add"`, `"edit"`, `"delete"`, `"rename"` | ADO: `MapChangeType`. GitHub: `MapStatus`. Local: `MapChangeType` |
| **status** (PR files) | `"add"`, `"edit"`, `"delete"`, `"rename"` | Same mapping as changeType for PR tools |
| **status** (local files) | `"added"`, `"modified"`, `"removed"`, `"renamed"` | Local: `MapStatus(char)` — git status codes |
| **op** (diff line) | `"+"`, `"-"`, `" "` | `DiffLine.Op.ToString()` — char to string |
| **encoding** (file content) | `"utf-8"`, `"base64"` | Text vs binary files |
| **reviewPriority** | `"high"`, `"medium"`, `"low"` | `FileClassifier.DetermineReviewPriority(category)` |
| **scope** (local tools) | `"working-tree"`, `"staged"`, `"<branch-name>"` | `LocalReviewScope.Parse(string?)` |
| **fileCategory** (internal) | Source, Test, Config, Docs, Binary, Generated | `FileCategory` enum — mapped to summary counts, not directly in output |
