# Pull Request Code Review

Perform a professional code review of the pull request.

Pull request number: {{input}}

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

get_pr_metadata(prNumber)

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

get_pr_files(prNumber)

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
```

## Skip categories

| Category | skipReason | When it applies |
|---|---|---|
| File deletions | `file deleted` | File was removed. No content is fetched. |
| File renames | `file renamed` | Pure rename. Content diff would be misleading. |
| Binary files | `binary file` | Detected by extension (`.dll`, `.png`, `.zip`, `.pdf`, etc.). |
| Generated files | `generated file` | Detected by path (`/obj/`, `/bin/`, `.g.cs`, `.designer.cs`, lock files, etc.). |
| Full-file rewrites | `full file rewrite` | Both versions exist (≥10 lines each) but every line changed — indicates formatting rewrite or tooling output. |

## How to handle skipped diffs

- **Do not** try to analyze the diff content of a skipped file.
- **Acknowledge** the skip in the review notes (e.g. "File `/lib/tool.dll` skipped: binary file").
- For deleted files, note the deletion but do not request full content.
- For renamed files, note the rename.
- For binary and generated files, skip entirely unless there is a specific concern.
- For full-file rewrites, consider requesting full file content via `get_file_content_at_ref` only if the file appears to be important source code.

---

# Review Strategy

Choose the strategy depending on PR size.

---

## Small PR

If the PR contains only a few files and limited changes:

- call `get_pr_diff(prNumber)` to retrieve all changes in a single call
- only retrieve full file content when necessary

---

## Medium PR

If the PR contains a moderate number of files:

- prioritize high-value files first
- review file-by-file using `get_file_diff`
- retrieve full file content only when the patch lacks context
- use tests only as supporting evidence

---

## Large PR

If the PR is large:

- do NOT load the entire PR into context
- review iteratively file-by-file
- start with high-priority files
- retrieve only the diff for each file
- retrieve full file content only when necessary
- focus on high-risk logic changes first

If the PR is extremely large, explain that the review focuses on the most critical files.

---

# Full File Retrieval Rules

When using `get_file_content_at_ref`:

1. Prefer `head.sha` to inspect the current implementation.
2. Use `base.sha` only if comparing previous behavior is necessary.
3. Never retrieve full file content for all files automatically.
4. Avoid retrieving full content for:
   - documentation
   - generated files
   - binary files
   - trivial changes

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

- which files were reviewed in detail
- whether full file content was required
- whether review scope was limited due to PR size
- which files had their diffs skipped by the server and why (use the `skipReason` value)

---

# Behavior Rules

- Be precise and concrete.
- Do not invent missing context.
- If something is uncertain, label it as a potential risk.
- Prefer fewer high-quality findings over many weak comments.
- Avoid flooding the review with trivial remarks.
- Optimize for minimal context usage.