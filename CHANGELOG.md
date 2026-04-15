# Changelog

All notable changes to DevBrain are tracked in this file. Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Two new document-editing tools add a safe preview/apply workflow for exact text replacements. This keeps DevBrain's whole-document storage model intact while giving AI callers a deterministic way to edit without stale overwrites.

### Added
- **`PreviewEditDocument(key, oldText, newText, expectedOccurrences?, caseSensitive?, project?)`** — previews a literal text replacement without writing. Returns match count, before/after snippets, and the current `contentHash` for the caller to feed into apply.
- **`ApplyEditDocument(key, oldText, newText, expectedContentHash, expectedOccurrences?, caseSensitive?, project?)`** — applies the same literal replacement only if the stored document still matches the preview hash. Refuses ambiguous or stale edits.
- **`DocumentEditService`** — shared edit-planning/execution layer that finds literal matches, builds preview snippets, and computes the final whole-document replacement payload.
- **`ConditionalWriteResult` + `IDocumentStore.ReplaceIfHashMatchesAsync(...)`** — conditional write path used by apply to avoid overwriting a document that changed after preview.
- **`ContentHashing` helper** — centralizes content-hash computation and normalization so compare/edit flows share the same hash semantics.

### Changed
- `README.md` now documents the new edit tools and the recommended preview/apply workflow.
- `docs/seed/ref-devbrain-usage.md` now teaches AI callers how to edit existing documents safely using `PreviewEditDocument` followed by `ApplyEditDocument`.

## [1.7.0] — 2026-04-12

Two new read-only tools that let callers check whether a document has changed without pulling the full content into context. Every write now stamps a SHA-256 `contentHash` and `contentLength` on the document, enabling cheap staleness checks and import-or-skip decisions.

### Added
- **`GetDocumentMetadata(key, project?)`** — returns key, project, tags, updatedAt, updatedBy, contentHash, and contentLength without the content body. Use to check existence, size, and freshness without consuming tokens or Cosmos RU on the full document.
- **`CompareDocument(key, content | contentHash, project?)`** — compares candidate content against a stored document. Accepts either raw content (hashed server-side) or a precomputed SHA-256 hex hash. Returns `{ found, match, storedContentHash, candidateHash, ... }`. Use to decide whether an import or sync is needed before committing to a full upsert.
- **`contentHash` and `contentLength` fields on `BrainDocument`.** Computed server-side on every write (upsert, append, chunked finalize). Nullable — existing documents show `null` until their next write, at which point the fields are populated automatically.
- **Content normalization for hashing.** Before computing SHA-256, content is normalized: line endings are unified to `\n` (`ReplaceLineEndings`) and trailing whitespace is trimmed. This ensures documents with `\r\n` vs `\n` line endings or trailing newline differences produce identical hashes. The stored content is never modified — normalization only affects hash input.

### Changed
- **`CosmosDocumentStore.UpsertAsync`** now computes and stores `contentHash` + `contentLength` on every write. Since `AppendAsync` and `UpsertChunkAsync` both flow through `UpsertAsync` for their final write, all write paths are covered with a single touch point.
- **`IDocumentStore`** gains `GetMetadataAsync(key, project)` — a Cosmos projection query that excludes `c.content`, keeping RU cost and response size low.
- `host.json` MCP `instructions` updated to mention the two new tools.
- `host.json` `serverVersion` bumped to `1.7.0`.

### Notes
- **Backward compatibility.** Existing documents in Cosmos will have `contentHash: null` and `contentLength: null` until their next write. `GetDocumentMetadata` returns these nulls gracefully. `CompareDocument` against a pre-existing doc correctly reports `match: false` (null != any hash), which is the safe default — it triggers a re-import that populates the hash.
- **Hash normalization is hash-only.** The normalization (line-ending unification + trailing trim) is applied identically in both `CosmosDocumentStore.ComputeSha256` and `DocumentTools.ComputeSha256`, so server-computed hashes and client-precomputed hashes match when the same normalization is applied. Callers providing a precomputed `contentHash` should apply the same normalization for consistent results.

## [1.6.0] — 2026-04-11

