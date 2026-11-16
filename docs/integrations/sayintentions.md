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
  both 0 for 400 ms = clear, 20 s cap (8 s until 2026-11-01: it stepped on two long approach clearances) then transmit anyway with a warning, stale (MSFS down) = clear. The sim
  auto-creates an unknown L:var as 0, so a SayIntentions that never writes them degrades to
  the fixed 400 ms settle. Each `sayintentions.request` event carries `radioWaitMs` and
  `radioBusyAtTx`.
- Optional frequency auto-tune from `getWX` comms data — always standby-then-swap, never write the
  active frequency directly.

## Assigned frequency (wrong-frequency report)

`SayIntentionsFrequencyMonitor` compares ProSim's COM1 active (integer kHz, the value of
`RadioStore` — see "COM1 read rule" below) with the frequency ATC last assigned. Settings:
`sayIntentions.frequencyMonitorEnabled` (true), `frequencyMonitorWaitSeconds` (60, floor 10),
`offerToTune` (true, since 2026-11-16). SI copilot on or off (owner decision 2026-10-05).

### Rules since 2026-11-16 (owner flight EGLL stand 313, 1.0.0-rc.9)

What was recorded: `sayintentions.frequency-assigned {station: "GROUND", khz: 121705, source:
"CorrectFrequency"}` at 00:22:52Z; Preflight from 00:22:47Z, Departure from 00:30:35Z. The
gateway read `system.analog.R_COM1_ACTIVE` = 0 and `R_COM1_STANDBY` = 0 while
`system.numerical.N_FREQ_COM1` = 124850 (the radio, 124.850). After the pilot tuned the radio
both `R_COM1_ACTIVE` and `N_FREQ_COM1` read 121705. The FO said nothing and the Radios tile
showed "PROSIM OFFLINE" with ProSim connected. Three causes: the report was quiet at the gate
by design, COM1 read 0, and the report was a statement with no offer.

- **Second recording, same day, about 00:57Z** (TaxiOut, `foRadios.enabled` on): the app had
  written 121705 through the Radios tile ("COM1 tune to 121705 kHz (station) -> Tuned"; both
  datarefs then read 121705). The pilot turned the hardware radio panel to 119.000:
  `N_FREQ_COM1` = 119000, `R_COM1_ACTIVE` still 121705. The first build of this fix read the
  R_ input first, saw a match with Ground and stayed silent. **`R_COM1_ACTIVE` is the last
  value software wrote (or 0), not the live radio. `N_FREQ_COM1` is the live radio: it follows
  the hardware knob and a software write alike.**
- **COM1 read rule** (`Core.Radios.Com1Reading`, one place: `Com1PanelService` → `RadioStore`
  and `IRadioTuner.ActiveKhz`): `N_FREQ_COM1` when it is live and above 0, else `R_COM1_ACTIVE`
  when the R_ pair is live and it is above 0. The monitor reads the store (`Com1Live ?
  Com1ActiveKhz : 0`); it has no subscription of its own any more. `N_FREQ_COM1` is read only.
  The catalogue has no numerical twin for the standby frequency, so standby is the R_ value
  alone. A tune still writes the R_ pair through `IRadioTuner.TuneCom1Async` (standby, verify,
  swap); "already on it", the frequency that goes to standby (`fromKhz`) and the verification
  after the swap all use the same reading, so a tune is `Tuned` when the radio itself shows
  the frequency (read up to 8 times, 250 ms apart) and the knob case above is tuned, not
  skipped as a no-op.
