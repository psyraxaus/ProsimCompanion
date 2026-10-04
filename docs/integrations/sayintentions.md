# SayIntentions.AI integration reference

- Base: `https://apipri.sayintentions.ai` (SAPI under `/sapi/`).
- API key: auto-discovered from `%LOCALAPPDATA%\SayIntentionsAI\flight.json` →
  `flight_details.api_key`; re-read per call (it changes between sessions). Manual override allowed.
- Flight context: poll `flight.json` (~1 s) for callsign, gate, runway, route.

## Endpoints used

| Endpoint | Use |
|---|---|
| `GET /sapi/assignGate?api_key&gate&airport` | push assigned arrival gate to ATC |
| `GET /sapi/getWX?api_key&icao[&with_comms=1]` | METAR (+ comm frequencies for auto-tune) |
| `GET /sapi/getCurrentFrequencies?api_key` | CPDLC logon; `correct_frequency` for the wrong-frequency report |
| `GET /sapi/getCommsHistory?api_key[&since_id]` | ATC hand-offs for the wrong-frequency report |
| `GET /sapi/sayAs?api_key&channel&message` | FO transmits a phrase on COM1 (voice ATC requests) |

## Behaviours to preserve

- Arrival gate is assigned to **both** GSX (`gate.select`) and SayIntentions (`assignGate`),
  auto-fired at cruise.
- Voice ATC requests (clearance, start, push+start, taxi, takeoff) with ICAO/FAA phraseology
  variants; departure comms gating (FO takes comms near the runway, tunes Tower, hands back after
  clearance).
- Radio-clear gating before transmitting, via MobiFlight WASM LVARs `SIAI_COM1_RECEIVING` and
  `SIAI_RADIO_PTT` (registered through `MF.SimVars.Add.(L:var)` client-data channels); fully
  self-degrading when the WASM module is absent.
- Optional frequency auto-tune from `getWX` comms data — always standby-then-swap, never write the
  active frequency directly.

## Assigned frequency (wrong-frequency report)

`SayIntentionsFrequencyMonitor` compares ProSim's COM1 active (`system.analog.R_COM1_ACTIVE`,
integer kHz) with the frequency ATC last assigned and has the FO say once *"Captain, we should
be on Tallinn Control, one three four decimal three two five."* Settings:
`sayIntentions.frequencyMonitorEnabled` (true), `frequencyMonitorWaitSeconds` (60, floor 10).
Runs from pushback to taxi-in, SI copilot on or off (owner decision 2026-10-05).

Reply shapes — live captures 2026-10-05, EFHK→LKPR (the SAPI docs show no sample):

```json
// getCurrentFrequencies at the gate
{"airport":"EFHK","airport_callsign":"Helsinki Vantaa Airport",
 "correct_frequency":"118.125","correct_position":"GROUND",
 "frequencies":[{"side":0,"station":"GND","state":"not_spoken","freq":"118.125","long_station":"Ground Control"},
                {"side":0,"station":"TWR","state":"normal","freq":"119.7","long_station":"Tower"},
                {"side":1,"station":"CPDLC","state":"normal","freq":"EFIN","long_station":null}]}

// getCommsHistory (one entry)
{"flight_id":883498018,"comm_history":[
 {"id":81242944,"channel":"COM1","position":"GROUND","frequency":"118.125","station_name":"Helsinki Ground",
  "copilot":0,"is_acars":0,"stamp_zulu":"2026-10-04T19:42:16Z","ident":"EFHK","incoming_message":"",
  "outgoing_message":"Contact Tower on 119.7. Have a Good morning",
  "outgoing_message_english":"Finnair-one-two-two-one, Contact Tower on 119.7. Have a Good morning"}]}
```

- `outgoing_message` = ATC to pilot, `incoming_message` = pilot to ATC, `copilot` 1 = the SI
  copilot spoke. `channel` is `COM1`, `INTERCOM3` (GSX ramp crew) or `ACARS` (the clearance).
  `id` rises; `since_id` asks for newer entries only.
- Every hand-off seen was `Contact <station> on <freq>`.

Empirical rules (2026-10-05) — keep these:

- **`correct_frequency` is null in cruise**, on the right frequency and on a wrong one alike;
  every `state` is `normal`. It named Ground at the gate while Ground was `not_spoken`. It is
  used only before the flight's first hand-off.
- **SayIntentions accepts a wrong station.** Assigned Tallinn Control 134.325, the owner tuned
  119.1; the SI copilot checked in and Helsinki Radar answered "Identified". So the assignment
  is the last hand-off *instruction*, never the frequency of the last exchange.
- **`frequencies[]` is the nearest airport's list** (EEEI with CTR 121.3 while assigned
  134.325). Never treat it as the set of valid frequencies.
- A hand-off that cannot be read ("Contact departure.") makes the assignment unknown and the
  report silent. "Radar contact" is not a hand-off.
- No report at the gate: SI names Ground from the moment the flight is filed.

Not yet seen live: hand-off wording on arrival and in FAA phraseology, `since_id` behaviour,
`L:SIAI_COPILOT` (documented: 1 = copilot has comms) and `L:SIAI_COM1_POSITION` (0 = unknown
station) — neither LVAR is read today.
