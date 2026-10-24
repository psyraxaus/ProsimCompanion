# ECAM abnormals and memory drills — the definition language

The abnormals pillar (Speech, `ProsimCompanion.Speech.Abnormals`) is **detect-and-report only**: it
reads the E/WD text and system datarefs, announces, and walks the procedure with the pilot line by
line. It never actuates a switch and never injects a failure. Definitions are JSON files, one per
procedure or drill, in `%LOCALAPPDATA%\ProsimCompanion\config\abnormals\` (seeded from the shipped
`config\abnormals` with keep-user-edits, ADR-0007). The 30 Prosim2FO files are carried verbatim;
package 8 of the November 2026 batch (#187) added the 31st and two extensions to the language.

## The file

| Field | Meaning |
|---|---|
| `id`, `title` | Stable id (the session events carry it) and the E/WD-style title. |
| `class` | `"memoryDrill"` for a drill (items read straight through, Critical); anything else is an ECAM procedure (interactive per-line dialogue). |
| `severity` | `"warning"` (Critical speech, master warning acknowledged) or `"caution"` (High). |
| `enabled` | A disabled file never arms. |
| `phases` | Arming phases (FlightPhase names). Empty = every phase of a flight — never `Unknown`, never `ColdAndDark` (issue #116). |
| `voiceTriggers` | Drill rehearsal phrases ("windshear escape") — read the file straight through, any phase. |
| `trigger` | See below. |
| `announce` | The FO's first words. |
| `actions[]` | Drill: `say` lines. Procedure: `kind memory|action`, `say`, `confirm[]` phrases, optional `verify` condition + `ack` / `discrepancy`, optional `condition` to skip the line. |
| `status[]` | Spoken after the actions. |
| `contextFlags[]` | Flags other features read (`engine1Out`, `singleEngine`, `abnormalLanding`). |
| `clearedWhen` | The ONLY thing that clears a latched fault (issue #103: a trigger signal dropping is not proof). |
| `sequencer` | **New (#187).** A drill whose items a service paces on the flight data; see below. |

## `trigger`

| Field | Meaning |
|---|---|
| `ewdText[]` | E/WD substrings, case-insensitive — the primary signal. |
| `condition` | A dataref condition (the checklist `VerifyCondition` language: `dataref`, `op` equals/notEquals/lessThan/greaterThan/between/in, `value` / `low` / `high` / `values`, or `logic and|or` + `conditions[]`). Unreadable = false. |
| `logic` | `"any"` (default) or `"all"` when both text and condition are present. |
| `corroborate` | A light that must read > 0.5 — a master light or an engine fault light, **never** an ECAM page button (#103). |
| `debounceSeconds` | The rising edge must hold this long (≥ 0.5 s). |
| `holdDuringDeice` | Held while the GSX de-icing crew is Requested→Completed (#123). |
| `window` | **New (#187).** The takeoff window the trigger must fall in; see below. |

### `trigger.window` — the takeoff window

```json
"window": { "afterV1": true, "maxAglFt": 400 }
```

What the condition language cannot say: "after the V1 callout" compares two live figures, the IAS
and the FMS V1, and "below 400 ft above the airfield" is the AGL figure. The monitor judges the
window from the flight engine's snapshot (`FailureMonitor.WindowOpen`, pure, tested):

- `afterV1` — on the ground: IAS at or above `V1Kt` and V1 entered (> 0); off the ground: open.
- `maxAglFt` — off the ground: `AltitudeAglFt` under the ceiling; on the ground: open.
- No snapshot (flight not live): closed, like an unreadable condition.

A window with neither field is always open. The window is checked after the condition and before
the corroborate light, every tick; the debounce clock runs only while it is open.

### `sequencer` — a drill paced on the flight data

```json
"class": "memoryDrill",
"sequencer": "efato",
```

A plain drill reads its `actions` straight through with 350 ms gaps — right for stall, TCAS,
windshear, EGPWS. The engine failure after V1 is different: "rotate" belongs at VR, "gear up" on a
positive climb, "ECAM actions" at 400 ft. With a `sequencer`, the monitor only announces
(`drill.started {id, sequencer}` → `FailureDetected`) and the named service does the rest:

- `efato` → `EfatoService` / `EfatoCore`: `efato.armed {engine}` (the lower N2), then
  `efato.item` Rudder · Rotate (VR; skipped airborne) · V2 (V2 − 2 kt) · GearUp (climbing radio
  altitude, VS > 100; skipped gear up) · EcamActions (400 ft AGL), each falling through after 25 s;
  then `efato.handoff {procedure eng-1-fail|eng-2-fail}` through `FailureMonitor.TryRunProcedure`.

The sequencer's switch gates the arming too: with `abnormals.efatoEnabled` off the monitor skips the
definition. A voice rehearsal (`voiceTriggers`) still reads the file straight through, so the
`actions` of a sequenced drill should be the memory items in order — they are the rehearsal text.

Unknown sequencer names are treated as always enabled and nobody paces them (the announcement is all
that plays); add the service before adding the name.

## `FailureMonitor.TryRunProcedure(id, origin)`

Runs an ECAM procedure outside its own trigger (the EFATO hand-over: `eng-N-fail`'s own phases exclude
the takeoff). The definition is latched as detected — so its `clearedWhen` ends it and its own trigger
cannot fire it a second time — and `abnormal.handoff {id, origin}` is recorded. False for an unknown
id, a drill, a disabled file or one already latched.

## Session events

`failure.detected {id, title}` · `failure.cleared {id}` · `abnormal.skipped` · `abnormal.handoff` ·
`drill.started {id[, sequencer]}` · `drill.completed` · `drill.invoked {id, origin voice}` ·
`efato.armed` · `efato.item` · `efato.handoff` · the ECAM dialogue's own `ecam.*` events.
