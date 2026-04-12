# Code Review Task

You are reviewing a **unified diff** (not a complete file). The diff shows only changed
lines with a few lines of surrounding context. Unchanged code is NOT shown.

## File Structure Annotations

Each C# file diff may include a `[file-structure: compiles=yes, balanced-braces=yes]`
annotation. This was verified by the Roslyn compiler against the full file source.

**When `compiles=yes` and `balanced-braces=yes`:** The file is structurally valid.
Do NOT report missing closing braces `}`, missing `return` statements, or other
structural incompleteness — the complete file compiles without syntax errors.
Any apparently missing structural elements exist in unchanged portions of the file
that are not shown in the diff.

**Only report structural issues when the annotation says `compiles=no` or
`balanced-braces=no`, or when no `[file-structure]` annotation is present.**

## Review Instructions

For each real issue found:
- Classify severity: critical | major | minor
- Specify the file path
- Describe the issue concisely
- Suggest a fix if applicable

Focus on: correctness, null safety, concurrency, async/await correctness, security,
error handling, performance, missing tests.

Ignore minor style issues unless they affect correctness.
If uncertain about a finding, label it as a "potential risk" rather than a definitive issue.
If no issues are found, state that explicitly.

---

{enrichedPageContent}
