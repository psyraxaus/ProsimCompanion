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

## FO works the radios (owner request 2026-10-25, opt-in)

`sayIntentions.foRadios.enabled` (default off; Voice First Officer → SayIntentions → "FO works
the radios"). `FoRadiosService` holds the SI copilot off (`SIAI_COPILOT` = 0, re-asserted every
2 min — SI resets it per flight; every other `SIAI_COPILOT` write in `SayIntentionsService` is
forced to 0 while the switch is on, and the comms hand-over by phase, its readouts and the
legacy departure gate are inert) and answers every ATC COM1 line the frequency monitor raises:

- **Read-back** (`ReadbackComposer`, pure, tests pinned to the copilot's own 2026-10-25
  transcript): the instruction minus callsign, the station's self-identification, "Identified",
  greetings ("Hej", "God kvall", "Have a good evening") and the winds; "Flight-level-140" → FL140
  (leading zero dropped, FL070 → FL70), "hold-short-of" → "hold short of", "A1" → "Alpha-one".
  Nothing left ("Identified. Good evening") = no transmission. A hand-off ("Contact X on Y") →
  "Contacting X on Y." No callsign on read-backs — exactly the copilot's habit.
- **Tune** on a hand-off through the Core `IRadioTuner` (standby → verify → swap) when
  `tuneOnHandoff`; a failed tune is spoken ("Could not tune …, Captain — the radios are yours").
- **Check-in** `checkInDelaySeconds` (8) later (`CheckInComposer`): "Good evening, Finnair-niner-
  six-two, climbing through 2,500 on KEMA5C." / "… climbing through FL180 for FL210 on KEMA5C." /
  "… maintaining FL370." (copilot wording, verified in the transcript); descent / approach mirror
  it with the STAR and "inbound runway 15"; TaxiOut "taxiing to holding point runway 22R";
  TaxiIn "runway 15 vacated, request taxi to stand B16" — the arrival forms are **unverified**
  (no arrival in the transcript). The greeting is the sim's local time of day (`ISimLocalTime`),
  the cleared level the last "climb/descend/maintain …" ATC said (`ClearedLevelParser`), the
  level FL from 10,000 ft, feet below; skipped when the pilot took the radios or ATC handed us on
  again meanwhile.
- Every transmission: `RadioClearGate` wait → FO voice (`foSpeaksTransmission`; `RadioSpeech`
  spells FL / frequencies / runways / squawk digit by digit) → sayAs. Serialised. Events
  `sayintentions.readback` {atc, kind: readback|handoff|none, transmit, spoken, radioWaitMs,
  radioBusyAtTx, sent}, `sayintentions.checkin` {station, khz, transmit, …},
  `sayintentions.fo-radios` {owner} on the voice override. Probe `si-fo-radios`.
- Timing basis (measured live 2026-10-25): a new ATC line is in `getCommsHistory` 1–4 s after
  its stamp; the copilot answered in 3 s. The monitor polls every `pollSeconds` (4, floor 3)
  while the switch is on, at the gate too; sayAs is silent on the SI side (owner heard the FO
  once, in the FO voice, with `foSpeaksTransmission` on).
- Pilot-initiated calls (clearance, push, taxi, takeoff requests) stay the voice requests above.
  "I have the radios" parks the FO (the captain answers ATC himself); "you have the radios"
  wakes it. Unverified live: ATC accepting every sayAs read-back, the arrival check-in shapes,
  and the push approval read-back (the copilot did NOT read "Push and start approved" back; the
  composer does).

## Radios tile (owner request 2026-10-25)

The Flight Status **Radios** tile (and a one-line mirror on the `/monitor` board) shows COM1
active/standby from ProSim (`system.analog.R_COM1_ACTIVE` / `_STANDBY`, push read model, Core
`Com1PanelService`) beside the frequency SayIntentions ATC expects, so the pilot never has to
bring the SayIntentions window forward. The data path is `Core.Radios.RadioStore`, written by:

- `SayIntentionsFrequencyMonitor` — the same hand-off / `correct_frequency` tracker as the
  wrong-frequency report. For the tile the polls (getCommsHistory 20 s, getCurrentFrequencies
  30 s until the first hand-off) run whenever the SayIntentions pillar is on and an API key is
  found — at the gate and without a live MSFS session too. The SPOKEN features keep their
  gates: the advisory and the comms hand-over readouts still fire only pushback→taxi-in on a
  live flight (`_voiceWanted`). Side effect: `sayintentions.frequency-assigned` is now also
  logged at the gate (SI names Ground from the moment the flight is filed).
- The station list: `getWX?icao=<local>&with_comms=1` for the local airport (OFP origin until
  Descent, destination from then on — the hero weather rule), on an airport change and every
  15 min; `comms[]` entries `{type, freq, callsign?}` at the root, one level down or inside
  `airports[]` (`SayIntentionsComms.Parse`). **Unverified live** which of the three shapes the
  API returns and whether `callsign` exists — the predecessor only read `type` and `freq`.
- The last ATC → pilot COM1 line from the history poll (the seed backlog included).

