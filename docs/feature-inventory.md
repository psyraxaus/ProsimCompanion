# Feature inventory (migration parity checklist)

Every user-facing feature of the three predecessors. Tick when ported to ProsimCompanion; strike
through with a note if deliberately dropped. Sources: Prosim2GSX 0.9.0, ProsimInterface, Prosim2FO.

## GSX ground automation (Prosim2GSX → Phase 2)

- [ ] Auto reposition on startup
- [ ] Departure service sequencing (refuel/catering/water/lavatory/cleaning/boarding; configurable order, activation policies, hub/turnaround constraints)
- [ ] Refuel sync — fixed-rate / time-target / refuel-panel methods; hose-driven; FOB save/restore per registration; round-up-100
- [ ] Boarding/deboarding pax sync (seat map, zone amounts, reconciliation, no-show randomization)
- [ ] Cargo sync (progressive %/sec, fwd/aft distribution, bulk folded into aft)
- [ ] Door automation (pax/cargo/catering doors, open-on-board, timed close, keep-open, GSX door-message suppression)
- [ ] Jetway/stairs call + retract; remove ProSim native stairs
- [ ] GPU / PCA / chocks placement & removal with safety interlocks; beacon interception
- [ ] Beacon-orchestrated pushback sequence (randomized delays, INT/RAD skip, pause/resume)
- [ ] Pushback direction preselect (Korry buttons, per profile)
- [ ] Auto engine-start confirmation
- [ ] De-icing auto-answer + fluid/concentration selection
- [ ] Operator auto-selection with preference list; company hubs
- [ ] Skip GSX questions (crew, tug, follow-me, cabin calls); walkaround skip (MSFS2024)
- [ ] GSX SimBrief reload for VDGS; VDGS event feed via in-sim handler
- [ ] GSX restart on taxi-in (optional)
- [ ] Arrival gate assignment (GSX retry ladder + SayIntentions), auto at cruise; stable-parked detection
- [ ] INT/RAD switch as universal service trigger ("smart button")
- [ ] Headless remote-control mode (experimental)
- [ ] Cabin call auto-answer (ground/air, delays); MECH call; cabin dings

## Flight data / EFB (Prosim2GSX + ProsimInterface → Phase 3)

- [ ] SimBrief OFP fetch (MCDU-triggered + manual) and EFB import; OFP gating of services
- [ ] EFB INIT page with overrides + SYNC TO FMS; FMS init sync (ZFW/ZFWCG/block)
- [ ] Preliminary + final loadsheets (in-house W&B, EDNO/REVISIONS, ACARS uplink, timing notifications T-30/STD/boarding-complete)
- [ ] Live W&B (CG envelope, MACTOW, silhouette with seats/doors); per-tank fuel page
- [ ] Takeoff perf (V1/VR/V2/FLEX/THS, FMS uplink) and landing perf (LDR)
- [ ] Deice holdover-time countdown
- [ ] Passenger-simulation manifest generator
- [ ] Interactive ECAM-style visual checklists (JSON-authorable, gating/retreat/freeze, momentary-switch support)
- [ ] EFB reset flows (full/soft); OOOI flight timestamps
- [ ] Web EFB parity: 13 pages, QR onboarding, bearer token, live updates

## Audio (Prosim2GSX → Phase 4)

- [ ] ACP knob/latch → CoreAudio per-app volumes (multi-ACP with power gating)
- [ ] VoiceMeeter strips/buses backend; live backend switch
- [ ] Device blacklist; elevated-process detection

## Voice First Officer (Prosim2FO → Phase 5)

- [ ] 16 spoken Airbus checklists (verify/acknowledge/number-readback; dataref verification with challenge; global commands; next-checklist preselect; hot reload)
- [ ] Flight-control check (captain sweep callouts + FO-side sweep with neutral safety)
- [ ] Voice FCU/MCDU actions (humanized timing); FCU executor from spoken instructions
- [ ] Read-backs: altimeter, V-speeds, runway, minimums
- [ ] SOP callouts (thrust set, 100kt, V1, rotate, V2, positive climb, RA gates, minimums, spoilers, reverse, decel; altitude callouts; 1000-to-go)
- [ ] Stabilized-approach gates (1000/500 ft); go-around advisory
- [ ] Flow monitor advisories (lights, flaps, gear, brake, seatbelts, beacon, spoilers, XPDR; placard speeds)
- [ ] Sterile cockpit suppression; periodic fuel checks; takeoff-perf gross-error check; destination weather watch; missed-approach auto re-brief
- [ ] ECAM abnormals (30 procedures, EWD cross-check, interactive dialogue, status review); memory drills (stall, TCAS RA, windshear, EGPWS)
- [ ] Speech: LAN whisper → WinRT → offline recognition chain; PTT keyboard/joystick; phonetic snapping; utterance interpreter
- [ ] TTS: Kokoro → Google Chirp 3 HD (cached, usage-tracked) → WinRT → SAPI5; intercom filter
- [ ] Briefings (departure/arrival; Navigraph DFD facts; LLM-composed with number verification; interactive minimums capture; self-check diagnostics)
- [ ] SayIntentions ATC requests + departure comms gating + radio management (standby-then-swap)
- [ ] MCDU reader ("read the MCDU") + gated MCDU actuation (RAD NAV tune, arrival runway/approach change)
- [ ] PF/PM role manager with duty swap

## Immersion & company (Prosim2FO → Phase 6)

- [ ] FO persona (name, chattiness, styles, small talk)
- [ ] Cabin crew simulation (purser reports via ACP CAB, cabin secure/ready, ambient events)
- [ ] Company/ACARS channel (loadsheet readout, cruise messages, chime)
- [ ] Tech log & MEL (persistent defects, A–D due dates, wear pool, rectification dialogues)
- [ ] Pilot logbook (voice queries, debrief line)
- [ ] Post-flight debrief (event log → facts → LLM → verified → spoken)
- [ ] Company day mode (multi-sector duties, turnaround summaries)

## Bridges & extras

- [ ] FS2Crew Fenix-profile bridge (both directions, verbatim maps)
- [ ] SayIntentions extras (ATIS/METAR/wind, CPDLC station)
- [ ] ActiveSky weather provider
- [ ] StreamDeck plugin via command registry
- [ ] Airline themes (JSON), light/dark
- [ ] Session event log (JSONL) + replay harness
- [ ] Config importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`

## Deliberately not carried forward

- The Prosim2GSX↔ProsimInterface hot-swap DLL discipline (obsolete in a single solution)
- Dual SimBrief clients; dual ad-hoc dataref write paths (consolidated by design)
- CFIT.AppFramework service-locator hosting (ADR-0003)
- WPF settings UI beyond the web-server card (ADR-0001)
