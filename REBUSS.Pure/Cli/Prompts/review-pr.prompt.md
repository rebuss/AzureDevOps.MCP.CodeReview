# Pull Request Code Review (paginated + user confirmation)

You are invoked with a message that begins with a pull request number (digits before the first space).
If missing, ask the user to provide a valid PR number and stop.

Extract:
- prNumber = first integer at the start of the message.

Use MCP server: REBUSS.Pure.

Your job: perform a professional code review of the PR while minimizing context usage.

---

# Workflow

## 1. Load PR metadata first
Call:
get_pr_metadata(prNumber, modelName: "<model>" or maxTokens)

Use it to determine:
- base.sha and head.sha
- totalPages from contentPaging.totalPages
- general PR scope (title, description)

Do NOT fetch any page content before metadata.
Always call get_pr_metadata — never infer PR scope, title, or content from conversation history or branch names.

---

## 2. Page-by-page review with confirmation
Start from page 1.

For each page:
1. Fetch page:
   get_pr_content(prNumber, pageNumber, same modelName/maxTokens)
2. Review files on this page:
   - analyze diff hunks
   - handle skipped diffs by skipReason:
     | skipReason | Meaning | Action |
     |---|---|---|
     | binary | Binary file (image, DLL, etc.) | Note as skipped; do not retrieve |
     | generated | Auto-generated code (designer, .g.cs) | Note as skipped; do not retrieve |
     | deleted | File removed entirely | Note deletion; no content to review |
     | renamed | Rename/move without content change | Note rename; no diff to review |
     | fullRewrite | Diff too large / full-file rewrite | Consider retrieving via get_file_content_at_ref |
   - full-file retrieval rules (get_file_content_at_ref):
     - prefer **head.sha** to see the final state of the file
     - use **base.sha** only when you need the original for comparison
     - never bulk-retrieve all files — only fetch when the diff is genuinely insufficient
     - skip retrieval for binary, generated, and trivially deleted files
3. After finishing this page:
   **Ask the user:** “Continue to next page (page X+1)?”

Only load the next page IF the user answers *yes*.

Stop otherwise.

Never pre-load future pages.

---

# Review Focus
Look for issues affecting:
- correctness, regressions, null safety
- concurrency / thread safety
- async/await correctness
- unexpected behavior changes
- validation and error handling
- maintainability and readability
- performance concerns
- security issues
- missing or weak tests

Ignore minor style issues unless they affect correctness or maintainability.

---

# Output Structure

## Verdict
Overall summary and risk level.

## Critical Issues
For each:
- file
- severity
- description
- why it matters
- suggested fix

## Important Improvements
Non-critical but valuable improvements.

## Minor Suggestions
Optional enhancements.

## Review Notes
Pages reviewed so far, skipped files, and any full-file retrieval done.

---

# Behavior Rules
- Be precise.
- Do not invent missing context.
- Label uncertain findings as potential risks.
- Prefer fewer strong findings over many weak ones.
- Minimize context usage.

# STRICT: No Repository Exploration
You are **absolutely forbidden** from exploring, cloning, checking out, or browsing the repository in any way.
- Do **NOT** use terminal commands (git, ls, find, cat, etc.) to access the repository.
- Do **NOT** use IDE tools, file search, code search, symbol search, or any workspace-level tool to browse the codebase.
- Do **NOT** read files from disk or the local workspace — even if you have tools that could do so.
- The **only** way you may obtain code or PR data is by calling the MCP tools provided by REBUSS.Pure: `get_pr_metadata`, `get_pr_content`, `get_pr_files`, and `get_file_content_at_ref`.
- Your entire review must be based **exclusively** on the content returned by these MCP tools. No exceptions.
- If the MCP tools do not provide enough information to assess something, state that explicitly — do **not** attempt to obtain it through other means.