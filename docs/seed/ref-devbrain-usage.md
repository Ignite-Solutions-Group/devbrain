# DevBrain — Usage Guide for AI Assistants

## Important: Keys Include the Project

DevBrain organizes documents by project, but a key identifies one document across the whole store; `project` filters reads and isn't part of a document's identity.

**Every key starts with its project name, and every call passes the project.** For example, use `key: "devbrain:state:current"` with `project: "devbrain"`. Two projects that both write an unprefixed `state:current` overwrite each other.

Unprefixed keys belong to the `default` project, which holds general DevBrain instructions such as this guide (`ref:devbrain-usage`). Calls that omit `project` read and write `default`. If a query returns no results, check the key's project prefix and the project scope.

## Known Projects
- `devbrain` — DevBrain's own documentation, architecture, sprint docs, backlog, known issues

## Session Startup Pattern
For any project, the correct session startup is:
1. `GetDocument(key="{project}:state:current", project="{project}")` — load current state
2. If working a sprint: `GetDocument(key="{project}:sprint:{name}", project="{project}")` — load active spec
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
GetDocument(key: "devbrain:state:current", project: "devbrain")
```

### Search across a project
```
SearchDocuments(query: "durable functions", project: "devbrain")
```

## Checking Before Writing — Metadata and Compare

Before importing or syncing a document, check whether the stored version is already up to date. This avoids unnecessary writes and wasted tokens.

### Quick existence and size check
```
GetDocumentMetadata(key: "devbrain:sprint:license-sync", project: "devbrain")
```
Returns key, project, tags, updatedAt, updatedBy, contentHash (SHA-256), and contentLength (character count) — **without** the content body. Use this to:
- Check if a document exists
- See when it was last updated and by whom
- Compare content length against your candidate as a fast "obviously different" check

### Confirm content matches before skipping a write
```
CompareDocument(key: "devbrain:sprint:license-sync", content: "...candidate text...", project: "devbrain")
```
Returns `{ found, match, storedContentHash, candidateHash, ... }`. The server hashes the candidate content and compares against the stored hash. You can also pass a precomputed `contentHash` instead of raw content.

### Recommended sync workflow
1. Call `GetDocumentMetadata` — if not found, upsert immediately
2. If found, compare `contentLength` against your candidate's character count — if obviously different, upsert
3. If lengths match, call `CompareDocument` with the candidate content — if `match: true`, skip the write
4. If `match: false`, upsert

This pattern avoids pulling the full stored document into context just to decide whether to write.

## Editing Existing Documents Safely — Preview and Apply

For exact text edits, use the guarded two-step flow instead of doing a blind full overwrite.

### Preview an exact edit first
```
PreviewEditDocument(
  key: "ref:devbrain-usage",
  oldText: "old phrase",
  newText: "new phrase",
  project: "default",
  expectedOccurrences: 1
)
```
Preview returns whether the edit is possible, how many matches were found, whether the request is ambiguous, and the current `contentHash` to use when applying.

### Apply only against the previewed version
```
ApplyEditDocument(
  key: "ref:devbrain-usage",
  oldText: "old phrase",
  newText: "new phrase",
  project: "default",
  expectedOccurrences: 1,
  expectedContentHash: "<hash from preview>"
)
```
If the document changed after preview, apply refuses the write and tells you to preview again.

### Recommended edit workflow
1. Call `PreviewEditDocument`
2. Confirm `WouldReplace: true` and the `MatchCount` is what you expected
3. Pass the returned `CurrentContentHash` into `ApplyEditDocument`
4. If apply reports that the document changed since preview, re-run preview and try again

### Useful guardrails
- Use `expectedOccurrences` to refuse zero-match or multi-match edits unless they are intentional
- Use `caseSensitive: true` when casing matters and you want to avoid accidental matches
- Use `UpsertDocument` when you are replacing the whole document on purpose; use preview/apply when you want a narrow, literal patch

## Editing Tags Without Re-Upserting

To adjust tag metadata on an existing document, use `EditTags` instead of re-sending the full content through `UpsertDocument`.

### Add and/or remove tags in one call
```
EditTags(
  key: "ref:devbrain-usage",
  project: "default",
  add: ["workflow"],
  remove: ["draft"]
)
```

The server applies the diff:
- Tags in `add` that are already present are no-ops
- Tags in `remove` that are absent are ignored (idempotent)
- A tag that appears in both `add` and `remove` is rejected — the call fails without writing
- If `add` and `remove` are both empty, nothing is written
- If the resulting tag set is identical to the current one, nothing is written

`EditTags` never modifies the document `content`. It touches `tags`, `updatedAt`, and `updatedBy` only. Use this whenever you just need to label or un-label an existing document — it's dramatically cheaper than an `UpsertDocument` that has to re-emit the whole body.

## Key Conventions

Every project key starts with the project name. Unprefixed keys are only for general DevBrain material in the `default` project.

Keys use **colon** as the separator. Slash-separated keys (`sprint/foo`) still work for backward compatibility, but colons are the canonical, recommended convention — they signal "DevBrain key" at a glance and avoid being confused with file paths.

| Prefix | Use |
|---|---|
| `{project}:sprint:{name}` | Sprint specs and retrospectives |
| `{project}:state:current` | Current project state |
| `{project}:arch:{name}` | Architecture docs |
| `{project}:decision:{name}` | Architecture decision records |
| `{project}:ref:{name}` | Project reference material |
| `ref:{name}` | General DevBrain reference material in the `default` project |

## If You Get No Results
1. Check that the key starts with the project name, e.g. `devbrain:state:current`
2. Check casing — project names and keys are case sensitive
3. Try specifying the project explicitly
4. Use ListDocuments with no prefix to see what's stored in that project
5. Try SearchDocuments with a broad keyword
6. The document may not exist yet — ask the user if they'd like to create it

## Frequently Asked Questions

### Does DevBrain persist across sessions?
Yes — fully. Cosmos DB is the backing store, not in-memory. Documents survive indefinitely across all sessions and all clients until explicitly deleted or overwritten.

### Is there a document size limit?
Cosmos DB has a 2MB per-document limit. Typical sprint specs (15-30KB) and state documents (up to ~40KB) are well within this limit.

### Are documents versioned?
No. UpsertDocument is full overwrite semantics — there is no history. The `updatedAt` field tracks the last write time but previous versions are not retained, so be deliberate about overwrites. Use the narrowest write for the change: `PreviewEditDocument` / `ApplyEditDocument` for exact text edits, `AppendDocument` to add entries to a growing log (session history, decision logs), `EditTags` for tag-only changes, and `UpsertDocument` only when you mean to replace the whole document.
