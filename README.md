<p align="center">
  <img src="REBUSS.Pure.png" alt="REBUSS.Pure" />
</p>

# 🚀 REBUSS.Pure – AI Code Review That Focuses Only on What Matters

**Stop sending irrelevant code to AI.**  
Send only the *right context* — and understand Pull Requests faster.

---

## 💡 What is this?

`REBUSS.Pure` is a lightweight MCP server that enables AI agents (GitHub Copilot, ChatGPT, Claude) to perform **high-signal code reviews** by providing only the context that actually matters.

Instead of overwhelming the model with your entire repository, REBUSS.Pure:

- 🔍 analyzes **Azure DevOps and GitHub Pull Requests**
- 📄 provides **only relevant code changes**
- 🧠 enables **focused code review & self-review**
- ⚡ delivers **minimal, high-signal context**

---

## 🎯 Why this exists

Most AI workflows today:

- ❌ send too much code
- ❌ drown the model in noise
- ❌ produce generic, low-quality feedback

REBUSS.Pure changes the approach:

- ✅ sends **only relevant context**
- ✅ reduces noise, not just tokens
- ✅ helps AI focus on **what actually matters**

👉 built for **real-world code review**, not demos

---

## 🧠 Core idea

AI doesn’t need more code.  
It needs the *right* code.

Instead of:

```
❌ full repo → LLM
```

You get:

```
LLM → MCP → high-signal context only
```

---

## ✨ Key Features

- 🔹 Azure DevOps and GitHub Pull Request integration
- 🔹 High-signal, diff-based AI context
- 🔹 Local self-review (no network required)
- 🔹 No repo cloning needed
- 🔹 Incremental, on-demand data access
- 🔹 Ready-to-use review prompts
- 🔹 Works with any MCP-compatible agent
- 🔹 Authentication via Azure CLI, GitHub CLI (`gh auth`), or PAT
- 🔹 Auto-detects VS Code and Visual Studio
- 🔹 Auto-detects provider from Git remote URL

---

## 🔒 Security & Privacy

**Your source code never leaves your machine.**

REBUSS.Pure runs as a **local process** on your workstation. It does not upload, store, or relay your code to any external service. The MCP server acts as a controlled gateway between your AI agent and the data it actually needs:

- **Local processing only** — the server runs on `localhost`; no outbound code transmission occurs.
- **Minimal data exposure** — the AI model receives only **relevant context**, not the full repository.
- **Azure DevOps stays yours** — when fetching PR data, requests go directly to **your organization's** Azure DevOps APIs using **your credentials**. No intermediary services are involved.
- **GitHub stays yours** — GitHub API requests go directly to `api.github.com` using **your personal access token**. No intermediary services are involved.
- **Offline self-review** — local review (`#self-review`) operates entirely without network access. Git operations run against your local repository; nothing is sent anywhere.
- **No telemetry, no tracking** — the server collects zero usage data and phones home to nobody.

> **In short:** REBUSS.Pure gives AI agents *precise, scoped access* to exactly the context they need — and nothing more.

---

## 🆚 Compared to typical AI workflows

| Feature | REBUSS.Pure | Typical approach |
|---------|-------------|------------------|
| Context quality | High-signal | Noisy |
| Context size | Minimal | Huge |
| Token usage | Efficient | Wasteful |
| Setup | 1 command | Complex |
| Review quality | Focused | Generic |
| Data privacy | Code stays local | Full repo sent to AI |

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
- ✔ copy instruction files to `.github/instructions/` (for GitHub Copilot custom instructions)
- ✔ authenticate via Azure CLI (opens browser for login) or accept a GitHub PAT

### Global mode (`-g`)

If Visual Studio does not detect the local `.vs/mcp.json` file in your repository, use the global flag:

```bash
cd /path/to/your/repo
rebuss-pure init -g
```

This writes the MCP configuration to the **user-level** paths (`~/.vs/mcp.json` and `~/.vscode/mcp.json`) instead of the repository-local directories. The global config points `--repo` to the current repository, so it works for any workspace you open.

> **Switching between repositories:** If you use multiple repositories, run `rebuss-pure init -g` in the target repository before switching to it. This updates the global configuration to point to the correct workspace.

---

## 3. Review a Pull Request

In Copilot / AI chat:

```
123 #review-pr
```
or use `execute` to force tool invocation (recommended for smaller models that may not call tools autonomously):

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

By default, REBUSS.Pure ships with `GatewayMaxTokens` set to **128 000** in `appsettings.json`. This hard cap matches the context window limit imposed by **GitHub Copilot's proxy** and prevents `model_max_prompt_tokens_exceeded` errors — even when the model's native context window is larger (e.g. Claude's 200K).

**If you use Claude Code, Anthropic API directly, or any other client without a gateway limit**, remove or disable this cap so you can use the model's full context window:

In `appsettings.Local.json` (next to the server executable):

```json
{
  "ContextWindow": {
    "GatewayMaxTokens": null
  }
}
```

Or via environment variable:

```
ContextWindow__GatewayMaxTokens=0
```

When `GatewayMaxTokens` is `null` or `0`, the limit is disabled and the full model-native context window from the `ModelRegistry` is used.