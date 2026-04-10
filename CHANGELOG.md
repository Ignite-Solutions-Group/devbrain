# Changelog

All notable changes to DevBrain are tracked in this file. Versions follow [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
