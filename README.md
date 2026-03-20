# REBUSS.Pure – Pull Request Analysis & Self-Review MCP Server

REBUSS.Pure is a lightweight **MCP (Model Context Protocol) server** designed to enable intelligent code analysis by AI agents such as **GitHub Copilot**.

The server provides structured access to code changes in two distinct modes:

1. **PR review** — pull request data from **Azure DevOps**, fetched remotely without cloning
2. **Self-review** — local git changes from the developer's own working tree, without any Azure DevOps connection

---

# Why this project exists

Modern AI coding assistants often struggle with large pull requests because:

- loading an entire PR diff can overflow the model context window
- cloning repositories locally is slow and unnecessary
- AI agents need structured access to repository data instead of large text blobs

REBUSS.Pure solves this problem by exposing **small composable MCP tools** that allow an AI agent to retrieve only the information it needs.

This enables scalable pull request analysis even for large repositories, and also enables self-review of local changes **without any Azure DevOps setup at all**.

---

# Two Review Flows

## PR Review

Review changes from an Azure DevOps pull request. Requires Azure DevOps credentials.

The agent calls `get_pr_metadata` → `get_pr_files` → `get_file_diff` / `get_pr_diff` → optionally `get_file_content_at_ref`.

## Self-Review

Review local git changes from your working tree, staged index, or a branch diff — **no Azure DevOps, no PAT, no network**.

The agent calls `get_local_files` → `get_local_file_diff`.

Works with any git repository on disk. The self-review tools use the same diff infrastructure and output shape as the PR tools, so AI agents follow the same incremental review pattern.

---

# Architecture

The system follows a layered architecture:

```
GitHub Copilot
│
│ prompt file  (stdio / JSON-RPC 2.0)
▼
MCP Server (REBUSS.Pure)
│
│ Azure DevOps REST API
▼
Azure DevOps Repository
```

Instead of sending a large diff to the AI model, the MCP server exposes tools that allow the agent to retrieve data **incrementally**.

---

# Installation

## Download

