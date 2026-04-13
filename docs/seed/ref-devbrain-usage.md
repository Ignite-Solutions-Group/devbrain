# DevBrain — Usage Guide for AI Assistants

## Important: Documents Are Scoped by Project

DevBrain organizes documents by project. If a query returns no results, you are likely searching the wrong project scope.

**Always specify the project parameter explicitly.** The default project ("default") is empty for most use cases.

## Known Projects
- `devbrain` — DevBrain's own documentation, architecture, sprint docs, backlog, known issues

## Session Startup Pattern
For any project, the correct session startup is:
1. `GetDocument(key="state:current", project="{project}")` — load current state
2. If working a sprint: `GetDocument(key="sprint:{name}", project="{project}")` — load active spec
3. Updates: write directly with `UpsertDocument` — no manual upload needed

This replaces any manual file upload workflow. DevBrain is the canonical source.

## Case Sensitivity Warning
Project names and document keys are case sensitive on some platforms. Always use lowercase:
- ✅ `project: "devbrain"`
- ❌ `project: "DevBrain"`

**Note:** OpenAI Codex Desktop allows mixed-case names in its UI but lowercases them internally.

## How to Query Correctly

### List all documents in a project
```
ListDocuments(project: "devbrain")
```

### Get a specific document
```
GetDocument(key: "state:current", project: "devbrain")
```

### Search across a project
```
SearchDocuments(query: "durable functions", project: "devbrain")
```

## Checking Before Writing — Metadata and Compare

Before importing or syncing a document, check whether the stored version is already up to date. This avoids unnecessary writes and wasted tokens.

### Quick existence and size check
```
GetDocumentMetadata(key: "sprint:license-sync", project: "devbrain")
```
Returns key, project, tags, updatedAt, updatedBy, contentHash (SHA-256), and contentLength (character count) — **without** the content body. Use this to:
- Check if a document exists
- See when it was last updated and by whom
- Compare content length against your candidate as a fast "obviously different" check

### Confirm content matches before skipping a write
```
CompareDocument(key: "sprint:license-sync", content: "...candidate text...", project: "devbrain")
```
Returns `{ found, match, storedContentHash, candidateHash, ... }`. The server hashes the candidate content and compares against the stored hash. You can also pass a precomputed `contentHash` instead of raw content.

### Recommended sync workflow
1. Call `GetDocumentMetadata` — if not found, upsert immediately
2. If found, compare `contentLength` against your candidate's character count — if obviously different, upsert
3. If lengths match, call `CompareDocument` with the candidate content — if `match: true`, skip the write
4. If `match: false`, upsert

This pattern avoids pulling the full stored document into context just to decide whether to write.

## Key Conventions

Keys use **colon** as the separator. Slash-separated keys (`sprint/foo`) still work for backward compatibility, but colons are the canonical, recommended convention — they signal "DevBrain key" at a glance and avoid being confused with file paths.

| Prefix | Use |
|---|---|
| `sprint:{name}` | Sprint specs and retrospectives |
| `state:current` | Current project state |
| `arch:{name}` | Architecture docs |
| `decision:{name}` | Architecture decision records |
| `ref:{name}` | Reference material |

## If You Get No Results
1. Check casing — project names and keys are case sensitive
2. Try specifying the project explicitly
3. Use ListDocuments with no prefix to see what's stored in that project
4. Try SearchDocuments with a broad keyword
5. The document may not exist yet — ask the user if they'd like to create it

## Frequently Asked Questions

### Does DevBrain persist across sessions?
Yes — fully. Cosmos DB is the backing store, not in-memory. Documents survive indefinitely across all sessions and all clients until explicitly deleted or overwritten.

### Is there a document size limit?
Cosmos DB has a 2MB per-document limit. Typical sprint specs (15-30KB) and state documents (up to ~40KB) are well within this limit.

### Are documents versioned?
No. UpsertDocument is full overwrite semantics — there is no history. The `updatedAt` field tracks the last write time but previous versions are not retained. Be deliberate about overwrites. Never patch — always do full rewrites of the current content.
