<p align="center">
  <img src="REBUSS.Pure.png" alt="REBUSS.Pure" />
</p>

# CodeReview.MCP

Context-aware code review engine designed to support Github Copilot on large, real-world pull requests.

> AI is not the bottleneck. Context is.

---

## What this is

CodeReview.MCP is a server that helps AI agents perform code review on large and messy repositories.

It does not compete with AI tools like Github Copilot or Copilot CLI.  
It acts as a support layer that prepares the problem for them.

---

## The problem

AI-assisted code review works well for small, clean diffs.

It breaks down when:

- pull requests are large
- repositories are complex or messy
- context does not fit into the model window
- too much input creates noise instead of signal
- important dependencies are missing

In real-world systems, this is the default case.

---

## What this project does differently

### 1. Designed for LARGE pull requests (and messy repositories)

Primary focus.

- handles pull requests that exceed context window limits
- works with multi-project and legacy-heavy repositories
- tolerates inconsistent structure
- does not assume clean architecture

Large and messy codebases are not edge cases — they are the baseline.

---

### 2. Offloads the hardest part from AI

This tool does the work AI is worst at:

- selecting relevant context
- structuring input
- reducing noise
- organizing review flow

Instead of forcing the model to "figure things out",  
CodeReview.MCP prepares the problem for it.

---

### 3. Context selection (not full dump)

- sends only relevant fragments
- expands context when needed
- avoids redundancy

---

### 4. Token-aware processing

- respects context limits
- splits and paginates intelligently
- keeps usage predictable

---

### 5. Deterministic review flow

- metadata → file list → targeted analysis
- avoids random exploration
- enables repeatable results

---

## What this is NOT

- not an AI model
- not a replacement for Copilot
- not a one-click review tool

This is a control layer for AI agents.

---

## Core idea

Better context → better reasoning → better review

---

## 🌐 Language Support

High-signal context enrichment (scope detection, surrounding usings, call sites, language-aware classification) is currently implemented for **C#** only. Pull requests and local changes in other languages are still fully supported, but the AI agent receives them as **plain unified diffs** without language-specific enrichment.

Planned (near-term):

- C / C++
- TypeScript / JavaScript
- F#

Contributions adding analyzers for additional languages are welcome — see `.github/ExtensionRecipes.md`.

---

## 🔒 Security

CodeReview.MCP acts as a local orchestrator for GitHub Copilot. It runs as a local process on your workstation. 

It does not introduce any external services or additional data flows.  
All AI interactions are handled exclusively through GitHub Copilot, under the license and policies already approved by your organization.

The MCP server runs locally and communicates only with:
- your Git provider (Azure DevOps or GitHub)
- GitHub Copilot (via your existing setup)

No code or data is sent to any third-party services beyond what is already used by Copilot.

---

# ⚡ Quick Start

## 1. Install

### Option A — .NET global tool (recommended)

```bash
dotnet tool install -g CodeReview.MCP
```

### Option B - PowerShell

```powershell
irm https://raw.githubusercontent.com/rebuss/CodeReview.MCP/master/install.ps1 | iex
```

### Option C - Bash

```bash
curl -fsSL https://raw.githubusercontent.com/rebuss/CodeReview.MCP/master/install.sh | bash
```

---

## 2. Initialize in your repo

```bash
cd /path/to/your/repo
rebuss-pure init
```

This will:

- ✔ detect your IDE (VS Code → `.vscode/mcp.json`, Visual Studio → `.vs/mcp.json`)
- ✔ generate MCP server configuration
- ✔ copy review prompts to `.github/prompts/`
- ✔ authenticate via Azure CLI (opens browser for login) or accept a GitHub PAT
- ✔ **optionally** set up GitHub Copilot CLI (`gh copilot` extension) for the enhanced, summarization-resilient review flow (declining is safe — existing tools still work)

When `gh copilot` is available and the `CopilotReview.Enabled` switch is on, the MCP server performs large-PR reviews server-side via the Copilot SDK: each page of enriched diff is reviewed in parallel by Copilot, and the IDE agent receives compact review summaries to organize by severity — instead of walking raw diff content page by page through the conversation window.

**Throttling & parallelism** — two knobs control how the MCP server drains a multi-page review against the Copilot rate limit:

- `MaxConcurrentPages` — how many review pages run in parallel per batch (default `6`).
- `MinRequestIntervalSeconds` — minimum spacing between outbound Copilot SDK calls, in seconds (default `3`). Set to `0` to disable (tests only).

Set them in the `env` block of your **`mcp.json`** — the same file `rebuss-pure init` generated (typically `.vscode/mcp.json`, `.vs/mcp.json`, or `~/.copilot/mcp-config.json`). The MCP client passes `env` to the server process on launch, and REBUSS.Pure reads `CopilotReview:*` keys from environment variables using the standard `__` (double-underscore) separator:

```json
{
  "servers": {
    "REBUSS.Pure": {
      "type": "stdio",
      "command": "rebuss-pure",
      "args": ["--repo", "C:\\path\\to\\repo"],
      "env": {
        "CopilotReview__MaxConcurrentPages": "4",
        "CopilotReview__MinRequestIntervalSeconds": "2"
      }
    }
  }
}
```

Restart the MCP server (reload the IDE, or re-open the chat session) so the new `env` values are picked up.

See [DeveloperGuide.md — Copilot Review Layer](DeveloperGuide.md#copilot-review-layer-feature-013) for the full config table, the alternative `appsettings.json` location, and tuning guidance.

### Global mode (`-g`)

If Visual Studio does not detect the local `.vs/mcp.json` file in your repository, use the global flag:

```bash
cd /path/to/your/repo
rebuss-pure init -g
```

This writes the MCP configuration to the **user-level** paths (`~/.vs/mcp.json` and `~/.vscode/mcp.json`) instead of the repository-local directories. The global config points `--repo` to the current repository, so it works for any workspace you open.

> **Switching between repositories:** If you use multiple repositories, run `rebuss-pure init -g` in the target repository before switching to it. This updates the global configuration to point to the correct workspace.

> **After updating the tool:** If you run `dotnet tool update -g CodeReview.MCP`, run `rebuss-pure init` again afterwards to refresh the prompt files and configurations to the latest version.

---

## 3. Review a Pull Request

In Copilot / AI chat:

```
123 #review-pr
```
or use `execute` to force tool invocation:

```
execute 123 #review-pr
```

Where `123` is the Azure DevOps or GitHub Pull Request number.

---

## 4. Self-review local changes

```
#self-review
```

Works **offline** — no Azure DevOps connection required.

---

## ⚙️ Gateway Token Limit

**Override (only if you need a different value):** set `ContextWindow:GatewayMaxTokens` in `appsettings.Local.json` next to the server executable:

```json
{
  "ContextWindow": {
    "GatewayMaxTokens": 50000
  }
}
```

Or via environment variable: `ContextWindow__GatewayMaxTokens=50000`.

When you set this value explicitly, autodetection is skipped — your value wins. Set it to `0` or `null` to disable the cap entirely (use only if your host has no per-response token limit). Per-call `maxTokens` arguments to the MCP tools also bypass the cap.