Per-user OAuth replaces the function-key gate. DevBrain now acts as an RFC 7591 Dynamic Client Registration (DCR) facade in front of a single pre-registered Entra app, making it authenticatable from Claude Code CLI, Claude.ai web, VS Code, ChatGPT, and Cursor — all of which previously failed against Entra-direct MCP servers. Writes now record the real Entra UPN as `updatedBy` instead of `"unknown"`.

### Hard cutover — no rollback path

v1.6 is a one-way deploy. There is no dual-mode feature flag and no rollback runbook. If a v1.6 deployment fails its acceptance checks, the recovery path is a v1.6.1 forward-fix, not redeploying v1.5.0. The v1.5.0 tag remains in the back pocket as a worst-case escape hatch but is no longer treated as a routine smoke-test target. This is intentional — single-tenant posture + small known user population means the blast radius is small and the cost of a dual-mode rollback test surface exceeds the cost of a forward-fix. DevBrain remains "deploy once, it's yours" single-tenant by design.

### Deploy prerequisite — must complete BEFORE `azd up`

A tenant admin must create a single Entra app registration and wire its values through. Without this, the Function deploys successfully but every OAuth flow returns 500 at `/callback`.

1. Create app registration `DevBrain` in the target tenant. **Set `signInAudience: AzureADMyOrg` (single-tenant)** — do NOT pick the multi-tenant option in the portal wizard.
2. Add redirect URI: `https://{function-host}/callback`.
3. Expose API: `documents.readwrite` scope.
4. Set the `entraTenantId` and `entraClientId` Bicep parameters (e.g. `azd env set ENTRA_TENANT_ID <guid>` and `azd env set ENTRA_CLIENT_ID <guid>`).
5. `azd up` (or `az deployment group create`). Bicep provisions the Key Vault and the `oauth_state` Cosmos container.
6. Set the two Key Vault secrets manually — Bicep deliberately does not populate them:
   - `az keyvault secret set --vault-name <kv> --name jwt-signing-secret --value $(openssl rand -base64 32)`
   - `az keyvault secret set --vault-name <kv> --name entra-client-secret --value <secret from Entra app>`
7. Restart the Function app so it picks up the Key Vault references.

### Added

- **DCR facade endpoints** under `src/DevBrain.Functions/Auth/DcrFacade/`:
  - `POST /register` — RFC 7591 DCR. Returns an opaque `client_id` handle backed by the pre-registered Entra app. 90-day TTL on the registration record.
  - `GET /authorize` — validates the client, generates DevBrain's own upstream PKCE pair, persists an `AuthTransaction`, and redirects to Entra. S256-only; plain PKCE is rejected; redirect URIs match exactly.
  - `GET /callback` — exchanges the Entra code with DevBrain's own PKCE verifier (never the client's), mints a pre-committed JTI, creates the upstream token vault record at `upstream:{jti}`, and redirects back to the client's `redirect_uri` with a DevBrain code.
  - `POST /token` — handles both `authorization_code` and `refresh_token` grants. Atomic code redemption (single-take) and refresh token rotation (every use mints a new refresh and invalidates the old one).
  - `GET /.well-known/oauth-authorization-server` + `/.well-known/oauth-protected-resource` — DevBrain-hosted discovery documents. Everything points at DevBrain itself, never at Entra — claude-ai-mcp issue #82 requires this.
