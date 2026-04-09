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
              │ Azure Functions │ ← Entra ID auth
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
- Microsoft Entra ID tenant
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Deploy in 5 Minutes

### 1. Register the Entra app

```bash
# Create the app registration
az ad app create --display-name "DevBrain" --sign-in-audience AzureADMyOrg

# Note the appId from the output — you'll need it below
# Expose an API scope
az ad app update --id <APP_ID> \
  --identifier-uris "api://<APP_ID>" \
  --set "api.oauth2PermissionScopes=[{\"id\":\"$(uuidgen)\",\"adminConsentDescription\":\"Read and write documents\",\"adminConsentDisplayName\":\"documents.readwrite\",\"isEnabled\":true,\"type\":\"User\",\"value\":\"documents.readwrite\"}]"
```

### 2. Deploy with azd

```bash
azd init -t Ignite-Solutions-Group/devbrain
azd env set ENTRA_CLIENT_ID <APP_ID>
azd up
```

### 3. Update the app registration redirect URI

```bash
# Get the function URL from azd output
az ad app update --id <APP_ID> \
  --web-redirect-uris "https://<FUNCTION_URL>/.auth/login/aad/callback"
```

## Configure Your MCP Client

### Claude Code CLI (`.claude/mcp.json`)

```json
{
  "mcpServers": {
    "devbrain": {
      "type": "url",
      "url": "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse"
    }
  }
}
```

### Claude Desktop (`claude_desktop_config.json`)

```json
{
  "mcpServers": {
    "devbrain": {
      "type": "url",
      "url": "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse"
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
      "url": "https://<FUNCTION_URL>/runtime/webhooks/mcp/sse"
    }
  }
}
```

## Tools Reference

| Tool | Inputs | Purpose |
|------|--------|---------|
| `UpsertDocument` | `key` (required), `content` (required), `tags` (optional) | Create or replace a document by key |
| `GetDocument` | `key` (required) | Retrieve a document by key |
| `ListDocuments` | `prefix` (optional) | List document keys, optionally filtered by prefix |
| `SearchDocuments` | `query` (required) | Substring search across keys and content |

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
   ```bash
   az login
   ```

3. Copy and configure local settings:
   ```bash
   cp src/DevBrain.Functions/local.settings.json.example src/DevBrain.Functions/local.settings.json
   # Edit with your Cosmos DB account endpoint
   ```

4. Run:
   ```bash
   cd src/DevBrain.Functions
   func start
   ```

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
