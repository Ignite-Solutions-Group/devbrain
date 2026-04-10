# Project Initialization

When you start using DevBrain for a new project, seed it with a small set of canonical documents so every AI tool has enough context to be useful from the first prompt. This guide lists the recommended starter documents and what each should contain.

All documents below are namespaced by the `project` parameter — pass the same project name consistently across every `UpsertDocument` and `GetDocument` call.

## Required

### `state:current` — the living project snapshot

The single most important document. Every AI session should start with `GetDocument(key="state:current", project="{your-project}")`. Keep it short enough to load cheaply, detailed enough to orient a new session without follow-up questions.

Recommended sections:

- **What We're Building** — one paragraph on the product and who it's for
- **Stack** — languages, frameworks, key services, deployment target
- **Current Focus** — what's actively being worked on right now
- **Key Repos/People** — where the code lives, who owns what
- **Recent Decisions** — last few architectural or scope decisions, with dates

Update whenever any of those sections materially change. Treat it as the canonical handoff document between sessions.

## When a sprint is active

### `sprint:{name}` — the active sprint spec

Create one per sprint, keyed by a short slug (e.g. `sprint:license-sync`). Load it at session start whenever work on that sprint is in flight.

Recommended sections:

- **Goal** — the single sentence outcome this sprint is trying to achieve
- **Scope** — what's in, what's explicitly out
- **Tasks** — checklist of work items, marked as they complete
- **Decisions Made** — decisions taken during the sprint, with reasoning
- **Blockers** — anything stopping progress, with owner

When the sprint ends, leave the document in place as a historical record — don't delete it. Future sessions can reference it via `SearchDocuments`.

## Recommended

### `arch:overview` — high-level architecture

A stable document describing the system's shape: major components, data flow, external dependencies, deployment topology. Updated infrequently — only when the architecture actually changes. Keep it short; link to `decision:{name}` documents for the reasoning behind specific choices.

## Seeding a new project

A minimal first-day setup:

```
UpsertDocument(key="state:current",  project="{your-project}", content="...")
UpsertDocument(key="arch:overview",  project="{your-project}", content="...")
```

Add `sprint:{name}` documents as sprints start. Add `decision:{name}` and `ref:{name}` documents as the project accumulates history.
