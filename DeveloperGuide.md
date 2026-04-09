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

# Global mode — writes user-level config (~\.mcp.json & %APPDATA%\Code\User\mcp.json)
rebuss-pure init -g
```

**What it does:**

1. Finds the Git repository root
2. Authenticates (Azure CLI or PAT)
3. Detects IDEs and writes `mcp.json` to the appropriate directory
4. Copies prompt files to `.github/prompts/`

**IDE detection logic (local mode):**

| Markers found | Config written to |
|---|---|
| `.vscode/` or `*.code-workspace` only | `.vscode/mcp.json` |
| `.vs/` or `*.sln` only | `.vs/mcp.json` |
| Multiple IDEs detected | All detected locations |
| No markers found | `.vscode/mcp.json` + `.vs/mcp.json` |

**Global mode (`-g` / `--global`):**

When the `-g` flag is used, the MCP configuration is written to the user-level directories
(`~/.mcp.json` for Visual Studio, `%APPDATA%\Code\User\mcp.json` for VS Code on Windows / `~/.config/Code/User/mcp.json` on Linux/macOS) instead of the repository-local directories.
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

The `ContextWindow` section in `appsettings.json` includes a `GatewayMaxTokens` setting — a hard cap on the resolved token budget imposed **before** the safety margin is applied. This accounts for API gateways (e.g. GitHub Copilot proxy) that enforce a context window limit lower than the model's native capacity.

**Default: `128000`** — matches GitHub Copilot's proxy limit.

| Platform | Recommended `GatewayMaxTokens` |
|---|---|
| GitHub Copilot (VS Code / Visual Studio) | `128000` (default) |
| Cursor | `128000` (verify with your setup) |
| Direct API access | `null` (disabled) |

To disable the gateway cap, add to `appsettings.Local.json`:

```json
{
  "ContextWindow": {
    "GatewayMaxTokens": null
  }
}
```

Or via environment variable: `ContextWindow__GatewayMaxTokens=0`

### Workflow timeouts (Progressive PR Metadata)

For large PRs the diff fetch + enrichment pipeline can exceed the host's hard ~30 s tool-call ceiling. The **Progressive PR Metadata** workflow handles this:

1. `get_pr_metadata` enforces an internal 28 s timeout. On timeout it returns the basic-summary response with an explicit "Content paging: not yet available" indicator instead of failing — the host never sees a tool-call timeout.
2. Background enrichment continues to run in the singleton `PrEnrichmentOrchestrator` even after the metadata response has returned.
3. A follow-up `get_pr_content` call gets its own fresh 28 s budget and serves the result from the orchestrator's cache. The effective end-to-end processing budget for one review is therefore >60 s without any host-visible timeout.
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

#### Primary tools — pagination-aware (recommended for all PR sizes)

| Tool | Description |
|---|---|
| `get_pr_metadata(prNumber, [modelName], [maxTokens])` | Returns PR metadata. Pass `modelName` or `maxTokens` to also receive `contentPaging` — total page count and per-page file breakdown for use with `get_pr_content` |
| `get_pr_content(prNumber, pageNumber, [modelName], [maxTokens])` | Returns diff content for a specific page of the PR. Call `get_pr_metadata` with budget params first to discover the total page count |
| `get_pr_files(prNumber, [pageReference])` | Returns classified list of changed files with per-file stats and review priority; supports pagination via `pageReference` |

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

### PR Review — paginated (recommended)

```
get_pr_metadata(prNumber, modelName)          ← discovers total pages via contentPaging
  → loop: get_pr_content(prNumber, page, modelName)  ← one page at a time until hasMorePages = false
```

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
```

> **Note for contributors:**

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
