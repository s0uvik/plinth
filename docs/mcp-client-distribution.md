# Install the add-in, connect any MCP client (plan)

Status: proposed 2026-11-27, to be approved and built later. Today a user installs Python, runs `install.ps1`,
starts the service by hand and edits a JSON config. The goal: a user installs the add-in, then connects any MCP
client (Claude Desktop, Cursor, VS Code, Windsurf, Claude Code, LM Studio...) to it. Sign-in, licences and the portal
are in [`distribution-and-sign-in.md`](distribution-and-sign-in.md).

## Recommended design: one installer that carries everything

```
┌───────────── User's PC ─────────────────────────────────────┐
│  MCP client (Claude Desktop, Cursor, VS Code, Claude Code…)  │
│        │  stdio                                              │
│        ▼                                                     │
│  plinth-mcp.exe  ← small launcher shipped with the add-in    │
│        │                                                     │
│        ▼                                                     │
│  Plinth engine (bundled Python, started automatically)       │
│        │  local pipe + local token                           │
│        ▼                                                     │
│  Revit + Plinth add-in  (signed in → licence checked)        │
└──────────────────────────────────────────────────────────────┘
```

What the user does:
1. Runs `Plinth-Setup.exe`. It installs the add-in for each Revit year it finds (2024–2027) and the Plinth engine
   with its own Python, so there is nothing else to install.
2. Opens Revit, goes to the Plinth tab and signs in through the browser. This is where the licence is checked.
3. Clicks **Connect an AI app** on the Plinth tab, which shows the settings to copy:
   - Claude Desktop: a one-click Claude Desktop extension, or a config snippet;
   - stdio clients (Cursor, VS Code, Windsurf, Claude Code, LM Studio): `{"command": "C:\\…\\plinth-mcp.exe"}`;
   - HTTP clients: `http://127.0.0.1:<port>/mcp`, protected by a local token.
4. Talks to the model from that app.

Why it works with any MCP client:
- Every desktop MCP client can launch a local program over stdio, the standard transport.
- The launcher starts the engine if it isn't running and finds the open Revit.
- No key goes in the client config: the launcher reads the PC's own sign-in token.

Also part of the design:
- **The review page moves into Revit:** a docked Plinth panel (WebView2) replaces the browser page for the import
  and fit-out reviews.
- **A compact tool set:** some clients limit how many tools they take, so expose about 25 main tools plus one
  "run any Revit command" tool with search, which covers all 267 add-in commands.
- **Licence enforced:** the add-in signs in, and the engine and launcher refuse to work without a valid licence.
- **Unchanged:** drawings and models stay on the user's PC; the user's own AI app does the reasoning, so there is no
  AI cost to Plinth; the ODA File Converter remains a separate free download, because its licence doesn't allow
  bundling.

## Alternatives not chosen (for now)

| Option | What it means | Why not now |
| --- | --- | --- |
| Engine rewritten in C# inside the add-in | Truly "add-in only" | Months of rewriting DWG reading, planning, fit-out etc. that already work in Python |
| Engine on a Plinth cloud server | Add-in + thin MCP locally, the brain on a server | Drawings and models leave the PC, which offices resist; hosting cost. Easier licence control and code protection: possible later |

Web-only clients (claude.ai in the browser, ChatGPT) can't reach a program on the user's PC. Supporting them would
need a small **Plinth relay** on a server: the add-in connects out to it, and the client connects to `https://…/mcp`
with a sign-in. Optional, later.

## Build steps (rough effort)

| Step | Work | Size |
| --- | --- | --- |
| 1 | Bundled engine, `plinth-mcp.exe` launcher, auto-start, local token, locked-down local service | ~1 week |
| 2 | Signed `Plinth-Setup.exe` for all Revit years (needs a purchased code-signing certificate) | ~3–4 days |
| 3 | "Connect an AI app" button, Claude Desktop extension, compact tool set | ~3–4 days |
| 4 | Review panel inside Revit, replacing the browser page | ~1–1.5 weeks |
| 5 | Sign-in, licence and portal (`docs/distribution-and-sign-in.md`) | ~2 weeks, plus hosting choice |

Steps 1–3 give "install the add-in, connect any MCP client"; steps 4–5 make it a product to sell.

Open decisions:
- approve the design (one installer, local engine, stdio launcher);
- build steps 1–3 first, or 1–5 together;
- whether web clients (claude.ai web, ChatGPT) are needed soon, which means the relay.
