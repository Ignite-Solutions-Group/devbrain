# DevBrain

An Azure-native remote MCP server — built on Azure Functions and Cosmos DB — that gives any AI tool persistent, shared access to developer knowledge. One brain, zero upload tax, for teams already living in the Microsoft Azure dev ecosystem.

## The Problem

Every AI tool starts from zero. You paste the same sprint doc into Claude, copy architecture notes into Copilot, re-explain project state to Cursor. Each tool is an island. Your knowledge lives in scattered markdown files, and every conversation begins with a 6,000-character upload ritual.

DevBrain eliminates this. Deploy once, point any MCP client at the endpoint, and every AI tool you use shares the same persistent knowledge store.

## Why DevBrain over alternatives

**One instance. Every project. Any AI tool.**

Deploy DevBrain once and every project you work on shares the same knowledge store. Load context from multiple projects in a single session — no workspace switching, no separate deployments, no file uploads.

```
# Morning session — three projects, three tool calls
GetDocument(key="state:current", project="acme-platform")
GetDocument(key="state:current", project="devbrain")
GetDocument(key="state:current", project="client-abc")
```

Compare that to alternatives:

- **Serena** — per-repo MCP server, requires workspace switching between projects
- **Claude Project Knowledge** — manual file uploads, single project scope, resets between sessions
- **Local markdown files** — not shared across AI tools, no persistence

DevBrain is the only approach that gives every AI tool (Claude, Copilot, Codex, Cursor) shared persistent access across all your projects from a single deployed endpoint.

## How It Works

```
┌──────────────────┐     ┌──────────────────┐     ┌──────────────────┐
│  Claude Code CLI │     │  Claude Desktop   │     │  VS Code/Copilot │
└────────┬─────────┘     └────────┬──────────┘     └────────┬─────────┘
         │                        │                          │
         └────────────┬───────────┴──────────────────────────┘
                      │  MCP (Streamable HTTP)
              ┌───────▼────────┐
              │ Azure Functions │ ← function key auth
              │   (DevBrain)   │
              └───────┬────────┘
                      │  Managed Identity
              ┌───────▼────────┐
              │   Cosmos DB    │
              │   (NoSQL)      │
              └────────────────┘
```

## Prerequisites

- Azure subscription
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Deploy in 5 Minutes

```powershell
azd init -t Ignite-Solutions-Group/devbrain
azd up
```

After deployment, retrieve your MCP extension system key from the Azure Portal (**Function App > App keys > System keys > `mcp_extension`**) and use it in your client configuration below.

## First Run

After a fresh `azd up`, seed the default reference documents so any AI tool connecting to your new instance immediately has usage guidance available:

```powershell
$env:DEVBRAIN_KEY = '<YOUR_FUNCTION_KEY>'
./scripts/seed-devbrain.ps1
```

The script reads the Function App URL from the active azd environment (`AZURE_FUNCTION_URL`) and the MCP key from `$env:DEVBRAIN_KEY`, prompting for either if not set. It calls the DevBrain MCP endpoint directly and upserts a baseline set of documents (currently `ref:devbrain-usage` in the `default` project). Re-running is safe — every upsert is a full overwrite. Source content for the seed lives under [`docs/seed/`](docs/seed/).

## Configure Your MCP Client

