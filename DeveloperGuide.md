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

# Global mode — writes user-level config (~\.mcp.json, %APPDATA%\Code\User\mcp.json & ~\.copilot\mcp-config.json)
rebuss-pure init -g
```

**What it does:**

1. Finds the Git repository root
2. Authenticates (Azure CLI or PAT)
3. Detects IDEs and writes `mcp.json` to the appropriate directory
4. Copies prompt files to `.github/prompts/`
5. **(Optional)** Ensures GitHub CLI is installed and authenticated with the Copilot
   scope so the summarization-resilient Copilot-powered review flow can use the bundled
   Copilot CLI. This step runs regardless of SCM provider or whether `--pat` was supplied,
   and is fully optional — declining, failure, or a non-interactive session never changes
   `init`'s exit code. State is detected fresh on every run, so a previous decline does
   not suppress the prompt on the next run. When `gh` itself is missing, the first prompt
   is framed as Copilot setup and declining there skips the entire chain. To enable later
   without re-running `init`, install GitHub CLI and run: `gh auth login --web -s copilot`
   — or set `REBUSS_COPILOT_TOKEN` to a Copilot-entitled GitHub token.

### Copilot Review Layer

When the standalone Copilot CLI (bundled with REBUSS.Pure under `runtimes/<rid>/native/`,
or pointed at via `REBUSS_COPILOT_CLI_PATH`) is reachable and `gh` is authenticated with
the Copilot scope, the MCP server can perform PR reviews **server-side** by sending every
page of enriched content to GitHub Copilot in parallel and returning compact review
summaries to the IDE agent. This eliminates the "IDE conversation summarization drops
earlier findings" problem on large PRs.

**Two modes**: every `get_pr_content` response carries a mode indicator in its first block:

- `[review-mode: copilot-assisted]` — the MCP performed the review. The response contains
  `=== Page N Review ===` blocks with free-form Copilot output (and `=== Page N Review (FAILED) ===`
  blocks listing file paths when a page exhausts all 3 retry attempts). The IDE agent
  organizes findings by severity and does NOT prompt the user page-by-page.
- `[review-mode: content-only]` — the existing enriched-diff flow. Unchanged behavior.

**Configuration keys** — every key below lives under the `CopilotReview` section:

| Key | Default | Meaning |
|---|---|---|
| `Enabled` | `true` | Master switch. `false` forces content-only mode regardless of Copilot availability. |
| `ReviewBudgetTokens` | `128000` | Per-call Copilot context budget. Used to re-paginate the enrichment result into Copilot-sized pages. |
| `Model` | `"claude-sonnet-4.6"` | Copilot model passed to `SessionConfig.Model`. If the SDK rejects this string, check `client.ListModelsAsync()` output. |
| `MaxConcurrentPages` | `6` | Upper bound on how many pages the orchestrator dispatches to Copilot in parallel per batch. Values `< 1` are clamped to `1`. Raise cautiously — the Copilot backend silently re-queues fan-outs above its per-client limit, which can double wall-clock time. |
| `MinRequestIntervalSeconds` | `3` | Minimum spacing between successive outbound Copilot SDK calls (`CreateSessionAsync` / `SendAsync`), enforced by a process-wide gate. Combines with `MaxConcurrentPages` to shape throughput: the batch size controls fan-out width, this interval controls request rate. Set to `0` to disable (tests only). |
| `CopilotCliPath` | _(unset)_ | Absolute path to a standalone Copilot CLI binary (the `@github/copilot` npm package's `copilot.exe` / `copilot`, **not** the `gh copilot` extension). When set, forwarded to `CopilotClientOptions.CliPath` so the SDK spawns this binary instead of searching for the bundled one under its NuGet `runtimes/` folder. Use this when the SDK package is missing its native payload for your OS/architecture and the server logs `Copilot CLI not found at '…\runtimes\<rid>\native\copilot.exe'`. Environment override: `REBUSS_COPILOT_CLI_PATH` (takes precedence when non-blank). |

**How to set them — `mcp.json` (recommended)**

When installed as a `dotnet tool`, REBUSS.Pure runs from the tool shim directory and the AI-chat host launches it from `mcp.json`. The cleanest way to pass config is the `env` block of the same `mcp.json` that `rebuss-pure init` wrote — typically one of:

- `.vscode/mcp.json` (VS Code, repo-local)
- `.vs/mcp.json` (Visual Studio, repo-local)
- `~/.copilot/mcp-config.json` (Copilot CLI)
- `~/.mcp.json` or `%APPDATA%\Code\User\mcp.json` (global mode — `rebuss-pure init -g`)

REBUSS.Pure reads environment variables using .NET's standard `__` (double-underscore) → `:` mapping, so `CopilotReview__MaxConcurrentPages` binds to `CopilotReview:MaxConcurrentPages`:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "rebuss-pure",
      "args": ["--repo", "C:\\path\\to\\repo"],
      "env": {
        "CopilotReview__MaxConcurrentPages": "4",
        "CopilotReview__MinRequestIntervalSeconds": "2"
      }
    }
  }
}
```

