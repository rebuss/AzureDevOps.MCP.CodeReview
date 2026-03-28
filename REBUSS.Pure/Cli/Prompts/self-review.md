# Self-Review (MCP-Only)

Perform a professional self-review of **local git changes**, using **exclusively** the MCP server `REBUSS.Pure`.

This review MUST:
- use only MCP tools from `REBUSS.Pure`
- NEVER inspect files or project state directly from the editor or local workspace
- NEVER reason about local changes without calling MCP tools
- rely on `get_local_content` as the primary review tool
- abort if MCP tools are unavailable

If any required MCP tool is missing, respond with:
**"Cannot proceed: required MCP tools not available."**

---

# Scope Handling

Unless the user explicitly specifies a scope (e.g. `working-tree`, `main`, `origin/main`), use:

**`staged`**

This ensures only intentionally staged changes are reviewed.
No other input is needed to determine the scope.

---

# Allowed MCP Tools (Strict)

### `get_local_content(pageNumber, [scope], [modelName], [maxTokens])`

**Primary review tool.** Use this to iterate through pages of local change content.

Purpose:
- retrieve structured diffs for all files on a specific page
- each page is pre-sized to fit within the context budget
- the response includes `repositoryRoot`, `currentBranch`, `scope`, page info, and per-file diffs

The first page response tells you the `totalPages` - loop through all pages.

Start with page 1:
```
response = get_local_content(pageNumber: 1, scope: "staged")
```

The response includes:
- `repositoryRoot` - absolute path to the git repository
- `currentBranch` - current branch name (null if detached HEAD)
- `scope` - the scope used
- `pageNumber` / `totalPages` - pagination info
- `files` - array of structured diffs per file
- `summary.filesOnPage`, `summary.totalFiles`, `summary.hasMorePages`, `summary.categories`

---

### Legacy tools (available but not recommended)

These tools remain available for specific use cases:

- `get_local_files([scope])` - list changed files with stats (no diffs)
- `get_local_file_diff(path, [scope])` - single-file diff

Prefer `get_local_content` - it provides server-managed pagination and reduces tool calls.

---

# Mandatory Workflow

## Step 1 - Retrieve first page of content

Call:

`get_local_content(pageNumber: 1, scope: "staged")`

From the response:
- note `repositoryRoot` and `currentBranch`
- note `totalPages` to plan iteration
- review each file in the `files` array
- check `summary.categories` for change distribution

---

## Step 2 - Iterate remaining pages

If `summary.hasMorePages` is true, continue:

```
for pageNumber in 2..totalPages:
    response = get_local_content(pageNumber, scope: "staged")
    review each file in response.files
```

For each file:
- analyze the diff hunks for issues
- note files with `skipReason` - do not analyze their content

---

# Review Priorities

Order:
1. High-priority source files
2. Other source files
3. Configuration files
4. Test files
5. Documentation

---

# What to Look For

Check diffs for:
- correctness issues
- potential bugs or regressions
- null safety problems
- concurrency hazards
- async/await correctness
- missing validation or error-handling
- unintended behavior changes
- performance regressions
- duplicated or fragile logic
- missing or insufficient tests

For test changes:
- ensure coverage matches modified logic
- confirm assertions are meaningful

---

# Skipped Diffs

Files may have their diff skipped with a `skipReason` field (e.g. `"binary file"`, `"generated file"`, `"file deleted"`). Do not analyze their content - acknowledge the skip in review notes.

---

# Output Format

## Verdict
Short summary including:
- repository root
- current branch
- scope used
- number of files reviewed
- number of pages processed

---

## Critical Issues
For each:
- file path
- severity
- issue description
- why it matters
- recommended fix

---

## Important Improvements
For each:
- file path
- what to improve
- reason
- suggested correction

---

## Minor Suggestions

---

## Review Notes
Include:
- scope used
- which pages were reviewed
- which files were reviewed
- which files were skipped and why
- limitations due to large change sets (if applicable)

---

# Behavioral Requirements

- Never infer file contents.
- Never analyze files outside MCP responses.
- Avoid unnecessary tool calls.
- Prefer fewer, higher-value insights.
- If something is uncertain, mark it as a potential risk.