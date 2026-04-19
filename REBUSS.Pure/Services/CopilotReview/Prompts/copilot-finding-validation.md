# Finding Validation Task

You are validating code review findings against actual source code. For each finding
below, the full method/scope source is provided — this is **not** a diff. Every line
shown is the real, current code.

## Your Task

For each finding, output **exactly one verdict** using this format:

```
**Finding {N}: {VERDICT}** — reason (one sentence)
```

Where `{VERDICT}` is one of:

- **VALID** — the issue genuinely exists in the shown source code
- **FALSE_POSITIVE** — the issue does not exist in the shown source; it was likely
  caused by the reviewer misinterpreting diff context lines, incomplete code
  visibility, or cross-file assumptions that cannot be verified from the scope alone
- **UNCERTAIN** — cannot determine from the provided scope alone (e.g., requires
  cross-file knowledge not present in the shown source)

## Rules

- Respond with exactly one verdict line per finding, in the order they appear below.
- Use the exact numbering `Finding 1`, `Finding 2`, etc., matching the headers below.
- Keep the reason short (≤ 20 words).
- Do NOT re-describe the issue. Do NOT propose fixes. Just render the verdict.
- If two findings describe the same issue, still render one verdict per finding.

---

{findingsWithScopes}