Reload the IDE (or restart the chat session) after editing `mcp.json` — the MCP client spawns a fresh server process and the new `env` takes effect.

**Alternative — `appsettings.json` (advanced)**

`appsettings.json` is loaded from the tool's install directory (`AppContext.BaseDirectory`), not from the repo. For a `dotnet tool install -g CodeReview.MCP` the file lives at:

- **Windows:** `%USERPROFILE%\.dotnet\tools\.store\codereview.mcp\<VERSION>\codereview.mcp\<VERSION>\tools\net10.0\any\appsettings.json`
- **Linux/macOS:** `~/.dotnet/tools/.store/codereview.mcp/<VERSION>/codereview.mcp/<VERSION>/tools/net10.0/any/appsettings.json`

This path is **version-scoped** — every `dotnet tool update -g CodeReview.MCP` installs a new version alongside the old one and your edits do not carry forward. Prefer `mcp.json` `env` unless you are running a repo-local dev build where `appsettings.json` sits next to the binary. Either way the schema is the same:

```json
"CopilotReview": {
  "Enabled": true,
  "ReviewBudgetTokens": 128000,
  "Model": "claude-sonnet-4.6",
  "MaxConcurrentPages": 6,
  "MinRequestIntervalSeconds": 3
}
```

**Tuning throughput**: `MaxConcurrentPages` and `MinRequestIntervalSeconds` are the two knobs
that shape how fast the orchestrator drains a multi-page review against the Copilot rate
limit. Values are re-read from config on every SDK call (hot-reload from `appsettings.json`
works without a server restart; changes to `mcp.json` `env` require reloading the IDE so the
child process is respawned). Lowering the interval (e.g. `1.5`) speeds up small PRs but
risks transient 429s on larger ones; raising concurrency past the default `6` rarely helps
and often hurts, because the backend re-queues the excess silently.

**Retry**: each page review is attempted up to **3 times** before giving up. Retries fire
immediately (no backoff). On exhaustion, the response still succeeds and carries a
`=== Page N Review (FAILED) ===` block listing the source files that were on the failed
page, plus the last-attempt reason. (Clarification Q1.)

**Idempotency**: the copilot review cache is keyed on **PR number only** (Clarification Q2).
Changing `ReviewBudgetTokens` mid-session does NOT invalidate an already-cached PR review;
restart the server to force a re-run under a new budget. Within one server session,
triggering a review of the same PR twice consumes zero additional Copilot calls.

**Privacy (Principle VIII)**: enriched PR content is relayed only to GitHub Copilot via the
user's own authenticated `gh` session. No intermediary. No telemetry. The operator explicitly
opts into the feature via `CopilotReview.Enabled` plus the feature-012 onboarding.

**IDE detection logic (local mode):**

| Markers found | Config written to |
|---|---|
| `.vscode/` or `*.code-workspace` only | `.vscode/mcp.json` |
| `.vs/` or `*.sln` only | `.vs/mcp.json` |
| Multiple IDEs detected | All detected locations |
| No markers found | `.vscode/mcp.json` + `.vs/mcp.json` |

**Global mode (`-g` / `--global`):**

When the `-g` flag is used, the MCP configuration is written to the user-level directories
(`~/.mcp.json` for Visual Studio, `%APPDATA%\Code\User\mcp.json` for VS Code on Windows / `~/.config/Code/User/mcp.json` on Linux/macOS, `~/.copilot/mcp-config.json` for Copilot CLI) instead of the repository-local directories.
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

