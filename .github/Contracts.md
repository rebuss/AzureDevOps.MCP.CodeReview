# MCP Tool Contracts

> Complete JSON contract reference for all MCP tools.
> For serialization conventions, see `ProjectConventions.md` §3.
> Update this file whenever a tool's input schema, output shape, or error format changes.

## 1. Serialization Pipeline

Tool output goes through **two serialization layers**:

1. **Tool handler** serializes DTO → JSON string (`camelCase`, `WriteIndented = true`, `WhenWritingNull` ignore)
2. String placed in `ToolResult.Content[0].Text`
3. **MCP transport** (`SystemTextJsonSerializer`) serializes `JsonRpcResponse` (`camelCase`, `WriteIndented = false`, `WhenWritingNull` ignore)

Result: compact JSON-RPC envelope wrapping a pretty-printed payload string:

```json
{"jsonrpc":"2.0","id":1,"result":{"content":[{"type":"text","text":"{\n  \"prNumber\": 42,\n  \"files\": [...]\n}"}]}}
```

## 2. Error Contracts

### Tool-level errors (business logic)

Returned when: validation failure, PR not found, file not found, git command failure.
`ToolResult.isError = true`, message is human-readable, not structured.

```json
{"content":[{"type":"text","text":"Error: Pull Request not found: PR #999 does not exist"}],"isError":true}
```

Error prefixes by tool:
- `get_pr_diff` / `get_pr_files` / `get_pr_metadata`: `"Error: Pull Request not found: ..."`, `"Error: Error retrieving PR ..."`
- `get_file_diff`: `"Error: File not found in Pull Request: ..."`, `"Error: Pull Request not found: ..."`
- `get_file_content_at_ref`: `"Error: File not found: ..."`, `"Error: Error retrieving file content: ..."`
- `get_local_files`: `"Error: Repository not found: ..."`, `"Error: Git command failed: ..."`
- `get_local_file_diff`: `"Error: File not found in local changes: ..."`, `"Error: Repository not found: ..."`, `"Error: Git command failed: ..."`

### JSON-RPC errors (protocol level)

Returned when: unknown method, malformed request. Uses standard JSON-RPC error codes.

```json
{"jsonrpc":"2.0","id":"err-1","error":{"code":-32601,"message":"Method not found: nonexistent/method"}}
```

## 3. Tool Contracts

### 3.1 `get_pr_metadata`

