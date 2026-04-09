# DevBrain v1 — Sprint Spec

**Repository:** `github.com/Ignite-Solutions-Group/devbrain`  
**Purpose:** A remote MCP server that gives any AI tool (Claude web, Claude Code CLI, GitHub Copilot, Cursor) persistent, shared access to developer knowledge — sprint docs, architecture decisions, project state. One brain. Zero upload tax.  
**Stack:** .NET 10 · C# 14 · Azure Functions (isolated worker) · Cosmos DB NoSQL · Entra ID · Bicep · `azd`  
**License:** MIT  

---

## What This Is

DevBrain is a deployable Azure Function app that exposes four MCP tools backed by Cosmos DB. Deploy once with `azd up`, point any MCP client at the endpoint, and every AI tool you use shares the same persistent knowledge store. No local processes to run. No files to upload. No markdown to rewrite.

This is a single-tenant v1. You deploy it, it's yours. Entra ID gates access.

---

## Project Structure

```
devbrain/
├── src/
│   └── DevBrain.Functions/
│       ├── Tools/
│       │   └── DocumentTools.cs
│       ├── Models/
│       │   └── BrainDocument.cs
│       ├── Services/
│       │   ├── IDocumentStore.cs
│       │   └── CosmosDocumentStore.cs
│       ├── Program.cs
│       ├── host.json
│       ├── local.settings.json.example
│       └── DevBrain.Functions.csproj
├── infra/
│   ├── main.bicep
│   ├── main.parameters.json
│   └── abbreviations.json
├── .github/
│   └── workflows/
│       └── validate.yml
├── azure.yaml
├── Directory.Build.props
├── devbrain.sln
├── CONTRIBUTING.md
├── LICENSE
└── README.md
```

---

## Document Model

Every document stored in Cosmos has this shape. No schema enforcement — `content` is free-form text (markdown, JSON, whatever). The model exists only for serialization.

| Field | Type | Notes |
|---|---|---|
| `id` | string | Same value as `key`. Required by Cosmos. |
| `key` | string | Partition key. Human-readable path, e.g. `sprint/license-sync`. |
| `content` | string | Raw text content. No format enforced. |
| `tags` | string[] | Optional. Empty array if not provided. |
| `updatedAt` | DateTimeOffset | Set server-side on every upsert. |
| `updatedBy` | string | Entra UPN or OID from the bearer token claims. Set server-side. |

Partition key path: `/key`. The `id` and `key` fields are always identical — this satisfies Cosmos's requirement for a unique `id` within a partition while keeping the query model simple.

### Recommended Key Conventions (documented, not enforced)

| Prefix | Use |
|---|---|
| `sprint/{name}` | Sprint specs, e.g. `sprint/license-sync` |
| `state/current` | Current project state document |
| `arch/{name}` | Architecture docs |
| `decision/{name}` | Architecture decision records |
| `ref/{name}` | Reference material, infra constants |

---

## Cosmos DB Setup

- **Account:** NoSQL API, free tier enabled (1000 RU/s, 25 GB — sufficient for all developer knowledge workloads)
- **Database:** `devbrain`
- **Container:** `documents`
- **Partition key:** `/key`
- **Indexing:** Default policy (all paths) — appropriate for v1 volume
- **Auth:** Managed Identity (no connection strings). The Function app's system-assigned managed identity is granted the `Cosmos DB Built-in Data Contributor` role on the account.

---

## MCP Tools

All four tools are implemented in `DocumentTools.cs` using `[McpServerTool]` attributes from the `ModelContextProtocol` C# SDK. The Azure Functions MCP extension (`Microsoft.Azure.Functions.Worker.Extensions.Mcp`) wires them into the Functions runtime via `[McpToolTrigger]`.

### `UpsertDocument`

**Purpose:** Create or fully replace a document by key.  
**Inputs:** `key` (string, required), `content` (string, required), `tags` (string[], optional)  
**Behavior:** Upserts the document. Sets `updatedAt` to `DateTimeOffset.UtcNow`. Sets `updatedBy` from the authenticated caller's Entra claims (UPN preferred, OID fallback). Returns the saved `BrainDocument`.  
**Error:** Returns a descriptive error string if the Cosmos write fails.

### `GetDocument`

**Purpose:** Retrieve a single document by key.  
**Inputs:** `key` (string, required)  
**Behavior:** Point-reads the document using key as both partition key and id. Returns the full `BrainDocument` including content.  
**Error:** Returns a clear "Document not found" message (not an exception) if the key does not exist.

