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
| `gsx.requestRefuel` | — | Per-service calls (this row and the next ten) go through the `IGsxServiceControl` seam — the SAME serialized single-slot `service.trigger` path the departure automation uses, mirror-checked first, never a second writer and never raw menu ordinals. Outcomes: already requested/active/completed (or already called and awaiting GSX) → `alreadySatisfied`; GSX absent/not running → `unavailable`; not offered / not callable / slot busy with another service → `preconditionFailed` with the reason; GSX rejection → `failed`. |
| `gsx.requestCatering` | — | |
| `gsx.requestBoarding` | — | |
| `gsx.requestDeboarding` | — | Normally auto-called on stable-parked arrival (`gsx.autoCallDeboardOnArrival`); this is the manual path. |
| `gsx.requestJetway` | — | The operate trigger is a **toggle**: connected (Active/Completed) answers `alreadySatisfied`, never re-fires. `preconditionFailed` at jetway-less stands (jetway LVAR = 2 — GSX lists and acks the service but nothing moves; use `gsx.requestStairs`), and while ground preparation is still connecting jetway/stairs itself (single-writer rule; only during the Preflight/ColdAndDark prep window). |
| `gsx.retractJetway` | — | Triggers the toggle only when connected; otherwise `alreadySatisfied` ("nothing to retract"). `preconditionFailed` when GSX reports it cannot trigger (fall back to the gate menu). |
| `gsx.requestStairs` | — | Same toggle semantics as the jetway. |
| `gsx.retractStairs` | — | |
| `gsx.requestGpu` | — | Triggers GSX's `GPU` service (the visual truck). ProSim-side ground power/chocks are placed by the ground-equipment automation independently of this. |
| `gsx.requestDeice` | — | Triggers the `DeIce` service when GSX offers it; fluid/concentration questions are answered per `gsx.autoDeIce`/`deIceFluidType`. |
| `gsx.requestPushback` | — | **Always `preconditionFailed` — by design.** Pushback is beacon-orchestrated (`GsxPushbackSequenceService`): departure services complete + beacon on → doors close, jetway retracts, equipment clears on crew-realism delays, then the pushback call goes out (`gsx.callPushbackOnBeacon`). A direct trigger would race that sequence or push with the jetway attached. The reason text explains the flow (and points at the GSX menu when the beacon sequence is disabled). |
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

## Voice control over the same commands

`GsxVoiceService` (`src/ProsimCompanion.Speech/Gsx/`, gated by `gsx.voiceControlEnabled`,
default true) maps exact-match phrases (trimmed, case-insensitive) onto this registry, so voice
inherits every guard above and never grows a second write path. The phrase→command table lives
in `GsxVoicePhrases` — one catalog shared with the hail dialogues below (ADR-0006):

| Phrase(s) | Command |
|---|---|
| "commence ground services", "start ground services" | `gsx.startDepartureServices`, or `gsx.forceNextService` when the sequence is already started (falls back to force-next on `alreadySatisfied` when the departure seam is absent). Also releases the `gsx.groundPrepActivation = voice` prep gate. |
| "call the next service", "next service" | `gsx.forceNextService` |
| "request boarding" | `gsx.requestBoarding` |
| "start boarding", "cabin crew start boarding" | `gsx.requestBoarding` + a purser "Boarding underway." ack (tag `cabin.boarding.ack`) only on success/alreadySatisfied |
| "request refueling", "call the fuel truck" | `gsx.requestRefuel` |
| "request catering" | `gsx.requestCatering` |
| "request pushback" | `gsx.requestPushback` |
| "request de-icing" | `gsx.requestDeice` |

Each outcome is spoken briefly (arbiter priority Normal, tag `gsx.voice`): success speaks a
short confirmation ("Boarding requested."), everything else speaks the command's reason.

### Hail dialogues (ADR-0006, issue #51)

**"Cockpit to ground" is no longer a command** — it (and "flight deck to ground") hails the
ground crew: `CrewHailService` borrows the mic, the ground crew answers in its own voice
("Ground here — go ahead, captain", after the ACP INT receive latch like the purser's CAB
rule), then a narrow listening window (~8 s) accepts any phrase from the table above plus
cancel words. "Cockpit/flight deck to crew/cabin" does the same with the purser (boarding
phrases only). Successful hail requests are acknowledged by the hailed crew ("Copied — fuel
truck on the way"); refusals are relayed by the FO. Options: `groundCrew.*`,
`cabin.hailReplyText`; the ground voice is `voices.ground` with `accents.*` localization.

## `GET /api/status` — read-only state for live key faces

Mapped in `src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs`, mirroring the command API's
gate exactly: 404 while `commandApi.enabled` is off, then the same token-even-on-loopback auth.
Cheap and read-only — every value comes from an in-memory store/cached subscription, no sim
round-trips. camelCase JSON, string enums:

```json
{
  "phase": "preflight",
  "connections": { "prosim": true, "msfs": true, "gsx": true },
  "gsx": {
    "automationActive": true,
    "nextService": "Boarding",
    "services": [
      { "type": "Refueling", "state": "active", "detail": "3200/5400 kg" }
    ],
    "refuelPercent": 59.3,
    "paxBoarded": null,
    "paxTotal": 180
  },
  "checklist": { "name": "Before Start", "item": "Beacon", "index": 4, "count": 9 }
}
```

- `phase`: the camelCased `FlightPhase` (`"unknown"` when the engine is absent).
- `connections`: `ConnectionStatusStore` states (`true` = Connected) for ProSim, SimConnect
  (reported as `msfs`) and GSX.
- `gsx`: `null` when the GSX pillar is absent. `services` is the departure status board in
  configured order (`state`: `notAvailable` (waiting) | `callable` (held — `detail` carries the
  reason) | `requested` (called or accepted) | `active` | `completed` | `skipped`), falling
  back to the raw mirror service list before the automation publishes a board. `nextService` is
  the first row still waiting/held — what force-next would go for. `refuelPercent` is non-null
  only while a fuel transfer runs; `paxBoarded`/`paxTotal` come from GSX's boarding counters
  (null until the pax target is armed).
- `checklist`: the active visual checklist (`item` = active line's label, `""` once complete),
  `null` when none is selected or the pillar is absent.

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
