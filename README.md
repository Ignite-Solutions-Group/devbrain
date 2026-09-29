# DevBrain

An Azure-native remote MCP server built on ASP.NET Core, Azure Container Apps, and Cosmos DB. DevBrain gives AI tools persistent, shared access to developer knowledge across projects and clients.

DevBrain 2.0 uses the official [Model Context Protocol (MCP) C# SDK](https://github.com/modelcontextprotocol/csharp-sdk) and implements the [2026-07-28 revision of the MCP specification](https://modelcontextprotocol.io/specification/2026-07-28). The protocol revision is intentionally pinned here: informal references to “MCP v2” can otherwise be confused with the C# SDK’s own 2.x package version.

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
                        │ Container Apps   │ ← ASP.NET Core + MCP SDK
                        │  (DevBrain 2.0)  │   OAuth facade (Entra-backed)
                        └────────┬─────────┘
                                 │  Managed Identity
                        ┌────────▼─────────┐
                        │    Cosmos DB     │
                        │     (NoSQL)      │
                        └──────────────────┘
```

### Hosting defaults

The MCP transport is stateless, so requests do not require session affinity or a distributed protocol-state cache. The template therefore does not provision Redis. It exposes a separate anonymous `/healthz` process/readiness endpoint rather than treating MCP JSON-RPC traffic as a health probe.

| Setting | Default |
|---------|---------|
| Container Apps replicas | Minimum `0`, maximum `3` |
| Container resources | `0.5` vCPU, `1 GiB` memory |
| Public endpoint rate limit | `120` requests per `60` seconds per replica and authenticated object ID (IP fallback) |
| Request body limit | `4 MiB` |
| CORS | Disabled; configure explicit origins only when a browser client requires them |
| Public edge | Native Container Apps HTTPS FQDN; no Front Door dependency |

Minimum replicas are a latency/cost choice. This repository defaults to zero; latency-sensitive interactive deployments should consider one or more warm replicas, consistent with [Microsoft’s stateless MCP hosting guidance](https://techcommunity.microsoft.com/blog/appsonazureblog/mcp-just-went-stateless-%E2%80%94-what-the-2026-spec-changes-about-scaling-on-app-servic/4530222).

## Prerequisites

- An Azure subscription where you can create resources and role assignments (Owner, or Contributor + User Access Administrator)
- Permission to create an app registration in the Entra tenant whose users will sign in (Application Developer or higher), and to assign users to its enterprise application
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [Azure CLI (`az`)](https://learn.microsoft.com/cli/azure/install-azure-cli), used for the Entra and verification commands below
- [PowerShell 7 (`pwsh`)](https://learn.microsoft.com/powershell/scripting/install/installing-powershell), which runs the cross-platform post-provision hook
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

Docker isn't required locally, because `azd` builds the container image remotely in Azure Container Registry.

## Deploy

DevBrain is single-tenant: one deployment serves one Entra tenant. These steps take a fresh tenant from nothing to a working MCP endpoint.

### 1. Create the Entra app registration

DevBrain signs users in through one pre-registered Entra app. It asks Entra only for `openid profile offline_access`, so the app doesn't need an exposed API scope or extra API permissions.

**Portal:** Entra ID → App registrations → New registration

| Setting | Value |
|---------|-------|
| Name | `DevBrain` (any name works) |
| Supported account types | **Accounts in this organizational directory only** (single tenant). Don't pick a multi-tenant option. |
| Redirect URI | Leave blank for now. You'll add it in step 4, after the Container App host name exists. |

Then, on the new registration:

1. Copy the **Application (client) ID** and **Directory (tenant) ID** from the Overview page.
2. **Certificates & secrets** → **New client secret**. Copy the secret **Value** now; Entra shows it only once. Note the expiry date (see [Operating notes](#operating-notes)).
3. **App roles** → **Create app role**: display name `DevBrain User`, allowed member types **Users/Groups**, value **`DevBrain.User`**, enabled. `/mcp` rejects any caller without this role.
4. If your tenant blocks user consent, open **API permissions** and select **Grant admin consent**. Otherwise, users consent to the basic sign-in scopes on first login.

**Azure CLI equivalent:**

```powershell
az login --tenant <tenant-guid>
$app = az ad app create --display-name DevBrain --sign-in-audience AzureADMyOrg | ConvertFrom-Json
az ad sp create --id $app.appId | Out-Null   # creates the enterprise application used for role assignment
$clientSecret = az ad app credential reset --id $app.appId --display-name devbrain --years 1 --query password -o tsv

$roles = ConvertTo-Json -AsArray @(@{
    allowedMemberTypes = @('User'); isEnabled = $true; value = 'DevBrain.User'
    displayName = 'DevBrain User'; description = 'Can connect to DevBrain'; id = [guid]::NewGuid().ToString()
})
Set-Content -Path approles.json -Value $roles
az ad app update --id $app.appId --app-roles approles.json
Remove-Item approles.json

$app.appId                                     # this is ENTRA_CLIENT_ID
```

### 2. Choose who can connect

Entra ID → **Enterprise applications** → `DevBrain`:

1. **Users and groups** → **Add user/group** → select the people or security groups (group assignment requires Entra ID P1 or higher) → role **DevBrain User**.
2. Recommended: **Properties** → set **Assignment required?** to **Yes**. Entra then stops unassigned users at sign-in, instead of letting them sign in and get rejected by DevBrain.

Role changes take effect the next time a client's short-lived access token refreshes, not immediately.

### 3. Provision the Azure resources

```powershell
azd init -t Ignite-Solutions-Group/devbrain
azd auth login --tenant-id <tenant-guid>
azd env new <env-name>                        # e.g. devbrain-prod; drives resource naming
azd env set ENTRA_TENANT_ID <tenant-guid>
azd env set ENTRA_CLIENT_ID <app-client-id>
azd env set ENTRA_CLIENT_SECRET <client-secret-value>
azd env set JWT_SIGNING_SECRET ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
azd provision
```

`azd provision` creates the Container App and its environment, Azure Container Registry, Cosmos DB, Storage, Key Vault, and Log Analytics with Application Insights. It also creates the managed-identity role assignments. The two secret values seed Key Vault through secure deployment parameters, so they never appear in plain deployment history.

The post-provision hook finalizes the Container App's Cosmos DB data-role assignment. It retries while a newly created managed identity propagates through Entra, so the first deployment doesn't need a manual second provision. It's safe to rerun.

`ENTRA_TENANT_ID` and `ENTRA_CLIENT_ID` stay in the azd environment, and every later `azd provision` or `azd up` re-applies them. After the first successful provision, you can clear the two bootstrap secrets from the local environment. Later provisions leave the existing Key Vault secrets unchanged:

```powershell
azd env set ENTRA_CLIENT_SECRET ""
azd env set JWT_SIGNING_SECRET ""
```

### 4. Add the redirect URI

```powershell
azd env get-value OAUTH_REDIRECT_URI    # https://ca-devbrain-<token>.<region>.azurecontainerapps.io/callback
```

In the portal, open the app registration → **Authentication** → **Add a platform** → **Web**, and paste that value exactly. Or use the CLI:

```powershell
az ad app update --id <app-client-id> --web-redirect-uris (azd env get-value OAUTH_REDIRECT_URI)
```

### 5. Deploy the server

```powershell
azd deploy
```

After the first deployment, `azd up` (provision + deploy) is fine for later updates.

### 6. Verify

```powershell
$url = azd env get-value AZURE_CONTAINER_APP_URL
Invoke-RestMethod "$url/healthz"                                    # status: healthy
Invoke-RestMethod "$url/.well-known/oauth-authorization-server"     # DevBrain's own issuer metadata
```

Then connect a client (see [Configure Your MCP Client](#configure-your-mcp-client)) at `$url/mcp`. The first tool call opens an Entra sign-in, and every write records the signed-in user's UPN in `updatedBy`.

The Container App uses the platform-provided HTTPS host name. Front Door and a custom domain are optional additions, not requirements. If you add a custom domain, update `OAuth__BaseUrl` and `AllowedHosts` in `infra/main.bicep`, and the Entra redirect URI, to match.

## First Run

After a fresh deployment, seed the shared usage guide so any AI tool connecting to the new instance knows how to use DevBrain.

1. Edit the **Known Projects** list in [`docs/seed/ref-devbrain-usage.md`](docs/seed/ref-devbrain-usage.md) to name your organization's projects.
2. From any connected MCP client (for example, Claude Code running in a clone of this repo), ask:

   > Read `docs/seed/ref-devbrain-usage.md` and store it in DevBrain with `UpsertDocument`, key `ref:devbrain-usage`, project `default`, tags `meta`, `instructions`, `usage`.

Re-running is safe because every upsert is a full overwrite. Seeding goes through a signed-in client because every DevBrain tool call requires a per-user OAuth token with the `DevBrain.User` role.

## Upgrading from 1.x (Azure Functions)

DevBrain 2.0 removes the Azure Functions host. On an existing 1.x environment:

1. On the existing Entra app, add the `DevBrain.User` app role and assign it (step 1, item 3, and step 2). Then, in the existing azd environment, run `azd provision`, add the new redirect URI (step 4), and run `azd deploy`. The Container App reads and writes the same Cosmos `documents` container, so no data migration is needed.
2. Point every MCP client at `https://<container-app-fqdn>/mcp` in place of `https://<function-app>/runtime/webhooks/mcp`. Each client signs in once against the new endpoint.
3. Once no client uses the old endpoint, delete the resources that `azd provision` no longer manages. Provisioning is incremental, so it doesn't remove them for you:
   - the `func-devbrain-<token>` Function App and its `plan-devbrain-<token>` Flex Consumption plan. Deleting the Function App also removes its system-assigned identity; Azure then shows that identity's old role assignments as "Identity not found", and you can remove them.
   - the `deploymentpackage` and `dataprotection-keys` blob containers in the storage account
4. Remove the old Function App URL from the Entra app's redirect URIs.

1.x OAuth records left in the `oauth_state` container expire on their own through Cosmos TTL.

## Operating notes

- **Client secret expiry.** When the Entra client secret expires, sign-ins and token refreshes fail. Before it expires, create a new secret and write it to the `entra-client-secret` Key Vault secret, either with `az keyvault secret set` or by setting `ENTRA_CLIENT_SECRET` and re-running `azd provision`. Container Apps picks up the new version within 30 minutes. To apply it immediately, restart the active revision with `az containerapp revision restart`.
- **JWT signing secret rotation.** Replacing `jwt-signing-secret` invalidates every DevBrain access token already issued, so clients re-authenticate on their next call. It's the "sign everyone out" lever.
- **Cold starts.** The template defaults to zero minimum replicas, so the first request after an idle period waits for a container to start. Set `containerAppMinReplicas` to `1` in `infra/main.bicep` if interactive latency matters more than idle cost.
- **Cost profile.** The main fixed costs are Cosmos DB, which uses standard provisioned throughput rather than serverless, and the Basic Azure Container Registry. Container Apps compute scales to zero by default. Check the Cosmos throughput settings against your budget before a long-running trial.
- **Tearing down a trial.** The Key Vault has purge protection on, so `azd down` leaves it soft-deleted for 90 days and its name can't be reused in that time. For another trial, create a new azd environment name (`azd env new`) rather than re-provisioning the old one.

## Configure Your MCP Client

DevBrain uses OAuth 2.0 with Dynamic Client Registration (DCR). Clients that support the MCP OAuth spec connect with just a URL — no API keys, no manual configuration, no local proxies. The server handles registration, authorization, and token exchange automatically via the built-in DCR facade backed by your Entra tenant.

### Claude Code CLI

```bash
claude mcp add devbrain --transport http https://<CONTAINER_APP_FQDN>/mcp
```

On first use, Claude Code opens a browser for Entra login. Subsequent sessions re-use the stored token.

### Claude Desktop / Claude Mobile / Claude.ai Web

Add as a custom MCP connector pointing at:

```
https://<CONTAINER_APP_FQDN>/mcp
```

OAuth completes automatically — no proxy, no function key, no manual headers.

### ChatGPT / Codex (Windows App and CLI)

The modern unified ChatGPT/Codex app for Windows is currently working well with DevBrain OAuth. This is treated as operationally healthy but still under monitoring, rather than a permanent compatibility guarantee.

```bash
codex mcp add devbrain --transport http https://<CONTAINER_APP_FQDN>/mcp
```

### OAuth token windows

DevBrain rotates OAuth refresh tokens on every refresh. To tolerate brief client retry or restart races while a credential cache catches up, the just-rotated token remains a replay marker for a short period and returns the same replacement refresh token during that window.

Deployments can tune the access-token lifetime and refresh replay window when their client mix or operating environment needs a different refresh cadence. For example:

```powershell
azd env set OAUTH_ACCESS_TOKEN_LIFETIME_MINUTES 45
azd env set OAUTH_REFRESH_REPLAY_LIFETIME_MINUTES 5
azd provision
```

`azd provision` applies the settings to the Container App and rolls a new revision. `azd deploy` only deploys a new application image, so existing settings persist across code-only updates. If the `OAUTH_*` values are not set, DevBrain uses its built-in defaults.

These `azd` values provision the equivalent application settings:

```text
OAuth__AccessTokenLifetimeMinutes=45
OAuth__RefreshReplayLifetimeMinutes=5
```

For a one-off test on an already-provisioned app, set the same `OAuth__*` environment variables on the Container App directly, which creates a new revision. Both values must be whole minutes from 1 through 1,440. Defaults are 10 minutes for access tokens and 5 minutes for refresh replay markers.

Keep both windows as short as the client population allows. A longer access-token lifetime reduces refresh frequency but extends the useful lifetime of a stolen bearer token. A longer replay window makes an old refresh token reusable for longer and should only be used to accommodate a measured client retry interval.

### VS Code / GitHub Copilot

DevBrain 2.0 owns the `/mcp` protocol surface directly and returns the specification-required `401` plus `WWW-Authenticate: Bearer resource_metadata="..."` challenge. That removes the Azure Functions host-layer limitation that stopped VS Code and GitHub Copilot from starting OAuth in 1.x. VS Code and GitHub Copilot now complete OAuth and use DevBrain's tools end to end.

```
https://<CONTAINER_APP_FQDN>/mcp
```

Add it as an HTTP MCP server. VS Code starts the Entra sign-in on first use.

### Cursor

Not yet validated. It's expected to work if the client supports MCP OAuth with DCR.

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
| `PreviewEditDocument` | `key` (required), `oldText` (required), `newText` (required), `expectedOccurrences`, `caseSensitive`, `project` | Preview a literal text replacement without writing; returns match count, before/after preview, and the current content hash |
| `ApplyEditDocument` | `key` (required), `oldText` (required), `newText` (required), `expectedContentHash` (required), `expectedOccurrences`, `caseSensitive`, `project` | Apply a literal text replacement only if the document still matches the preview hash |
| `EditTags` | `key` (required), `add`, `remove`, `project` | Add and/or remove tags on a document without re-emitting content. A tag in both `add` and `remove` is rejected. |
| `ListDocuments` | `prefix`, `project` | List document keys, optionally filtered by prefix |
| `SearchDocuments` | `query` (required), `project` | Substring search across keys and content |
| `DeleteDocument` | `key` (required), `project` | Delete a document by key. Idempotent on missing keys. |

### Editing Documents Safely

DevBrain still stores documents as whole values, but it now supports a safe two-step edit flow for exact text changes:

1. Call `PreviewEditDocument` with the literal `oldText` and `newText`
2. Inspect the returned `matchCount`, preview snippets, and `currentContentHash`
3. Call `ApplyEditDocument` with the same edit inputs and `expectedContentHash`

Why two steps:

- **Ambiguity guard.** Preview refuses edits when the number of matches differs from `expectedOccurrences` (defaults to `1`).
- **Concurrency guard.** Apply fails if the stored content hash changed after preview, preventing stale overwrites.
- **Agent-friendly ergonomics.** Exact snippet replacement is more reliable than offsets or regex for most AI callers.

Example:

```text
PreviewEditDocument(
  key="state:current",
  project="devbrain",
  oldText="Status: draft",
  newText="Status: in progress"
)
```

```text
ApplyEditDocument(
  key="state:current",
  project="devbrain",
  oldText="Status: draft",
  newText="Status: in progress",
  expectedContentHash="<hash from preview>"
)
```

### Editing Tags Without Re-Upserting

`EditTags` applies a tag diff to a document, leaving `content` untouched. Pass `add` and/or `remove` as disjoint lists — a tag that appears in both is rejected. Already-present tags in `add` are no-ops; absent tags in `remove` are silently ignored (idempotent). Both lists empty returns a "nothing to do" message without a write.

```text
EditTags(
  key="ref:devbrain-usage",
  project="default",
  add=["workflow"],
  remove=["draft"]
)
```

Use `EditTags` whenever you only need to adjust tag metadata — it avoids the overhead of sending the entire document body through `UpsertDocument`.

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

1. Install the .NET 10 SDK and Azure CLI.

2. Sign in to Azure so `DefaultAzureCredential` can reach the data services:
   ```powershell
   az login
   ```
   Point local runs at a **dev** DevBrain environment, not production. Your identity needs **Cosmos DB Built-in Data Contributor** (a Cosmos SQL role assignment), **Storage Blob Data Contributor** on the storage account, and **Key Vault Crypto User** on the vault.

3. Configure the required settings with .NET user secrets. The server fails fast at startup if any of these is missing:
   ```powershell
   $p = 'src/DevBrain.Server'
   dotnet user-secrets --project $p set CosmosDb:AccountEndpoint 'https://<cosmos-account>.documents.azure.com:443/'
   dotnet user-secrets --project $p set OAuth:BaseUrl 'http://localhost:5000'
   dotnet user-secrets --project $p set OAuth:EntraTenantId '<tenant-guid>'
   dotnet user-secrets --project $p set OAuth:EntraClientId '<app-client-id>'
   dotnet user-secrets --project $p set OAuth:EntraClientSecret '<client-secret>'
   dotnet user-secrets --project $p set OAuth:JwtSigningSecret ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))
   dotnet user-secrets --project $p set DataProtection:BlobUri 'https://<storage-account>.blob.core.windows.net/dataprotection-keys-v2/keys.xml'
   dotnet user-secrets --project $p set DataProtection:KeyVaultKeyUri 'https://<key-vault>.vault.azure.net/keys/data-protection-key'
   ```
   Add `http://localhost:5000/callback` as a second **Web** redirect URI on the Entra app to sign in against the local server.

4. Run the server:
   ```powershell
   dotnet run --project src/DevBrain.Server
   ```
   The local MCP endpoint is `http://localhost:5000/mcp`, and `/healthz` is anonymous.

5. Run the tests. They use xUnit v3 on Microsoft.Testing.Platform, which `global.json` opts into:
   ```powershell
   dotnet test --solution devbrain.slnx
   ```

6. Optional dependency health checks from the repository root:
   ```powershell
   dotnet list devbrain.slnx package --vulnerable --include-transitive
   dotnet list devbrain.slnx package --outdated
   dotnet list devbrain.slnx package --deprecated --include-transitive
   ```

## Authentication

DevBrain implements RFC 7591 Dynamic Client Registration (DCR) with an in-process OAuth proxy that brokers a single pre-registered Entra app. From the client's perspective, DevBrain *is* the authorization server. Internally it delegates to your tenant's Entra ID for user authentication.

The 2026-07-28 specification deprecates DCR in favor of Client ID Metadata Documents but retains it for backward compatibility. DevBrain keeps DCR for the clients in its compatibility matrix while honoring the revision's authorization hardening: `application_type` metadata, RFC 8707 resource binding, and RFC 9207 issuer identification on authorization responses.

This solves two problems that previously blocked MCP OAuth:

1. **Entra doesn't support DCR** — DevBrain's facade implements it, issuing opaque `client_id` handles that all map to the same upstream Entra app.
2. **Claude.ai ignores external IdP endpoints in discovery metadata** — DevBrain hosts its own `/.well-known/oauth-authorization-server` and `/.well-known/oauth-protected-resource` on its own domain.

Every write operation records the authenticated user's Entra UPN in the `updatedBy` field.

The deployment is intentionally single-tenant. Validated Entra `roles` claims are carried into the local DevBrain session, and `/mcp` requires the `DevBrain.User` app role. There is no application-level administrator role or maintenance endpoint; administration is performed through Azure and Entra control planes.

### Refresh Token Rotation

Access tokens are short-lived and DevBrain refresh tokens rotate on every refresh. By default, the old refresh token becomes a five-minute replay marker that points at the replacement token, which makes immediate MCP client retries idempotent without reopening the OAuth flow. Replays outside the configured window still fail with `invalid_grant`, and every successful refresh or replay extends the upstream token vault record for the same local refresh window. See [OAuth token windows](#oauth-token-windows) for configuration and security tradeoffs.

The first use of each rotated refresh token also refreshes the upstream Entra session and revalidates tenant, user identity, and app-role claims. Assignment changes therefore take effect when the current short-lived access token expires rather than remaining cached for the full local refresh-token lifetime.

## Client compatibility

The 2.0 host keeps the same OAuth DCR flow as 1.x and fixes the VS Code/Copilot challenge blocker. "Working" means the client works with DevBrain's OAuth flow in production use.

| Client | Platform | Auth | Status |
|--------|----------|------|--------|
| Claude Code CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Claude Code CLI | WSL | OAuth (DCR) | ✅ Working |
| Claude Code | claude.ai web | OAuth (DCR) | ✅ Working |
| Claude Desktop | Windows | OAuth (DCR) | ✅ Working |
| Claude Mobile | Android | OAuth (DCR) | ✅ Working |
| ChatGPT / Codex unified app | Windows | OAuth (DCR) | ✅ Working; monitoring continues |
| Codex CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Codex CLI | WSL | OAuth (DCR) | ✅ Working |
| VS Code / GitHub Copilot | Windows | OAuth (DCR) | ✅ Working (new in 2.0) |
| Cursor | — | OAuth (DCR) | Not tested |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
