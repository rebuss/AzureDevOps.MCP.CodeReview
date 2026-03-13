# REBUSS.PR – Pull Request Analysis MCP Server

REBUSS.PR is a lightweight **MCP (Model Context Protocol) server** designed to enable intelligent pull request analysis by AI agents such as **GitHub Copilot**.

The server provides structured access to pull request data from **Azure DevOps** while minimizing context usage and avoiding the need to clone repositories locally.

Its primary goal is to support **efficient AI-assisted code reviews** by exposing small, well-defined tools that allow agents to analyze pull requests incrementally.

---

# Why this project exists

Modern AI coding assistants often struggle with large pull requests because:

- loading an entire PR diff can overflow the model context window
- cloning repositories locally is slow and unnecessary
- AI agents need structured access to repository data instead of large text blobs

REBUSS.PR solves this problem by exposing **small composable MCP tools** that allow an AI agent to retrieve only the information it needs.

This enables scalable pull request analysis even for large repositories.

---

# Architecture

The system follows a layered architecture:

```
GitHub Copilot
│
│ prompt file  (stdio / JSON-RPC 2.0)
▼
MCP Server (REBUSS.PR)
│
│ Azure DevOps REST API
▼
Azure DevOps Repository
```

Instead of sending a large diff to the AI model, the MCP server exposes tools that allow the agent to retrieve data **incrementally**.

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

## get_pr_diff(prNumber, format?)

Returns the complete diff for all changed files in the pull request.

Supports an optional `format` parameter:

- `text` (default) – human-readable summary followed by unified diff content
- `json` / `structured` – structured JSON object with per-file diffs

This tool is useful for a quick overview of all changes at once. For large pull requests, prefer `get_file_diff` to retrieve changes file-by-file.

---

## get_file_diff(prNumber, path, format?)

Returns the diff for a specific file in the pull request.

Supports an optional `format` parameter:

- `text` (default) – human-readable summary followed by unified diff content
- `json` / `structured` – structured JSON object scoped to the requested file

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

# AI-Driven Review Workflow

The intended workflow for AI agents is:

1. Retrieve pull request metadata
2. Retrieve the list of changed files
3. Review files one by one (or the full diff for small PRs)
4. Retrieve full file content only when necessary
5. Produce a structured review report

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

REBUSS.PR was designed with the following goals:

- minimize AI context usage
- avoid cloning repositories locally
- provide deterministic tool outputs
- enable scalable pull request analysis
- integrate seamlessly with GitHub Copilot

---

# Configuration

REBUSS.PR requires Azure DevOps connection settings.

Required settings under the `AzureDevOps` section:

| Setting | Description |
|---|---|
| `OrganizationName` | Azure DevOps organization name |
| `ProjectName` | Azure DevOps project name |
| `RepositoryName` | Git repository name within the project |
| `PersonalAccessToken` | PAT with read access to code and pull requests |

The server will fail to start if any required field is missing.

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
3. Environment variables — highest priority, useful for CI/CD or container deployments

---

# Integration with GitHub Copilot

This repository includes a prompt file:

.github/prompts/review-pr.prompt.md


The prompt instructs GitHub Copilot how to perform a structured code review using the MCP tools provided by REBUSS.PR.

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
- https://rebuss.pro
