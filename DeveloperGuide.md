# 📖 Technical Reference

## CLI Commands

### `rebuss-pure init`

Initializes MCP configuration in the current Git repository.

```bash
# Default — auto-detects provider from Git remote
rebuss-pure init

# With a Personal Access Token
rebuss-pure init --pat <your-pat>

# Explicit provider selection
rebuss-pure init --provider github
rebuss-pure init --provider azuredevops

# Force specific IDE target (skips auto-detection)
rebuss-pure init --ide vscode
rebuss-pure init --ide vs
rebuss-pure init --ide claude

# Global mode — writes user-level config (~\.mcp.json & %APPDATA%\Code\User\mcp.json)
rebuss-pure init -g
```

**What it does:**

1. Finds the Git repository root
2. Authenticates (Azure CLI or PAT)
3. Detects IDEs and writes `mcp.json` to the appropriate directory
4. Copies prompt files to `.github/prompts/` (and to `.claude/commands/` as `<name>.md` slash commands when Claude Code is detected — e.g. `review-pr.prompt.md` becomes `/review-pr`)

**IDE detection logic (local mode):**

| Markers found | Config written to |
|---|---|
| `.vscode/` or `*.code-workspace` only | `.vscode/mcp.json` |
| `.vs/` or `*.sln` only | `.vs/mcp.json` |
| `.claude/` or `CLAUDE.md` only | `.mcp.json` at the repo root (uses `mcpServers` key) |
| Multiple IDEs detected | All detected locations |
| No markers found | `.vscode/mcp.json` + `.vs/mcp.json` |

> **Note:** Claude Code uses `mcpServers` as the top-level key (not `servers`). The `init` command handles this automatically.

**Global mode (`-g` / `--global`):**

When the `-g` flag is used, the MCP configuration is written to the user-level directories
(`~/.mcp.json` for Visual Studio, `%APPDATA%\Code\User\mcp.json` for VS Code on Windows / `~/.config/Code/User/mcp.json` on Linux/macOS, and `~/.claude.json` for Claude Code — merged into the existing `mcpServers` key, preserving all other top-level settings) instead of the repository-local directories.
The `--repo` argument in the config points to the current repository's git root.

This is useful when Visual Studio does not detect the local `.vs/mcp.json` file.
If you work with multiple repositories, run `rebuss-pure init -g` in the target repository
before switching to it to update the global configuration.

---

### Server mode (launched automatically by MCP client)

The MCP client starts the server via the generated `mcp.json`. You can also start it manually:

```bash
rebuss-pure --repo /path/to/repo [--pat <token>] [--org <org>] [--project <project>] [--repository <repo-name>]
```

| Argument | Description |
|---|---|
| `--repo` | Path to the local Git repository |
| `--pat` | Personal Access Token (Azure DevOps or GitHub) |
| `--provider` | SCM provider: `github` or `azuredevops` (auto-detected from Git remote if omitted) |
| `--org` | Azure DevOps organization name (auto-detected from Git remote if omitted) |
| `--project` | Azure DevOps project name (auto-detected if omitted) |
| `--repository` | Azure DevOps repository name (auto-detected if omitted) |
| `--owner` | GitHub owner/organization (auto-detected from Git remote if omitted) |

---

## Authentication

REBUSS.Pure supports two SCM providers. The active provider is auto-detected from the Git remote URL. Each provider uses its own authentication chain — it tries each method in order and uses the first that succeeds.

### Azure DevOps Authentication

#### 1. Personal Access Token (PAT) — explicit config (highest priority)

Provide via CLI:

```bash
rebuss-pure init --pat <your-pat>
```

Or create `appsettings.Local.json` next to the server executable:

```json
{
  "AzureDevOps": {
    "PersonalAccessToken": "<your-pat-here>"
  }
}
```

**How to create an Azure DevOps PAT:**

1. Go to `https://dev.azure.com/<your-org>/_usersSettings/tokens`
2. Click **+ New Token**
3. Select scope: **Code (Read)**
4. Copy the token

#### 2. Cached token (automatic)

Tokens acquired via Azure CLI are cached locally at:

```
%LOCALAPPDATA%\REBUSS.Pure\config.json     (Windows)
~/.local/share/REBUSS.Pure/config.json      (Linux/macOS)
```

Bearer tokens are refreshed automatically when expired.

#### 3. Azure CLI (recommended for interactive use)

If no PAT is configured and no valid cached token exists, the server acquires a token via:

