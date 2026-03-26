# Create Pull Request

You are invoked with a message that may start with an optional numeric work item ID, followed by `#create-pr`.

Example invocations:
- `123 #create-pr` — create a PR linked to work item or issue 123
- `#create-pr` — create a PR, automatically resolving the active work item

**Interpretation rule (mandatory):**
- If the message starts with a **contiguous sequence of digits at the very beginning** (before the first space), treat that number as the **work item ID**.
- If no leading number is present, use the work item resolution logic described in Step 3.

Use MCP server: `REBUSS.Pure`.
Use the built-in terminal tool to run `git`, `gh`, and `az` commands where indicated.

---

# Goal

Create a pull request from the current branch into its correct base branch, optionally linked to a work item (Azure DevOps) or issue (GitHub).

---

# Step 1 — Determine the current branch

Run in terminal:

```
git rev-parse --abbrev-ref HEAD
```

Save the result as `currentBranch`.

---

# Step 2 — Determine the base branch

Run in terminal:

```
git rev-parse --symbolic-full-name --abbrev-ref @{upstream}
```

If this succeeds, strip any `origin/` prefix from the result and save it as `baseBranch`.

If this fails (no upstream configured), fall back to:

```
git remote show origin | grep "HEAD branch"
```

Extract the branch name from the output and save it as `baseBranch`.

If both commands fail, use `main` as the default value of `baseBranch`.

---

# Step 3 — Resolve the work item ID

## Case A — ID provided explicitly

The user typed `<numericId> #create-pr`. Use `<numericId>` as `workItemId`.

Determine whether the repository is hosted on GitHub or Azure DevOps by inspecting the remote URL:

```
git remote get-url origin
```

- If the URL contains `github.com`: treat `workItemId` as a **GitHub issue number**.
  - Run: `gh issue view <workItemId> --json number,title,body`
  - Extract `title` and `body` as the work item title and description.
- Otherwise: treat `workItemId` as an **Azure DevOps work item ID**.
  - Run: `az boards work-item show --id <workItemId>`
  - Extract `fields["System.Title"]` and `fields["System.Description"]` from the JSON output.

---

## Case B — No ID provided

Try to find the active work item assigned to the current user.

**GitHub** — list open issues assigned to the current user:

```
gh issue list --assignee @me --state open --json number,title --limit 10
```

**Azure DevOps** — query active work items assigned to the current user:

```
az boards query --wiql "SELECT [System.Id],[System.Title] FROM workitems WHERE [System.AssignedTo] = @Me AND [System.State] = 'Active'"
```

> **Note — custom process templates:** The state value `'Active'` matches the standard Agile and Scrum process templates. Teams using custom process templates may use different state names (e.g., `'In Progress'`, `'Doing'`). If the query returns no results unexpectedly, update the state value in the WIQL query to match your team's process template. You can customise this by editing `.github/prompts/create-pr.md` in your repository.

Apply the following logic:

1. If **exactly one** work item or issue is found: use its ID as `workItemId` and fetch its metadata as in Case A.
2. If **more than one** is found:
   - Set `workItemId` to null.
   - Attempt PR creation without a linked work item (proceed to Step 7).
   - If PR creation fails due to a policy that requires a work item, stop and return this error:
     > "Failed to create pull request without a related work item. Multiple active work items found for this user; please specify the work item ID explicitly, e.g. `123 #create-pr`."
3. If **none** are found: set `workItemId` to null and proceed without a linked work item.

---

# Step 4 — Gather local changes

Call MCP tool: `get_local_files` with `scope` set to `baseBranch`.

This returns the list of changed files with classifications, statistics, and the current branch.

For a representative diff, call `get_local_file_diff(path, baseBranch)` for the highest-priority source files (up to 3–5 files). Skip binary, generated, and trivially changed files.

---

# Step 5 — Generate the PR title and description

## PR title

- If `workItemId` is available and work item metadata was retrieved:
  - Format: `[<workItemId>] <work item title>`
  - Example: `[123] Add feature X`
- Otherwise: derive a short, descriptive title from the changed files and diff summary.

## PR description

You are an assistant that prepares a short, clear pull request description.

### Inputs

You will receive:
- A work item or issue ID (from Azure DevOps or GitHub).
- Basic metadata for that work item/issue (such as title and description), when available.
- A summary or diff of the local code changes between the current branch and its base branch.

### Goal

Write a concise description of what was changed in this pull request, using the work item context and the local changes.

### Instructions

1. Read the work item/issue title and description to understand the main goal of the change.
2. Read the provided diff or summary of local changes.
3. Identify the most important functional changes from the diff, focusing on:
   - new features or behaviors,
   - bug fixes,
   - important refactors or configuration changes.
4. Write a short description of what was changed:
   - Prefer 2–5 short bullet points OR a brief paragraph (2–4 sentences).
   - Use clear, simple, professional language.
   - If helpful, you may mention the work item/issue ID in the text to keep the context clear.
5. At the end, add a single line that references the related work item/issue, for example:
   - `Related: #<ID>` (GitHub) or `Related: <ID>` (Azure DevOps)

### Output format

- Output only the pull request description text in Markdown.
- Do NOT include titles, headings, or any explanation of your reasoning.
- Do NOT include any additional commentary; only the final description that can be used directly as the PR body.

---

# Step 6 — Ask whether the PR should be a draft

Ask the user:

> "Should this pull request be created as a **draft**? (yes/no)"

- If the user answers **yes** (or any affirmative variant), set `isDraft = true`.
- If the user answers **no** (or any negative variant), set `isDraft = false`.
- If the session is non-interactive (e.g., running in a scripted context), default to `isDraft = false`.

---

# Step 7 — Create the pull request

## GitHub

```
gh pr create \
  --title "<PR title>" \
  --body "<PR description>" \
  --base <baseBranch> \
  --head <currentBranch>
```

If `isDraft` is `true`, add `--draft` to the command above.

## Azure DevOps

```
az repos pr create \
  --title "<PR title>" \
  --description "<PR description>" \
  --target-branch <baseBranch> \
  --source-branch <currentBranch>
```

If `isDraft` is `true`, add `--draft` to the command above.

After creating the PR on Azure DevOps, if `workItemId` is set, link the work item:

```
az repos pr work-item add --id <PR_ID> --work-item-id <workItemId>
```

For GitHub, the work item reference is already included in the PR body as `Related: #<workItemId>`.

---

# Step 8 — Output the result

**On success:**
- Print a short confirmation that includes the PR URL.
- Example: `Pull request created: https://github.com/org/repo/pull/42`

**On failure:**
- Return a clear, actionable error message that includes:
  - What failed (e.g., "PR creation failed", "Could not resolve work item").
  - What the user should do next (e.g., "Please specify a work item ID and try again: `123 #create-pr`").

---

# Behavior Rules

- Always confirm the current branch before creating the PR.
- Never create a PR from a branch into itself.
- If `currentBranch` equals `baseBranch`, stop and inform the user.
- Prefer fewer, higher-value findings in the PR description over an exhaustive list of every changed line.
- Do not invent work item metadata; use only what was retrieved from the API or CLI.
