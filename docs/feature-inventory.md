# Feature inventory (migration parity checklist)

Every user-facing feature of the three predecessors. Tick when ported to ProsimCompanion; strike
through with a note if deliberately dropped. Sources: Prosim2GSX 0.9.0, ProsimInterface, Prosim2FO.

## GSX ground automation (Prosim2GSX → Phase 2)

- [x] Auto reposition on startup (ground-prep coordinator: reposition → settle → equipment)
- [x] Departure service sequencing (ordered steps, per-service activation policies, leg
      constraints; company-hub constraints + per-service minimum duration still deferred)
- [x] Refuel sync — fixed-rate hose-driven with pause/resume, FOB save/restore per aircraft
      title, round-up-100 (time-target / refuel-panel methods deliberately not ported)
- [x] Boarding/deboarding pax sync (seat-map model; zone-amount writes dropped — datarefs are
      read-only; reconciliation + no-show randomization ported)
- [x] Cargo sync (progressive %, fwd/aft capacity split, bulk folded into aft)
- [x] Door automation (cargo/catering/entry doors, loader-finished LVARs, crew-realism delays,
      GSX door-message suppression across Couatl restarts)
- [x] Jetway/stairs call + retract (LVAR-truth selection, verification + fallback)
- [x] GPU / PCA / chocks placement & removal with interlocks; beacon-edge removal
- [x] Beacon-orchestrated pushback sequence (randomized delays, beacon-off pause; INT/RAD
      force-next covers the skip)
- [ ] Pushback direction preselect (Korry buttons, per profile)
- [ ] Auto engine-start confirmation
- [x] De-icing auto-answer + fluid/concentration selection (question catalogue)
- [x] Operator auto-selection with preference list ([GSX choice] fallback; company hubs deferred)
- [x] Skip GSX questions (crew, tug, follow-me, pushback confirm); walkaround skip deferred
      (MSFS2024 keystroke)
- [ ] GSX SimBrief reload for VDGS; VDGS event feed via in-sim handler
- [ ] GSX restart on taxi-in (optional)
- [x] Arrival gate assignment (retry ladder, armed at flight phase; SayIntentions source is
      Phase 6); stable-parked detection
- [x] INT/RAD switch as universal service trigger ("smart button") + web force-next button
- [ ] Headless remote-control mode (experimental)
- [ ] Cabin call auto-answer (ground/air, delays); MECH call; cabin dings

## Flight data / EFB (Prosim2GSX + ProsimInterface → Phase 3)

- [x] SimBrief OFP fetch (MCDU-triggered + manual force-fetch, typed model, retry) and EFB
      import; OFP gating of services
- [x] EFB INIT page with overrides (zfw/block/cargo/pax) + SYNC TO FMS; FMS init sync
      (ZFW/ZFWCG/block, tonnes conversion, final→prelim→live source resolution)
- [x] Preliminary + final loadsheets (in-house bit-exact W&B, EDNO/REVISIONS, ACARS uplink to
      slots 01/02; STD/T-30 timing notifications not ported — the status board covers it)
- [x] Live W&B (SVG CG envelope, MACTOW, per-tank fuel bars; seat/door silhouette not ported —
      candidate for a later polish pass)
- [x] Takeoff perf (V1/VR/V2/FLEX/THS/shift, FMS uplink) and landing perf (LDR, LDR+15%, LDA
      margin with displaced threshold)
- [x] Deice holdover-time countdown (representative HOT matrix, GSX deice-complete armed)
- [x] Passenger manifest generator (read-only from the booked map; the predecessor's
      simulate-cabin dataref write deliberately dropped — GSX boarding owns occupation)
- [x] Interactive ECAM-style visual checklists (Prosim2FO-compatible JSON, hot reload,
      gating/retreat/freeze; momentary-switch sweep support is a voice-FO concern → Phase 5)
- [ ] EFB reset flows (full/soft); OOOI flight timestamps
- [ ] Web EFB parity: 13 pages, QR onboarding, bearer token, live updates (QR/token/live done
      in Phase 1/2.5; remaining predecessor pages tracked by the rows above)

## Audio (Prosim2GSX → Phase 4)

- [x] ACP knob/latch → CoreAudio per-app volumes (multi-ACP with power gating) — all 8 channels
      × 3 ACPs; CoreAudio listens to one configurable ACP, VoiceMeeter to any combination
- [x] VoiceMeeter strips/buses backend; live backend switch
- [x] Device blacklist; elevated-process detection

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

- [ ] SayIntentions extras (ATIS/METAR/wind, CPDLC station)
- [ ] ActiveSky weather provider
- [ ] StreamDeck plugin via command registry
- [ ] Airline themes (JSON), light/dark
- [ ] Session event log (JSONL) + replay harness
- [ ] Config importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`

## Deliberately not carried forward

- FS2Crew Fenix-profile bridge — superseded by the built-in voice First Officer (ADR-0005)
- The Prosim2GSX↔ProsimInterface hot-swap DLL discipline (obsolete in a single solution)
- Dual SimBrief clients; dual ad-hoc dataref write paths (consolidated by design)
- CFIT.AppFramework service-locator hosting (ADR-0003)
- WPF settings UI beyond the web-server card (ADR-0001)