Download the latest release from [GitHub Releases](https://github.com/rebuss/REBUSS.Pure/releases).

Or install automatically using the provided scripts:

**Windows (PowerShell):**
```powershell
irm https://raw.githubusercontent.com/rebuss/REBUSS.Pure/master/install.ps1 | iex
```

**Linux / macOS:**
```sh
curl -fsSL https://raw.githubusercontent.com/rebuss/REBUSS.Pure/master/install.sh | bash
```

The scripts download the `.nupkg` from the latest GitHub Release and install `REBUSS.Pure` as a global .NET tool. After installation, the tool is available as `rebuss-pure` on your PATH.

> **Prerequisite:** [.NET 8 SDK or Runtime](https://dotnet.microsoft.com/download) must be installed.

---

# Quick Start

## Recommended configuration

**The recommended authentication method is Azure CLI** — run `az login` once (or let `rebuss-pure init` do it for you), and the server will automatically acquire and refresh tokens for Azure DevOps. No PAT needed.

## 1. Initialize the MCP configuration

Navigate to your Azure DevOps repository and run:

```
REBUSS.Pure.exe init
```

or, if installed as a global .NET tool:

```
rebuss-pure init
```

This creates MCP server configuration files in the detected IDE directories:

- `.vscode/mcp.json` — for VS Code (created when a `.vscode` folder or `.code-workspace` file is detected)
- `.vs/mcp.json` — for Visual Studio (created when a `.vs` folder or `.sln` file is detected)
- both files are written when both IDEs are detected, or when neither is detected

It also copies GitHub Copilot prompt files to `.github/prompts/`:

- `review-pr.prompt.md` — GitHub Copilot prompt for PR reviews
- `self-review.prompt.md` — GitHub Copilot prompt for local self-reviews

If any of these files already exist, they are **not overwritten** — the command skips them and prints a message.

### Authentication during init

`init` uses **Azure CLI** as the authentication method. No PAT is needed.

When you run `init`:

1. MCP configuration files and prompt files are created first.
2. If Azure CLI is not installed, the command offers to install it automatically.
3. If you are already logged in via `az login`, the existing session is reused.
4. If not, a browser window opens for interactive Azure login (using `--allow-no-subscriptions` — no active Azure subscription is required).
5. On success, an Azure DevOps token is acquired and cached locally.

In most cases, authentication is fully automatic — just run `init` and log in once.

### Using a Personal Access Token (not recommended)

If you prefer not to use Azure CLI, you can pass `--pat` to `init`:

```
rebuss-pure init --pat your-pat-here
```

> ⚠️ This is **not the recommended approach**. PATs expire, must be rotated manually, and risk being accidentally committed to source control. Prefer `az login`.

When `--pat` is provided, the command skips Azure CLI login and embeds the token directly in the generated config. Make sure `.vscode/mcp.json` and `.vs/mcp.json` are listed in `.gitignore` if you use this option.

If you prefer to keep secrets out of `mcp.json` entirely, skip `--pat` and configure your PAT via `appsettings.Local.json` instead — see [Storing Secrets Locally](#storing-secrets-locally).

Generated config:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": ["--repo", "${workspaceFolder}"]
    }
  }
}
```

> **Why `--repo` in args?**
> `--repo ${workspaceFolder}` ensures the server always knows which repository to analyze, regardless of the working directory. It takes the **highest priority** and overrides all other configuration sources.


## 2. Open the repository in VS Code / Visual Studio

The MCP client will automatically detect the configuration and launch the server with the correct `--repo` argument.

## 3. Use it with GitHub Copilot

In GitHub Copilot Chat:

```
input = 123 #review-pr.prompt.md
```
or

```
#self-review.prompt.md
```
---

# Available MCP Tools

## get_pr_metadata(prNumber)

Returns high-level information about the pull request.

Used by the AI agent to determine the review strategy and understand the scope of the changes.

Example fields:

- PR title
- author
- base branch and commit SHA
- head branch and commit SHA
- number of commits
- number of changed files
- additions and deletions
- truncated PR description

This call is always the **first step** in the review workflow.

---

## get_pr_files(prNumber)

Returns a structured list of files changed in the pull request.

Each file entry includes:

- file path
- change status (add, edit, delete, rename)
- additions and deletions
- total number of changes
- file extension
- binary, generated, and test file flags
- review priority (`high` / `medium` / `low`)

The response also includes an aggregated summary by file category (source, test, config, docs, binary, generated).

This allows the AI agent to review the pull request **file-by-file** instead of loading the entire diff.

---

## get_pr_diff(prNumber)

Returns a structured JSON object with per-file hunks for all changed files in the pull request.

The response includes for each file:

- file path and change type
- skip reason (if the diff was skipped)
- additions and deletions count
- a list of hunks, each with location metadata and ordered lines
- each line includes an operation (`+`, `-`, or ` `) and the line text

Files where a diff is not meaningful (deletions, renames, binary files, generated files, and full-file rewrites) are **automatically skipped** — see [Diff Skip Behavior](#diff-skip-behavior) below. Skipped files have a `skipReason` field and an empty `hunks` list.

This tool is useful for a quick overview of all changes at once. For large pull requests, prefer `get_file_diff` to retrieve changes file-by-file.

---

## get_file_diff(prNumber, path)

Returns a structured JSON object with the diff for a specific file in the pull request.

The response includes:

- file path and change type
- skip reason (if the diff was skipped)
- additions and deletions count
- a list of hunks, each with location metadata and ordered lines
- each line includes an operation (`+`, `-`, or ` `) and the line text

If the requested file falls into a skip category (deleted, renamed, binary, generated, or full-file rewrite) the response includes a `skipReason` field and an empty `hunks` list — see [Diff Skip Behavior](#diff-skip-behavior) below.

The agent uses this to analyze code changes with minimal context cost.

This is the **default analysis method** for reviewing code changes.

---

## get_file_content_at_ref(path, ref)

Returns the full content of a file for a specific revision.

The `ref` parameter can be:

- a commit SHA
- a branch name
- a tag

This is typically used with:

- `head.sha` – the new version of the file
- `base.sha` – the previous version of the file

Full file retrieval is used **only when the diff alone is not sufficient** to understand the change.

---

## get_local_files([scope])

Lists all locally changed files in the git repository with classification metadata.

Use this as the **first step of a self-review** to discover what changed before inspecting diffs.

### Scope parameter

| Value | Behavior |
|---|---|
| *(omitted)* or `working-tree` | All uncommitted changes vs HEAD (staged + unstaged) |
| `staged` | Only staged (indexed) changes vs HEAD |
| Any branch/ref name | All commits on the current branch not yet on the base (e.g. `main`, `origin/main`) |

### Response fields

Each file entry includes:

- file path
- change status (added, modified, removed, renamed)
- file extension
- binary, generated, and test file flags
- review priority (`high` / `medium` / `low`)

The response also includes:

- `repositoryRoot` — the resolved git repository path
- `scope` — the effective scope used
- `currentBranch` — the checked-out branch name
- `totalFiles` — the total number of changed files
- an aggregated `summary` by file category (source, test, config, docs, binary, generated)

> **Tip:** No Azure DevOps credentials are needed for this tool.

---

## get_local_file_diff(path, [scope])

Returns a structured diff for a single locally changed file.

Call `get_local_files` first to discover which files changed, then call this tool for files you want to inspect in detail.

The `scope` parameter behaves identically to `get_local_files`.

The response uses the same structure as `get_file_diff` (hunks with line-level `+`/`-`/` ` operations), so AI agents can apply the same review logic as for PR diffs.

Files where a diff is not meaningful (deleted files, renamed files, full-file rewrites) are automatically skipped with a `skipReason` field — consistent with PR diff skip behavior.

> **Tip:** No Azure DevOps credentials are needed for this tool.

---

# Diff Skip Behavior

The diff provider **automatically skips** diff generation for files where a diff would be unnecessary, misleading, or wasteful.

When a diff is skipped, the file entry includes a `skipReason` field that explains why, and the `hunks` list is empty.

## Skip categories

| Category | skipReason value | Description |
|---|---|---|
| **File deletions** | `file deleted` | The file was removed entirely. No content is fetched. |
| **File renames** | `file renamed` | A pure rename. Fetching content at the new path against the base commit would produce a misleading full-file diff. |
| **Binary files** | `binary file` | Detected by file extension (`.dll`, `.png`, `.zip`, `.pdf`, `.woff2`, etc.). Diffs are meaningless for binary content. |
| **Generated files** | `generated file` | Detected by path patterns (`/obj/`, `/bin/`, `node_modules/`, `.g.cs`, `.designer.cs`, lock files, etc.). Generated output changes are noise. |
| **Full-file rewrites** | `full file rewrite` | Both file versions exist and have at least 10 lines, but the diff contains zero unchanged context lines — every line was replaced. This typically indicates a formatting rewrite, re-encoding, or tooling regeneration. |

## Skipped file format

Skipped files appear in the structured JSON output with:

```json
{
  "path": "path/to/file",
  "changeType": "delete",
  "skipReason": "file deleted",
  "additions": 0,
  "deletions": 0,
  "hunks": []
}
```

This allows consumers to see the file in the changed-files list while keeping the diff output minimal.

## When diffs are still computed

Diff generation proceeds normally for:

- file additions (`add`)
- file edits (`edit`) to non-binary, non-generated source files
- any file that does not match a skip category

---

# AI-Driven Review Workflow

## PR Review workflow

The intended workflow for AI agents reviewing an Azure DevOps pull request:

1. Retrieve pull request metadata
2. Retrieve the list of changed files
3. Review files one by one (or the full diff for small PRs)
4. Recognize and skip files whose diffs were automatically omitted
5. Retrieve full file content only when necessary
6. Produce a structured review report

## Self-Review workflow

The intended workflow for AI agents reviewing local git changes:

1. Call `get_local_files` with the desired scope to discover changed files
2. Review the file list — note classification, review priority, and the summary
3. Call `get_local_file_diff` for each file worth inspecting
4. Produce a structured review report — no Azure DevOps access required

Both workflows optimize for **incremental inspection**: start broad, then drill into details.

---

# Example Workflow

## PR Review

```
get_pr_metadata(PR)

