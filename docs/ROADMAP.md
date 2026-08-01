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
      decision log, gate control), unknown-key/unknown-state telemetry, command summaries
      — **all UNVERIFIED against live GSX; first sim session evaluates them**
- [ ] Menu intent framework (text/regex match, verify-outcome, safe-fail "leave menu for user")
- [ ] Timing-critical LVAR reads (pushback, fuel hose, pax/cargo counters, de-ice, gate readback)
- [ ] Ground automation state machine (SessionStart→…→TurnAround) with per-service activation
      policies and constraints (hub/non-hub, turnaround-only)
- [ ] Departure services: refuel (rate/time-target/panel methods), catering, water, lavatory,
      cleaning, boarding — configurable order and activation
- [ ] Refuel sync (GSX hose ↔ ProSim fuel datarefs, FOB save/restore per registration)
- [ ] Boarding/deboarding pax + cargo sync (seat-map reconciliation, no-show randomization)
- [ ] Door automation + GSX door-message suppression
- [ ] Jetway/stairs, GPU/PCA/chocks with safety interlocks; beacon-orchestrated pushback sequence
- [ ] De-icing, operator selection, skip-questions, walkaround skip (MSFS2024)
- [ ] Arrival: stable-parked detection, gate assignment (GSX `gate.select` retry ladder + SayIntentions)
- [ ] OFP gating of departure services

## Phase 3 — Flight data & EFB

- [ ] SimBrief OFP fetch (MCDU-triggered + manual; identity from `efb.simbrief.id`) — one typed client
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
