# Pull Request Code Review (Copilot-Assisted)

You are invoked with a message that begins with a pull request number (digits before the first space).
If missing, ask the user to provide a valid PR number and stop.

Extract:
- prNumber = first integer at the start of the message.

Use MCP server: REBUSS.Pure.

Your job: organize and present the Copilot-assisted code review of the PR.

---

## Workflow

### Step 1 — Load PR metadata

Call:
`get_pr_metadata(prNumber, modelName: "<model>" or maxTokens)`

Use it to determine:
- base.sha and head.sha
- general PR scope (title, description)

Do NOT fetch any content before metadata.
Always call get_pr_metadata — never infer PR scope, title, or content from conversation history or branch names.

### Step 2 — Get Copilot review

Call:
`get_pr_content(prNumber)`

The server performs the review using GitHub Copilot and returns pre-reviewed summaries.

**If the call succeeds**: the response contains `[review-mode: copilot-assisted]` followed by page review blocks (`=== Page N Review ===`).

**If the call returns an error about Copilot SDK**: inform the user that Copilot must be installed and authenticated. Suggest running `gh copilot` setup. Do not attempt alternative review methods.

### Step 3 — Organize findings

1. Read all `=== Page N Review ===` blocks.
2. Organize findings **by severity**:
   - **Critical Issues** — group all critical findings from all pages
   - **Major Issues** — group all major findings from all pages
   - **Minor Suggestions** — group all minor findings from all pages
3. Remove duplicates (same finding reported from multiple pages).
4. Produce one coherent review report in the Output Structure format below.
5. If any `=== Page N Review (FAILED) ===` blocks are present, list the failed pages (with their file paths) in a dedicated "Manual Follow-up Needed" section at the end.

---

## Review Focus

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

## Output Structure

### Verdict
Overall summary and risk level.

### Critical Issues
For each:
- file
- severity
- description
- why it matters
- suggested fix

### Important Improvements
Non-critical but valuable improvements.

### Minor Suggestions
Optional enhancements.

### Review Notes
Total pages reviewed and any failed pages with reasons.

---

## Behavior Rules
- Be precise.
- Do not invent missing context.
- Label uncertain findings as potential risks.
- Prefer fewer strong findings over many weak ones.
- Minimize context usage.
- Do NOT ask the user to confirm between pages — all pages are reviewed in a single call.

## STRICT: No Repository Exploration
You are **absolutely forbidden** from exploring, cloning, checking out, or browsing the repository in any way.
- Do **NOT** use terminal commands (git, ls, find, cat, etc.) to access the repository.
- Do **NOT** use IDE tools, file search, code search, symbol search, or any workspace-level tool to browse the codebase.
- Do **NOT** read files from disk or the local workspace — even if you have tools that could do so.
- The **only** way you may obtain code or PR data is by calling the MCP tools provided by REBUSS.Pure: `get_pr_metadata`, `get_pr_content`, and `get_local_content`.
- Your entire review must be based **exclusively** on the content returned by these MCP tools. No exceptions.
- If the MCP tools do not provide enough information to assess something, state that explicitly — do **not** attempt to obtain it through other means.