- **`McpJwtValidationMiddleware` + `JwtAuthenticator`** — the MCP webhook gate. The `webhookAuthorizationLevel` is now `anonymous`; the JWT middleware is the sole authentication path for tool invocations. Populates `FunctionContext.Features.Get<ClaimsPrincipal>()` so `DocumentTools.GetCallerIdentity` continues to work with **zero code changes** — the existing `preferred_username` / `oid` extraction now sees real values.
- **Single-tenant enforcement at the token layer.** Every issued JWT carries the configured `tid` claim. The middleware validates `tid` against `OAuth__EntraTenantId` **before** any Cosmos lookup, so cross-tenant tokens are rejected cheaply. This is a deliberate load-bearing decision — single-tenant is a permanent non-goal, not a current-phase simplification.
- **New Cosmos container `oauth_state`** (`/key`, TTL enabled) holding five record kinds: `client:{id}`, `txn:{state}`, `code:{code}`, `upstream:{jti}`, `refresh:{token}`. Cosmos native TTL is best-effort — every read defensively re-checks `expiresAt` against an injected `TimeProvider`.
- **Key Vault** (`kvdb{resourceToken}`) with soft-delete + purge protection. Function managed identity gets **both** `Key Vault Crypto User` and `Key Vault Secrets Officer` role assignments. Single-role configurations fail silently until the first key rotation — bake both in on day one.
- **Upstream token encryption at rest via ASP.NET Core Data Protection.** `IUpstreamTokenProtector` / `DataProtectionUpstreamTokenProtector` wraps the Entra access + refresh tokens (as an `UpstreamTokenEnvelope`) behind a stable purpose string (`DevBrain.OAuth.UpstreamToken`). The Data Protection key ring is persisted to a new `dataprotection-keys` blob container in the existing storage account and wrapped by a new Key Vault key (`data-protection-key`, RSA 2048). Both `CosmosOAuthStateStore` and `FakeOAuthStateStore` route every upstream record save and read through the protector — a state-store-level test asserts this with a `FakeUpstreamTokenProtector` call counter.
- **Full id_token JWKS validation in `EntraOAuthClient`.** Every Entra `id_token` is now fully validated (signature, issuer, audience, lifetime) against the tenant's OpenID Connect discovery document before any claim is read. Wired via `IConfigurationManager<OpenIdConnectConfiguration>` using the standard `Microsoft.IdentityModel.Protocols.OpenIdConnect` discovery infrastructure with its default 24-hour refresh. Validation failure raises the new `IdTokenValidationException`; `CallbackHandler` translates it into a local 400 with `invalid_grant` (a security event — the transaction is consumed but the client is not redirected, so the failure surfaces in logs rather than being papered over in the client's error UI).
- **Test project `tests/DevBrain.Functions.Tests/`** (xUnit, `Microsoft.Extensions.TimeProvider.Testing`). 102 tests cover all ten acceptance gates:
  1. **CVE-2025-69196** audience guard — JWTs with base-URL audience rejected
  2. **PKCE downgrade** — mismatched, empty, short, long verifiers all rejected
  3. **Authorization code replay** — atomic single-take, second redeem returns `invalid_grant`
  4. **Expired transaction** — transactions older than 600s rejected before any upstream call (uses `FakeTimeProvider`, no sleeping)
  5. **Refresh token rotation** — old refresh invalid after use, new refresh works
  6. **Per-user identity E2E** — rehydrated `ClaimsPrincipal` carries the real Entra UPN from `upstream:{jti}`
  7. **Audience-scoped cross-host** — token issued for host A rejected by host B
  8. **Cross-tenant rejection** — wrong `tid` rejected with **zero** state store reads
  9. **Upstream token encryption** — `EphemeralDataProtectionProvider`-backed unit tests cover round-trip preservation, ciphertext-not-equal-to-plaintext, single-byte-flip tamper detection, truncation rejection, cross-key-ring rejection, and stable purpose string. Plus a state-store-level test asserting every upstream write and read invokes the protector.
  10. **id_token JWKS validation** — happy path (correct signature, issuer, audience, unexpired) accepted; wrong signing key, wrong issuer, wrong audience, and expired tokens all rejected with the typed `IdTokenValidationException`. Discovery-fetch failures also surface as the typed exception.

### Changed

- **`webhookAuthorizationLevel`** → `anonymous` in `host.json`. The MCP extension's implicit system key is no longer the gate; the JWT middleware is. Attempting to replay a v1.5 function key at v1.6 returns 401.
- **`DocumentTools.cs`**: **unchanged**. `GetCallerIdentity` already read `ClaimsPrincipal` from `FunctionContext.Features`; v1.6 just populates that feature with real identity instead of leaving it empty.
- **`Program.cs`**: startup fail-fast on all required OAuth config (`OAuth:BaseUrl`, `OAuth:JwtSigningSecret`, `OAuth:EntraTenantId`, `OAuth:EntraClientId`, `OAuth:EntraClientSecret`, `CosmosDb:AccountEndpoint`). Tenant ID must parse as a GUID. Missing values throw before the host starts.
- **`infra/main.bicep`**: new `oauth_state` Cosmos container, new Key Vault with both role assignments, new `dataprotection-keys` blob container on the existing storage account, new Key Vault key (`data-protection-key`) for wrapping the DP key ring, new OAuth + Data Protection app settings (double-underscore form for Linux hosting: `OAuth__*`, `KeyVault__Name`, `CosmosDb__OAuthContainerName`, `DataProtection__BlobUri`, `DataProtection__KeyVaultKeyUri`).
- **`DevBrain.Functions.csproj`** `<Version>` bumped to `1.6.0`. Added `Microsoft.IdentityModel.JsonWebTokens 8.3.0`, `Microsoft.IdentityModel.Protocols.OpenIdConnect 8.3.0`, `Microsoft.AspNetCore.DataProtection 10.0.0`, `Azure.Extensions.AspNetCore.DataProtection.Blobs 1.5.0`, `Azure.Extensions.AspNetCore.DataProtection.Keys 1.5.0`.
- **`host.json`** `serverVersion` bumped to `1.6.0`.

