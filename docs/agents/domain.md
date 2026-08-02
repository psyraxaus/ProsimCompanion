# Domain Docs

How the engineering skills should consume this repo's domain documentation when exploring the codebase.

## Before exploring, read these

- **`CONTEXT.md`** at the repo root, if it exists.
- **`docs/decisions/`** — this repo's ADRs. Naming: `ADR-NNNN-short-slug.md` (e.g. `ADR-0001-web-first-ui.md`). Read the ADRs that touch the area you're about to work in. Skills that record architectural decisions write **here** (not `docs/adr/`), continuing the existing numbering.
- **`docs/integrations/`** — distilled protocol/dataref/LVAR knowledge for ProSim, GSX, SimBrief, etc. Consult before re-deriving any external-integration detail (this is a repo convention from `CLAUDE.md`, restated here because it plays the same role as domain docs).

If `CONTEXT.md` doesn't exist, **proceed silently**. Don't flag its absence; don't suggest creating it upfront. The `/domain-modeling` skill (reached via `/grill-with-docs` and `/improve-codebase-architecture`) creates it lazily when terms or decisions actually get resolved.

## File structure

Single-context repo:

```
/
├── CONTEXT.md                 ← created lazily by /domain-modeling; absent today
├── docs/decisions/            ← ADRs (this repo's convention; use instead of docs/adr/)
│   ├── ADR-0001-web-first-ui.md
│   ├── ADR-0002-blazor.md
│   └── ...
├── docs/integrations/         ← hard-won external-integration knowledge
└── src/
```

## Use the glossary's vocabulary

When your output names a domain concept (in an issue title, a refactor proposal, a hypothesis, a test name), use the term as defined in `CONTEXT.md`. Don't drift to synonyms the glossary explicitly avoids.

If the concept you need isn't in the glossary yet, that's a signal — either you're inventing language the project doesn't use (reconsider) or there's a real gap (note it for `/domain-modeling`).

## Flag ADR conflicts

If your output contradicts an existing ADR, surface it explicitly rather than silently overriding:

> _Contradicts ADR-0001 (web-first UI) — but worth reopening because…_
