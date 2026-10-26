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

Every request also carries `User-Agent: ProsimCompanion-StreamDeck/<plugin version>` (the
version from `manifest.json`, via the SDK's `streamDeck.info.plugin.version`). The app's
`/api/*` middleware records the last request per address and user agent, and the diagnostics
REMOTE page (`/diagnostics/remote`) shows the deck as connected while that user agent polled
within the last 5 s, amber when older, `XX` when it never polled. Node's `fetch` allows the
header to be set; the value is informational only and never gates a request.

- `GET /api/status` — polled at 1 Hz while any key is visible (exponential
  backoff capped at 30 s on failure):
  `{ phase, connections:{prosim,msfs,gsx}, gsx:{ automationActive, nextService,
  services:[{type,state,detail}], refuelPercent, paxBoarded, paxTotal,
  paxRemaining }, checklist:{name,item,index,count}, voice:{listening,paused} }` —
  sections may be null; `paxRemaining` counts down during a deboard and is null
  otherwise; service `state` is `notAvailable|callable|requested|active|completed|skipped`;
  `voice` is null when the speech pillar is absent (`listening` = engine truth,
  `paused` = the pilot's ear-off latch behind the Voice Pause key);
  `switches:[{id,value,position,upperLit,lowerLit,stale}]` (2026-10-24) is the live
  state of the configured cockpit switches — null without the ProSim pillar.
- `GET /api/controls` — read on connect, every 30 s and when a key's Property Inspector
  opens: `{ switchesEnabled, switches:[{id,name,look,pressMode,upperLegend,
  lowerLegend,upperColor,lowerColor,bezelColor,showUnlitLegend,positions:[{value,label}]}],
  viewsEnabled, views:[{name,button}] }` — the configuration the Cockpit Switch and
  Camera View keys draw from (`cockpit-switches.md`); no dataref names on the wire.
- `POST /api/command/{name}` — optional JSON body; response `{outcome, reason}`
  with outcome `success|alreadySatisfied|phaseMismatch|preconditionFailed|
  failed|unavailable`. Command names used: `gsx.startDepartureServices`,
  `gsx.forceNextService`, `gsx.request{Refuel,Catering,Boarding,Deboarding,
  Jetway,Stairs,Gpu,Deice,Pushback}`, `gsx.retract{Jetway,Stairs}`,
  `checklists.advanceNext`, `checklists.restart`, `speech.toggleListening`,
  `cockpit.press {id, long}`, `input.pressView {name}`.
- Error semantics the key states depend on: HTTP **401** → re-pair (token
  rotated), HTTP **404** on `/api/status` → command API disabled. Onboarding
  URL format is `http://{host}:{port}/?token={token}` (query string, not
  fragment) — the Property Inspector's pair-via-URL box parses it.

## Running the plugin on another PC

Nothing in the plugin is loopback-specific: with `webUi.bindToAllInterfaces` on (Settings →
Setup → Web server → LAN access, restart), TCP 5320 allowed through the sim PC's firewall and
the onboarding link pasted into a key, the plugin runs in the Stream Deck app of any PC on the
LAN. The installer only installs it on the sim PC; copy `{app}\streamdeck\
com.prosimcompanion.streamdeck.streamDeckPlugin` to the other PC and double-click it there.
Manual chapter 12 has the steps. Everything the remote deck needs goes through this one plugin
(owner decision 2026-10-24): the cockpit-switch and camera-view keys below press ProSim switches
and vJoy buttons on the sim PC from there (`cockpit-switches.md`).

## Historical reference

The predecessor plugin (WebSocket push + Prosim2GSX's command names) is on the
`StreamDeckIntegration` branch of the Prosim2GSX repo under `StreamDeckPlugin/`.
It is a design reference only — the wire contract here is different (REST
polling, camelCase states, `?token=` pairing, port 5320).