↓ determine review strategy

get_pr_files(PR)

↓ iterate files

get_pr_diff(PR)              ← full diff (small PRs)
or
get_file_diff(PR, path)      ← per-file diff (all PR sizes)

↓ if more context required

get_file_content_at_ref(path, head.sha)
```

## Self-Review

```
get_local_files()                   ← working-tree (default)
get_local_files("staged")           ← staged changes only
get_local_files("main")             ← current branch vs main

↓ iterate files of interest

get_local_file_diff(path)           ← working-tree diff
get_local_file_diff(path, "staged") ← staged diff
get_local_file_diff(path, "main")   ← branch diff
```

---

# Design Goals

REBUSS.Pure was designed with the following goals:

- minimize AI context usage
- avoid cloning repositories locally
- provide deterministic tool outputs
- skip diff generation for files where diffs are not meaningful
- enable scalable pull request analysis
- integrate seamlessly with GitHub Copilot

---

## CLI Commands

## `init`

Generates MCP server configuration file(s) in the detected IDE directories and copies GitHub Copilot prompt files to `.github/prompts/` in the current repository root.

The command uses **Azure CLI** for authentication — no PAT is needed.

```
cd /path/to/your/azure-devops-repo
REBUSS.Pure.exe init
```

or, if installed as a global .NET tool:

```
rebuss-pure init
```

The `init` command performs the following steps:

1. **Creates MCP configuration file(s)**:
   - `.vscode/mcp.json` — for VS Code (when a `.vscode` folder or `.code-workspace` file is detected)
   - `.vs/mcp.json` — for Visual Studio (when a `.vs` folder or `.sln` file is detected)
   - both files are written when both IDEs are detected, or when neither is detected
2. **Copies prompt files to `.github/prompts/`**:
   - `review-pr.prompt.md` — structured PR code review prompt
   - `self-review.prompt.md` — structured local self-review prompt
3. **Attempts Azure CLI login** — reuses an existing session or opens a browser for interactive login (uses `--allow-no-subscriptions` so no active Azure subscription is required)

Configuration files and prompts are written **before** the Azure CLI login step, so they are always created even if the login is cancelled or fails.

If any file already exists, the command **skips it without overwriting** and prints a message.

If Azure CLI is not installed, the command offers to install it automatically.

### Using a Personal Access Token (not recommended)

If you prefer not to use Azure CLI, pass `--pat` to skip the login step and embed the token directly:

```
rebuss-pure init --pat your-pat-here
```

> ⚠️ This is **not the recommended approach**. PATs expire, must be rotated manually, and risk being accidentally committed to source control. Prefer `az login`.
> If you use `--pat`, add `.vscode/mcp.json` and `.vs/mcp.json` to `.gitignore` before committing.

---

# Configuration

REBUSS.Pure connects to Azure DevOps and supports flexible configuration — all fields are optional and can be auto-detected.

## Configuration fields

| Setting | Description | Required |
|---|---|---|
| `OrganizationName` | Azure DevOps organization name | Auto-detected from Git remote |
| `ProjectName` | Azure DevOps project name | Auto-detected from Git remote |
| `RepositoryName` | Git repository name within the project | Auto-detected from Git remote |
| `PersonalAccessToken` | PAT with read access to code and pull requests | Optional (see Authentication below) |
| `LocalRepoPath` | Local filesystem path to the Git repository | Optional (fallback for workspace detection) |

## Workspace detection

The server resolves the Git repository path using the following priority order:

1. **`--repo` argument** (highest priority) — passed by the MCP client at server startup. The `init` command configures this automatically using `${workspaceFolder}`.
2. **MCP Roots** — the client sends workspace root URIs (`file://` scheme) during the `initialize` handshake. The server selects the first root that exists locally and contains a `.git` directory.
3. **`LocalRepoPath` configuration** — if no valid MCP root is available, the server uses `AzureDevOps:LocalRepoPath` from configuration.
4. **Default detection** — falls back to the current working directory and the executable's ancestor directories.

