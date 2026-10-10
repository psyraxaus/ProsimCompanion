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
  auto-fired at cruise. Since 2026-10-08 the gate also flows the other way (opt-in
  `sayIntentions.arrivalGateFromAtc`, see below).
- Voice ATC requests (clearance, start, push+start, taxi, takeoff) with ICAO/FAA phraseology
  variants; departure comms gating (FO takes comms near the runway, tunes Tower, hands back after
  clearance).
- Radio-clear gating before transmitting. The predecessor read `SIAI_COM1_RECEIVING` and
  `SIAI_RADIO_PTT` through MobiFlight WASM client data; ProsimCompanion reads the same two
  L:vars natively through `ISimVars` (`SayIntentionsLvarNames`, `RadioClearGate`, 2026-10-08):
  both 0 for 400 ms = clear, 8 s cap then transmit anyway, stale (MSFS down) = clear. The sim
  auto-creates an unknown L:var as 0, so a SayIntentions that never writes them degrades to
  the fixed 400 ms settle. Each `sayintentions.request` event carries `radioWaitMs` and
  `radioBusyAtTx`.
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
station) — neither of those two is read today (`SIAI_COM1_RECEIVING` / `SIAI_RADIO_PTT` are,
for the radio-clear gate).

## Push-to-talk from a ProSim switch (2026-10-10, opt-in)

`sayIntentions.pttSource` (default `none`; `captainSidestick`, `foSidestick`, `captainHandMic`,
`foHandMic`, `observerHandMic`) keys SayIntentions from a ProSim PTT switch
(`S_SIDESTICK_PTT_CAPT/_FO`, `S_HAND_MIC_PTT_CAPT/_FO/_OBS`, 0 normal / 1 pushed, Critical
tier). Each edge writes `sayIntentions.pttLvar` (default `L:SIAI_CONTROL_PTT_COM`) through
SimConnect: 1 to talk, 0 to stop — the SI client's **Controls → Map PTT inside the sim with
an LVAR or dataref**, which lists `SIAI_CONTROL_PTT_COM`, `_COM1`, `_COM2` (forced),
`_INTERCOM1..3`, `_GROUP`. Those seven names are NOT in the published LVAR reference (which
only has the read-only `SIAI_RADIO_PTT` / `SIAI_INTERCOM_PTT` status flags) — they were read
off the client on 2026-10-10. They sit on `SimWriteGate` by exact name. The relay watches
`L:SIAI_RADIO_PTT` while the button is held and the release event `sayintentions.ptt`
carries `transmitted` — the live proof the LVAR keyed the client. Why: the sidestick PTT is
on the second PC and reaches ProSim as a dataref; this path needs no joystick polling in
our process (the winmm/dinput heap-corruption suspect of the 2026-10-10 crash).

## Arrival gate from ATC (2026-10-08, opt-in)

`sayIntentions.arrivalGateFromAtc` (default off) takes flight.json's `current_flight.assigned_gate`
as the GSX arrival gate. flight.json carries ONE `assigned_gate` for the whole flight: on the
ground before departure it is the departure stand, and ATC's "taxi to gate …" (or an earlier
reassignment) replaces it. `AtcAssignedGateRule` (Core, pure) therefore:

- notes the gate seen in ColdAndDark … TakeoffRoll as the departure stand (`DepartureGateNoted`);
- from InitialClimb to TaxiIn reports a *changed* gate once; the same text as the departure stand
  is `SameAsDeparture` (ignored); a gate the pilot queued on the OFP page wins (`PilotGateWins`);
  otherwise `QueueArrival` → `ArrivalGateCoordinator.ConfirmFromAtc`;
- past the cruise entry (Descent/Approach/LandingRollout/TaxiIn) the gate goes to GSX at once —
  the cruise edge the normal queue fires on has passed;
- the ATC half is NOT sent back (status line "Assigned by SayIntentions ATC — nothing to send
  back"); a later pilot Confirm / Send Now with a typed gate sends to both again;
- resets on ColdAndDark / Shutdown so a turnaround back to the same stand works.

Every decision but "nothing new" is a `sayintentions.arrival-gate` session event
(`gate`, `phase`, `decision`). Unknown until a flight: when SayIntentions changes
`assigned_gate` on arrival (approach or only after landing), and whether its pick exists in the
loaded scenery — a miss falls into the normal `gsx-gate-not-found` diagnostics. Probe
`si-arrival-gate-from-atc`.