- **The judgement** (`FrequencyJudgement`, pure):
  - At the gate (ColdAndDark, Preflight, Departure) on a live flight: COM1 on the assigned
    frequency or on ANY station of the local airport's list (the Radios tile's list — getWX
    `with_comms=1`, see "Radios tile") is correct; any other in-band frequency is wrong. No
    station list for the local airport (not fetched, empty, no OFP) = not judged — never a
    false alarm. SI names Ground from the moment the flight is filed, which is why the
    assignment alone cannot be the rule there.
  - PushbackAndStart to TaxiIn on a live flight: COM1 must be the assigned frequency
    (unchanged; 5 kHz tolerance for the 8.33 kHz channel names).
  - `foRadios.enabled` on: the same rules, the same question and the same push-to-talk trigger
    (owner's rig 2026-11-16 — he flies with the FO on the radios). Read-backs, hand-off tuning
    and check-ins are untouched. `FoHandoffTuneWindow` keeps the FO's own hand-off out of it:
    `FoRadiosService` opens it before the hand-off read-back goes out and closes it when the
    tune and the check-in are over (or given up); while it is open, and for 10 s after,
    nothing is judged — the wait restarts and the push-to-talk does not ask. A hand-off tune
    that did not take is asked about after the normal wait.
  - Unknown, Shutdown, no live flight, no assignment (unreadable hand-off, 90 s without a good
    history poll), COM1 not read or out of band: not judged.
- **The wait** (`FrequencyWatchCore`, unchanged): COM1 on ONE wrong frequency for the whole
  wait; anything not judged wrong resets it.
- **What the FO does** (`Com1TuneOfferService`): with `offerToTune` on it raises a captain's
  question on `Core.Radios.Com1TuneOfferStore` (topic `com1TuneOffer`, yes/no, 60 s, no answer
  counts as no) — *"Captain, COM one is on one two four decimal eight five. Ground is one two one
  decimal seven zero five. Shall I tune it?"* — voiced by the shared `CaptainsQuestionDialogue`
  (table `Com1TuneOfferPhrases`) and shown on the open-questions card. Yes →
  `TuneCom1Async(assignedKhz, "fo-offer")`, then *"Ground tuned."* on `Tuned`, else *"Unable to
  tune COM1, Captain. The radio is yours."* No / timeout → nothing. One question at a time; one
  question per (assigned kHz, COM1 kHz) pair until another pair was asked or the flight cycle
  resets (`TuneOfferMemory`). The statement *"Captain, we should be on Ground, one two one
  decimal seven zero five."* is kept when `offerToTune` is off, when `gsx.voiceControlEnabled`
  is off (the dialogue would not voice the question) and when no tuner is composed in.
- **The station COM1 is on** (`Com1StationNames`, owner request 2026-11-16: on 119.000 at
  Heathrow SayIntentions answered as "Scottish Control"). With a name the lines are *"Captain,
  COM1 is on Scottish Control, one one niner decimal zero. We should be on Ground, one two one
  decimal seven zero five. Shall I tune it?"* and, on the card, "COM1 is on Scottish Control,
  119.000. We should be on Ground, 121.705. Shall I tune it?"; without one they stay as above.
  Sources, in order, nothing guessed: (1) SayIntentions' own — every getCommsHistory COM1
  record carries `frequency` and `station_name` (the 2026-10-05 capture above), so the newest
  COM1 record whose `frequency` is the one COM1 is on now (5 kHz tolerance) names it; the seed
  backlog counts, a new `flight_id` forgets; (2) the local airport's station list (callsign,
  else the type as a word); (3) no name. The lookup is by the frequency COM1 is on at the
  moment of the report, so a name never outlives a frequency change. Not used: the
  getCurrentFrequencies `frequencies[]` list (the nearest airport's — the trap below) and
  `L:SIAI_COM1_POSITION` (only "0 = unknown station" is documented). **Unverified live:** that
  the history record of a call on a wrong frequency carries that frequency and the answering
  station's `station_name` (the one capture is of a call on the right frequency), and how soon
  after the first call the record exists — before any exchange on the frequency there is no
  SayIntentions name. Events carry `com1Station`.
- **ATC push-to-talk** (`IAtcPttSignal`: the press edge of `SayIntentionsPttRelay`'s ProSim
  switch and of `PushToTalkService`'s ATC binding — no new input polling): while COM1 is judged
  wrong by the same rules, the question is raised at once, without the wait. Offer only: with
  `offerToTune` off the push-to-talk does nothing here.
- **Session events**: `fo.wrong-frequency-advisory` {text, station, assignedKhz, com1Khz,
  source, waitedSec, trigger: wait|ptt, atGate, offered} when the report fires (question or
  statement); `fo.wrong-frequency-question` from the dialogue (raised / accepted / declined /
  unanswered, with `subject` = `assignedKhz|com1Khz|station|com1Station`); `fo.wrong-frequency-offer`
  {assignedKhz, com1Khz, com1Station, station, trigger, atGate, accepted, outcome, answeredBy} with outcome
  `Tuned|NotConnected|OutOfBand|Failed|Unverified` after a yes, `declined` or `timeout`; the
  tune itself is a `radio.tile-tune` with reason `fo-offer`. Probe `si-wrong-frequency-offer`.
- **Radios tile**: unknown verdict with an assignment reads "PROSIM OFFLINE" only when ProSim
  is not connected; connected with COM1 not read it is "COM1 NOT READ"
  (`flightStatus.radios.com1NotRead`). The tile's own verdict is still COM1 against the
  assignment — at the gate on ATIS it shows WRONG FREQUENCY while the FO stays quiet.

Unverified live: whether `N_FREQ_COM1` is pushed through the SDK subscription as it reads on
the gateway, how fast it follows a write to the R_ pair, and the question itself against a
live microphone. Seen live 2026-11-16: a write to the R_ pair while it read 0 took (the tile
tune), and `N_FREQ_COM1` followed it.

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
- SI names Ground from the moment the flight is filed. Until 2026-11-16 that meant no report
  at the gate; now the gate has its own rule (above).

Not yet seen live: hand-off wording on arrival and in FAA phraseology, `since_id` behaviour,
`L:SIAI_COPILOT` (documented: 1 = copilot has comms) and `L:SIAI_COM1_POSITION` (0 = unknown
station) — `SIAI_COM1_POSITION` is not read today; `SIAI_COPILOT` is read by the FO-radios
service since 2026-11-14 (below), `SIAI_COM1_RECEIVING` / `SIAI_RADIO_PTT` by the
radio-clear gate.

## FO works the radios (owner request 2026-10-25, opt-in)

`sayIntentions.foRadios.enabled` (default off; Settings → SayIntentions → "FO works
the radios"). `FoRadiosService` holds the SI copilot off (`SIAI_COPILOT` = 0 once per flight —
SI resets it per flight — and again only when `L:SIAI_COPILOT` reads 1, at most every 30 s:
`CopilotOffRule`. **Every setVar of `SIAI_COPILOT` makes the SayIntentions client play its
two-note copilot tone, changed or not** — the original two-minute re-assert was heard as a
tone every two minutes for weeks (owner's rig 2026-11-14: the client's own
`streamdeck-plugin.log` showed `receiving` audio on a 120–135 s rhythm with `copilot` false
throughout, one burst five seconds after an app restart; switching the feature off stopped
it; SAPI's docs describe no sound for any call). Never re-send a SayIntentions setVar as a
keep-alive. The L:var read is unverified live — reading 0 forever only means no re-send.
Each send: Information log line + `sayintentions.copilot-off` {reason, lvar}. Every other
`SIAI_COPILOT` write in `SayIntentionsService` is
forced to 0 while the switch is on, and the comms hand-over by phase, its readouts and the
legacy departure gate are inert) and answers every ATC COM1 line the frequency monitor raises:

- **Read-back** (`ReadbackComposer`, pure, tests pinned to the copilot's own 2026-10-25
  transcript): the instruction minus callsign, the station's self-identification, "Identified",
  greetings ("Hej", "God kvall", "Have a good evening") and the winds; "Flight-level-140" → FL140
  (leading zero dropped, FL070 → FL70), "hold-short-of" → "hold short of", "A1" → "Alpha-one".
  Nothing left ("Identified. Good evening") = no transmission. A hand-off ("Contact X on Y") →
  "Contacting X on Y." **Every read-back and hand-off answer ends with the callsign** (owner
  request 2026-10-29, standard RT): "Push and start approved. Face South-East,
  Speedbird-eight-five-zero." / "Contacting Tower on 118.505, Speedbird-eight-five-zero." — the
  copilot's own habit was no callsign; a blank callsign leaves the line bare. EGLL 2026-10-29
  corrections: "Winds calm" / "wind light and variable" is winds too (dropped like "winds 160 at
  28"); a thousands separator survives the comma split ("climb altitude 6,000" went out as
  "6, 000"); a broadcast ("All aircraft be advised …", "Attention all aircraft …", "All
  stations …") gets NO read-back (the FO had answered the ATIS broadcast with "QNH 1028").
- **Stands are spelled digit by digit** on every transmission (`RadioSpeech.Stand`, owner
  2026-10-29: SI's ATC voice and the FO both read "stand 311" as "three hundred and eleven"):
  "311" → "three-one-one", "546R" → "five-four-six-Romeo", "B16" → "Bravo-one-six" — SI's own
  hyphenated callsign style, which `RadioSpeech.Spoken` turns into spaces for the FO voice. A
  compact designator only (`^[A-Z]{0,2}\d{1,3}[A-Z]{0,2}$`); SI's "PIER 2 GATE STAND 208" and a
  GSX "Dock 4" go out as written. Applied in `AtcRequestTemplate` ({gate}) and the check-in
  composer ("request taxi to stand Bravo-one-six"). The FO's own voice for a pilot request now
  goes through `RadioSpeech.Spoken` too ("runway 27R" → "two seven right"); sayAs keeps the text.
- The holding-point call (`atc-requests.json`, Tower): phrases *request departure*, *request
  takeoff clearance*, *request takeoff*, *ready for departure*, *request departure clearance*,
  *call tower ready for departure*, *call the tower* → "Tower, {callsign}, holding point runway
  {runway}, ready for departure" (ICAO; FAA keeps "holding short"). With the FO working the
  radios the legacy `SIAI_COPILOT=1` hand-back after this request no longer fires (2026-10-29 —
  it would have put the SI copilot on the takeoff clearance beside our FO).
- **Tune** on a hand-off through the Core `IRadioTuner` (standby → verify → swap) when
  `tuneOnHandoff`; a failed tune is spoken ("Could not tune …, Captain — the radios are yours").
- **Check-in** `checkInDelaySeconds` (4; 8 until 2026-11-01) later (`CheckInComposer`): "Good evening, Finnair-niner-
  six-two, climbing through 2,500 on KEMA5C." / "… climbing through FL180 for FL210 on KEMA5C." /
  "… maintaining FL370." (copilot wording, verified in the transcript); descent / approach mirror
  it with the STAR and "inbound runway 15"; TaxiOut "taxiing to holding point runway 22R";
  TaxiIn "runway 15 vacated, request taxi to stand Bravo-one-six" — the arrival forms are **unverified**
  (no arrival in the transcript). The SID / STAR is spoken the crew's way (owner request
  2026-10-29): "on Brookmans Park seven Foxtrot", not "on BPK7F" — `ISpokenText.Procedure`
  over the Core `IFixNames` seam (`ProcedureSpeech`, pure): the OFP navlog's fix names first
  (`OfpFixNames` — SimBrief prints "BROOKMANS PARK" beside BPK, and the route's own fixes are
  unambiguous), the DFD's navaid / waypoint name columns behind it (`DfdFixNames` →
  `DfdNavDataProvider.FixName`: VOR, NDB, terminal waypoint, enroute waypoint; rows filtered
  to the airport's ICAO region and ranked by distance to its reference point, because "LAM" is
  Lambourne, Lamezia and Lamerd and the enroute waypoint "BPK" is an Australian weather
  radar — with no airport an ambiguous ident is left unnamed). Unnamed: a 4+-letter base is a
  word ("Fisha one"), a shorter one is NATO-spelled ("Bravo Papa Kilo seven Foxtrot"). The
  briefing's SID / STAR lines and fact block use the same text. The greeting is the sim's
  local time of day (`ISimLocalTime`),
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
- **One record per exchange, filled late (2026-11-07, owner's EGLL flight):** SayIntentions
  creates the `comm_history` record when the pilot transmits (`stamp_zulu` = the pilot's time,
  `incoming_message` = the pilot's words, `outgoing_message` empty) and writes ATC's reply into
  the SAME record 2–3 s later (the reply mp3's own stamp). A poll landing in that gap and a
  `since_id` moved past the record lost the push approval and the taxi clearance for good
  (`#100134783`, `#100137176`; Nov 1 lost the push approval the same way). Rules now:
  `CommEntry.IsPendingReply` (COM1 + pilot text + no ATC text) holds the tracker's `since_id`
  marker below the record until it completes or 30 s pass (a pilot read-back ATC never
  answers looks identical); records behind a held marker are re-fetched and handed out once.
  The history is polled on the falling edge of `L:SIAI_COM1_RECEIVING` ("ATC just finished a
  line") with the clock poll relaxed to 20 s while edges arrive; no edge for 5 min (MSFS down,
  SI not keying the L:var) and the clock is back at `pollSeconds`. SAPI offers no push feed —
  REST only, nothing in flight.json carries ATC text — so the edge is the best trigger there is.
  Trail: Debug line per poll, `sayintentions.history-poll` {trigger, entries, sinceId, marker,
  pendingId, newLines} on edge polls and non-empty ones. Probe `si-comms-history-cursor`.
- **Push clearance for the beacon sequence (owner request 2026-11-07):** `PushClearanceRadio`
  implements Core `IPushClearanceRadio` — the GSX pushback sequencer asks ATC for push and start
  (the catalog's "request push and start", exactly as the voice request goes) at ready-for-push
  and waits for the approval (`gsx.pushClearanceWaitSec`, 120 s) before it calls the tug.
  `PushClearanceParser` reads the approval ("Push and start approved. Face South." /
  "Face North-West."), a hold ("hold position, expect a five minute delay") and the facing;
  `PushClearanceTracker` is the pure state (an approval without a request counts — the captain
  may have asked by voice; `SayIntentionsService.RequestTransmitted` notes voice requests so the
  sequence never asks twice). ATC's facing → `PushbackChoiceStore.Choose(Facing(deg, "atc"))`,
  so GSX's direction menu is answered with ATC's words. Events `sayintentions.push-clearance`
  {state, text, facingDeg}. Probe `gsx-beacon-push-clearance`. Unverified live.
- Pilot-initiated calls (clearance, push, taxi, takeoff requests) stay the voice requests above.
  "I have the radios" parks the FO (the captain answers ATC himself); "you have the radios"
  wakes it. Unverified live: ATC accepting every sayAs read-back, the arrival check-in shapes,
  and the push approval read-back (the copilot did NOT read "Push and start approved" back; the
  composer does).

## Radios tile (owner request 2026-10-25)

The Flight Status **Radios** tile (and a one-line mirror on the `/monitor` board) shows COM1
active/standby from ProSim (`system.analog.R_COM1_ACTIVE` / `_STANDBY`, with
`system.numerical.N_FREQ_COM1` read FIRST for active since 2026-11-16 — the R_ input is the
last software write, not the radio, and is only the fallback; push read
model, Core `Com1PanelService`) beside the frequency SayIntentions ATC expects, so the pilot never has to
bring the SayIntentions window forward. The data path is `Core.Radios.RadioStore`, written by:

- `SayIntentionsFrequencyMonitor` — the same hand-off / `correct_frequency` tracker as the
  wrong-frequency report. For the tile the polls (getCommsHistory 20 s, getCurrentFrequencies
  30 s until the first hand-off) run whenever the SayIntentions pillar is on and an API key is
  found — at the gate and without a live MSFS session too. The SPOKEN features keep their
  gates: the comms hand-over readouts still fire only pushback→taxi-in on a live flight
  (`_voiceWanted`); the wrong-frequency report needs a live flight and, since 2026-11-16,
  also runs at the gate by its own rule. Side effect: `sayintentions.frequency-assigned` is now also
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
