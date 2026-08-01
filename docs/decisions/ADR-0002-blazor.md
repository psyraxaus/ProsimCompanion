# ADR-0002: Blazor (interactive server) for the web UI

**Status:** Accepted (2026-08-01, project owner decision)

## Context

Prosim2GSX's web EFB is Vite + React + TypeScript: a second language, a Node build step (fail-soft
MSBuild target), and hand-written DTO mirroring between C# and TS.

## Decision

The web UI is Blazor with interactive server rendering, hosted in the same Kestrel/DI container as
the rest of the app.

## Consequences

- C# end-to-end: components bind directly to the same options classes and state stores the backend
  uses — no DTO mirroring, no REST layer for our own UI, no Node in the build.
- Live updates come free over the Blazor circuit (no hand-rolled WebSocket state fan-out).
- Server rendering fits a LAN-local app (latency negligible, state is server-side anyway).
- Trade-off accepted: browser UI requires the app running (it always is — it *is* the app), and the
  React EFB's component ecosystem is given up in favour of one language.
