# Code Review Task

You are reviewing a **unified diff** (not a complete file). The diff shows only changed
lines with a few lines of surrounding context. Unchanged code is NOT shown.

## Context Lines

Lines prefixed with `[ctx]` are **unchanged surrounding context** added by the
enrichment pipeline. They exist solely to orient you within the file — they are
NOT part of the change.

**Rules for `[ctx]` lines:**

- Do NOT report issues found only in `[ctx]` lines.
- Do NOT compare `[ctx]` lines against `+`/`-` lines to find contradictions — they
  may contain similar patterns (e.g., `Assert.True`) that operate on completely
  different values than the changed lines.
- Only use `[ctx]` lines to understand the surrounding structure (class, method,
  control flow).

## File Structure Annotations

Each C# file diff includes a `[file-structure: compiles=..., balanced-braces=...]`
annotation. This was verified by the Roslyn compiler against the full file source.

**When `compiles=yes` and `balanced-braces=yes`:** The file is structurally valid.
Do NOT report missing closing braces `}`, missing `return` statements, or other
structural incompleteness — the complete file compiles without syntax errors.
Any apparently missing structural elements exist in unchanged portions of the file
that are not shown in the diff.

**When any value is `unknown`:** The source code was unavailable for structural
validation (download timeout, file too large, or file missing from the archive).
Treat the file as structurally valid — do NOT report structural issues.

**Only report structural issues when the annotation says `compiles=no` or
`balanced-braces=no`.**

## Review Instructions

For each real issue found, emit it on its **own line** in exactly this format:

```
**[severity]** `file/path.cs` (line N): description
```

Format rules — the downstream tool parses findings with a strict regex, so deviations
drop findings silently:

- **No leading markers** before `**[severity]**`. Do not prefix the line with `- `,
  `* `, `1. `, or any indentation. The `**[` must be the first non-whitespace
  characters on the finding line.
- `severity` is exactly one of: `critical`, `major`, `minor` (lowercase preferred; case-insensitive).
- `file/path.cs` must be wrapped in single backticks — one path, no extra formatting
  around it (no `**` bold, no additional backticks inside).
- `(line N)` — **N is a single integer**. Do NOT write `(line ~138)`, `(line 100-150)`,
  `(line approx 100)`, `(line 100, 120)`, or any range/approximation. If you cannot
  cite a specific line, write `(line unknown)` instead.
- `description` is a single-line issue description. Multi-line elaboration / code
  fences / additional prose belongs on subsequent lines (they will be preserved).
- Separate findings with a blank line (or `---`) — each finding header must stand
  alone on its own line so the regex anchors match.

## Evidence Requirement — Ground Every Finding in the Diff

Only the changed portion of each file is shown. You have **no visibility** into code
outside the `+`, `-`, and `[ctx]` lines. Every finding MUST be anchored to code that is
literally present in the shown diff.

**Before emitting a finding, self-check:**

1. Every identifier you name in the description (field like `_foo`, method like `Bar()`,
   type like `Baz`, variable, test name) must appear **verbatim** in at least one `+`,
   `-`, or `[ctx]` line of the same file. If you cannot point to the exact line that
   contains it, do NOT emit the finding.
2. Do NOT claim a method "calls X", "uses Y", or "assigns to Z" based on inference from
   class/method names, `using` directives, or familiarity with similar code in other
   files. The file may not contain X, Y, or Z at all.
3. Do NOT describe code as doing the opposite of what the diff shows. If the diff shows
   `Assert.Contains(...)`, do not claim the code was "flipped to `Assert.DoesNotContain`".
   Read the actual characters on the line.
4. Findings that cross file boundaries (e.g. "this method should call X defined in
   OtherFile.cs") are allowed only when X is visible in the shown diff for one of the
   included files — otherwise they are unverifiable and will be filtered out.

If a suspected issue requires knowledge you do not have from the diff, either downgrade
to a `minor` risk framed around what the diff actually shows, or omit it.

Focus on: correctness, null safety, concurrency, async/await correctness, security,
error handling, performance, missing tests.

Ignore minor style issues unless they affect correctness.
If uncertain about a finding, label it as a "potential risk" rather than a definitive issue.
If no issues are found, state that explicitly.

---

{enrichedPageContent}