Authentication uses the Azure Functions MCP extension system key, passed via the `x-functions-key` header. See [Authentication](#authentication) for details.

### Claude Code CLI (`.claude/mcp.json`)

```json
{
  "mcpServers": {
    "devbrain": {
      "type": "url",
      "url": "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse",
      "headers": {
        "x-functions-key": "<YOUR_FUNCTION_KEY>"
      }
    }
  }
}
```

### Claude Desktop (`claude_desktop_config.json`)

Claude Desktop doesn't support remote MCP auth natively, so use [`mcp-remote`](https://www.npmjs.com/package/mcp-remote) as a stdio-to-SSE proxy:

```json
{
  "mcpServers": {
    "devbrain": {
      "command": "npx",
      "args": [
        "mcp-remote",
        "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse",
        "--header",
        "x-functions-key:${DEVBRAIN_KEY}"
      ],
      "env": {
        "DEVBRAIN_KEY": "<YOUR_FUNCTION_KEY>"
      }
    }
  }
}
```

### VS Code / GitHub Copilot (`.vscode/mcp.json`)

```json
{
  "servers": {
    "devbrain": {
      "type": "http",
      "url": "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse",
      "headers": {
        "x-functions-key": "<YOUR_FUNCTION_KEY>"
      }
    }
  }
}
```

### Cursor

Add via Cursor's MCP settings with the same URL and `x-functions-key` header.

## Session Startup / AGENTS.md

DevBrain is only as useful as the context your AI tools actually load. The recommended pattern is a small `AGENTS.md` file at the repo root that tells any AI tool how to pull context from DevBrain at the start of a session.

**Why AGENTS.md:** GitHub Copilot, Cursor, and Codex all read `AGENTS.md`. Claude Code reads `CLAUDE.md` but can `@import` other files — so a one-line `@AGENTS.md` in `CLAUDE.md` keeps a single source of truth across every tool.

### `AGENTS.md`

```markdown
## DevBrain Session Startup

At the start of every session, load project context from DevBrain:

1. GetDocument(key="state:current", project="{your-project}")
2. If a sprint is active: GetDocument(key="sprint:{sprint-name}", project="{your-project}")

Before ending a session, write back any significant changes:
- UpsertDocument key="state:current" if project state changed
- UpsertDocument key="sprint:{name}" if sprint progress changed

DevBrain is the canonical source of truth. Do not ask the user to upload
files or paste context — read it directly from DevBrain.
```

### `CLAUDE.md`

```markdown
@AGENTS.md
```

New to a project? See [docs/project-init.md](docs/project-init.md) for the recommended first documents to seed.

## Tools Reference

All tools accept an optional `project` parameter (defaults to `"default"`) to isolate documents by project.

| Tool | Inputs | Purpose |
|------|--------|---------|
| `UpsertDocument` | `key` (required), `content` (required), `tags`, `project` | Create or replace a document by key |
| `AppendDocument` | `key` (required), `content` (required), `separator`, `tags`, `project` | Append content to an existing document (or create it). Server-side concatenation; tag union. |
| `UpsertDocumentChunked` | `key` (required), `content` (required), `chunkIndex` (required), `totalChunks` (required), `tags`, `project` | Upload a document in multiple chunks when it is too large to emit in a single LLM turn. |
| `GetDocument` | `key` (required), `project` | Retrieve a document by key |
| `ListDocuments` | `prefix`, `project` | List document keys, optionally filtered by prefix |
| `SearchDocuments` | `query` (required), `project` | Substring search across keys and content |
| `DeleteDocument` | `key` (required), `project` | Delete a document by key. Idempotent on missing keys. |

### When to use Append vs Chunked

Both tools exist to work around the LLM-client per-turn output budget, but they solve different problems:

- **`AppendDocument`** — for **growing logs** (session history, decision logs, audit trails). Each call adds a short entry to a document whose existing body the caller doesn't need to re-emit. Concurrent appenders are serialized via Cosmos ETag concurrency with bounded retry.
- **`UpsertDocumentChunked`** — for **a single document that's too big to emit atomically**. Callers split the content across calls with `(chunkIndex, totalChunks)`; chunks may arrive out of order. The server concatenates on the final chunk and upserts the real key in one step. Abandoned uploads expire automatically.

Pick Append when the doc grows over time. Pick Chunked when you already have the whole thing and just can't fit it in one call.

## Key Conventions

Documents are organized by key prefix. These conventions are recommended but not enforced:

Keys use colon as the separator (e.g. `sprint:license-sync`). **Writes** (`UpsertDocument`, `AppendDocument`, `UpsertDocumentChunked`) reject keys containing `/` with a "did you mean" error suggesting the colon form. **Reads** (`GetDocument`, `ListDocuments`, `SearchDocuments`) and `DeleteDocument` continue to accept slash keys so legacy data and cleanup operations keep working.

| Prefix | Use |
|--------|-----|
| `sprint:{name}` | Sprint specs, e.g. `sprint:license-sync` |
| `state:current` | Current project state document |
| `arch:{name}` | Architecture docs |
| `decision:{name}` | Architecture decision records |
| `ref:{name}` | Reference material, infra constants |

## Local Development

1. Install prerequisites: .NET 10 SDK, [Azure Functions Core Tools v4](https://learn.microsoft.com/azure/azure-functions/functions-run-local), Azure CLI.

2. Log in to Azure (for Cosmos access via `DefaultAzureCredential`):
   ```powershell
   az login
   ```

3. Copy and configure local settings:
   ```powershell
   Copy-Item src/DevBrain.Functions/local.settings.json.example src/DevBrain.Functions/local.settings.json
   # Edit with your Cosmos DB account endpoint
   ```

4. Run:
   ```powershell
   cd src/DevBrain.Functions
   func start
   ```

## Authentication

DevBrain uses the Azure Functions MCP extension system key (`x-functions-key` header) for authentication. Easy Auth (Entra ID OAuth) was disabled due to upstream OAuth compatibility issues with MCP clients (see below).

The system key is auto-generated by the MCP extension and can be retrieved from the Azure Portal under **Function App > App keys > System keys > `mcp_extension`**.

## Known Limitations

### Microsoft Entra ID + MCP OAuth — ecosystem-wide incompatibility

DevBrain was originally designed to use Entra ID Easy Auth for OAuth-based clients. Two Entra enforcement issues block all web-based MCP clients:

1. **`AADSTS9010010` — `resource` parameter rejected.** Since March 2026, Entra's v2 endpoint rejects OAuth requests that include both `scope` and `resource` parameters. Multiple MCP clients send `resource` by default (Claude, GitHub Copilot CLI). This is not client-specific — it affects any MCP client hitting an Entra-protected endpoint.

2. **Dynamic Client Registration (DCR) not supported.** Web-based MCP clients (Claude web/mobile, ChatGPT) require DCR to initiate OAuth flows. Entra ID does not support DCR. There is no server-side workaround.

These are upstream issues between Entra and the MCP OAuth spec. Easy Auth was disabled in favor of function key auth to unblock header-capable clients.

**Status:** Blocked — requires changes in Microsoft Entra ID, MCP client OAuth implementations, or both.

### Client compatibility

| Client | Auth | Status |
|--------|------|--------|
| Claude Code CLI (Windows) | `x-functions-key` header | Working |
| Claude Desktop (Windows) | `x-functions-key` via `mcp-remote` proxy | Working (with workaround) |
| VS Code / GitHub Copilot (Windows) | `x-functions-key` header | Working |
| Codex App (Windows) | `x-functions-key` header | Working |
| Cursor | `x-functions-key` header | Expected to work (not tested) |
| Claude Web / Mobile | OAuth (DCR + `resource` param) | Blocked — both issues |
| ChatGPT | OAuth (DCR required) | Blocked — DCR not supported |
| GitHub Copilot Chat (web) | OAuth | Blocked |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
