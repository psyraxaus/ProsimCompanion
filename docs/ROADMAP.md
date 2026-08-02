# ProsimCompanion Roadmap

Consolidates Prosim2GSX + ProsimInterface + Prosim2FO into one application. Phases are ordered so that
every phase ends with a usable, shippable application. Feature parity checklists live in
[feature-inventory.md](feature-inventory.md); tick items off there as they are ported.

## Phase 0 — Foundation (scaffold) ✅

- [x] Solution layout (`src/`, `tests/`, `docs/`), central package management, `.editorconfig`
- [x] WPF shell hosting Kestrel + Blazor (interactive server), single DI container
- [x] Settings service (`config/settings.json`, per-feature options, safe defaults)
- [x] Serilog via `Microsoft.Extensions.Logging`, logs to `%LOCALAPPDATA%\ProsimCompanion\logs`
- [x] Planning docs, ADRs, integration knowledge base
- [x] git repository

## Phase 1 — Core infrastructure

The plumbing every pillar depends on. Exit criteria: app connects to ProSim and MSFS, shows live
aircraft state in the browser, and survives any of them being absent or restarting.

- [x] **ProSim SDK access** (`ProsimCompanion.Prosim`) — verified live against ProSim 2026-08-01
  - [x] SDK loading: typed compile-time reference (`Private=false`), runtime load from the
        **user-configured** path (installer/web Settings; no assumed install location), assembly
        resolver + `SetDllDirectory`, optional API key. Targets the current SDK only — the old
        dual legacy/beta reflection probing is deliberately dropped.
  - [x] Push subscription read model (register-once cached DataRefs, tiered 100/250/500/2000 ms,
        shared registrations at the fastest requested tier)
  - [x] Reconnect: SDK self-retrying async connect + single re-armed Connect after an established
        connection drops; registrations replayed on reconnect; stale-flagging on disconnect
  - [x] Write path with code-level allow-list (`ProsimWriteGate`); serialized momentary-press channel
  - [x] Dataref catalog — `ProsimDataRefNames` (717 wire identifiers ported verbatim from
        `ProsimConstants`, set-diff verified; `ProsimDataref.csv` at repo root as full reference)
- [x] **ProSim EFB gateway client** (GraphQL dataref read/write with exact predecessor wire
      format, cancelBoarding, vspeeds/ldr calc, runways, METAR with 204-no-retry, failures)
- [x] **SimConnect layer** (`ProsimCompanion.Sim`) — clean-room: headless event-handle pump,
      retry loop, SimVar read model behind `ISimVars`, self-degrading when MSFS absent.
      **LVAR transport deliberately deferred to the start of Phase 2** (investigate how
      CFIT.SimConnectLib reads LVARs without MobiFlight before choosing; gsx.md §5)
- [x] **Flight phase engine** — 14-phase model (matches Prosim2FO), pure
      `FlightPhaseEvaluator` + debounced `FlightStateEngine`, `FlightDataSnapshot` seam with
      live ProSim source; verified live 2026-08-01
- [ ] **State stores** — ConnectionStatusStore exists; feature stores (flight status, OFP, W&B…)
      arrive with their features
- [x] **JSONL event log** — structured session record (sessions/, retain 20, non-blocking
      writer); replay data source still to come with the replay harness
- [x] **Web shell** — layout/nav, connection status page (live data, phase, profile), settings
      page, LAN access with auto-generated bearer token (loopback always exempt; cookie after
      QR/link onboarding; verified 401/token/cookie paths live), QR onboarding + web-server
      settings card in the WPF shell (lockout prevention)
- [x] **Aircraft profiles** — model/matcher/persistence done; per-profile feature settings arrive
      with the pillars. Detection (broken in the predecessors) largely verified live 2026-08-01:
      the loaded livery/title was picked up correctly with MSFS running (`simulator.aircraft.title`
      only populates while the sim is up). Remaining check when Phase 2 first consumes
      `ActiveProfile`: a real configured profile matching end-to-end + re-match on aircraft change

## Phase 2 — GSX ground automation (first feature pillar)

Port of the Prosim2GSX pillar onto the new foundation. Primary references:
`docs/integrations/gsx.md` and **`docs/integrations/gsx-remote-api.md`** (full wire spec,
extracted 2026-08-01, incl. the locked decisions carried verbatim).

- [x] LVAR transport decision — native SimConnect on both sims, no WASM (gsx.md §5); LVAR
      write path + write gate in `ProsimCompanion.Sim`