Repository detection is **lazy** — it happens only when a tool requires it (after the MCP `initialize` handshake), not during server startup. If resolution fails, the server returns a clear error suggesting to either provide an MCP root or configure `LocalRepoPath`.

## Manual MCP client configuration

If you prefer not to use `init`, you can manually create the configuration:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": ["--repo", "${workspaceFolder}"]
    }
  }
}
```

Place this in `.vscode/mcp.json` (per-workspace) or `~/.mcp.json` (global).

Store your PAT in `appsettings.Local.json` or as an environment variable — see [Storing Secrets Locally](#storing-secrets-locally).

If you need to pass the PAT inline (e.g. in a CI environment or when no config file is available), you can add `--pat` as an argument:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": ["--repo", "${workspaceFolder}", "--pat", "your-pat-here"]
    }
  }
}
```

> ⚠️ Avoid committing `mcp.json` with a real PAT to source control.

## Command-line arguments

All configuration fields can also be provided as command-line arguments. CLI arguments take the **highest priority** and override all other configuration sources (environment variables, JSON files, cached values, and auto-detected values).

| Argument | Description | Example |
|---|---|---|
| `--repo <path>` | Local repository path (workspace root) | `--repo ${workspaceFolder}` |
| `--pat <token>` | Personal Access Token | `--pat your-pat-here` |
| `--org <name>` | Azure DevOps organization name | `--org my-organization` |
| `--project <name>` | Azure DevOps project name | `--project my-project` |
| `--repository <name>` | Azure DevOps repository name | `--repository my-repo` |

