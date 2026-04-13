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
┌──────────────────┐   ┌──────────────────┐   ┌──────────────────┐
│ Claude Code CLI  │   │  Claude Desktop  │   │  Codex / Others  │
└─────────┬────────┘   └─────────┬────────┘   └─────────┬────────┘
          │                      │                      │
          └──────────────────────┼──────────────────────┘
                                 │  MCP (Streamable HTTP + OAuth 2.0)
                        ┌────────▼─────────┐
                        │ Azure Functions  │ ← DCR OAuth facade
                        │    (DevBrain)    │   (Entra-backed)
                        └────────┬─────────┘
                                 │  Managed Identity
                        ┌────────▼─────────┐
                        │    Cosmos DB     │
                        │     (NoSQL)      │
                        └──────────────────┘
```

## Prerequisites

- Azure subscription
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Deploy

```powershell
azd init -t Ignite-Solutions-Group/devbrain
azd env set ENTRA_TENANT_ID <your-tenant-guid>
azd env set ENTRA_CLIENT_ID <your-entra-app-client-id>
azd up
```

**Before `azd up`**, create a single Entra app registration in your tenant (see CHANGELOG v1.6.0 for the full prerequisite checklist). After deployment, populate the two Key Vault secrets:

```powershell
az keyvault secret set --vault-name <kv-name> --name jwt-signing-secret --value $(openssl rand -base64 32)
az keyvault secret set --vault-name <kv-name> --name entra-client-secret --value <secret-from-entra-app>
```

Restart the Function App to pick up the Key Vault references, then connect any MCP client.

## First Run

After a fresh deployment, seed the default reference documents so any AI tool connecting to your new instance immediately has usage guidance available. Connect any authenticated MCP client and call:

```
UpsertDocument(key="ref:devbrain-usage", project="default", content=<contents of docs/seed/ref-devbrain-usage.md>)
```

Or use the seed script (requires a valid MCP connection):

```powershell
./scripts/seed-devbrain.ps1
```

Re-running is safe — every upsert is a full overwrite. Source content for the seed lives under [`docs/seed/`](docs/seed/).

## Configure Your MCP Client

DevBrain uses OAuth 2.0 with Dynamic Client Registration (DCR). Clients that support the MCP OAuth spec connect with just a URL — no API keys, no manual configuration, no local proxies. The server handles registration, authorization, and token exchange automatically via the built-in DCR facade backed by your Entra tenant.

### Claude Code CLI

```bash
claude mcp add devbrain --transport http https://<FUNCTION_URL>/runtime/webhooks/mcp
```

On first use, Claude Code opens a browser for Entra login. Subsequent sessions re-use the stored token.

### Claude Desktop / Claude Mobile / Claude.ai Web

Add as a custom MCP connector pointing at:

```
https://<FUNCTION_URL>/runtime/webhooks/mcp
```

OAuth completes automatically — no proxy, no function key, no manual headers.

### Codex (Windows App / CLI)

```bash
codex mcp add devbrain --transport http https://<FUNCTION_URL>/runtime/webhooks/mcp
```

### VS Code / GitHub Copilot

⚠️ **Known issue:** The VS Code MCP extension connects successfully and discovers all tools, but does not trigger the OAuth flow. See [Known Limitations](#vs-code--github-copilot-mcp-extension--oauth-not-triggered) below for the full explanation and fix paths.

### Cursor

Not yet tested with v1.6 OAuth. Expected to work if the client supports MCP OAuth with DCR.

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
| `GetDocumentMetadata` | `key` (required), `project` | Retrieve document metadata (tags, timestamps, contentHash, contentLength) without the content body |
| `CompareDocument` | `key` (required), `content` or `contentHash` (one required), `project` | Check whether candidate content matches a stored document by SHA-256 hash |
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

DevBrain implements RFC 7591 Dynamic Client Registration (DCR) with an in-process OAuth proxy that brokers a single pre-registered Entra app. From the client's perspective, DevBrain *is* the authorization server. Internally it delegates to your tenant's Entra ID for user authentication.

This solves two problems that previously blocked MCP OAuth:

1. **Entra doesn't support DCR** — DevBrain's facade implements it, issuing opaque `client_id` handles that all map to the same upstream Entra app.
2. **Claude.ai ignores external IdP endpoints in discovery metadata** — DevBrain hosts its own `/.well-known/oauth-authorization-server` and `/.well-known/oauth-protected-resource` on its own domain.

Every write operation records the authenticated user's Entra UPN in the `updatedBy` field.

## Known Limitations

### VS Code / GitHub Copilot MCP extension — OAuth not triggered

VS Code connects to the MCP endpoint, gets a 200 OK on `tools/list`, discovers all 7 tools, and proceeds as if no auth is required. Tool calls then fail with a missing Bearer token. **VS Code's behavior is correct per the MCP authorization spec** — the spec requires the server to challenge unauthenticated requests with `401 + WWW-Authenticate: Bearer resource_metadata="..."`, at which point the client reads PRM and starts OAuth.

**Why DevBrain returns 200 here:** `initialize` and `tools/list` are handled by the Azure Functions MCP extension at the host process layer and never dispatch a function, so DevBrain's JWT middleware (which runs in the isolated worker) never sees them. The extension assumes Microsoft's documented deployment pattern — App Service Auth in front of the extension, owning the 401 challenge. DevBrain can't use that pattern because enabling App Service Auth with Entra would make the PRM advertise `login.microsoftonline.com` as the authorization server, which Claude.ai web ignores ([anthropics/claude-ai-mcp#82](https://github.com/anthropics/claude-ai-mcp/issues/82)), breaking a client that currently works.

Other clients work because they probe PRM proactively rather than waiting to be challenged. VS Code follows the spec strictly.

**Workaround:** None currently.

**Fix paths (future DevBrain versions):**

1. File a feature request against [`Azure/azure-functions-mcp-extension`](https://github.com/Azure/azure-functions-mcp-extension) for a pluggable auth hook at the host layer so custom OAuth servers can gate the MCP protocol surface.
2. Replace the extension's webhook handler with a custom anonymous HTTP trigger implementing `initialize`, `tools/list`, and `tools/call` directly, under DevBrain's JWT middleware.

### Client compatibility (v1.6.0)

| Client | Platform | Auth | Status |
|--------|----------|------|--------|
| Claude Code CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Claude Code CLI | WSL | OAuth (DCR) | ✅ Working |
| Claude Code | claude.ai web | OAuth (DCR) | ✅ Working |
| Claude Desktop | Windows | OAuth (DCR) | ✅ Working |
| Claude Mobile | Android | OAuth (DCR) | ✅ Working |
| Codex App | Windows | OAuth (DCR) | ✅ Working |
| Codex CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Codex CLI | WSL | OAuth (DCR) | ✅ Working |
| VS Code / GitHub Copilot | Windows | OAuth (DCR) | ⚠️ [See above](#vs-code--github-copilot-mcp-extension--oauth-not-triggered) |
| ChatGPT | — | — | ❌ MCP not supported |
| Cursor | — | OAuth (DCR) | Not tested |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
