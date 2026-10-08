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
- [x] Pushback direction preselect (Korry buttons on the OFP page, gsx.pushbackPreference)
- [x] Live GSX menu card (#135): every open GSX menu as buttons on Flight Status / OFP /
      Ground Services — the addon-airport direction lines the Korry preference cannot reach
- [x] Header NEXT SERVICE button (#133, opt-in on Appearance) and the pax-scaled cabin-secure
      wait (#134)
- [x] Voice Reference drawer (#136): every live voice phrase from any page, five crew tabs,
      composed from the recognition grammar (IVoiceReference / VoiceReferenceBuilder)
- [x] Auto engine-start confirmation (#106 — `GsxPushbackSequenceService.TryConfirmEngineStart`
      answers "Confirm good engine start" at vehicle state 12; no on/off switch yet)
- [x] De-icing auto-answer + fluid/concentration selection (question catalogue); auto-request
      by weather/OAT (`gsx.deice.autoRequest` off / ask / auto, rc.23) with the
      `FSDT_GSX_DEICING_STATE` LVAR read and shown
- [x] Operator auto-selection with preference list ([GSX choice] fallback; company hubs deferred)
- [x] Skip GSX questions (crew, follow-me, pushback confirm; tug questions are caught by the
      generic "Do you want to request…" prefix handler, not a tug-specific entry); walkaround
      skip deferred (MSFS2024 keystroke)
- [x] VDGS display + event feed via the in-sim handler (`installer/GSXProfiles/gsx_handler.py`
      → `/api/gsxmenu/events`, `/api/gsxmenu/flight-info`); GSX SimBrief reload
      (`GsxSimbriefReloadService`, `gsx.reloadSimbriefOnNewOfp` off, `gsx.reloadSimbrief`
      command, rc.23)
- [x] Arrival gate assignment (retry ladder, armed at flight phase; SayIntentions source is
      Phase 6); stable-parked detection
- [x] INT/RAD switch as universal service trigger ("smart button") + web force-next button
- [x] MECH call (`GroundCrewUpcallService`, `groundCrew.mechCall`) and cabin dings
      (`CabinDingService`, `cabin.dingOnStartup` / `dingOnFinal`); "cockpit to cabin" hail
      answered by the purser (`CrewHailService`). Cabin-call **auto**-answer (#11,
      `CabinAutoAnswer`, off; one `S_ASP*_CAB_REC_LATCH` write, rc.23)

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
- [x] EFB reset flows (full/soft, rc.23): RESET FLIGHT (SOFT) clears the overrides and the
      loadsheet cycle; UNLOAD OFP (FULL) also clears the SimBrief import's write set in the
      ProSim EFB and raises the flight-cycle reset (`EfbResetService`, commands
      `efb.resetFlight` / `efb.unloadOfp`)
- [x] OOOI flight timestamps (off-blocks / takeoff / landing / on-blocks from the phase
      engine's edges, `FlightTimesTracker`, 2026-09-23; written to the session log and carried
      on each logbook flight since #146)
- [x] Web EFB parity: QR onboarding, bearer token, live updates, and every predecessor page has
      a home (/ofp, /loadsheet, /init, /wnb, /performance, /fuel, /checklists, /gsx …) — the
      remaining per-page gaps are the rows above (the separate tablet `/efb` shell was dropped
      2026-10-08 — see below)

## Audio (Prosim2GSX → Phase 4)

- [x] ACP knob/latch → CoreAudio per-app volumes (multi-ACP with power gating) — all 8 channels
      × 3 ACPs; CoreAudio listens to one configurable ACP, VoiceMeeter to any combination
- [x] VoiceMeeter strips/buses backend; live backend switch
- [x] Device blacklist; elevated-process detection

## Voice First Officer (Prosim2FO → Phase 5)

- [x] 16 spoken Airbus checklists (verify/acknowledge/number-readback; dataref verification
      with challenge; global commands incl. hold/resume (`HoldUntilResumedAsync`); hot reload;
      next-checklist preselect and per-checklist key/joystick prompts not ported)
- [x] Flight-control check (captain sweep callouts + FO-side sweep with neutral safety;
      write gate FO-side only)
- [x] Voice FCU actions (announce → cancel window → knob-back-off → write+verify; humanized
      by the gate flow); MCDU actions ported (`McduRadNavTuner`, `McduArrivalChanger`,
      `McduActuator` over the settled-read `McduReader`)
- [x] Read-backs: altimeter, V-speeds, runway, minimums (number-readback inside checklists;
      standalone read-backs delivered 2026-10, #148, off by default — unverified live)
- [x] SOP callouts (thrust set, 100kt, V1, rotate, V2, positive climb, RA gates, minimums,
      spoilers, reverse, decel; altitude callouts; 1000-to-go)
- [x] Stabilized-approach gates (1000/500 ft); go-around advisory
- [x] Flow monitor advisories (lights, flaps, gear, brake, seatbelts, beacon, spoilers, XPDR;
      placard speeds) + icing/anti-ice/ISA weather advisories
- [x] Sterile cockpit suppression; periodic fuel checks, takeoff-perf gross-error check and
      destination weather watch (2026-10, #148, each off by default — unverified live);
      missed-approach auto re-brief (`MissedApproachRebrief`)
- [x] ECAM abnormals (30 procedures, EWD cross-check, interactive per-line dialogue
      `EcamDialogueCore` — confirm / verify / branch / standby / skip; status review not
      ported); memory drills (stall, TCAS RA, windshear, EGPWS)
- [x] Speech: LAN whisper → System.Speech offline chain (WinRT engine deferred); PTT
      keyboard/joystick; phonetic snapping; utterance interpreter
- [x] TTS: Kokoro → ElevenLabs → Google Chirp 3 HD (both cloud voices cached, usage-tracked,
      budget-enforced) → WinRT → SAPI5; intercom filter; prewarm cache
- [x] Briefings (departure/arrival; Navigraph DFD facts; LLM-composed with number
      verification, streamed sentence by sentence since #147; interactive minimums capture
      `MinimaCaptureDialogue` + the /speech card)
- [x] SayIntentions ATC requests + departure comms gating + radio management
      (standby-then-swap)
- [x] SayIntentions wrong-frequency report (COM1 vs ATC's last "Contact … on …"; said once,
      pushback to taxi-in); radio-clear gate over the SIAI L:vars (2026-10-08); ATC's assigned
      gate as the GSX arrival gate (opt-in, 2026-10-08)
- [x] MCDU reader ("read the MCDU", scratchpad; two identical reads = settled) + gated MCDU
      actuation (RAD NAV tune, arrival runway/approach change) — "MCDU" section on
      /settings/speech. Unverified live
- [x] PF/PM role manager with duty swap (voice handover, instant take-back)

## Immersion & company (Prosim2FO → Phase 6)

- [x] FO persona (name, experience, formality, chattiness 0–3, style flags —
      `PersonaOptions` + `PersonaService` / `PhraseBank` over `config/phrases.json`, 2026-08-08;
      small talk via Ask the First Officer #152); **FO Persona** card on /settings/speech since
      2026-10-08
- [x] Cabin crew simulation (purser reports gated on any ACP CAB latch, cabin secure/ready,
      opt-in boarding-delay ambient, distinct purser voice + interphone filter;
      cruise-query ambient deferred)
- [x] Company/ACARS channel (loadsheet readout with session persistence, opt-in deterministic
      cruise messages, ACARS chime, distinct company voice, push seam for day mode)
- [x] Tech log & MEL (persistent defects, A–D due dates, wear pool, web raise/rectify,
      voice brief + guided raise/rectify dialogues + post-abnormal offer; procedural hooks
      deferred)
- [x] Pilot logbook (shutdown fold, aggregates, backfill, debrief comparison line, duty-day
      records; voice queries — `LogbookVoiceService`; the **Logbook page** with totals, a
      sortable table, flight detail, touchdown-rate trend, delete and CSV export, and the
      touchdown recorder: rate, IAS, pitch, bounces per landing — #146, Unverified live)
- [x] Post-flight debrief (event log → facts → LLM behind the number verifier → verified →
      spoken, deterministic template floor)
- [x] Company day mode (multi-sector duties, per-leg session rotation, planned rotations,
      turnaround + end-of-day summaries, /day page; off by default)

## Bridges & extras

- [x] SayIntentions extras (ATIS/METAR/TAF/wind batch, CPDLC station, /weather page)
- [x] Flight Status hero weather cards (local / destination, sky graphic from the METAR) and the
      gate monitor strip (five forward-only states from GSX boarding + door 1L + STD) — new,
      no predecessor equivalent (2026-09-22)
- [x] Pop-out Flight Monitor board (gate → flight → arrival modes, per-airline logo, fixed
      1920×1080 stage scaled to the window) — new (2026-09-23)
- [x] Aircraft position and distance-based flight progress (#145): great-circle distance
      flown / to go, ground-speed ETA, 3:1 top-of-descent estimate with a once-per-flight
      `tod-approaching` event, the route strip on the Flight Monitor and the distance row on
      Flight Status; time-based fallback without a position — new (2026-10-03), Unverified live
- [x] ActiveSky weather provider (snapshot file + local API, composite chain with gateway
      METAR and SayIntentions fallback; briefings consume it)
- [x] Command registry + HTTP command API (29 commands incl. per-service GSX requests through
      the single-writer trigger path, opt-in, token even on loopback) + GET /api/status
- [x] StreamDeck plugin (streamdeck/ — five actions, REST poll, pair-via-URL; build with
      npm run build / pack per streamdeck/README.md)
- [x] GSX voice control ("commence ground services", "request boarding", "cabin crew start
      boarding", …) via the command registry (gsx.voiceControlEnabled)
- [x] Voice-gated ground ops (ADR-0006, issues #50–#53): prep gate (gsx.groundPrepActivation),
      per-step Voice activation, crew hail dialogues ("cockpit to ground" → "go ahead,
      captain"), ground-crew upcalls on INT with MECH call, SayIntentions FO ack + audible
      transmission, airport-accent ground voices (accents.*) — all Unverified live
- [x] Airline themes (JSON), light/dark
- [x] Session event log (JSONL, Phase 1) + replay harness (`FlightSampleRecorder` /
      `FlightReplay`, ADR-0008; recordings under `tests/ProsimCompanion.Core.Tests/Flight/Recordings`)
- [x] Config importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`
      (`PredecessorConfigImporter.TryImportOnFirstRun`, marker-guarded, called from `Program.cs`)

## Deliberately not carried forward

- WinRT speech recognition engine — the LAN whisper + System.Speech chain made it pointless
  (owner decision 2026-10-08)
- Per-checklist key/joystick prompts to start a checklist — voice, the Voice Reference drawer
  and the Stream Deck plugin cover it (2026-10-08)
- Walkaround skip (MSFS2024 keystroke) — fragile key injection; walkaround is detected and
  holds services instead (2026-10-08)
- GSX / Couatl restart on taxi-in — destructive; restarts are detected and recovered from
  (2026-10-08)
- Headless remote-control mode — the web-first UI (ADR-0001) replaced it (2026-10-08)
- The separate tablet `/efb` shell (`feature/tablet-efb-surface`, 2026-08-30) — the PWA (#150),
  the restyle and the Flight Monitor made it redundant; branch deleted 2026-10-08
- FS2Crew Fenix-profile bridge — superseded by the built-in voice First Officer (ADR-0005)
- The Prosim2GSX↔ProsimInterface hot-swap DLL discipline (obsolete in a single solution)
- Dual SimBrief clients; dual ad-hoc dataref write paths (consolidated by design)
- CFIT.AppFramework service-locator hosting (ADR-0003)
- WPF settings UI beyond the web-server card (ADR-0001)