> **Note:** Local self-review (`get_local_content`) works without any authentication.

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
    "PersonalAccessToken": "",
    "Diff": {
      "ZipFallbackThreshold": 30
    }
  }
}
```

The optional `Diff:ZipFallbackThreshold` controls how the diff provider fetches per-file
content. For PRs with **N ≤ threshold** changed files the provider issues 2N `items`
requests (one per side). Above the threshold it downloads the base + target repository
ZIPs once each and reads file contents from disk, bounding API request count to two
regardless of file count — this avoids Azure DevOps' per-window TSTU rate limits on
large refactor PRs.

- **Default `30`** — keeps small PRs on the cheap per-file path while protecting large
  PRs from throttling.
- **`0`** — disables the ZIP path entirely (always per-file, regardless of count).
- **Higher value** — appropriate when running against a private Azure DevOps Server
  with no practical rate limits, or when bandwidth is constrained.

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
| `get_pr_content(prNumber, [pageNumber], [modelName], [maxTokens])` | Returns the full Copilot-assisted review for the PR in a single call. `pageNumber` is accepted for backward compatibility but ignored — all pages are returned together. Call `get_pr_metadata` with budget params first if you want a breakdown of the planned page count |

### Local Self-Review Tools (no authentication needed)

| Tool | Description |
|---|---|
| `get_local_content([pageNumber], [scope], [modelName], [maxTokens])` | Returns the full Copilot-assisted review for local changes in a single call. Page allocation is computed internally. `pageNumber` is accepted for backward compatibility but ignored — all pages are returned together. No separate metadata call needed |

**Scopes for local tools:**

| Scope | Description |
|---|---|
| `working-tree` (default) | All uncommitted changes (staged + unstaged) vs HEAD |
| `staged` | Only staged (indexed) changes vs HEAD |
| `<branch-name>` | All commits on current branch not yet merged into `<branch-name>` |

---

## Review Workflows

### PR Review (recommended)

```
get_pr_metadata(prNumber, modelName)   ← optional: reports the planned page count via contentPaging
get_pr_content(prNumber, modelName)    ← returns the full Copilot-assisted review in one call
```

### Self-Review (recommended)

```
get_local_content(scope, modelName)    ← computes pages internally and returns the full review in one call
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

### "COPILOT SESSION NOT AUTHENTICATED" banner after `rebuss-pure init`

Copilot requires an OAuth session that carries the `copilot` scope — the
scope that standard `gh auth login` does **not** grant by default. The
`init` command always requests it (`gh auth login --web -s copilot`) and
will self-heal a pre-existing `gh` session by running
`gh auth refresh -h github.com -s copilot` before giving up. If you still
see the banner:

1. Re-run the refresh manually (opens a browser for consent):

   ```bash
   gh auth refresh -h github.com -s copilot
   ```

2. Verify the scope landed:

   ```bash
   gh auth status
   ```

   The `Token scopes:` line must include `'copilot'`.

3. Confirm your GitHub account has an active Copilot subscription
   (<https://github.com/settings/copilot>) — a session without entitlement
   will still fail verification even with the scope granted.

4. For CI / headless environments, export a pre-minted Copilot-entitled
   OAuth token and skip the gh session altogether:

   ```bash
   export REBUSS_COPILOT_TOKEN=<token>      # Linux/macOS
   set REBUSS_COPILOT_TOKEN=<token>         # Windows
   ```

   Classic personal access tokens are **not** valid here — they don't
   carry the `copilot` scope regardless of their permissions.

### "Copilot review layer unavailable (StartFailure)" / "Copilot CLI not found at …\runtimes\<rid>\native\copilot(.exe)"

The Copilot SDK spawns a standalone `copilot` CLI binary (from the `@github/copilot` npm package — **not** the `gh copilot` extension) that ships under the tool's `runtimes/<rid>/native/` folder.

REBUSS.Pure bundles this CLI for **win-x64, linux-x64, and osx-arm64** out of the box — if you are on one of those platforms and still see this error, the installed tool is likely an old version published before the bundling was added; run `dotnet tool update -g CodeReview.MCP` and retry.

For **other RIDs (win-arm64, linux-arm64, osx-x64)** the CLI is not bundled because each binary is ~130 MB and including all six RIDs would push the nupkg over NuGet.org's size limit. Use the workaround below.

**Workaround / manual CLI override** — point the SDK at a system-installed Copilot CLI:

1. Install the standalone Copilot CLI (`npm install -g @github/copilot`) or reuse one you already have.
2. Note its absolute path (`where copilot.exe` on Windows / `which copilot` on Linux/macOS).
3. Set **one** of:
   - `CopilotReview:CopilotCliPath` in `appsettings.json` / `mcp.json` `env` (as `CopilotReview__CopilotCliPath`)
   - The `REBUSS_COPILOT_CLI_PATH` environment variable (takes precedence over the config value)

   to that absolute path.
4. Restart the MCP server (reload the IDE).

The error message includes this remediation inline; check server logs for the actual failure reason before assuming it is an auth problem.

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
