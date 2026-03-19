# Pull Request Code Review

You are invoked with a message that starts with the pull request number, followed by a space and the rest of the prompt content.

Example invocation:
`123 #review-pr.md`

**Interpretation rule (mandatory):**
- Treat the **first contiguous sequence of digits at the very beginning of the message (before the first space)** as:
  - `prNumber`
  - the pull request number to use in all MCP tool calls.

If the message does not start with such a number, ask the user to provide a valid pull request number and stop.

---

Perform a professional code review of the pull request.

Pull request number: the leading integer at the start of this message (`prNumber`).

Use MCP server: `REBUSS.Pure`.

The goal is to detect real technical risks while minimizing unnecessary context usage.

---

# Review Goals

Focus on issues affecting:

- correctness
- potential bugs or regressions
- null safety
- concurrency and thread safety
- async/await correctness
- breaking changes
- validation and error handling
- maintainability
- performance
- security
- missing or insufficient tests

Avoid focusing on minor style issues unless they affect correctness or maintainability.

---

# MCP Tools

Use the following MCP tools from `REBUSS.Pure`.

### get_pr_metadata(prNumber)

Use **first**.

Purpose:
- understand the scope of the PR
- retrieve `base.sha` and `head.sha`
- retrieve PR title, author and description
- determine review strategy

---

### get_pr_files(prNumber)

Use **after metadata**.

Purpose:
- retrieve the list of changed files
- obtain per-file statistics (additions/deletions/changes)
- determine which files to review first

Use this to avoid loading the entire PR diff at once.

---

### get_pr_diff(prNumber)

Use for **small PRs** as a faster alternative to iterating `get_file_diff` over each file.

Purpose:
- retrieve the complete structured diff for all changed files in a single call
- get a quick overview of all changes at once

The response is a structured JSON object containing per-file hunks. Each file includes `path`, `changeType`, `additions`, `deletions`, and a `hunks` array. Each hunk contains location metadata and ordered lines with operation types (`+`, `-`, ` `).

Some files may have their diff **automatically skipped** by the server (see *Skipped Diffs* below). Their `hunks` array will be empty and a `skipReason` field will explain why.

Do not use for large PRs — prefer `get_file_diff` to keep context small.

---

### get_file_diff(prNumber, path)

Default method for reviewing code.

Purpose:
- retrieve the structured diff for a specific file
- analyze changes with minimal context cost

The response is a structured JSON object containing the file's `path`, `changeType`, `additions`, `deletions`, and a `hunks` array. Each hunk contains `oldStart`, `oldCount`, `newStart`, `newCount`, and ordered `lines` with an `op` field (`+`, `-`, ` `) and `text`.

If the file belongs to a skip category (deleted, renamed, binary, generated, or full-file rewrite), the response contains a `skipReason` value and an empty `hunks` array. In that case, do not attempt to analyze the diff content — acknowledge the skip reason and move on.

Always prefer this before retrieving full file content.

---

### get_file_content_at_ref(path, ref)

Use only when the diff alone does not provide enough context.

Purpose:
- retrieve the full file content from a specific revision.

Use:

- `head.sha` to inspect the new implementation
- `base.sha` to inspect the previous implementation

Do not retrieve full content for every file by default.

---

# Mandatory Review Workflow

## Step 1 — Load metadata

Call:

`get_pr_metadata(prNumber)`

Use the result to determine:

- PR size
- number of changed files
- number of commits
- total additions/deletions
- base SHA and head SHA
- high-level intent of the change

---

## Step 2 — Retrieve changed files

Call:

`get_pr_files(prNumber)`

Use this to:

- list all changed files
- determine review order
- identify files that will have their diffs skipped (binary, generated, deleted, renamed)
- prioritize important source code

Preferred review order:

1. source files
2. configuration files
3. test files
4. documentation

Do not request diffs for files that are clearly binary or generated — the server will skip them automatically, but avoiding unnecessary calls saves time.

---

# Skipped Diffs

The diff provider **automatically skips** diff generation for certain files. When a diff is skipped, the file entry will contain:

- a `skipReason` field explaining why (e.g. `"file deleted"`, `"file renamed"`, `"binary file"`, `"generated file"`, `"full file rewrite"`)
- an empty `hunks` array

Example skipped file in the structured response:

```json
{
  "path": "lib/tool.dll",
  "changeType": "add",
  "skipReason": "binary file",
  "additions": 0,
  "deletions": 0,
  "hunks": []
}