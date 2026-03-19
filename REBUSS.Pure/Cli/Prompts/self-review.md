# Self-Review (MCP-Only)

Perform a professional self-review of **local git changes**, using **exclusively** the MCP server `REBUSS.Pure`.

This review MUST:
- use only MCP tools from `REBUSS.Pure`
- NEVER inspect files or project state directly from the editor or local workspace
- NEVER reason about local changes without calling MCP tools
- rely solely on `get_local_files` and `get_local_file_diff`
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

### `get_local_files([scope])`
Call this **first**.

Purpose:
- detect locally changed files
- understand the scope and categories of changes
- decide which files need further inspection

The response includes:
- repositoryRoot
- scope
- currentBranch
- totalFiles
- list of files with: path, status, additions, deletions, extension, classification, reviewPriority
- summary by category (source / test / config / docs / binary / generated)

---

### `get_local_file_diff(path, [scope])`
Call **only after** `get_local_files`.

Purpose:
- obtain structured diffs for selected files
- minimize context consumption

Notes:
- must reuse the SAME `scope` as used in `get_local_files`
- files may be skipped with a `skipReason`
- skipped diffs must NOT be analyzed

---

# Mandatory Workflow

## Step 1 — List changed files
Call:

get_local_files([scope])

Then:
- note repositoryRoot and currentBranch  
- read file categories and priorities  
- decide which files are worth reviewing (source › config › tests › docs)

---

## Step 2 — Retrieve diffs only for relevant files

For each file that merits actual review:

get_local_file_diff(path, [scope])

Skip:
- binary files  
- generated files  
- trivial changes  
- files with `skipReason`  

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

# Output Format

## Verdict
Short summary including:
- repository root  
- current branch  
- scope used  
- number of files reviewed  

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