- [x] Couatl Remote API v2 WebSocket client (hello/capabilities, subscribe, state mirror,
      coarse patches, engine events, readiness model, wire-trace every frame)
- [x] Service lifecycle tracker (once-per-cycle events, return-to-available rule, reconcile)
- [x] Menu intent framework (safe-fail pipeline, TOCTOU re-resolve, disabled guards) +
      rising-edge question dispatcher
- [x] Question/answer catalogue (FollowMe, crew, pushback confirm, de-ice offer + fluid
      selection, operator preference matching with [GSX choice] fallback) — every answer and
      every "left for user" decision-logged
- [x] Gate selection (arm/dispatch, single-retry ladder, SetGate readback confirmation) +
      manual arm/cancel from the /gsx page
- [x] Automation coordinator: phase mapping from the flight state engine, one-at-a-time
      departure sequencing with OFP gating, auto/manual start, autoSelectOperator per gate
      session, arrival-gate arming at flight, turnaround cycle reset
- [x] First-flight diagnostics: /gsx page (readiness, services wire-vs-mapped, menu, commands,
      decision log, gate control), unknown-key/unknown-state telemetry, command summaries —
      verified across five live sim sessions 2026-08-02; every issue found was diagnosable
      from the logs alone
- [x] Prosim2GSX-style departure status board (per-service lifecycle stage + hold/skip reason)
      on /gsx and the Home page
- [x] Timing-critical LVAR reads: fuel hose, live boarding counter
      (`NUMPASSENGERS_BOARDING_TOTAL` vs planned — live-verified), jetway-absent truth
      (`FSDT_GSX_JETWAY=2`, mirror lies). Pushback + de-ice LVAR timing gates still to come
      with their features
- [x] Ground-prep coordinator: deterministic reposition → settle → GPU/chocks → jetway/stairs
      before any departure service (owner-specified order, live-verified)
- [x] Departure services: refuel, catering, water, lavatory, cleaning, boarding — configurable
      order (`gsx.departureServiceOrder`), concurrent or strict-sequential
      (`gsx.concurrentServices`), board-after-all or board-after-selected (`gsx.boardingAfter`),
      once-per-cycle re-trigger guard. Per-service activation policies (hub/non-hub,
      turnaround-only) still to come
