# GSX Couatl Remote API v2 — wire-protocol specification

Extracted 2026-08-01 from Prosim2GSX's flight-tested implementation (`GSX\RemoteApi\*`,
`docs\00-shared-context.md`, phase docs). This is the contract ProsimCompanion's clean-room
client is written against. Transport: **WebSocket, text frames, JSON, localhost only**; one
long-lived socket; state is push-only (snapshot + patches) after an explicit subscribe.
Protocol version **1** is the only supported version.

## 1. Connection

**URL**: `ws://127.0.0.1:{port}` (no path). Port from `%APPDATA%\Virtuali\CouatlAddons.ini`,
section `[gsx]`, key `remote_server_port` (case-insensitive section/key, key only inside
`[gsx]`, `;`/`#` comments and inline comments stripped, last-wins duplicates). **Default 8744**
on any failure. Re-read the ini on **every** connection attempt (never cache — a stale cached
port silently degraded once; the key is deliberately not configurable in-app).

**Reconnect**: single background loop; on loss → state Disconnected, pending commands completed
with synthetic `not_connected`, wait ~5000 ms, retry. First consecutive failure logs Warning,
subsequent Debug only. Reset the protocol-mismatch latch per session. Accumulate WS frames until
`EndOfMessage` (messages span frames).

**Readiness is three states** (server listens while GSX itself boots):
`Disconnected` → `ConnectedGsxNotRunning` (socket open but `gsxRunning` false / missing required
capability / protocol ≠ 1) → `Ready`. Required capabilities for Ready: `gate` **and**
`handlerData` (case-insensitive). `handlerSet` is optional, feature-detected per call.

**Handshake**: the **server speaks first** with an unsolicited hello:
```json
{ "type": "hello", "protocol": 1, "gsxRunning": true,
  "capabilities": ["gate", "handlerData", "handlerSet"] }
```
Client then subscribes: `{ "type": "subscribe", "channels": ["state"] }` → server acks (result
without id — ignore) and sends a full `snapshot`. If the subscribe send fails, abort the socket
and reconnect clean (no re-subscribe path; a connected-but-unsubscribed client is silently
starved). A new hello on a live session = fresh session (clear restart latches).

## 2. Envelope

Every server frame carries `"v": 1` — guard like `hello.protocol` (≠1 → warn once, hold
non-Ready). Parse everything field-by-field and leniently: every field is optional; unknown
frame types/topics/codes are ignored or round-tripped, never thrown on.

**Commands (client → server)**:
```json
{ "type": "command", "id": "g-1", "verb": "gate.select", "args": { } }
```
`id` = unique client string echoed in the result (per-verb prefixed counter). **Omit `args`
entirely** for arg-less verbs (`menu.open`, `menu.close`) — not `{}`.

**Results**: `{ "v":1, "type":"result", "id":"g-1", "ok":true, "payload":{ "code":"ok", … } }`
or `"ok":false, "error":{ "code":"ambiguous", … }`. Correlate by id; result without string id is
unsolicited. Codes are strings. Client-side timeout ~10 s → synthetic `timeout`; other synthetic
codes: `not_connected`, `gsx_not_running`, `bad_args`.

**Snapshot**: `{ "v":1, "type":"snapshot", "ts":…, "state": { … } }` — the model may arrive
under `state` **or inline beside the envelope**; iterate top-level keys skipping
`v`/`type`/`ts`/`id`.

**Patch** (coarse — whole top-level key replaced):
`{ "v":1, "type":"patch", "path":"/services", "value":[…] }`. Trim leading `/`;
`value: null`/absent **removes** the key. No deep patching exists.

**Events**: `{ "v":1, "type":"event", "topic":"engine", "gsxRunning":false, "restarting":true }`.
Only `engine` is consumed. `restarting:true` arrives **before** the socket drops — latch it to
suppress the reconnect warning and expect a new `startup.sid`.

## 3. Verbs

There is **no `state.get`** in practice — state is purely push.

### gate.select
`args: { "gate": "B12", "revokeServices": false, "force": false }` — `gate` is a display token
as typed in GSX's in-game gate search (uppercased, no "Gate" prefix). Success codes: `ok`,
`prepared`, `already_selected`, `already_parked` (payload may carry `gate` ref + `warnings`).
Failure codes: `not_found`; `ambiguous` (with `error.candidates[]` of
`{uiName, gate, number, bglName}`); `services_active` (retry with `revokeServices:true`);
`assigned_to_other` (retry with `force:true`, `error.gate` = occupant); `no_airport` and
`gsx_not_running` (not failures — stay armed). Prepares/assigns arrival gate only; it does not
move the aircraft.

