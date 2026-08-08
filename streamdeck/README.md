# ProsimCompanion — Stream Deck Plugin

A standalone Elgato Stream Deck plugin that fires GSX/checklist commands and shows
live status from a running **ProsimCompanion** instance. It talks to
ProsimCompanion's embedded web server over plain REST: it **fires semantic
commands** via `POST /api/command/{name}` and **reads live state** by polling
`GET /api/status` once a second (only while at least one key is visible). It
never touches raw GSX menu ordinals or LVARs — those stay inside the app.

---

## Prerequisites

- **Node.js 20+** on the dev machine.
- **Stream Deck app 6.5+** with the Elgato CLI: `npm i -g @elgato/cli`.
- A running **ProsimCompanion** with, in `config/settings.json`:
  - a `webUi.accessToken` (shown as the onboarding URL / QR in the web UI), and
  - `commandApi.enabled: true` — the command surface is opt-in; without it every
    key shows an "API off / enable command API" state (HTTP 404).

> The web server binds to **loopback** by default, so run the Stream Deck app on
> the sim PC unless you enable `webUi.bindToAllInterfaces` in ProsimCompanion.

## Build & install

> **End users don't need any of this:** the ProsimCompanion installer offers a "Stream Deck
> plugin" task (shown when the Elgato app is detected) that installs/updates the plugin
> directly, and also ships the packed `.streamDeckPlugin` to `{app}\streamdeck\` for manual
> double-click installs. The steps below are for development.

```bash
cd streamdeck
npm install
npm run build      # bundle src -> com.prosimcompanion.streamdeck.sdPlugin/bin/plugin.js
npm run pack       # build + emit dist/com.prosimcompanion.streamdeck.streamDeckPlugin
```

Double-click the emitted `.streamDeckPlugin` to install it, or during development
use `streamdeck link com.prosimcompanion.streamdeck.sdPlugin` then `npm run watch`.

> **Icon assets:** action/category icons are SVG under `imgs/` (accepted by the
> current `streamdeck validate`), but the plugin's marketplace `Icon` must be
> PNG — `imgs/plugin/marketplace.png` (+`@2x`) are rasterized from
> `marketplace.svg` (e.g. `npx sharp-cli -i marketplace.svg -o . -f png resize
> 144 144`); regenerate them if you edit the SVG. The live **key faces are drawn
> at runtime** as SVG via `setImage`, so they are unaffected.

## Pairing

Open any ProsimCompanion key's settings (Property Inspector) and either:

- paste the **Host / Port / Token** directly (default `127.0.0.1:5320`), or
- paste the **onboarding URL** from the ProsimCompanion web UI
  (`http://{host}:{port}/?token={token}`) into the "Pair via URL / QR" box —
  host, port and token are parsed out automatically.

Settings are stored as Stream Deck **global** settings, so all keys share the one
connection. If the token is rotated in ProsimCompanion, keys go to a **re-pair**
state (HTTP `401`) — paste a fresh token/URL and they recover on their own.

## Actions

| Action | What it does |
|---|---|
| **GSX Service** | Pick a service in the Property Inspector (refuel / catering / boarding / deboarding / jetway ± retract / stairs ± retract / GPU / de-ice / pushback / departure-services). Shows that service's live colour-coded state (+ pax `n/total` while boarding/deboarding, refuel `%` while fuelling); press fires the matching `gsx.request*` / `gsx.retract*` command. Greyed out when the service is not available in the current phase. |
| **Next Service** | Shows the ground automation's next planned service (`gsx.nextService`); press POSTs `gsx.forceNextService` to run it now. |
| **Checklist** | Shows the active checklist's name, current item and `index/count`. Pick the function: **Advance** (`checklists.advanceNext`) or **Restart** (`checklists.restart`). |
| **Flight Phase** | Display only — the current ProsimCompanion flight phase. |
| **Connection Health** | Display only — three dots for the ProSim / MSFS / GSX connections, plus distinct tiles for *set up* (unpaired), *connecting*, *re-pair* (token rejected) and *API off* (command API disabled). |

## Architecture

- `src/connection/connection-manager.ts` — the **single** shared REST client.
  Polls `/api/status` every 1 s while any key is visible (visible-key
  ref-counting), backs off exponentially (capped 30 s) on failure, surfaces
  token rotation (401) as a re-pair signal and a missing command API (404) as a
  distinct "API disabled" state. `post()` never throws.
- `src/actions/*` — one class per action; `base-status-action.ts` tracks visible
  keys and re-renders them on every state change.
- `src/rendering/tiles.ts` — runtime SVG key rendering (no per-state binary images).
- `com.prosimcompanion.streamdeck.sdPlugin/ui/*` — Property Inspectors
  (self-contained; no external component library, works offline).

## Troubleshooting

| Symptom | Meaning / fix |
|---|---|
| Keys show **set up** | No host/port/token yet — open any key's Property Inspector and pair. |
| Keys show **connecting** | ProsimCompanion (or its web server) is unreachable — check it is running, and that host/port match the web UI. The plugin retries with backoff automatically. |
| Keys show **re-pair** | The access token was rejected (HTTP 401) — it was rotated or mistyped. Paste a fresh token / onboarding URL. |
| Keys show **API off** | `/api/status` returned 404: the command API is disabled. Set `commandApi.enabled: true` in ProsimCompanion (Settings → web UI) and the keys recover without a restart. |
| Commands flash the alert triangle | The server refused the command — the key title briefly shows the reason (e.g. wrong phase, precondition failed, GSX unavailable). |
| Nothing updates while a folder is open | By design: polling only runs while at least one plugin key is visible. |