#### Input

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prNumber` | integer | ✅ | PR number/ID |

#### Output — `PullRequestMetadataResult`

```json
{
  "prNumber": 42,
  "id": 12345,
  "title": "Add caching support",
  "author": { "login": "jdoe", "displayName": "Jane Doe" },
  "state": "active",
  "isDraft": false,
  "createdAt": "2024-11-15T10:30:00.0000000Z",
  "updatedAt": null,
  "base": { "ref": "refs/heads/main", "sha": "abc123" },
  "head": { "ref": "refs/heads/feature/cache", "sha": "def456" },
  "stats": { "commits": 3, "changedFiles": 5, "additions": 120, "deletions": 40 },
  "commitShas": ["def456", "789abc", "012def"],
  "description": { "text": "PR description...", "isTruncated": false, "originalLength": 18, "returnedLength": 18 },
  "source": { "repository": "org/repo", "url": "https://dev.azure.com/org/project/_git/repo/pullrequest/42" }
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `state` | string | no | `"active"`, `"completed"`, `"abandoned"` (GitHub `open`→`active`, `closed+merged`→`completed`, `closed`→`abandoned`) |
| `isDraft` | bool | no | |
| `createdAt` | string | no | ISO 8601 round-trip format (`"O"`) |
| `updatedAt` | string | **yes** | Set to `closedDate` if present; omitted when null |
| `description.text` | string | no | Truncated to **800 chars** if longer |
| `description.isTruncated` | bool | no | `true` when original exceeded 800 chars |

---

### 3.2 `get_pr_files`

#### Input

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prNumber` | integer | ✅ | PR number/ID |
| `modelName` | string | ❌ | Optional model name to resolve context window size |
| `maxTokens` | integer | ❌ | Optional explicit context window size in tokens |
| `pageReference` | string | ❌ | Opaque page reference from a previous response (Feature 004). When provided, `prNumber` becomes optional. Mutually exclusive with `pageNumber`. |
| `pageNumber` | integer | ❌ | Page number for direct access (Feature 004). Mutually exclusive with `pageReference`. |

> **Feature 004 note:** `prNumber` is **optional** when `pageReference` is provided (the page reference encodes the original request parameters). `pageReference` and `pageNumber` are mutually exclusive.

#### Output — `PullRequestFilesResult`

```json
{
  "prNumber": 42,
  "totalFiles": 2,
  "files": [
    {
      "path": "src/Cache/CacheService.cs",
      "status": "edit",
      "additions": 45, "deletions": 12, "changes": 57,
      "extension": ".cs",
      "isBinary": false, "isGenerated": false, "isTestFile": false,
      "reviewPriority": "high"
    }
  ],
  "summary": {
    "sourceFiles": 1, "testFiles": 1, "configFiles": 0,
    "docsFiles": 0, "binaryFiles": 0, "generatedFiles": 0,
    "highPriorityFiles": 1
  },
  "manifest": {
    "items": [
      { "path": "src/Cache/CacheService.cs", "estimatedTokens": 100, "status": "Included", "priorityTier": "Source" }
    ],
    "summary": {
      "totalItems": 2, "includedCount": 2, "partialCount": 0, "deferredCount": 0,
      "totalBudgetTokens": 140000, "budgetUsed": 200, "budgetRemaining": 139800, "utilizationPercent": 0.1
    }
  }
}
```

Uses **`PullRequestFileItem`** (shared with `get_local_files`), **`PullRequestFilesSummaryResult`** (shared), and **`ContentManifestResult`** (packing manifest).

When paginated (Feature 004), the response includes additional top-level fields:

```json
{
  "prNumber": 42,
  "totalFiles": 2,
  "files": [ /* ... */ ],
  "summary": { /* ... */ },
  "manifest": { /* ... */ },
  "pagination": {
    "currentPage": 1,
    "totalPages": 3,
    "hasMore": true,
    "currentPageReference": "eyJ0IjoiZ2V0X3ByX2ZpbGVzIi...",
    "nextPageReference": "eyJ0IjoiZ2V0X3ByX2ZpbGVzIi..."
  },
  "stalenessWarning": {
    "message": "PR data has changed since pagination started...",
    "originalFingerprint": "abc123",
    "currentFingerprint": "def456"
  }
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `pagination` | object? | **yes** | Feature 004: omitted when not paginated |
| `stalenessWarning` | object? | **yes** | Feature 004: omitted when no staleness detected |

---

### 3.3 `get_pr_diff`

#### Input

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prNumber` | integer | ✅ | PR number/ID |
| `modelName` | string | ❌ | Optional model name to resolve context window size |
| `maxTokens` | integer | ❌ | Optional explicit context window size in tokens |
| `pageReference` | string | ❌ | Opaque page reference from a previous response (Feature 004). When provided, `prNumber` becomes optional. Mutually exclusive with `pageNumber`. |
| `pageNumber` | integer | ❌ | Page number for direct access (Feature 004). Mutually exclusive with `pageReference`. |

> **Feature 004 note:** `prNumber` is **optional** when `pageReference` is provided (the page reference encodes the original request parameters). `pageReference` and `pageNumber` are mutually exclusive.

#### Output — `StructuredDiffResult`

```json
{
  "prNumber": 42,
  "files": [
    {
      "path": "src/Cache/CacheService.cs",
      "changeType": "edit",
      "additions": 5, "deletions": 2,
      "hunks": [
        {
          "oldStart": 10, "oldCount": 7, "newStart": 10, "newCount": 10,
          "lines": [
            { "op": " ", "text": "    public class CacheService" },
            { "op": "-", "text": "        private int _ttl = 60;" },
            { "op": "+", "text": "        private int _ttl = 300;" }
          ]
        }
      ]
    },
    {
      "path": "docs/logo.png",
      "changeType": "add",
      "skipReason": "Binary file",
      "additions": 0, "deletions": 0,
      "hunks": []
    }
  ]
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `prNumber` | int? | **yes** | Set for PR diffs; `null` (omitted) for local diffs |
| `skipReason` | string? | **yes** | Set when hunks are empty: `"Binary file"`, `"Generated file"`, `"Full rewrite"`, or partial truncation reason |
| `hunks` | array | no | Empty array `[]` when `skipReason` is set |
| `manifest` | object? | **yes** | Packing manifest with items + summary; `null` (omitted) when WhenWritingNull |

**Shared by:** `get_pr_diff`, `get_file_diff`, `get_local_file_diff`.

When paginated (Feature 004), the response includes additional top-level fields:

```json
{
  "prNumber": 42,
  "files": [ /* ... */ ],
  "manifest": { /* ... */ },
  "pagination": {
    "currentPage": 1,
    "totalPages": 3,
    "hasMore": true,
    "currentPageReference": "eyJ0IjoiZ2V0X3ByX2RpZmYiL...",
    "nextPageReference": "eyJ0IjoiZ2V0X3ByX2RpZmYiL..."
  },
  "stalenessWarning": {
    "message": "PR data has changed since pagination started...",
    "originalFingerprint": "abc123",
    "currentFingerprint": "def456"
  }
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `pagination` | object? | **yes** | Feature 004: PaginationMetadataResult; omitted when not paginated |
| `stalenessWarning` | object? | **yes** | Feature 004: StalenessWarningResult; omitted when no staleness detected |

---

### 3.4 `get_file_diff`

#### Input

| Parameter | Type | Required | Description |
|---|---|---|---|
| `prNumber` | integer | ✅ | PR number/ID |
| `path` | string | ✅ | Repository-relative file path |

#### Output

Same `StructuredDiffResult` shape. `files` array contains **exactly 1 element** (or tool-level error if file not in PR).

---

### 3.5 `get_file_content_at_ref`

#### Input

| Parameter | Type | Required | Description |
|---|---|---|---|
| `path` | string | ✅ | Repository-relative file path |
| `ref` | string | ✅ | Commit SHA, branch name, or tag |

#### Output — `FileContentAtRefResult`

```json
{
  "path": "src/Cache/CacheService.cs",
  "ref": "abc123def456",
  "size": 1234,
  "encoding": "utf-8",
  "content": "using System;\n\nnamespace Cache...",
  "isBinary": false
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `encoding` | string | no | `"utf-8"` for text, `"base64"` for binary |
| `content` | string? | **yes** | `null` when binary and base64 not available |
| `size` | int | no | Byte count of content (UTF-8 encoded) |

---

### 3.6 `get_local_files`

#### Input

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `scope` | string | no | `"working-tree"` | `"working-tree"`, `"staged"`, or any branch/ref name |
| `modelName` | string | no | — | Optional model name to resolve context window size |
| `maxTokens` | integer | no | — | Optional explicit context window size in tokens |
| `pageReference` | string | no | — | Opaque page reference from a previous response (Feature 004). Mutually exclusive with `pageNumber`. |
| `pageNumber` | integer | no | — | Page number for direct access (Feature 004). Mutually exclusive with `pageReference`. |

#### Output — `LocalReviewFilesResult`

```json
{
  "repositoryRoot": "C:/Projects/MyApp",
  "scope": "working-tree",
  "currentBranch": "feature/cache",
  "totalFiles": 3,
  "files": [ /* PullRequestFileItem[] — same shape as get_pr_files */ ],
  "summary": { /* PullRequestFilesSummaryResult — same shape as get_pr_files */ },
  "manifest": { /* ContentManifestResult — same shape as get_pr_files manifest */ }
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `currentBranch` | string? | **yes** | Omitted when detached HEAD |
| `scope` | string | no | Echoes the resolved scope value |
| `manifest` | object? | **yes** | Packing manifest with items + summary |

Reuses `PullRequestFileItem` and `PullRequestFilesSummaryResult` from `get_pr_files`.

When paginated (Feature 004), the response includes an additional top-level field:

```json
{
  "repositoryRoot": "C:/Projects/MyApp",
  "scope": "working-tree",
  "currentBranch": "feature/cache",
  "totalFiles": 3,
  "files": [ /* ... */ ],
  "summary": { /* ... */ },
  "manifest": { /* ... */ },
  "pagination": {
    "currentPage": 1,
    "totalPages": 2,
    "hasMore": true,
    "currentPageReference": "eyJ0IjoiZ2V0X2xvY2FsX2Zp...",
    "nextPageReference": "eyJ0IjoiZ2V0X2xvY2FsX2Zp..."
  }
}
```

| Field | Type | Nullable | Notes |
|---|---|---|---|
| `pagination` | object? | **yes** | Feature 004: PaginationMetadataResult; omitted when not paginated |

> **Note:** `get_local_files` does **not** include `stalenessWarning` (local tools use null fingerprint).

**Local`status` values differ from PR tools:** `"added"`, `"modified"`, `"removed"`, `"renamed"` (git status codes).

---

### 3.7 `get_local_file_diff`

#### Input

| Parameter | Type | Required | Default | Description |
|---|---|---|---|---|
| `path` | string | ✅ | — | Repository-relative file path |
| `scope` | string | no | `"working-tree"` | Same scope values as `get_local_files` |
| `modelName` | string | no | — | Optional model name to resolve context window size |
| `maxTokens` | integer | no | — | Optional explicit context window size in tokens |

#### Output

Same `StructuredDiffResult` shape with `manifest`. `prNumber` is `null` (omitted from JSON). `files` array contains **exactly 1 element**.

---

## 4. Shared DTO Reference

| DTO | Used by | Fields |
|---|---|---|
| **StructuredDiffResult** | `get_pr_diff`, `get_file_diff`, `get_local_file_diff` | `prNumber` (int?), `files` (StructuredFileChange[]), `manifest`? (ContentManifestResult), `pagination`? (PaginationMetadataResult — Feature 004), `stalenessWarning`? (StalenessWarningResult — Feature 004) |
| **StructuredFileChange** | nested in above | `path`, `changeType`, `skipReason`?, `additions`, `deletions`, `hunks` |
| **StructuredHunk** | nested in above | `oldStart`, `oldCount`, `newStart`, `newCount`, `lines` |
| **StructuredLine** | nested in above | `op`, `text` |
| **PullRequestFileItem** | `get_pr_files`, `get_local_files` | `path`, `status`, `additions`, `deletions`, `changes`, `extension`, `isBinary`, `isGenerated`, `isTestFile`, `reviewPriority` |
| **PullRequestFilesSummaryResult** | `get_pr_files`, `get_local_files` | `sourceFiles`, `testFiles`, `configFiles`, `docsFiles`, `binaryFiles`, `generatedFiles`, `highPriorityFiles` |
| **ContentManifestResult** | `get_pr_diff`, `get_pr_files`, `get_local_files`, `get_local_file_diff` | `items` (ManifestEntryResult[]), `summary` (ManifestSummaryResult) |
| **ManifestEntryResult** | nested in ContentManifestResult | `path`, `estimatedTokens`, `status`, `priorityTier` |
| **ManifestSummaryResult** | nested in ContentManifestResult | `totalItems`, `includedCount`, `partialCount`, `deferredCount`, `totalBudgetTokens`, `budgetUsed`, `budgetRemaining`, `utilizationPercent`, `includedOnThisPage`? (int? — Feature 004), `remainingAfterThisPage`? (int? — Feature 004), `totalPages`? (int? — Feature 004) |
| **AuthorInfo** | `get_pr_metadata` | `login`, `displayName` |
| **RefInfo** | `get_pr_metadata` | `ref`, `sha` |
| **PrStats** | `get_pr_metadata` | `commits`, `changedFiles`, `additions`, `deletions` |
| **DescriptionInfo** | `get_pr_metadata` | `text`, `isTruncated`, `originalLength`, `returnedLength` |
| **SourceInfo** | `get_pr_metadata` | `repository`, `url` |
| **ContextBudgetMetadata** | context window awareness (Feature 003 integration) | `totalBudgetTokens`, `safeBudgetTokens`, `source`, `estimatedTokensUsed`?, `percentageUsed`?, `warnings`? |
| **PaginationMetadataResult** | `get_pr_diff`, `get_pr_files`, `get_local_files` (Feature 004) | `currentPage` (int), `totalPages` (int), `hasMore` (bool), `currentPageReference` (string), `nextPageReference`? (string) |
| **StalenessWarningResult** | `get_pr_diff`, `get_pr_files` (Feature 004) | `message` (string), `originalFingerprint` (string), `currentFingerprint` (string) |

## 5. Enum & Constant Values

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