The tile's Tune button and the station chips call `IRadioTuner.TuneCom1Async` (Core): write
standby, verify (twice), then swap active/standby — never the SayIntentions `setFreq` call,
which writes the active frequency directly. Session event `radio.tile-tune` {khz, fromKhz,
reason: assigned|station, outcome: Tuned|NotConnected|OutOfBand|Failed|Unverified}. Probe
`radios-tile`.

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
The same switch is also offered as the **ATC mute** (`speech.atcMuteSource`, same keys;
`PushToTalkService` OR-s the dataref into the ATC-mute state next to the key/joystick
binding, so the FO ignores the mic while the pilot talks to ATC). The settings page sets
`atcMuteSource` to the chosen SI switch when it is still `none`. With both set and no
joystick bindings left, `JoystickPollingWanted` is false and winmm/dinput are never touched.

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
(`gate`, `phase`, `decision`). `DepartureGateNoted` is reported once per distinct stand text
(issue #175 — the 2026-10-10 leg logged it 951 times, once per poll). Unknown until a flight:
when SayIntentions changes `assigned_gate` on arrival (approach or only after landing), and
whether its pick exists in the loaded scenery — a miss falls into the normal
`gsx-gate-not-found` diagnostics. Probe `si-arrival-gate-from-atc`.

## Request text and the stand (issue #140, 2026-10-11)

`atc-requests.json` templates are filled by `AtcRequestTemplate` (Speech, pure): a template
is a list of comma-separated clauses and a clause whose token has no value is dropped whole —
all four requests of the 2026-10-10 flight went out as "Ground, Finnair-one-three-six-one,
stand , request push and start" because `assigned_gate` was empty when the pilot asked.
`{gate}` falls back to the stand GSX loaded when SayIntentions has none: the pushback
advisor's resolved stand (`PushbackChoiceStore.Stand`, "Gate 208"), else the GSX gate
context's last `|` segment ("Stand 546R"); a "stand {gate}" / "gate {gate}" label strips the
value's own Gate/Stand word ("stand 208"). Ground phases only. The `sayintentions.request`
event carries `standFrom` (`sayintentions` | `gsx` | `none`). Probe `si-request-stand-fragment`.

## Comms hand-over by flight phase (issue #171, 2026-10-11, opt-in)

`sayIntentions.commsHandover.*` (master `enabled`, off by default; every step its own switch,
texts editable) decides who works the radios from the app's own facts — the committed phase
edges, the departure runway threshold (FMS runway via the briefing procedure source, else
flight.json's; geometry from the gateway runway list, else the DFD; cached per runway) and
ATC's hand-offs from the comms history — never flight.json's `distance_to_runway`, which was
absent on both 2026-10-10 departures and left the legacy `departureGatingEnabled` gate dead
(zero `sayintentions.departureGate` events, no `SIAI_COPILOT` writes). With the master on the
legacy gate is ignored; the voice takeoff request no longer hands the radios back either.

| Step | Trigger | `SIAI_COPILOT` | FO line (default) |
|---|---|---|---|
| (a) gate | new active SI flight on the ground | 1 | "I have the radios." |
| (b) taxi | copilot owns; new COM1 ATC lines from `getCommsHistory` | — | "Taxi clearance: …" / "ATC handed us to Tower, one one niner decimal seven." |
| (c) holding point | TaxiOut and ≤ `holdingPointDistanceNm` (0.3) from the RUNWAY STRIP (nearest point between the two ends — since 2026-10-25; the threshold alone missed the EKCH 22R intersection departure at A1, ~1 nm down the runway), once per taxi | 0, Tower tuned via setFreq when known | "Comms are yours, Captain — call the takeoff request when ready." |
| (d) climb | Climb phase edge (Cruise edge as the safety net when Climb was skipped) | 1 | "I have the radios." |
| (e) cruise/descent | copilot owns; hand-offs announced, wrong-frequency report as before | — | |
| (f) approach | Approach phase edge, or the first Tower hand-off heard in Descent | 0 | "Comms are yours for the landing, Captain." |
| (g) taxi in | TaxiIn phase edge | 1 | "I have the radios." |
| override | "I have the radios" / "you have the radios" (voice) | 0 / 1 | "Your radios." / "I have the radios." |

`CommsHandoverCore` (Speech, pure) holds the owner and emits at most one decision per call,
never more than one automatic hand-over per phase edge; the shell in `SayIntentionsService`
writes the setVar, speaks the line and records `sayintentions.comms` {`owner`, `trigger`,
`phase`, `text`}. The readouts are `sayintentions.atc-readout` {`kind`, `text`, `source`};
they come from `SayIntentionsFrequencyMonitor`'s history poll (20 s), which now runs while
either feature is on, and only while the copilot owns the radios. Unverified live: whether
the SI copilot really goes quiet on `SIAI_COPILOT=0` mid-taxi and resumes on 1, and whether
the FO's own "I have the radios." line survives the self-echo guard. Probe `si-comms-handover`.

## Diversion request (issue #184, 2026-10-24)

`{diversion}` is a sixth template value: the diversion destination ICAO while one overlays the
OFP (`OfpStore.IsDiverted`), blank otherwise — so the shipped "request diversion" entry
(`{callsign}, request diversion to {diversion}`, station Center, commType `CENTER`) drops its
clause whole when no diversion is declared, like every other missing token. The FO offers the
phrase after a diversion when `sayIntentions.enabled` is on. **Unverified live**: whether
SayIntentions' getWX comms list carries a `CENTER` type for the auto-tune — the request is
spoken on the tuned COM1 either way.
