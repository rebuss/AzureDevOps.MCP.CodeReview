# REBUSS.Pure Development Guidelines

Auto-generated from all feature plans. Last updated: 2026-04-14

## Active Technologies
- C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>` + Microsoft.Extensions.Logging, Microsoft.Extensions.Http (Polly via `AddStandardResilienceHandler`) (014-log-noise-reduction)
- N/A (in-memory `ConcurrentDictionary` only — no persistent storage changes) (014-log-noise-reduction)
- C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>` + `GitHubApiClient` (HTTP via `IHttpClientFactory`), `GitHubDiffProvider`, `CopilotClientProvider` (Copilot SDK), `CopilotReviewOptions` (015-api-call-optimization)
- N/A (in-memory `ConcurrentDictionary` cache only) (015-api-call-optimization)

- C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>` + None new. Reuses existing `System.Diagnostics.Process`, `Microsoft.Extensions.Logging`, and `REBUSS.Pure.GitHub.Configuration.GitHubCliProcessHelper`. External runtime dependency on `gh` CLI and the `github/gh-copilot` GitHub CLI extension (installed on demand, never bundled). (012-copilot-cli-setup)

## Project Structure

```text
src/
tests/
```

## Commands

# Add commands for C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>`

## Code Style

C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>`: Follow standard conventions

## Recent Changes
- 020-remove-content-only: Removed legacy content-only review fallback — Copilot SDK is now the sole review mechanism. `FormatContentOnlyModeHeader()` deleted, prompts simplified, `IPageAllocator` removed from content handlers, unavailable Copilot returns error with remediation
- 019-unified-self-review: Unified self-review pipeline — `IEnrichmentResult` interface, `LocalEnrichmentOrchestrator`, `CopilotReviewWaiter`, generalized `CopilotReviewOrchestrator` (string keys), rewritten `GetLocalContentToolHandler` with Copilot SDK + progress notifications + mode headers
- 015-api-call-optimization: Added C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>` + `GitHubApiClient` (HTTP via `IHttpClientFactory`), `GitHubDiffProvider`, `CopilotClientProvider` (Copilot SDK), `CopilotReviewOptions`
- 014-log-noise-reduction: Added C# 14 on .NET 10 (`net10.0`), `<Nullable>enable</Nullable>` + Microsoft.Extensions.Logging, Microsoft.Extensions.Http (Polly via `AddStandardResilienceHandler`)
- 013-copilot-review-layer: Added [if applicable, e.g., PostgreSQL, CoreData, files or N/A]


<!-- MANUAL ADDITIONS START -->
<!-- MANUAL ADDITIONS END -->
