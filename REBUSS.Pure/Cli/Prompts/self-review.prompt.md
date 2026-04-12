# Self-Review (MCP-Only, paginated with confirmation)

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

## Allowed MCP Tool

### `get_local_content(pageNumber, [scope], [modelName], [maxTokens])`

Primary tool for reviewing local changes.

It returns:
- `repositoryRoot`
- `currentBranch`
- `scope`
- `pageNumber`, `totalPages`
- `files` – structured diffs for this page
- `summary.filesOnPage`, `summary.totalFiles`, `summary.hasMorePages`, `summary.categories`

Start with page 1 and the chosen scope (default: `"staged"`).

Example:
`get_local_content(pageNumber: 1, scope: "staged")`

---

## Response Mode Detection

After calling `get_local_content`, check the **first content block** for a mode indicator:

### If `[review-mode: copilot-assisted]`
The MCP server has already performed the code review using GitHub Copilot.
The response contains review summaries for each page — NOT raw diff content.

Your task:
1. Read all page review summaries.
2. Organize findings by severity:
   - **Critical Issues** — group all critical findings from all pages
   - **Major Issues** — group all major findings from all pages
   - **Minor Suggestions** — group all minor findings from all pages
3. Remove duplicates (same finding reported from different pages).
4. Produce a single coherent review report in the Output Format below.
5. Do NOT ask the user to continue to the next page — all pages are already reviewed.

### If `[review-mode: content-only]`
Standard flow — review the diff content yourself, page by page with user confirmation.
Follow the Mandatory Workflow below.

---

## Mandatory Workflow

### Step 1 – First page

1. Determine the scope (user-provided or default `"staged"`).
2. Call:
   `get_local_content(pageNumber: 1, scope: <scope>)`
3. From the response:
   - note `repositoryRoot`, `currentBranch`, `scope`
   - note `totalPages` and `summary` info
   - review each file in `files`
   - for each file:
     - analyze diff hunks
     - if `skipReason` is present, do not analyze content, just note the skip

After finishing page 1, ask the user:
**“Page 1 of N reviewed. Continue to page 2?”**

Do NOT load page 2 before the user confirms.

---

### Step 2 – Subsequent pages (with user confirmation)

For each next page (2, 3, … up to `totalPages`):

1. Only if the user confirms, call:
   `get_local_content(pageNumber: X, scope: <scope>)`
2. Review files on that page as above.
3. After finishing the page:
   - if `pageNumber < totalPages`, ask:
     **“Page X of N reviewed. Continue to page X+1?”**
   - if this was the last page, finish the review.

Never pre-fetch future pages without explicit user confirmation.

---

## Review Focus

When inspecting diffs, look for:
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
- which pages were reviewed
- which files were reviewed
- which files were skipped and why
- any limitations due to large change sets

---

## Behavioral Rules

- Never infer file contents outside MCP responses.
- Never analyze files or project state outside MCP responses.
- Avoid unnecessary tool calls.
- Prefer fewer, higher-value findings over many trivial comments.
- If something is uncertain, label it as a potential risk.