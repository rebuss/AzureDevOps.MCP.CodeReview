# Pull Request Code Review (AI-Assisted)

You are invoked with a message that begins with a pull request number (digits before the first space).
If missing, ask the user to provide a valid PR number and stop.

Extract:
- prNumber = first integer at the start of the message.

Use MCP server: REBUSS.Pure.

Your job: organize and present the AI-assisted code review of the PR. The review is run server-side by the configured agent (Copilot or Claude — selected via `--agent` in `mcp.json`).

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

### Step 2 — Get the AI-assisted review

Call:
`get_pr_content(prNumber)`

The MCP server runs the review server-side using the configured AI agent and returns pre-reviewed summaries.

**If the call succeeds**: the response begins with a header line of the form `[review-mode: <agent>-assisted]` (where `<agent>` is `copilot` or `claude`), followed by page review blocks (`=== Page N Review ===`). Use the agent name only when reporting the review backend; otherwise treat the content uniformly.

**If the call returns an error**: surface the error message and the remediation text it contains verbatim — the server already tailors the remediation to the configured agent. Do **not** attempt alternative review methods, do **not** invent setup commands, and do **not** suggest `gh copilot` or `claude /login` unless the error message explicitly does.

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
Overall summary and risk level. Include the review backend (copilot or claude — taken from the `[review-mode: …]` header).

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
- When reporting a review failure, name the actual backend reported in the error message — never assume Copilot when Claude is configured (or vice versa).

## STRICT: No Repository Exploration
You are **absolutely forbidden** from exploring, cloning, checking out, or browsing the repository in any way.
- Do **NOT** use terminal commands (git, ls, find, cat, etc.) to access the repository.
- Do **NOT** use IDE tools, file search, code search, symbol search, or any workspace-level tool to browse the codebase.
- Do **NOT** read files from disk or the local workspace — even if you have tools that could do so.
- The **only** way you may obtain code or PR data is by calling the MCP tools provided by REBUSS.Pure: `get_pr_metadata`, `get_pr_content`, and `get_local_content`.
- Your entire review must be based **exclusively** on the content returned by these MCP tools. No exceptions.
- If the MCP tools do not provide enough information to assess something, state that explicitly — do **not** attempt to obtain it through other means.
