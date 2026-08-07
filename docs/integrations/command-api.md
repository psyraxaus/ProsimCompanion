# HTTP command API

The single command seam for remote surfaces (web page buttons, Stream Deck, scripts): named
commands in a `CommandRegistry` (`src/ProsimCompanion.Core/Commands/`), exposed over two
minimal-API routes mapped in `src/ProsimCompanion.App/Hosting/CommandApiEndpoints.cs`.

Ported (clean re-implementation, not a copy) from Prosim2GSX's `Commands\CommandRegistry` +
the `StreamDeckIntegration` branch's `CommandController`/`GsxCommandOutcome` model. Deltas from
the predecessor: no WPF dispatcher marshalling (this app's stores are UI-agnostic), one generic
`POST /api/command/{name}` route instead of one controller action per command, and a stricter
loopback policy (below).

## Routes

| Route | Purpose |
|---|---|
| `GET /api/commands` | `{ "commands": ["checklists.advanceNext", ...] }` — registered names, sorted. |
| `POST /api/command/{name}` | Execute. Optional JSON body (per-command request DTO); an empty body means an empty request — per-command validation decides which fields were required. |

JSON is camelCase with string enums (`"outcome": "alreadySatisfied"`). Every command response
body is `{ "outcome": ..., "reason": ... }` (plus extra fields for richer results such as
`fms.syncInit`) — never a bare ack. Error responses (400/401/404) carry `{ "reason": ... }`.

Naming convention: dotted lowercase area + camelCase verb — `gsx.forceNextService`,
`minima.set`.

## Command inventory (this slice)

| Command | Body | Notes |
|---|---|---|
| `gsx.startDepartureServices` | — | Idempotent; `alreadySatisfied` once started. |
| `gsx.forceNextService` | — | The INT/RAD "smart button": bypasses the next service's activation rule. |
| `gsx.requestGate` | `{"gate":"B12"}` | Arms an arrival gate request. |
| `gsx.cancelGate` | — | |
| `checklists.select` | `{"name":"Before Start"}` | Case-insensitive; unknown name → 400. |
| `checklists.check` | `{"index":0}` | Must be the active line; auto items refuse (409) — skip instead. |
| `checklists.skip` | `{"index":0}` | Auto items may be skipped. |
| `checklists.restart` | — | |
| `checklists.deselect` | — | |
| `checklists.advanceNext` | — | Checks the active line whatever its index — the one-button flow. |
| `loadsheet.generatePreliminary` | — | |
| `loadsheet.generateFinal` | — | Needs a prior prelim (failure reports it). |
| `loadsheet.resetCycle` | — | |
| `fms.syncInit` | — | Success payload adds `source`, `zfwTonnes`, `zfwCgPercent`, `blockTonnes`. |
| `ofp.fetch` | — | Forced SimBrief re-fetch/re-import. |
| `minima.set` | `{"kind":"da","altitudeFt":740}` | `kind`: `da`/`dh`/`mda` (or enum names). 0–20000 ft. |
| `minima.clear` | — | |
| `speech.speakTest` | `{"text":"Radio check"}` | ≤ 300 chars (routes into real, possibly paid, TTS). |

## Outcome → HTTP status

Mapping lives in `CommandOutcomeHttp` (Core, pure, unit-tested):

| Condition | Status |
|---|---|
| `success` / `alreadySatisfied` | 200 |
| `phaseMismatch` / `preconditionFailed` | 409 |
| `unavailable` (pillar not running, or registry not wired) | 503 |
| `failed` | 500 |
| `CommandValidationException` (bad/missing field, unknown checklist) | 400 |
| Unknown command name | 404 |
| Malformed JSON body | 400 |

## Gate & auth

Settings section `commandApi` (`CommandApiOptions`):

- `enabled` — **false by default**. This is a write surface (it actuates ground services,
  checklists, the MCDU), so it is strictly opt-in; while disabled every `/api/command*` route
  answers 404 as if it did not exist.
- `requireTokenOnLoopback` — **true by default**. Deliberate delta from `LanTokenMiddleware`
  (where loopback always passes so the local UI can never be locked out): any local process can
  reach loopback, and "any local process may actuate the aircraft" is not an acceptable default.

Credentials (both fixed-time compared; the token is the `webUi.accessToken` and is never
logged):

- `Authorization: Bearer <token>` header — the Stream Deck/scripting path, or
- the `prosimcompanion-token` cookie an onboarded browser session already carries.

Non-loopback requests additionally pass through `LanTokenMiddleware` first, unchanged.

## Stream Deck client (next slice)

The predecessor plugin lives in `C:\Users\johncarlo\git\Prosim2GSX`, branch
`StreamDeckIntegration`, folder `StreamDeckPlugin/` (`com.prosim2gsx.streamdeck.sdPlugin`) —
consult it for action design, runtime SVG key rendering, and the connection manager, but do not
copy code.

Pairing model to keep: Host/Port/Token entered in a key's Property Inspector, or parsed from
the onboarding URL/QR (`http://{host}:{port}/?token={token}` in this app); stored as Stream
Deck **global** settings so all keys share one connection; a rotated token surfaces as a
"re-pair" state (REST 401).

The intended client is a **Node-based Elgato plugin**, not Stream Deck's built-in "Website"
action: the built-in action can only fire tokenless GETs, while this API needs POSTs with an
`Authorization: Bearer` header (and live key faces need state reads). Until that plugin exists,
anything that can set a header works, e.g.:

```
curl -X POST http://localhost:5320/api/command/checklists.advanceNext ^
     -H "Authorization: Bearer <token>"
```
