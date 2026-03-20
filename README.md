# 🚀 REBUSS.Pure – Token-Efficient AI Code Review for Azure DevOps

**Stop sending entire repositories to AI.**  
Use only what matters: **diff + changed files from your Pull Request.**

---

## 💡 What is this?

`REBUSS.Pure` is a lightweight MCP server that enables AI agents (GitHub Copilot, ChatGPT, Claude):

- 🔍 analyze **Azure DevOps Pull Requests**
- 📄 access **only changed files**
- 🧠 perform **code review & self-review**
- ⚡ use **minimal tokens (no full repo scan)**

---

## 🎯 Why this exists

Typical AI workflows:

- ❌ load entire repo
- ❌ waste tokens
- ❌ produce noisy results

This tool:

- ✅ works on **diff only**
- ✅ loads files **on demand**
- ✅ enables **incremental AI reasoning**

👉 designed for **real-world large repositories**

---

## 🧠 Core idea

Instead of sending everything to the model:

```
❌ full repo → LLM
```

You get:

```
LLM → MCP → only needed data
```

---

## ✨ Key Features

- 🔹 Azure DevOps Pull Request integration
- 🔹 Diff-based AI context (token efficient)
- 🔹 Local self-review (no network required)
- 🔹 No repo cloning needed
- 🔹 Incremental data access
- 🔹 Ready-to-use review prompts
- 🔹 Works with any MCP-compatible agent
- 🔹 Authentication via Azure CLI or PAT
- 🔹 Auto-detects VS Code and Visual Studio

---

## 🔒 Security & Privacy

**Your source code never leaves your machine.**

REBUSS.Pure runs as a **local process** on your workstation. It does not upload, store, or relay your code to any external service. The MCP server acts as a controlled gateway between your AI agent and the data it actually needs:

- **Local processing only** — the server runs on `localhost`; no outbound code transmission occurs.
- **Minimal data exposure** — the AI model receives only **diffs and metadata**, never the full repository.
- **Azure DevOps stays yours** — when fetching PR data, requests go directly to **your organization's** Azure DevOps APIs using **your credentials**. No intermediary services are involved.
- **Offline self-review** — local review (`#self-review`) operates entirely without network access. Git operations run against your local repository; nothing is sent anywhere.
- **No telemetry, no tracking** — the server collects zero usage data and phones home to nobody.

> **In short:** REBUSS.Pure gives AI agents *read-only, scoped access* to exactly the context they need — and nothing more. Your intellectual property stays where it belongs.

---

## 🆚 Compared to typical AI workflows

| Feature | REBUSS.Pure | Typical approach |
|---------|-------------|------------------|
| Context size | Minimal | Huge |
| Token usage | Low | High |
| Setup | 1 command | Complex |
| Signal quality | High | Noisy |
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
- ✔ authenticate via Azure CLI (opens browser for login)

---

## 3. Review a Pull Request

In Copilot / AI chat:

```
123 #review-pr
```

Where `123` is the Azure DevOps Pull Request ID.

---

## 4. Self-review local changes

```
#self-review
```

Works **offline** — no Azure DevOps connection required.

---