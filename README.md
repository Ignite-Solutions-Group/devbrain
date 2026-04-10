# DevBrain

A remote MCP server that gives any AI tool persistent, shared access to developer knowledge — one brain, zero upload tax.

## The Problem

Every AI tool starts from zero. You paste the same sprint doc into Claude, copy architecture notes into Copilot, re-explain project state to Cursor. Each tool is an island. Your knowledge lives in scattered markdown files, and every conversation begins with a 6,000-character upload ritual.

DevBrain eliminates this. Deploy once, point any MCP client at the endpoint, and every AI tool you use shares the same persistent knowledge store.

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

## Tools Reference

All tools accept an optional `project` parameter (defaults to `"default"`) to isolate documents by project.

| Tool | Inputs | Purpose |
|------|--------|---------|
| `UpsertDocument` | `key` (required), `content` (required), `tags`, `project` | Create or replace a document by key |
| `GetDocument` | `key` (required), `project` | Retrieve a document by key |
| `ListDocuments` | `prefix`, `project` | List document keys, optionally filtered by prefix |
| `SearchDocuments` | `query` (required), `project` | Substring search across keys and content |

## Key Conventions

Documents are organized by key prefix. These conventions are recommended but not enforced:

| Prefix | Use |
|--------|-----|
| `sprint/{name}` | Sprint specs, e.g. `sprint/license-sync` |
| `state/current` | Current project state document |
| `arch/{name}` | Architecture docs |
| `decision/{name}` | Architecture decision records |
| `ref/{name}` | Reference material, infra constants |

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
| Claude Code CLI | `x-functions-key` header | Working |
| VS Code / GitHub Copilot | `x-functions-key` header | Working |
| Cursor | `x-functions-key` header | Working |
| Claude Desktop | `x-functions-key` via `mcp-remote` proxy | Working (with workaround) |
| Claude Web / Mobile | OAuth (DCR + `resource` param) | Blocked — both issues |
| ChatGPT | OAuth (DCR required) | Blocked — DCR not supported |
| GitHub Copilot Chat (web) | OAuth | Blocked |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