### service.trigger
`args: { "service": "Refueling" }`. Canonical ids (exact casing, matched case-insensitively):
`Boarding`, `Deboarding`, `Refueling`, `Catering`, `Departure` (pushback request), `GPU`,
`DeIce`, `Water`, `Lavatory`, `Cleaning`, `OperateJetways`, `OperateStairs`. Response is a plain
ack — the real outcome is observed via the state mirror. Pre-flight before sending: Ready;
service present in mirror; skip if already requested/performing/completing/completed
(idempotent); require `canTrigger == true`. Jetway/stairs trigger is a **toggle**; retract via
trigger when `canTrigger`, else via the gate-menu `^operate jetway|stairs` intent.

**One trigger in flight at a time — confirm before the next.** GSX silently drops rapid-fire
triggers: five sent in the same instant left only the LAST one running while every ack came
back ok (round-7 smoke test, 2026-08-02 — only Cleaning ran of
Refueling/Catering/Water/Lavatory/Cleaning). A call counts as taken only when the mirror (or a
latched lifecycle edge — quick services can bounce straight back to `available`) shows the
service requested/active/completed; until then no further trigger goes out, and an unconfirmed
call times out (~10 s) and is re-sent. Concurrency is GSX's job, not the sender's: request one
at a time and GSX runs the accepted services side by side (legacy Prosim2GSX called at most one
service per 500 ms tick and verified `IsCalled` from state before advancing).

### menu.pick
`args: { "index": 2 }` — **0-based** into the mirrored `menu.entries`; always re-resolve against
the latest menu immediately before picking (TOCTOU). Refusal code `disabled` = greyed entry —
map to unavailable, **never retry**.

### menu.open / menu.close
No args. **Never use `menu.toggle`** (exists but toggling an already-shown menu closes it and
races the re-raise — flight-verified). Skip `menu.open` when the target menu is already shown
with a matching title. `menu.close` explicitly dismisses answered question prompts (GSX doesn't
reliably do it) — no-op when `menuShown` is already false.

### handler.set
`args: { "target": "gate", "name": "autoSelectOperator", "value": true }` — feature-detect via
the `handlerSet` capability. Used once per gate session (key = gate-context key + `|` +
`startup.sid`; re-arm on gate change or Couatl restart) so the operator popup never surfaces;
on absence/failure, the operator-picker menu intent answers instead.

## 4. State mirror

Top-level keys consumed:

- **`handlerData`**: `{ airport: { icao, parkings: [{uiName, uiGateName, bglName, number,
  terminal, type, lat|latitude, lon|longitude}] }, gate: {uiName, bglName, number} }` — every
  field optional (blacklist-mirrored). `gate` present ⇒ gate context loaded; its
  `uiName ?? bglName ?? number` is the stable gate-context key.
- **`startup`**: `{ "sid": "…" }` — changed non-empty sid ⇒ engine restarted: invalidate
  airport/gate/menu caches, re-arm pending gate request.
- **`services`** (array, keyed case-insensitively by `id`):
  `{ id, displayName, state, stateRaw, stateText, canTrigger, waiting, operator,
  progress: {current,total,unit}, progressText }`. **Control flow keys ONLY on the semantic
  `state` string**: `available`→Callable, `unavailable`→NotAvailable, `bypassed`→Bypassed,
  `requested`→Requested, `performing`/`completing`→Active, `completed`→Completed, else Unknown.
  `stateRaw`/`stateText`/`progressText` are diagnostics only.
  - **Return-to-available = completed**: quick services (Water/Lavatory/Cleaning; refuel-adjacent)
    go `performing` → `available` with no `completed` edge — treat `available` after
    was-active as completed, or "after all services" gating never fires. (Descendant of the
    LVAR quirk where `*_STATE` completes back at value 1.)
  - Reconcile tick: if mirror already reads completed (or available-after-active) but the model
    hasn't fired completion, re-fire (missed-edge catch-up).
  - **Jetway/Stairs state stays LVAR-authoritative** (mirror can read a docked jetway as
    `completed`): `IsConnected = Active && OPERATE*_STATE idle`, with the 30 s stale-Active
    grace for the GSX Pro v4 stuck-operation-LVAR defect.
- **`menu`**: `{ title, header, subtitle, layout, entries: [string], disabled: [bool] }` —
  entry index = pick index; parallel `disabled` array; `null` = menu gone.
- **`menuShown`** (bool): **authoritative** open/closed flag. Quirk: after `menu.open` the
  cached `menu.title` stays stale for a beat while `menuShown` is false — always gate waits on
  `menuShown && title` (picking in that window is silently dropped and the parent reappears).
- **`message`**: `{ text, visible }` — on-screen GSX tooltip, diagnostics only.

## 5. Menu intent framework

