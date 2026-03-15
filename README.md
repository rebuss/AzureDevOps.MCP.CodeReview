# REBUSS.Pure – Pull Request Analysis MCP Server

REBUSS.Pure is a lightweight **MCP (Model Context Protocol) server** designed to enable intelligent pull request analysis by AI agents such as **GitHub Copilot**.

The server provides structured access to pull request data from **Azure DevOps** while minimizing context usage and avoiding the need to clone repositories locally.

Its primary goal is to support **efficient AI-assisted code reviews** by exposing small, well-defined tools that allow agents to analyze pull requests incrementally.

---

# Why this project exists

Modern AI coding assistants often struggle with large pull requests because:

- loading an entire PR diff can overflow the model context window
- cloning repositories locally is slow and unnecessary
- AI agents need structured access to repository data instead of large text blobs

REBUSS.Pure solves this problem by exposing **small composable MCP tools** that allow an AI agent to retrieve only the information it needs.

This enables scalable pull request analysis even for large repositories.

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

# Quick Start

## Recommended configuration

The recommended way to configure REBUSS.Pure is to pass **`--repo`** and **`--pat`** directly as command-line arguments in your `mcp.json` file. This is the simplest and most reliable setup — the server receives the workspace path and authentication token at startup, with no additional configuration files or environment variables needed.

## 1. Initialize the MCP configuration

Navigate to your Azure DevOps repository and run:

```
REBUSS.Pure.exe init
```

This creates a `.vscode/mcp.json` file in your repository root. After running `init`, **add your PAT** to the generated configuration:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": [
        "--repo", "${workspaceFolder}",
        "--pat", "your-pat-here"
      ]
    }
  }
}
```

> **Why `--repo` and `--pat` in args?**
> - `--repo ${workspaceFolder}` ensures the server always knows which repository to analyze, regardless of the working directory.
> - `--pat` provides authentication inline — no need to manage separate config files or environment variables.
> - Both arguments take the **highest priority** and override all other configuration sources.

> **Alternative authentication methods:** If you prefer not to put the PAT in `mcp.json`, you can use `appsettings.Local.json` or environment variables instead (see [Storing Secrets Locally](#storing-secrets-locally)).

> **Visual Studio Professional** uses a global `%USERPROFILE%\.mcp.json` and does **not** expand `${workspaceFolder}`.
> However, Visual Studio automatically sends the open solution folder as an MCP root during initialization,
> so the server detects the repository without `--repo`. You still need to provide the PAT:
>
> ```json
> {
>   "servers": {
>     "REBUSS.Pure": {
>       "type": "stdio",
>       "command": "C:\\path\\to\\REBUSS.Pure.exe",
>       "args": ["--pat", "your-pat-here"]
>     }
>   }
> }
> ```

## 2. Open the repository in VS Code

The MCP client will automatically detect the configuration and launch the server with the correct `--repo` and `--pat` arguments.

## 3. Use it with GitHub Copilot

In GitHub Copilot Chat:

```
PullRequest 123 #review-pr
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

The intended workflow for AI agents is:

1. Retrieve pull request metadata
2. Retrieve the list of changed files
3. Review files one by one (or the full diff for small PRs)
4. Recognize and skip files whose diffs were automatically omitted
5. Retrieve full file content only when necessary
6. Produce a structured review report

This incremental approach prevents context overload and allows AI tools to analyze large pull requests efficiently.

---

# Example Workflow

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

# CLI Commands

## `init`

Generates a `.vscode/mcp.json` configuration file in the current repository root.

```
cd /path/to/your/azure-devops-repo
REBUSS.Pure.exe init
```

The generated configuration tells MCP clients to launch the server with `--repo ${workspaceFolder}`, which automatically passes the workspace path to the server at startup.

If a `.vscode/mcp.json` already exists, the command prints a message and exits without overwriting.

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

If you prefer not to use `init`, you can manually create the configuration. The recommended setup includes both `--repo` and `--pat`:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "path/to/REBUSS.Pure.exe",
      "args": [
        "--repo", "${workspaceFolder}",
        "--pat", "your-pat-here"
      ]
    }
  }
}
```

Place this in `.vscode/mcp.json` (per-workspace) or `~/.mcp.json` (global).

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

The server uses the following authentication chain (in priority order):

1. **Personal Access Token** (recommended) — if `PersonalAccessToken` is provided in configuration, it is always used (Basic auth).
2. **Cached token** — if a token was previously acquired and is not expired, it is reused.
3. **Error with instructions** — if no PAT is configured and no cached token is available, the server returns a clear error message with step-by-step instructions for creating and configuring a PAT.

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

> **Never put real secrets in `appsettings.json`.** That file is committed to the repository and must only contain empty defaults or non-sensitive values.

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
2. `appsettings.Local.json` — not committed, contains your personal secrets
3. Environment variables — useful for CI/CD or container deployments
4. Command-line arguments (`--pat`, `--org`, `--project`, `--repository`) — highest priority

---

# Integration with GitHub Copilot

This repository includes a prompt file:

.github/prompts/review-pr.prompt.md


The prompt instructs GitHub Copilot how to perform a structured code review using the MCP tools provided by REBUSS.Pure.

The agent follows a controlled workflow:

1. metadata analysis
2. file list analysis
3. diff-based inspection
4. optional full file retrieval

---

# Example Usage

In GitHub Copilot Chat you can trigger the review workflow:

PullRequest 123 #review-pr


Copilot will then:

1. load the review prompt file
2. call the MCP tools
3. analyze the pull request incrementally
4. generate a structured code review report

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
- https://REBUSS.Pureo
