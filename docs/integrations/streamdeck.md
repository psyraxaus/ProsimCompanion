# Stream Deck plugin

The Elgato Stream Deck plugin lives in **`streamdeck/`** at the repo root — a
self-contained TypeScript Node plugin (`com.prosimcompanion.streamdeck`), built
with rollup against `@elgato/streamdeck` v1. Build/install/pairing instructions
are in [`streamdeck/README.md`](../../streamdeck/README.md). The installer builds
and ships it: an optional task installs it into the Elgato plugins folder, and the
packed `.streamDeckPlugin` lands in `{app}\streamdeck\` for manual installs
(`installer/README.md`).

## API contract (what the plugin consumes)

REST-only against the embedded web server (default `http://127.0.0.1:5320`),
`Authorization: Bearer {webUi.accessToken}` on every request (required even on
loopback). Requires `commandApi.enabled: true`.

- `GET /api/status` — polled at 1 Hz while any key is visible (exponential
  backoff capped at 30 s on failure):
  `{ phase, connections:{prosim,msfs,gsx}, gsx:{ automationActive, nextService,
  services:[{type,state,detail}], refuelPercent, paxBoarded, paxTotal,
  paxRemaining }, checklist:{name,item,index,count} }` — sections may be null;
  `paxRemaining` counts down during a deboard and is null otherwise; service
  `state` is `notAvailable|callable|requested|active|completed|skipped`.
- `POST /api/command/{name}` — optional JSON body; response `{outcome, reason}`
  with outcome `success|alreadySatisfied|phaseMismatch|preconditionFailed|
  failed|unavailable`. Command names used: `gsx.startDepartureServices`,
  `gsx.forceNextService`, `gsx.request{Refuel,Catering,Boarding,Deboarding,
  Jetway,Stairs,Gpu,Deice,Pushback}`, `gsx.retract{Jetway,Stairs}`,
  `checklists.advanceNext`, `checklists.restart`.
- Error semantics the key states depend on: HTTP **401** → re-pair (token
  rotated), HTTP **404** on `/api/status` → command API disabled. Onboarding
  URL format is `http://{host}:{port}/?token={token}` (query string, not
  fragment) — the Property Inspector's pair-via-URL box parses it.

## Historical reference

The predecessor plugin (WebSocket push + Prosim2GSX's command names) is on the
`StreamDeckIntegration` branch of the Prosim2GSX repo under `StreamDeckPlugin/`.
It is a design reference only — the wire contract here is different (REST
polling, camelCase states, `?token=` pairing, port 5320).
