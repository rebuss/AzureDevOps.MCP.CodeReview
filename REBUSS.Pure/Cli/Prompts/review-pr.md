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

### get_pr_metadata(prNumber, [modelName], [maxTokens])

Use **first**. Always pass `modelName` or `maxTokens` to enable pagination info.

Purpose:
- understand the scope of the PR
- retrieve `base.sha` and `head.sha`
- retrieve PR title, author and description
- obtain `contentPaging` — the total page count and per-page file breakdown

The response includes a `contentPaging` section when budget parameters are provided:
```json
{
  "contentPaging": {
    "totalPages": 3,
    "totalFiles": 42,
    "budgetPerPageTokens": 89600,
    "filesByPage": [
      { "pageNumber": 1, "fileCount": 12 },
      { "pageNumber": 2, "fileCount": 15 },
      { "pageNumber": 3, "fileCount": 15 }
    ]
  }
}
```

---

### get_pr_content(prNumber, pageNumber, [modelName], [maxTokens])

**Primary review tool.** Use this to iterate through pages of review content.

Purpose:
- retrieve the structured diff for all files on a specific page
- each page is pre-sized to fit within the context budget
- use the same `modelName`/`maxTokens` as the metadata call for consistent page allocation

The response includes per-file diffs with `path`, `changeType`, `additions`, `deletions`, `hunks`, and a `summary` with `filesOnPage`, `totalFiles`, `estimatedTokens`, `hasMorePages`, and `categories`.

Files may have their diff **automatically skipped** — their `hunks` array will be empty and a `skipReason` field will explain why (see *Skipped Diffs* below).

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

### Legacy tools (available but not recommended for reviews)

These tools remain available for specific use cases:

- `get_pr_files(prNumber)` — list changed files with stats
- `get_pr_diff(prNumber)` — full PR diff in one call (risk of context overflow)
- `get_file_diff(prNumber, path)` — single-file diff

Prefer `get_pr_content` over these for reviews — it provides server-managed pagination.

---

# Mandatory Review Workflow

## Step 1 — Load metadata with pagination info

- Before reviewing any PR, ALWAYS call `get_pr_metadata` first.
- NEVER infer PR content, title, or scope from the branch name or conversation history.
- Treat any pre-existing summary/context about a PR as unverified until confirmed by `get_pr_metadata`.

Call:

`get_pr_metadata(prNumber, modelName: "<your model>")`

Use the result to determine:

- PR size and intent
- number of changed files
- base SHA and head SHA
- **total pages** from `contentPaging.totalPages`

---

## Step 2 — Iterate through content pages

Loop from page 1 to `totalPages`:

```
for pageNumber in 1..totalPages:
    response = get_pr_content(prNumber, pageNumber, modelName: "<your model>")
    review each file in response.files
    check response.summary.hasMorePages to confirm continuation
```

For each file on the page:
- analyze the diff hunks for issues
- note files with `skipReason` — do not analyze their content
- use `get_file_content_at_ref` only when the diff lacks sufficient context

---

# Skipped Diffs

The diff provider **automatically skips** diff generation for certain files. When a diff is skipped, the file entry will contain:

- a `skipReason` field explaining why (e.g. `"file deleted"`, `"file renamed"`, `"binary file"`, `"generated file"`, `"full file rewrite"`)
- an empty `hunks` array

## Skip categories

| Category | skipReason | When it applies |
|---|---|---|
| File deletions | `file deleted` | File was removed |
| File renames | `file renamed` | Pure rename |
| Binary files | `binary file` | Detected by extension |
| Generated files | `generated file` | Detected by path |
| Full-file rewrites | `full file rewrite` | Every line changed — formatting rewrite |

## How to handle skipped diffs

- **Do not** analyze the diff content of a skipped file.
- **Acknowledge** the skip in the review notes.
- For full-file rewrites of important source code, consider `get_file_content_at_ref`.

---

# Full File Retrieval Rules

When using `get_file_content_at_ref`:

1. Prefer `head.sha` to inspect the current implementation.
2. Use `base.sha` only if comparing previous behavior is necessary.
3. Never retrieve full file content for all files automatically.
4. Avoid retrieving full content for documentation, generated files, binary files, or trivial changes.

---

# What To Inspect

When analyzing each change look for:

- incorrect assumptions
- null reference risks
- race conditions
- deadlocks or synchronization issues
- incorrect async usage
- hidden behavior changes
- missing validation
- unhandled exceptions
- duplicated logic
- performance regressions
- insufficient tests

When reviewing tests:

- check if new logic is covered
- verify that tests actually validate behavior
- do not assume correctness based on test presence alone

---

# Output Format

Return the review using the following structure.

## Verdict

Short summary of the overall quality and risk level of the PR.

---

## Critical Issues

Issues that may cause bugs, crashes, security problems, or serious regressions.

For each issue include:

- file path
- severity
- problem description
- why it matters
- suggested fix

---

## Important Improvements

Significant improvements related to maintainability, validation, robustness, or performance.

Include:

- file path
- issue description
- reason
- suggested improvement

---

## Minor Suggestions

Optional improvements that are helpful but not critical.

---

## Review Notes

Briefly mention:

- which pages were reviewed
- which files were reviewed in detail
- whether full file content was required
- which files had their diffs skipped by the server and why

---

# Behavior Rules

- Be precise and concrete.
- Do not invent missing context.
- If something is uncertain, label it as a potential risk.
- Prefer fewer high-quality findings over many weak comments.
- Avoid flooding the review with trivial remarks.
- Optimize for minimal context usage.