- [x] OFP gating of departure services: NO service is called until the pilot imports the OFP in
      the ProSim EFB or loads the MCDU plan (which auto-triggers the SimBrief import with the
      predecessor's valid-ICAO detection) — live-verified 2026-08-02
- [x] SimBrief OFP import (pulled forward from Phase 3 — it is the aircraft-loading enabler):
      fetch by `efb.simbrief.id`, lbs→kg, pax clamp, booked seat map + passengerStatistics +
      planned fuel (rounded up to 100 kg fuel-order increments) + cargo written via gateway
- [x] Refuel sync — hose-gated fuel stepping toward the latched EFB target with pause/resume,
      defuel guard, completion snap; target latched once per cycle because ProSim rewrites
      `aircraft.refuel.fuelTarget` mid-refuel (live-verified). FOB save/restore per
      registration still to come
- [x] Boarding sync — seat-map progressive boarding (the predecessors' proven mechanism; zone
      `amount` datarefs are read-only): booked map from the EFB manifest or synthesized
      capacity-proportional + randomized within zones, boarded seats written to
      `aircraft.passengers.seatOccupation.string`, cargo split by hold capacity, boarding
      status flag. Deboarding is observe-only until live counter semantics are confirmed;
      no-show randomization follows later
- [x] GPU/chocks/PCA placement at preparation + removal on the beacon edge (chocks interlocked
      on park brake); ProSim native efb.gsx.* auto-flags disabled per connection (verified live
      via the gateway write path)
- [x] Jetway/stairs handling: LVAR-truth selection (jetway=2 ⇒ stairs), 20 s verification with
      one fallback, pre-existing-connection detection
- [x] Door automation + GSX door-message suppression: cargo doors follow boarding/deboarding
      and the per-hold loader-finished LVARs (crew-realism close delay), catering doors follow
      GSX's service toggles, entry doors open when stairs dock (L1 only at jetway-less stands),
      `DISABLE_DOORS_MSG` re-asserted across Couatl restarts — ProSim door state authoritative
      throughout. **Unverified live — first bulk test evaluates**
- [x] Beacon-orchestrated pushback sequence (pure `PushbackSequencer` + shell): beacon on →
      wait APU → close doors → retract jetway/stairs → clear ground equipment → call pushback,
      randomized crew delays, beacon-off pause, tug/bypass-pin LVAR progress decision-logged.
      **Unverified live**
- [x] Arrival: stable-parked detection (on ground, engines off, brake set, beacon off,
      stationary, held N s) → FOB saved per aircraft, GSX pax counter re-armed with the boarded
      count, Deboarding auto-called; deboarding sync empties seats front-first from
      `DEBOARDING_TOTAL` and drains cargo by unload %. **Unverified live — counters
      double-logged for semantics confirmation**
- [x] FOB save/restore per aircraft title (`gsx.fuelFobSaved` in settings.json; restore at
      preparation before any plan exists, predecessor guard)
- [x] GSX pax-target arming (`NUMPASSENGERS` ← booked manifest before services) + optional
      crew-question LVAR suppression + opt-in pax no-show/extra randomization with bag-weight
      cargo adjustment (`gsx.randomizePaxNoShows`)
- [ ] De-icing beyond the question catalogue (auto-request policy), walkaround skip (MSFS2024
      keystroke — deferred), per-service activation policies (hub/non-hub, turnaround-only)

## Phase 3 — Flight data & EFB

- [x] SimBrief OFP fetch (MCDU-triggered; identity from `efb.simbrief.id`) — delivered early in
      Phase 2. Manual fetch button + typed OFP model for loadsheets/EFB pages still to come
- [ ] EFB INIT page with per-field overrides + sync to FMS
- [ ] In-house W&B/loadsheet pipeline (prelim + final, ACARS uplink, EDNO/REVISIONS — port the
      bit-exact ProSim formulas, see `docs/integrations/prosim.md`)
- [ ] Live W&B page (CG envelope, silhouette), per-tank fuel page
- [ ] Takeoff/landing performance (gateway `/efb/calculate/*`, FMS uplink)
- [ ] Interactive ECAM-style checklists (visual; JSON-authorable, gating/retreat/freeze semantics)
- [ ] Deice holdover-time card; passenger manifest generator

## Phase 4 — Audio control

- [ ] ACP knob/latch → Windows per-app volume (CoreAudio) with multi-ACP power gating
- [ ] VoiceMeeter backend (strips/buses, runtime-loaded VoicemeeterRemote64.dll), live backend switch
- [ ] Device blacklist, elevated-process detection

## Phase 5 — Voice First Officer

The Prosim2FO pillar. Its three foundations (phase engine, speech arbiter, event log) already exist
from Phase 1 — this phase adds the speech stack and features on top.

- [ ] Speech arbiter (priority queue, pre-emption, sterile-cockpit suppression rules)
- [ ] TTS router: Kokoro local neural → Google Chirp 3 HD (cached, usage-tracked) → WinRT → SAPI5
- [ ] Recognition: LAN faster-whisper → WinRT → offline System.Speech fallback chain; PTT
      (keyboard/joystick), phonetic snapping, utterance interpreter
- [ ] Spoken checklists (verify-against-dataref, challenge on mismatch, global commands)
- [ ] Flight-control check (captain sweep callout + FO sweep with neutral-on-cancel safety)
- [ ] SOP callouts (V1/rotate/RA/minimums/…), stabilized-approach gates, flow monitor advisories
- [ ] Voice FCU/MCDU actions (humanized key timing, armed/verified/abortable actuation gates)
- [ ] Briefings (Navigraph DFD + weather + LLM composition with number verification)
- [ ] ECAM abnormals + memory drills (detect-and-report only)
- [ ] SayIntentions ATC requests, departure comms gating, radio management

## Phase 6 — Immersion & remaining integrations

- [ ] Cabin crew simulation, company/ACARS channel, chimes
- [ ] Tech log & MEL, pilot logbook, post-flight debrief, company day mode
- [ ] StreamDeck support via the named-command registry (single command seam: web/API/StreamDeck)
- [ ] SayIntentions extras (ATIS/METAR, CPDLC station), ActiveSky weather provider

## Phase 7 — Distribution & polish

- [ ] Installer (Inno Setup) — prompts for external component locations and writes them into
      `config/settings.json`: ProSimSDK.dll path, Virtuali directory (then installs the GSX
      aircraft profiles/handler there), VoiceMeeter directory. Verifies ProSimSDK.dll never ships.
      The app itself never assumes these paths — unset paths degrade the subsystem with guidance.
- [ ] System tray icon + minimize-to-tray; single-instance mutex
- [ ] Themes (airline JSON themes in the web UI), light/dark
- [ ] Config migration importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`
- [ ] Docs site / user manual