Example with all arguments:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": [
        "--repo", "${workspaceFolder}",
        "--org", "my-organization",
        "--project", "my-project",
        "--repository", "my-repo",
        "--pat", "your-pat-here"
      ]
    }
  }
}
```

## Automatic repository detection

If no configuration is provided, the server detects the Azure DevOps organization, project, and repository from the `origin` Git remote URL at the resolved workspace path.

Supported remote URL formats:

- `https://dev.azure.com/{org}/{project}/_git/{repo}`
- `https://{org}@dev.azure.com/{org}/{project}/_git/{repo}`
- `git@ssh.dev.azure.com:v3/{org}/{project}/{repo}`

Successfully detected values are cached locally for future runs.

## Authentication

> **Recommended:** Use `az login` (Azure CLI) for authentication. Run it once and the server handles token management automatically — no PAT needed. You can also run `rebuss-pure init` without `--pat` to log in during initialization.

The server uses the following authentication chain (in priority order):

1. **Personal Access Token** — if `PersonalAccessToken` is explicitly provided in configuration, it is always used (Basic auth). Choose this only if you intentionally prefer PAT-based authentication.
2. **Cached token** — if a token was previously acquired (via Azure CLI or otherwise) and is not expired, it is reused.
3. **Azure CLI** (`az account get-access-token`) — if the Azure CLI is installed and the user is logged in, the server automatically acquires a Bearer token for Azure DevOps and caches it locally.
4. **Error with instructions** — if no authentication method is available, the server returns a clear error message with instructions to run `az login` or configure a PAT.

### Token lifetime and re-authentication

| Token type | Lifetime | Re-authentication |
|---|---|---|
| Azure CLI Bearer token | ~1 hour (auto-refreshed from the Azure CLI session) | Transparent — as long as the `az login` session is valid (~90 days), the server refreshes tokens automatically |
| Azure CLI session (refresh token) | ~90 days | Run `az login` or `rebuss-pure init` again |
| Personal Access Token (PAT) | Configurable (up to 1 year) | Create a new PAT in Azure DevOps and update your configuration |