```bash
az account get-access-token --resource 499b84ac-1321-427f-aa17-267ca6975798
```

**If Azure CLI is not installed:**

- During `rebuss-pure init`, the tool offers to install it automatically
- Manual install: [https://aka.ms/install-azure-cli](https://aka.ms/install-azure-cli)

#### 4. Error (no auth available)

If none of the above methods work, the server returns a clear error message instructing you to run `az login` or configure a PAT.

---

### GitHub Authentication

#### 1. Personal Access Token (PAT) — explicit config (highest priority)

Provide via CLI:

```bash
rebuss-pure init --provider github --pat <your-pat>
```

Or create `appsettings.Local.json` next to the server executable:

```json
{
  "GitHub": {
    "PersonalAccessToken": "<your-github-pat-here>"
  }
}
```

**How to create a GitHub PAT:**

1. Go to `https://github.com/settings/tokens`
2. Click **Generate new token**
3. Select scope: **repo** (read access)
4. Copy the token

#### 2. Cached token (automatic)

Tokens acquired via GitHub CLI are cached locally at:

```
%LOCALAPPDATA%\REBUSS.Pure\github-config.json     (Windows)
~/.local/share/REBUSS.Pure/github-config.json      (Linux/macOS)
```

Tokens are refreshed automatically when expired (default lifetime: 24 hours).

#### 3. GitHub CLI (recommended for interactive use)

If no PAT is configured and no valid cached token exists, the server acquires a token via:

```bash
gh auth token
```

**If GitHub CLI is not installed:**

- During `rebuss-pure init`, the tool offers to install it automatically
- Manual install: [https://cli.github.com](https://cli.github.com)

#### 4. Error (no auth available)

If none of the above methods work, the server returns a clear error message instructing you to run `gh auth login` or configure a PAT.

> **Note:** Local self-review (`get_local_files`, `get_local_content`) works without any authentication.

---

## Configuration

### `appsettings.json`

Located next to the server executable. All fields are optional — auto-detected from Git remote when not specified.

**Azure DevOps:**

```json
{
  "AzureDevOps": {
    "OrganizationName": "",
    "ProjectName": "",
    "RepositoryName": "",
    "PersonalAccessToken": ""
  }
}
```

**GitHub:**

```json
{
  "GitHub": {
    "Owner": "",
    "RepositoryName": "",
    "PersonalAccessToken": ""
  }
}
```

### `appsettings.Local.json`

Same structure as above. Overrides `appsettings.json`. Excluded from Git via `.gitignore`. Use this for secrets like PATs.

### Environment variables

All settings can be overridden via environment variables.

**Azure DevOps:**

```
AzureDevOps__OrganizationName=myorg
AzureDevOps__ProjectName=myproject
AzureDevOps__RepositoryName=myrepo
AzureDevOps__PersonalAccessToken=mytoken
```

**GitHub:**

```
GitHub__Owner=myowner
GitHub__RepositoryName=myrepo
GitHub__PersonalAccessToken=mytoken
```

### Auto-detection

When provider-specific fields are not configured, the server automatically detects them from the `origin` Git remote URL. Both HTTPS and SSH remote formats are supported for Azure DevOps and GitHub repositories.

### Context Window — Gateway Cap

`ContextWindow:GatewayMaxTokens` is the hard cap on the resolved per-request token budget imposed **before** the safety margin is applied. It exists because most MCP hosts enforce a per-tool-result token ceiling that is much smaller than the model's native context window — exceeding it causes the host to reject the response.

**Zero-config behavior:** the key is **not set** in `appsettings.json` by default. At runtime, REBUSS.Pure inspects the MCP `initialize` handshake's `clientInfo.Name` and picks a safe value automatically:

| Detected `clientInfo.Name` | Auto cap |
|---|---|
| `claude-code` / `claude-ai` / `claude.ai` | **25 000** |
| `cursor` (any variant) | **24 000** |
| `codex` (any variant) | **20 000** |
| anything else / unknown / missing | **20 000** (safe fallback) |

**Precedence (first match wins):**
1. Explicit per-call `maxTokens` parameter — bypasses the gateway cap entirely.
2. `ContextWindow:GatewayMaxTokens` from configuration (when set, autodetect is skipped).
3. Autodetected value from `clientInfo.Name`.
4. Model registry / default budget.

To override the autodetected value (for example to give a custom gateway a larger budget), add to `appsettings.Local.json`:

```json
{
  "ContextWindow": {
    "GatewayMaxTokens": 50000
  }
}
```

Or via environment variable: `ContextWindow__GatewayMaxTokens=50000`. To disable the cap entirely set the value to `0` or `null`.

> **Behavior change:** prior to this version, the gateway cap clamped *all* budget sources including explicit `maxTokens` per call. Explicit per-call values now bypass the cap by design — callers passing `maxTokens` are treated as authoritative.

### Workflow timeouts (Progressive PR Metadata)

For large PRs the diff fetch + enrichment pipeline can exceed the host's hard ~30 s tool-call ceiling. The **Progressive PR Metadata** workflow handles this:

1. `get_pr_metadata` enforces an internal 28 s timeout. On timeout it returns the basic-summary response with an explicit "Content paging: not yet available" indicator instead of failing — the host never sees a tool-call timeout.
2. Background enrichment continues to run in the singleton `PrEnrichmentOrchestrator` even after the metadata response has returned.
3. A follow-up `begin_pr_review` call gets its own fresh 28 s budget and serves the result from the orchestrator's cache. The effective end-to-end processing budget for one review is therefore >60 s without any host-visible timeout.
4. If even the content call cannot complete in time — or the background job has failed — both handlers return a friendly plain-text status block via `PlainTextFormatter.FormatFriendlyStatus(...)`. The MCP tool response is always a successful payload.

Configuration in `appsettings.json`:

```json
{
  "Workflow": {
    "MetadataInternalTimeoutMs": 28000,
    "ContentInternalTimeoutMs": 28000
  }
}
```

Or via environment variables: `Workflow__MetadataInternalTimeoutMs`, `Workflow__ContentInternalTimeoutMs`. Both must be strictly less than the host's hard tool-call ceiling so the response has time to serialize. Default 28 000 ms leaves a 2 s margin under the typical 30 s ceiling.

The orchestrator's load-bearing semantic — caller cancellation never cancels the background body — is asserted by `PrEnrichmentOrchestratorTests.CallerCancellation_DoesNotCancelBackgroundBody`. Do not regress it.

---

## MCP Tools Reference

### PR Review Tools (require SCM provider authentication)

#### Primary tools

| Tool | Description |
|---|---|
| `get_pr_metadata(prNumber, [modelName], [maxTokens])` | Returns PR metadata. Pass `modelName` or `maxTokens` to also receive `contentPaging` info — useful for sizing decisions before starting a review session via `begin_pr_review` |
| `get_pr_files(prNumber, [pageReference])` | Returns classified list of changed files with per-file stats and review priority; supports pagination via `pageReference` |

#### Stateful PR Review Session (feature 012 — recommended for very large PRs)

For PRs that exceed your agent's context window (especially small-context agents
like Copilot, GPT-4o-mini, Haiku) the **review session** lets you walk the PR one
file at a time with server-side acknowledgment tracking. Every file is reviewed;
nothing is silently skipped.

| Tool | Description |
|---|---|
| `begin_pr_review(prNumber, [modelName], [maxTokens])` | Starts a stateful review session. Returns a session id and an alphabetically-sorted manifest of every file in the PR. |
| `next_review_item(sessionId)` | Returns the next file (or next chunk of an oversize file). Refuses to advance past a fully-delivered file until you call `record_review_observation`. |
| `record_review_observation(sessionId, filePath, observations, status)` | Records an observation. `status` is `reviewed_complete` or `skipped_with_reason`. Append-only — multiple observations on the same file are preserved. |
| `submit_pr_review(sessionId, reviewText, [force])` | Finalizes the review. Rejects with a structured error listing unacknowledged files unless `force=true` (which is recorded in the audit trail). |
| `refetch_review_item(sessionId, filePath, [chunkIndex])` | (feature 013) Re-reads any acknowledged file's exact original content. Pure read; never re-runs enrichment. Use mid-review to verify a cross-reference or recall a forgotten detail. Honors the acknowledgment gate — rejects files in `Pending` or partial-delivery state. |
| `query_review_notes(sessionId, query, [limit])` | (feature 013) Free-text searches your own previously-recorded observations across the session. Use before `submit_pr_review` to compose a final review from accumulated observations. Default limit 5, max 20. |

**Scan-only file classification (feature 014):** Mechanical files (auto-generated DTOs, EF migrations, designer files, snapshot fixtures, lockfiles) are classified as "scan-only" at session-begin time and delivered by `next_review_item` as a small ~1 KB synthetic summary instead of the full enriched diff. The agent still must `record_review_observation` to acknowledge them — there is NO auto-acknowledgment, NO bulk-skip. For typical mechanical-heavy PRs this gives roughly a 5× working-memory saving over reviewing every file in full. The default 15-pattern list ships in `appsettings.json` under `Workflow:ReviewSession:ScanOnlyPatterns`; override per environment in `appsettings.Local.json` (the same key, full list replaces). When multiple patterns match a file, the **first pattern in configuration order** is the one recorded as the reason in the manifest and summary. Pattern matching is **case-insensitive on all hosts** for cross-OS predictability. If the agent suspects a scan-classified file actually warrants inspection, it can call `refetch_review_item` to retrieve the full enriched content (the summary is computed on demand and never stored, so refetch always returns the underlying full content for any file regardless of classification).

**Key properties:**
- Server-enforced acknowledgment gate — no silent abandonment, cherry-picking, or lost-in-the-middle.
- Every response fits under the configured transport ceiling (default 20k tokens). Oversize single files are split into ordered chunks.
- Sessions live only in process memory. A server restart discards all in-flight sessions; call `begin_pr_review` again.
- See `specs/012-review-session-mvp/` for the full spec, plan, and acceptance criteria.

**Workflow:**

```
begin_pr_review(prNumber)              ← returns sessionId + manifest
  → loop:
      next_review_item(sessionId)      ← get next file or chunk
      record_review_observation(sessionId, filePath, observations, "reviewed_complete")
  → submit_pr_review(sessionId, reviewText)   ← gets audit trail
```

### Local Self-Review Tools (no authentication needed)

| Tool | Description |
|---|---|
| `get_local_content(pageNumber, [scope], [modelName], [maxTokens])` | Returns diff content for a specific page of local uncommitted changes. Page allocation is computed internally — no separate metadata call needed |
| `get_local_files([scope], [pageReference])` | Lists locally changed files with classification metadata; supports pagination via `pageReference` |

**Scopes for local tools:**

| Scope | Description |
|---|---|
| `working-tree` (default) | All uncommitted changes (staged + unstaged) vs HEAD |
| `staged` | Only staged (indexed) changes vs HEAD |
| `<branch-name>` | All commits on current branch not yet merged into `<branch-name>` |

---

## Review Workflows

### PR Review — stateful session (canonical)

```
begin_pr_review(prNumber)              ← returns sessionId + manifest
  → loop:
      next_review_item(sessionId)      ← get next file (deep) or scan summary
      record_review_observation(sessionId, filePath, observations, "reviewed_complete")
  → submit_pr_review(sessionId, reviewText)   ← gates on full acknowledgment + returns audit trail
```

Optionally interleave `refetch_review_item(sessionId, filePath)` to re-read a previously delivered file and `query_review_notes(sessionId, query)` to search past observations. Both are pure reads.

### Self-Review — paginated (recommended)

```
get_local_content(page, scope, modelName)     ← computes pages internally; loop until hasMorePages = false
```

---

## Prompts

After running `rebuss-pure init`, you get:

```
.github/prompts/
├── review-pr.prompt.md
└── self-review.prompt.md

# If Claude Code is detected (.claude/ or CLAUDE.md):
.mcp.json                  ← at repo root, uses "mcpServers" key
.claude/commands/
├── review-pr.md           ← invocable as /review-pr
└── self-review.md         ← invocable as /self-review
```

> **Note for contributors:** The files in `.github/prompts/` are **generated** by `rebuss-pure init` from embedded resources compiled into the tool (`REBUSS.Pure/Cli/Prompts/*.md`). The embedded files in `REBUSS.Pure/Cli/Prompts/` are the **source of truth**. Always edit the embedded source files — do **not** edit the deployed files directly, as `init` **always overwrites** them on every run to ensure prompt updates are deployed. `init` does **not** create or modify `.github/instructions/` — any instruction files there are owned by you.

These prompts instruct the AI agent on the review workflows. If you need to add custom rules for your repository, create your own files under `.github/instructions/` (e.g. `team-rules.instructions.md`); `init` will leave them alone.

---

## 🧪 Running Tests

### Unit tests

```bash
dotnet test REBUSS.Pure.Tests
dotnet test REBUSS.Pure.Core.Tests
dotnet test REBUSS.Pure.AzureDevOps.Tests
dotnet test REBUSS.Pure.GitHub.Tests
```

### Smoke tests

Smoke tests exercise the compiled binary as a child process — covering the `init` command (GitHub & Azure DevOps), MCP protocol tools over stdio, and a full pack → install → handshake flow.

```bash
dotnet test REBUSS.Pure.SmokeTests
```

### Contract tests

Live contract tests run the compiled binary against **real Azure DevOps and GitHub APIs** using dedicated fixture PRs that are never merged. They validate the full stack: CLI arg parsing → DI → provider → API → response structure.

**Protocol tests** (no credentials needed):

```bash
dotnet test REBUSS.Pure.SmokeTests --filter "Category=Protocol"
```

**Azure DevOps contract tests** (requires env vars):

```bash
REBUSS_ADO_PAT=<pat> REBUSS_ADO_ORG=<org> REBUSS_ADO_PROJECT=<project> \
REBUSS_ADO_REPO=<repo> REBUSS_ADO_PR_NUMBER=<pr> \
dotnet test REBUSS.Pure.SmokeTests --filter "Category=ContractAdo"
```

**GitHub contract tests** (requires env vars):

```bash
REBUSS_GH_PAT=<pat> REBUSS_GH_OWNER=<owner> \
REBUSS_GH_REPO=<repo> REBUSS_GH_PR_NUMBER=<pr> \
dotnet test REBUSS.Pure.SmokeTests --filter "Category=ContractGitHub"
```

When credentials are not configured, contract tests are **automatically skipped**.

---

## Logging

Server logs are written to daily-rotated files:

```
%LOCALAPPDATA%\REBUSS.Pure\server-yyyy-MM-dd.log   (Windows)
~/.local/share/REBUSS.Pure/server-yyyy-MM-dd.log    (Linux/macOS)
```

Logs older than 3 days are automatically cleaned up.

---

## Troubleshooting

### "AUTHENTICATION REQUIRED" error (Azure DevOps)

Run `az login` and restart your IDE, or configure a PAT in `appsettings.Local.json`.

### "AUTHENTICATION REQUIRED" error (GitHub)

Run `gh auth login` and restart your IDE, or configure a PAT in `appsettings.Local.json`:

```json
{
  "GitHub": {
    "PersonalAccessToken": "<your-github-pat>"
  }
}
```

### MCP tools not available in AI chat

1. Ensure `rebuss-pure init` completed successfully
2. Check that `.vscode/mcp.json` or `.vs/mcp.json` exists
3. Restart your IDE or reload the MCP client

### Azure DevOps organization/project not detected

If your Git remote uses a non-standard format, specify explicitly:

```bash
rebuss-pure --repo . --org myorg --project myproject --repository myrepo
```

Or configure in `appsettings.Local.json`:

```json
{
  "AzureDevOps": {
    "OrganizationName": "myorg",
    "ProjectName": "myproject",
    "RepositoryName": "myrepo"
  }
}
```

### GitHub owner/repository not detected

If your Git remote uses a non-standard format, specify explicitly:

```bash
rebuss-pure --repo . --provider github --owner myowner --repository myrepo
```

Or configure in `appsettings.Local.json`:

```json
{
  "GitHub": {
    "Owner": "myowner",
    "RepositoryName": "myrepo"
  }
}
```

### Token expired / 203 HTML redirect (Azure DevOps)

The server automatically invalidates stale tokens and retries via Azure CLI. If the issue persists, re-authenticate:

```bash
az login
```

### Token expired (GitHub)

The server automatically invalidates stale tokens and retries via GitHub CLI. If the issue persists, re-authenticate:

```bash
gh auth login
```

---

## Known Limitations

### Azure DevOps — zero line counts affect pagination quality

The Azure DevOps iteration-changes API does not return per-file line counts.
`Additions`, `Deletions`, and `Changes` are always **zero** for files fetched
through `AzureDevOpsFilesProvider`. The pagination system compensates with a
flat fallback estimate (`PaginationConstants.FallbackEstimateWhenLinecountsUnknown = 300` tokens per file), but this means every file — whether a one-line config
tweak or a 500-line rewrite — receives the same budget estimate. Consequently,
page sizes can be uneven: pages dominated by small files will finish under
budget while pages with large files may exceed expectations.

This does **not** affect GitHub-backed reviews, where the API provides accurate
per-file line counts.

---

## 📄 License

MIT

---

## 👤 Author

**Michał Korbecki**  
Creator of REBUSS ecosystem  
[https://github.com/rebuss/CodeReview.MCP](https://github.com/rebuss/CodeReview.MCP)
