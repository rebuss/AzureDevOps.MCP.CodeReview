# Self-Review of Local Changes (Copilot-Assisted)

Perform a professional self-review of **local git changes** using **only** MCP tools from the `REBUSS.Pure` server.

Hard requirements:
- Use exclusively MCP tools from `REBUSS.Pure`.
- NEVER read files or project state directly from the editor or local workspace.
- NEVER reason about local changes without MCP responses.
- Rely on `get_local_content` as the primary review tool.
- Abort if required MCP tools are unavailable.

If any required MCP tool is missing, respond with:
**"Cannot proceed: required MCP tools not available."**

---

## Scope

If the user does not explicitly specify a scope (e.g. `working-tree`, `main`, `origin/main`), use:

**`staged`**

Do not infer scope from anywhere else.

---

## Workflow

### Step 1 — Get Copilot review

Call:
`get_local_content(scope: "<scope>")`

The server performs the review using GitHub Copilot and returns pre-reviewed summaries.

**If the call succeeds**: the response contains `[review-mode: copilot-assisted]` followed by page review blocks (`=== Page N Review ===`).

**If the call returns an error about Copilot SDK**: inform the user that Copilot must be installed and authenticated. Suggest running `gh copilot` setup. Do not attempt alternative review methods.

### Step 2 — Organize findings

1. Read all `=== Page N Review ===` blocks.
2. Organize findings **by severity**:
   - **Critical Issues** — group all critical findings from all pages
   - **Major Issues** — group all major findings from all pages
   - **Minor Suggestions** — group all minor findings from all pages
3. Remove duplicates (same finding reported from multiple pages).
4. Produce one coherent review report in the Output Format below.
5. If any `=== Page N Review (FAILED) ===` blocks are present, list the failed pages (with their file paths) in a dedicated "Manual Follow-up Needed" section at the end.

---

## Review Focus

When organizing findings, prioritize:
- correctness and potential regressions
- null safety issues
- concurrency / async/await problems
- missing validation or error handling
- unintended behavior changes
- performance regressions
- fragile / duplicated logic
- missing or insufficient tests

For test changes:
- check coverage of new/modified logic
- ensure assertions are meaningful

---

## Output Format

### Verdict
Short summary including:
- repository root
- current branch
- scope used
- number of files reviewed
- number of pages reviewed

### Critical Issues
For each:
- file path
- severity
- issue description
- why it matters
- recommended fix

### Important Improvements
For each:
- file path
- what to improve
- reason
- suggested change

### Minor Suggestions
Optional improvements.

### Review Notes
Include:
- scope used
- total pages reviewed
- any failed pages with reasons

---

## Behavioral Rules

- Never infer file contents outside MCP responses.
- Never analyze files or project state outside MCP responses.
- Avoid unnecessary tool calls.
- Prefer fewer, higher-value findings over many trivial comments.
- If something is uncertain, label it as a potential risk.
- Do NOT ask the user to confirm between pages — all pages are reviewed in a single call.