### `ListDocuments`

**Purpose:** Enumerate stored document keys, optionally filtered by prefix.  
**Inputs:** `prefix` (string, optional)  
**Behavior:** Queries Cosmos for documents where `key` starts with the given prefix. If no prefix is provided, returns all documents. Returns a lightweight projection: `key`, `tags`, `updatedAt`, `updatedBy` — **no `content`** (avoids RU cost and token bloat). Results sorted by `updatedAt` descending.  
**Error:** Returns empty list with no error if nothing matches.

### `SearchDocuments`

**Purpose:** Full-text substring search across document keys and content.  
**Inputs:** `query` (string, required)  
**Behavior:** Executes a Cosmos SQL query using `CONTAINS(c.content, @query, true) OR CONTAINS(c.key, @query, true)` (case-insensitive). Returns a projection: `key`, `tags`, `updatedAt`, and a content excerpt (first 300 characters of `content`). Results sorted by `updatedAt` descending, capped at 20 results.  
**Note:** This is substring match, not semantic/vector search. Vector search via Cosmos DiskANN is planned for v1.1.  
**Error:** Returns empty list with descriptive message if query fails.

---

## Authentication

Authentication is handled by Azure Functions' built-in Easy Auth (App Service Authentication), configured for Entra ID. The MCP extension automatically handles the OAuth 2.1 challenge/response flow — clients receive a 401 with `WWW-Authenticate` on first connection, complete the Entra login, and retry with a bearer token.