An intent = expected title prefix(es) + text/regex line matcher — **never a bare ordinal**.
Pipeline: preconditions (phase/state/Ready — Ready failure ⇒ leave-for-user, no fallback) →
navigation (recurse parent menus; `menu.open` only if not already shown with matching title;
poll ≤5 s for `menuShown && title StartsWith prefix`) → title check → resolve (regex over
entries; 0 matches → ItemNotAvailable, >1 → AmbiguousMatch — both no-pick) → TOCTOU re-resolve →
disabled guard → `menu.pick` → verify (poll ≤5 s, per-intent overridable — reposition submenu
can exceed 5 s; verify = title moved off/onto expected prefix, against the mirror) → on any
failure: **menu left open for the user, logged — never a wrong click**.

GSX-raised questions dispatch on the **rising edge of the menu title** (dispatch once per title
appearance; clear the edge when the menu hides), serialized off the WS receive thread. The
question/answer catalogue (title prefix → regex/choice) is in the Prosim2GSX report — port the
table verbatim when building the dispatcher; notable: de-icing fluid/concentration token match,
operator-preference matching (incl. the `[GSX choice]` token), pushback direction by text with
meta-line-guarded positional fallback.

**The pushback invariant**: *intents answer menus; LVARs decide when.* Timing gates (e.g.
engine-start confirm requires brakes set + both engines running +
`VEHICLE_PUSHBACK_STATE == 12`) live outside intents — an intent firing on appearance alone
would answer a menu raised for a different reason.

## 6. Gate selection — retry ladder + confirmation

Arm-then-dispatch: dispatch only when Ready ∧ `handlerData.airport.icao` known ∧ destination
known ∧ loaded airport == destination; otherwise stay armed, re-dispatch on state changes;
re-arm on `sid` change. **At most one auto-retry total per user request.**

1. Plain: `{gate, revokeServices:false, force:false}`
2. `ambiguous` → pick unique candidate (exact normalized `uiName`/`gate` match, else unique
   normalized suffix match); resend `gate` = candidate's `bglName ?? uiName ?? gate`.
   Normalization = strip non-alphanumerics, uppercase.
3. `services_active` → resend with `revokeServices:true`
4. `assigned_to_other` → resend with `force:true`

`not_found` → fail with nearest-name suggestions from the parkings cache (exact → suffix →
contains, max 3). Success is only **provisional**: confirm via readback LVARs
`L:FSDT_GSX_SetGate_Name/Number/Suffix` within a **60 s window** (check immediately too —
re-assigning the same gate may already match). Letter map: Name 0 = NONE, 10 = "Gate {n}",
12..37 = A..Z → "{Letter}{Number}"; Suffix −1 = unassigned. Window expiry → "Assigned
(unconfirmed)", never a failure.

## 7. Locked decisions (carry verbatim)

1. Remote API is the only path — no menu-file walker, no handler script for control.
2. No v1 fallback: not Ready ⇒ leave for the user, logged.
3. Off-loop dispatch — the WS receive thread drives actions; no tick-loop command queue.
4. The mirror backs service state at the source; consumers keep the service-state model.
   (Exception: jetway/stairs stay LVAR-authoritative + 30 s grace.)
5. `detail`/`stateText`/`stateRaw` are diagnostics only — control flow keys on the semantic
   `state` string.
6. Safe-fail matching — text-resolved picks; a miss is ItemNotAvailable, never a wrong click.
7. `handler.set autoSelectOperator` once per gate session; operator intent as fallback.
9. External-control write LVARs unchanged (`SET_REMOTECONTROL`, `DISABLE_DOORS_MSG`) — both
   reset to 0 on Couatl restart/aircraft change, re-assert periodically.

## 8. LVAR ↔ API boundary

**The API drives the answer; LVARs drive the timing.** Stays on LVARs: pushback fine-state
(`PUSHBACK_STATUS`, `VEHICLE_PUSHBACK_STATE` 8/11/12/13/14, `BYPASS_PIN`),
`FUELHOSE_CONNECTED`, pax/cargo counters, `DEICING_TYPE`, jetway/stairs
`OPERATE*_STATE`, `SetGate_*` readback, external-control writes, `COUATL_STARTED` liveness
(+ registry install path `HKCU\SOFTWARE\FSDreamTeam\root`). Everything else on the API.

GSX Pro **4.0.0** is the hard-targeted version (renamed submenu "Additional Services";
no 3.9.x fallback). `gate.select` identity (settled empirically, 2026-09-05 EPWA flight):
it matches **GSX's own facility display name** — `{"gate":"Parking 14R"}` (the name from
GSX's menu) returned `ok`, while at a stand GSX did not recognize, every ProSim-derived
identity (`'Stand 311'`, `'Terminal 3 (301-365)|Stand 311'`, bare `'311'`) returned
`not_found` (2026-09-05 EGLL, issue #75). Prefer names GSX itself has shown (menu entries,
`Change Facility [...]`) over ProSim gate keys when anchoring.

**Client config defaults**: reconnect 5000 ms, command timeout 10 000 ms, intent verify 5000 ms,
menu-open wait 5000 ms. All frames go through `IWireTrace` ("GsxRemoteApi" channel).
