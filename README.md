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

The v2 MCP transport is stateless, so requests do not require session affinity or a distributed protocol-state cache. The template therefore does not provision Redis. It exposes a separate anonymous `/healthz` process/readiness endpoint rather than treating MCP JSON-RPC traffic as a health probe.

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

- Azure subscription
- [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
- [PowerShell 7 (`pwsh`)](https://learn.microsoft.com/powershell/scripting/install/installing-powershell) for the cross-platform post-provision hook
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Deploy

Create a single-tenant Entra app registration with a client secret. Add an app role whose value is `DevBrain.User` with `User/Groups` as the allowed member type, then assign that role to the users or groups allowed to connect. DevBrain does not expose a separate privileged application role; Azure resources remain administered through normal Azure RBAC.

Initialize the environment and supply the Entra values. `JWT_SIGNING_SECRET` must be a base64-encoded value containing at least 32 random bytes.

```powershell
azd init -t Ignite-Solutions-Group/devbrain
azd env set ENTRA_TENANT_ID <your-tenant-guid>
azd env set ENTRA_CLIENT_ID <your-entra-app-client-id>
azd env set ENTRA_CLIENT_SECRET <your-entra-app-client-secret>
$jwtSigningSecret = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
azd env set JWT_SIGNING_SECRET $jwtSigningSecret
azd provision
```

The post-provision hook finalizes the Container App's Cosmos DB data-role assignment. It retries the assignment while a newly created managed identity propagates through Entra, so the first deployment does not require a manual second provision. The operation is deterministic and safe to rerun.

The two secret values seed Key Vault through secure deployment parameters. The `.azure/` environment directory is git-ignored; after the first successful provision, the local bootstrap values can also be cleared because later provisions leave existing Key Vault secrets unchanged:

```powershell
azd env set ENTRA_CLIENT_SECRET ""
azd env set JWT_SIGNING_SECRET ""
```

After provisioning, add the emitted `AZURE_CONTAINER_APP_URL` plus `/callback` as a Web redirect URI on the Entra app registration. Then deploy the v2 server:

```powershell
azd deploy server
```

The template also retains the Azure Functions host as a compatibility deployment target while v2 client validation is completed. It uses the same `documents` container but a separate OAuth key namespace and Data Protection key ring, so both hosts can run safely side by side. Deploy it only when compatibility testing requires it:

```powershell
azd deploy api
```

The Container App uses the platform-provided HTTPS hostname. Front Door and a custom domain are optional additions, not requirements.

## First Run

After a fresh deployment, seed the default reference documents so any AI tool connecting to your new instance immediately has usage guidance available. Connect any authenticated MCP client and call:

```
UpsertDocument(key="ref:devbrain-usage", project="default", content=<contents of docs/seed/ref-devbrain-usage.md>)
```

Re-running the upsert is safe because it is a full overwrite. Source content for the seed lives under [`docs/seed/`](docs/seed/).

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
azd deploy server
```

`azd provision` applies the Bicep settings to both hosts. `azd deploy server` only deploys the v2 application image, so existing settings persist across code-only updates. If the `OAUTH_*` values are not set, DevBrain uses its built-in defaults.

These `azd` values provision the equivalent application settings:

```text
OAuth__AccessTokenLifetimeMinutes=45
OAuth__RefreshReplayLifetimeMinutes=5
```

For a one-off test on an already-provisioned host, set the same `OAuth__*` environment variables directly and create a new revision or restart the app. Both values must be whole minutes from 1 through 1,440. Defaults are 10 minutes for access tokens and 5 minutes for refresh replay markers.

Keep both windows as short as the client population allows. A longer access-token lifetime reduces refresh frequency but extends the useful lifetime of a stolen bearer token. A longer replay window makes an old refresh token reusable for longer and should only be used to accommodate a measured client retry interval.

### VS Code / GitHub Copilot

DevBrain 2.0 owns the `/mcp` protocol surface directly and returns the specification-required `401` plus `WWW-Authenticate: Bearer resource_metadata="..."` challenge. This removes the Azure Functions extension host-layer limitation that previously prevented VS Code and GitHub Copilot from starting OAuth. End-to-end client validation remains part of the v2 parallel rollout.

### Cursor

Not yet validated against v2. It is expected to work if the client supports MCP OAuth with DCR.

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

1. Install prerequisites: .NET 10 SDK and Azure CLI. Azure Functions Core Tools v4 is only needed to run the compatibility host.

2. Log in to Azure (for `DefaultAzureCredential`). A local identity using the deployed Azure data services needs Cosmos DB Built-in Data Contributor, Storage Blob Data Contributor (or Owner), and Key Vault Crypto User on the corresponding resources:
   ```powershell
   az login
   ```

3. Configure the required `CosmosDb__*`, `OAuth__*`, and `DataProtection__*` values with environment variables or .NET user secrets. The server fails fast when a required value is missing.

4. Run the v2 host:
   ```powershell
   dotnet run --project src/DevBrain.Server
   ```

   The local MCP endpoint is `http://localhost:<port>/mcp`; `/healthz` is anonymous. To run the compatibility Functions host instead, configure `src/DevBrain.Functions/local.settings.json` and use `func start` from that directory.

5. Optional dependency health checks from the repository root:
   ```powershell
   dotnet list devbrain.slnx package --vulnerable --include-transitive
   dotnet list devbrain.slnx package --outdated --highest-patch
   dotnet list devbrain.slnx package --outdated --include-transitive
   dotnet list devbrain.slnx package --deprecated
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

DevBrain 2.0 is designed to run beside the Functions implementation until its client matrix is validated. The direct ASP.NET Core host structurally resolves the former VS Code/Copilot OAuth challenge blocker. “Working” entries below reflect current DevBrain OAuth operational evidence; the v2 endpoint still needs to be checked across the same clients before the compatibility host is retired.

| Client | Platform | Auth | Status |
|--------|----------|------|--------|
| Claude Code CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Claude Code CLI | WSL | OAuth (DCR) | ✅ Working |
| Claude Code | claude.ai web | OAuth (DCR) | ✅ Working |
| Claude Desktop | Windows | OAuth (DCR) | ✅ Working |
| Claude Mobile | Android | OAuth (DCR) | ✅ Working |
| ChatGPT / Codex unified app | Windows | OAuth (DCR) | ✅ v2 validated; monitoring continues |
| Codex CLI | Windows Terminal | OAuth (DCR) | ✅ Working |
| Codex CLI | WSL | OAuth (DCR) | ✅ Working |
| VS Code / GitHub Copilot | Windows | OAuth (DCR) | 🧪 Former challenge blocker addressed in v2; validation pending |
| Cursor | — | OAuth (DCR) | Not tested |

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for guidelines, PR process, and local dev setup.

## License

[MIT](LICENSE) — Ignite Solutions Group