### Notes

- **JWT signing intentionally uses a plain Key Vault HMAC secret, not Data Protection.** Data Protection's API is Protect/Unprotect — it is *not* appropriate for HMAC signing key derivation. JWT signing reads `OAuth__JwtSigningSecret` (a Key Vault reference) directly as HMAC material. Data Protection is reserved exclusively for upstream token encryption (`DevBrain.OAuth.UpstreamToken` purpose). Both still depend on the same Key Vault — one KV dependency covers both concerns — but they use different secret surfaces inside it.
- **Single-tenant is a permanent non-goal, not a deferral.** If a SaaS-style multi-tenant DevBrain ever exists, it is a fork under a different name in a different repo — not a DevBrain v2. The code leans on this: `OAuth__EntraTenantId` is hard-required, the authority URL is a baked constant, the JWT issuer stamps `tid` into every token, the middleware validates `tid` before any store lookup, and the Entra app manifest must be single-tenant (`AzureADMyOrg`).
- **FastMCP OAuthProxy architecture inspired this but we avoided the two bugs they shipped:** CVE-2025-69196 (audience must be the webhook URL, not the base URL — see gate #1) and issue #1713 (client PKCE must not be forwarded upstream — see the independent `AuthTransaction.UpstreamPkceVerifier` / `ClientCodeChallenge` pair).
- **Consent screen** is deferred to `sprint:devbrain-v1.7-consent-screen`. The `consent:{client_id}:{user_oid}` Cosmos key shape is reserved. With single-tenant Entra as a permanent non-goal, the confused-deputy threat model essentially evaporates as long as DevBrain is deployed by the org that owns the tenant — v1.7's existence is now contingent on a future change in deployment posture (e.g., publishing DevBrain as a turnkey OSS artifact other orgs deploy), not on multi-tenancy.

## [1.5.0] — 2026-04-10

Three new write primitives — `DeleteDocument`, `AppendDocument`, `UpsertDocumentChunked` — plus key-hygiene enforcement on the write path. Additive, no breaking API changes.

### Added
- **`DeleteDocument(key, project?)`** — point-delete by key within a project. Idempotent: deleting a missing key returns `{ deleted: false }` with a "not found" note, not an error. Resolves the target via the project-scoped `GetAsync` query first (so it never deletes across project boundaries) and then deletes by the stored id (encoded form, URL-safe) and raw key (partition key). Accepts both colon and slash keys so legacy slash-orphans can be cleaned up through the tool.
- **`AppendDocument(key, content, separator?, tags?, project?)`** — append-only primitive for growing logs (session history, decision logs, audit trails). Creates the document if absent; otherwise concatenates `existing + separator + content` server-side. Default separator is two newlines. Tags are unioned with any existing tags. Concurrent appenders are serialized via Cosmos ETag optimistic concurrency with up to 5 retries. Refuses cross-project key collisions explicitly (never silently appends to another project's doc).
- **`UpsertDocumentChunked(key, content, chunkIndex, totalChunks, tags?, project?)`** — multi-part upload for documents too big to emit in a single LLM turn. Each call stages its chunk in a `_staging:{realKey}` document; the final chunk triggers server-side concatenation, a normal upsert to the real key, and deletion of the staging doc. Chunks may arrive out of order. A changed `totalChunks` mid-upload resets the staging buffer. Staging documents self-clean via Cosmos per-item TTL (currently 4 hours) so abandoned uploads don't linger.
- **Slash-key rejection on write paths.** `UpsertDocument`, `AppendDocument`, and `UpsertDocumentChunked` now reject keys containing `/` with an actionable error: *"Keys must use ':' as separator. Got 'X' — did you mean 'Y'?"*. Reads and `DeleteDocument` continue to accept slash keys so legacy data and cleanup operations keep working.
- **Per-item TTL enabled on the Cosmos container.** `infra/main.bicep` now sets `defaultTtl: -1` on the `documents` container, enabling the TTL feature without imposing a default expiration. Real documents have no `ttl` field and live forever; only chunked-upload staging docs set it explicitly.

### Changed
- `README.md` "Tools Reference" table adds the three new tools. A new "When to use Append vs Chunked" subsection explains the distinction (growing logs vs. one-shot large docs).
- `README.md` "Key Conventions" note now states explicitly that writes reject slash keys while reads accept them.
- `host.json` MCP `instructions` mentions the three new tools and the "writes require colon keys" rule.
- `host.json` `serverVersion` bumped to `1.5.0`.
- `DevBrain.Functions.csproj` `<Version>` bumped to `1.5.0`.

### Notes
- The Cosmos container's partition key path is `/key` (not `/project`), so two documents with the same key in different projects would physically collide. This is a pre-existing latent issue unrelated to v1.5. `AppendDocument` refuses cross-project collisions by raising an explicit error rather than silently clobbering; `UpsertDocument` retains its historical clobber-on-collision behavior for parity with v1.4 callers.
- `DeleteDocument` uses `DeleteItemAsync(EncodeId(key), PartitionKey(key))`. Because `EncodeId` strips `/` from the id, the `ReadItemAsync` URL-path-separator problem that affected v1.2's `GetDocument` does not apply — the old "future: delete needs query-then-delete" note in `ref:known-issues` is therefore moot.
- `ChunkedStaging.cs` models the staging payload as an order-agnostic `{ totalChunks, chunks: [{ index, content }] }` JSON blob stored in the staging doc's `content` field. No schema changes were needed on the container beyond enabling TTL.

## [1.4.0] — 2026-04-09

Colon keys are now the canonical user-facing convention. No breaking API changes — slash keys continue to work via the SQL-query fallback.

### Added
- **"Did you mean?" project suggestions.** When `ListDocuments` or `SearchDocuments` return zero results, `CosmosDocumentStore` now runs a `SELECT DISTINCT VALUE c.project FROM c` and looks for a similar known project name (case-insensitive startsWith / contains / reverse-contains). If one is found, a single synthetic entry with `key: "_suggestion"` is returned carrying a "Did you mean project 'X'?" message. Skips the suggestion when the requested project already exists (empty result is then a legitimate miss, not a typo). The extra query only runs on the empty-result path, so the hot path is unaffected. Tool descriptions on `ListDocuments` and `SearchDocuments` updated to note the behavior.

### Changed
- `host.json` MCP `instructions` now tells clients that keys use colon as separator, with examples (`state:current`, `sprint:my-feature`, `ref:notes`).
- `host.json` `serverVersion` bumped to `1.4.0`.
- `DevBrain.Functions.csproj` `<Version>` bumped to `1.4.0`.
- README "Key Conventions" table flipped from slash to colon; added a line noting slash keys remain accepted for backward compatibility.
- README "Session Startup / AGENTS.md" example (`AGENTS.md` / `CLAUDE.md` blocks) now shows colon keys.
- README "Why DevBrain" morning-session snippet now shows `state:current` instead of `state/current`.
- README "First Run" paragraph updated to reference `ref:devbrain-usage`.
- `docs/seed/ref-devbrain-usage.md` key-conventions table and session-startup examples flipped to colons; added guidance that colons are canonical.
- `scripts/seed-devbrain.ps1` now seeds the `ref:devbrain-usage` key instead of `ref/devbrain-usage`.
- DevBrain documents across the `devbrain` and `default` projects re-upserted under colon keys; legacy slash-keyed originals removed.

### Notes
- `CosmosDocumentStore.EncodeId` (`key.Replace('/', ':')`) is unchanged and is effectively a no-op for well-formed colon keys. It stays as a safety net for any slash keys that slip through.
- The `GetAsync` query-by-`c.key` fallback is unchanged, so any external integrations still using slash keys continue to work.
- `docs/devbrain-spec-v1.md` is left as-is — it is the historical v1 spec.

## [1.3.0] — 2026-04-09

Documentation, onboarding, and convention updates. No breaking API changes.

### Added
- "Why DevBrain over alternatives" README section calling out cross-project access (single deployed endpoint, shared across every AI tool) as the key differentiator vs. Serena, Claude Project Knowledge, and local markdown files.
- "Session Startup / AGENTS.md" README section with the recommended cross-tool pattern: `AGENTS.md` for Copilot/Cursor/Codex, `@AGENTS.md` import from `CLAUDE.md` for Claude Code.
- `docs/project-init.md` — starter-doc guide for new DevBrain projects (`state:current`, `sprint:{name}`, `arch:overview`) with section templates.
- `docs/seed/ref-devbrain-usage.md` — canonical usage guide for AI assistants, seeded as the baseline `ref/devbrain-usage` document.
- `scripts/seed-devbrain.ps1` — post-`azd up` bootstrap script that speaks MCP over SSE transport to seed baseline documents. Reads `AZURE_FUNCTION_URL` from the active azd environment and the key from `$env:DEVBRAIN_KEY` (prompts for either if missing).
- "First Run" README section pointing deployers at the new seed script.

### Changed
- Tool parameter descriptions in `DocumentTools.cs` now show colon-separated key examples (`sprint:license-sync`, `sprint:`) instead of slash-separated. Backend continues to accept both shapes.
- `host.json` `serverVersion` bumped to `1.3.0`.
- `DevBrain.Functions.csproj` now has an explicit `<Version>1.3.0</Version>`.

## [1.2.0] — 2026-04

Infrastructure, auth, and platform hardening.

### Added
- App Insights and Storage Queue Data Contributor role assignments for the Function App managed identity ([`2e99030`](../../commit/2e99030)).
- `postprovision` wait hook and Flex Consumption-specific deployment container in Bicep ([`2c9e8ed`](../../commit/2c9e8ed), [`6fb3295`](../../commit/6fb3295)).
- Cosmos doc id encoding so keys with separators round-trip cleanly ([`2e99030`](../../commit/2e99030), [`4a218e9`](../../commit/4a218e9)).

### Changed
- **Hosting plan:** Linux Consumption replaced with Flex Consumption Function App ([`9f28f22`](../../commit/9f28f22)).
- **Authentication:** Easy Auth (Entra ID OAuth) disabled; function key auth via `x-functions-key` header is now the only supported path due to upstream Entra/MCP OAuth incompatibilities (see README "Known Limitations").
- Cosmos doc ids now encode `/` as `:` instead of `Uri.EscapeDataString`, fixing round-trip for nested keys ([`4a218e9`](../../commit/4a218e9)).
- Monitoring Metrics Publisher role definition id corrected in Bicep ([`c5d48e7`](../../commit/c5d48e7)).
- README switched to PowerShell examples throughout ([`38d4a35`](../../commit/38d4a35)).
- Infra resources renamed to `devbrain`-prefixed pattern ([`38d4a35`](../../commit/38d4a35), [`6fb3295`](../../commit/6fb3295)).

### Fixed
- `GetDocument` slash bug — keys containing `/` failed point-reads; now handled via query ([`ff6e7b0`](../../commit/ff6e7b0)).
- Added explicit `Newtonsoft.Json 13.0.3` dependency to satisfy the Cosmos SDK transitive requirement ([`69ba65c`](../../commit/69ba65c)).
- Added `Microsoft.Azure.Functions.Worker.Sdk` reference and warm-up hook to unblock Flex Consumption deploys ([`2c9e8ed`](../../commit/2c9e8ed)).

## [1.1.0] — 2026-03

### Added
- **Project encapsulation.** `UpsertDocument`, `GetDocument`, `ListDocuments`, and `SearchDocuments` all accept an optional `project` parameter (defaults to `"default"`) that isolates documents by project scope ([`b31039c`](../../commit/b31039c)).

## [1.0.0] — 2026-03

Initial release.

### Added
- Azure Functions MCP server exposing `UpsertDocument`, `GetDocument`, `ListDocuments`, `SearchDocuments` tools.
- Cosmos DB NoSQL backing store with managed-identity access.
- `azd`-based deployment template.
- Entra ID Easy Auth (later removed in 1.2.0 due to MCP OAuth incompatibilities).