**Entra app registration (`DevBrain`):**
- Single-tenant (the deployer's tenant)
- Redirect URI: the Function app's default domain
- Expose an API scope: `documents.readwrite`
- No client secret required for the Function app itself — uses Managed Identity for Cosmos

**Functions Easy Auth config (set via Bicep / azd):**
- Provider: Microsoft (Entra ID)
- Client ID: the `DevBrain` app registration's client ID
- Require authentication: true
- Unauthenticated action: Return 401

**Caller identity extraction:** Read `ClaimsPrincipal` from the `FunctionContext`. Extract UPN from `preferred_username` claim, fall back to OID from `oid` claim, fall back to `"unknown"` for `updatedBy`.

---

## NuGet Packages

```xml
<PackageReference Include="Microsoft.Azure.Functions.Worker" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Mcp" />
<PackageReference Include="Microsoft.Azure.Functions.Worker.Extensions.Http" />
<PackageReference Include="ModelContextProtocol" />
<PackageReference Include="Microsoft.Azure.Cosmos" />
<PackageReference Include="Azure.Identity" />
<PackageReference Include="Microsoft.Extensions.Azure" />
```

Use current stable/GA versions. `Microsoft.Azure.Functions.Worker.Extensions.Mcp` is GA as of late 2025.

---

## host.json

```json
{
  "version": "2.0",
  "extensions": {
    "mcp": {
      "instructions": "DevBrain is a developer knowledge store. Use UpsertDocument to save sprint docs, architecture notes, or project state. Use GetDocument to retrieve by key. Use ListDocuments to browse what's stored. Use SearchDocuments to find content by keyword.",
      "serverName": "DevBrain",
      "serverVersion": "1.0.0"
    }
  }
}
```

Transport: Streamable HTTP (default for the MCP extension, preferred over SSE).

---

## Configuration

All configuration via environment variables / Function app settings. No secrets in code or config files.

| Setting | Source | Notes |
|---|---|---|
| `CosmosDb__AccountEndpoint` | App setting | Cosmos account URI, e.g. `https://myaccount.documents.azure.com:443/` |
| `CosmosDb__DatabaseName` | App setting | Default: `devbrain` |
| `CosmosDb__ContainerName` | App setting | Default: `documents` |
| `AZURE_CLIENT_ID` | Managed Identity | Set automatically when system-assigned MI is enabled |

No connection strings. Cosmos access uses `DefaultAzureCredential` with the system-assigned managed identity.

### local.settings.json.example

Provide this file (not the real one) committed to the repo so contributors know what's needed locally. Local runs use `DefaultAzureCredential` (Azure CLI login) for both Cosmos and identity.

---

## Infra (Bicep)

`infra/main.bicep` provisions all resources idempotently. `azd up` runs this then deploys the Function app.

**Resources:**
- **Resource Group** — created by `azd` based on environment name
- **Storage Account** — required by Azure Functions runtime (`AzureWebJobsStorage`)
- **Azure Functions App** — Flex Consumption plan, .NET 10 isolated worker, Linux
- **Cosmos DB Account** — NoSQL, free tier enabled, single region
- **Cosmos DB Database** — `devbrain`
- **Cosmos DB Container** — `documents`, partition key `/key`
- **Role Assignment** — Functions app system-assigned MI → `Cosmos DB Built-in Data Contributor` on the Cosmos account
- **Functions Auth Settings** — Easy Auth configured for Entra with the app registration client ID

**Parameters exposed via `main.parameters.json`:**
- `location` — Azure region (default: `eastus`)
- `environmentName` — used for resource naming (set by `azd`)
- `entraClientId` — the `DevBrain` Entra app registration client ID (required, provided by deployer)

**`azure.yaml`:**
```yaml
name: devbrain
services:
  api:
    project: src/DevBrain.Functions
    language: dotnet
    host: function
```

---

## `Directory.Build.props`

Single source of truth for version and package metadata:

```xml
<Project>
  <PropertyGroup>
    <Version>1.0.0</Version>
    <Authors>Ignite Solutions Group</Authors>
    <Company>Ignite Solutions Group</Company>
    <Product>DevBrain</Product>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>14</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

---

## Program.cs

Standard Functions isolated worker host setup. Register:
- `CosmosClient` via `AddAzureClients` with `DefaultAzureCredential`
- `IDocumentStore` / `CosmosDocumentStore` as singleton
- MCP server tooling via the Functions MCP extension registration

---

## GitHub Actions

`validate.yml` — runs on PR and push to main:
- `dotnet build`
- `dotnet test` (if tests added)
- No deploy in CI for v1 — deployers use `azd up` locally

---

## README Structure

The README is first-class. It is the product.

1. **Tagline** — one sentence: what DevBrain is
2. **The problem it solves** — 2–3 sentences. The 6,000-char markdown file. The upload tax. The tool boundary.
3. **How it works** — diagram or brief prose: Functions + Cosmos + Entra + MCP client
4. **Prerequisites** — Azure subscription, Entra tenant, `azd` CLI, .NET 10 SDK
5. **Deploy in 5 minutes** — step-by-step `azd` commands including the one manual step (app registration)
6. **Configure your MCP client** — copy-paste snippets for:
   - Claude Code CLI (`.claude/mcp.json`)
   - Claude Desktop (`claude_desktop_config.json`)
   - VS Code / GitHub Copilot (`.vscode/mcp.json`)
7. **Tools reference** — table of all 4 tools with inputs and purpose
8. **Key conventions** — the recommended prefix table (sprint/, state/, arch/, decision/, ref/)
9. **Local development** — how to run with `func start` and Azure CLI login
10. **Contributing** — link to `CONTRIBUTING.md`
11. **GitHub Sponsors** — tip cup 🫙

---

## What v1 Deliberately Excludes

- **Vector/semantic search** — v1.1, requires Cosmos DiskANN + embedding model
- **Multi-tenant** — future. Single-tenant deploy is the right v1 scope.
- **NuGet package** — the app is a deployable artifact, not a library. Client SDK is future scope.
- **Admin UI** — the MCP tools are the interface
- **Document versioning / history** — future. Cosmos change feed or audit container.
- **Bulk import** — future. For now, upsert documents one at a time via the tool.

---

## Acceptance Criteria

- [ ] `azd up` on a clean Azure subscription provisions all resources and deploys the Function app
- [ ] Claude Code CLI connects via streamable HTTP and lists available tools
- [ ] Claude web chat connects via MCP connector and lists available tools  
- [ ] `UpsertDocument` creates a new document and returns it
- [ ] `UpsertDocument` on an existing key replaces it and updates `updatedAt` / `updatedBy`
- [ ] `GetDocument` on an existing key returns the full document
- [ ] `GetDocument` on a missing key returns a clear "not found" message, not an exception
- [ ] `ListDocuments` with no prefix returns all documents (key/tags/updatedAt only)
- [ ] `ListDocuments` with prefix `sprint/` returns only sprint documents
- [ ] `SearchDocuments` with a keyword returns matching documents with excerpt
- [ ] Unauthenticated request returns 401 with proper MCP auth challenge
- [ ] Cosmos access uses Managed Identity — no connection strings in any config
- [ ] `local.settings.json` is gitignored; `.example` version is committed
- [ ] README deploy instructions work end-to-end for a net-new deployer