If you see authentication errors after a long period of inactivity, run `az login` or `rebuss-pure init` to re-authenticate.

## Configuration resolution priority

For each field, the first non-empty value wins:

1. Explicit configuration (appsettings, environment variables)
2. Locally cached configuration (`%LOCALAPPDATA%/REBUSS.Pure/config.json`)
3. Auto-detected from Git remote

---

## Diagnostics

The server writes detailed logs (including the full `initialize` request payload from the MCP client)
to a file at:

```
%LOCALAPPDATA%\REBUSS.Pure\server.log
```

This is useful when debugging workspace detection issues with clients such as Visual Studio Professional
that do not expose the server's stderr stream in their UI.

---

## Storing Secrets Locally

> **Never put real secrets in `appsettings.json`** — that file is committed to the repository.
> **Avoid putting real secrets in `mcp.json`** — that file may also be committed or visible in MCP client UIs.

### Option 1 — `appsettings.Local.json` (recommended)

Create a file named `appsettings.Local.json` in the `REBUSS.Pure` project directory:

```json
{
  "AzureDevOps": {
    "OrganizationName": "your-org",
    "ProjectName": "your-project",
    "RepositoryName": "your-repo",
    "PersonalAccessToken": "your-pat"
  }
}
```

This file is **already excluded from Git** via `.gitignore` (`appsettings.*.json`) and will never be committed.

The application loads it automatically and its values override anything in `appsettings.json`.

### Option 2 — Environment Variables

Set environment variables using the `AzureDevOps__` prefix (double underscore):

```
AzureDevOps__OrganizationName=your-org
AzureDevOps__ProjectName=your-project
AzureDevOps__RepositoryName=your-repo
AzureDevOps__PersonalAccessToken=your-pat
```

Environment variables take the highest priority and override both JSON files.

### Configuration priority (lowest → highest)

1. `appsettings.json` — committed, contains defaults (no secrets)
2. `appsettings.Local.json` — not committed, contains your personal secrets ✅ recommended
3. Environment variables — useful for CI/CD or container deployments
4. Command-line arguments (`--pat`, `--org`, `--project`, `--repository`) — highest priority, convenient but avoid in committed files

---

# Integration with GitHub Copilot

This repository includes two prompt files:

`.github/prompts/review-pr.prompt.md` — structured PR code review using Azure DevOps tools.

`.github/prompts/self-review.prompt.md` — structured self-review of local git changes using local tools (no Azure DevOps required).

Both prompts are automatically copied to your repository when you run `rebuss-pure init`. They instruct GitHub Copilot how to follow the incremental review workflow using the MCP tools provided by REBUSS.Pure.

---

# Example Usage

## PR review

In GitHub Copilot Chat:

```
PullRequest 123 #review-pr
```

Copilot will:
1. load the review prompt file
2. call `get_pr_metadata` → `get_pr_files` → `get_file_diff` (per file)
3. analyze the pull request incrementally
4. generate a structured code review report

## Self-review

In GitHub Copilot Chat (no arguments needed for working-tree review):

```
#self-review
```

Or with an explicit scope:

```
main #self-review
```

```
staged #self-review
```

Copilot will:
1. load the self-review prompt file
2. call `get_local_files` to discover changed files
3. call `get_local_file_diff` for each relevant file
4. generate a structured review report — entirely offline, no Azure DevOps credentials needed

---

# Project Status

This project is an experimental foundation for **AI-assisted repository intelligence tools**.

Future improvements may include:

- repository architecture analysis
- semantic code indexing
- automated PR risk scoring
- deeper integration with AI planning agents
- automated code quality metrics

---

# License

MIT License

---

# Author

**Michał Korbecki**

Application Architect & Software Engineer  
Creator of the **REBUSS developer tooling ecosystem**

- https://github.com/rebuss
