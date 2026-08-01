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

- [ ] **ProSim SDK access** (`ProsimCompanion.Prosim`)
  - [ ] Runtime SDK loading (assembly resolver, `SetDllDirectory`, dual legacy/beta `Connect` shapes, API key)
  - [ ] Push subscription read model (register-once cached DataRefs, tiered 100/250/500/2000 ms)
  - [ ] Reconnect watchdog with re-registration; stale-flagging on disconnect
  - [ ] Write path with code-level allow-lists; serialized momentary-press channel
  - [ ] Dataref catalog (port `ProsimConstants` names verbatim; keep `ProsimDataref.csv` as reference)
- [ ] **ProSim EFB gateway client** (GraphQL dataref read/write, EFB tasks, perf calc, runways, METAR)
- [ ] **SimConnect layer** (`ProsimCompanion.Sim`) — clean-room: connection lifecycle, SimVars,
      LVAR read/write, sim events, reconnect; self-degrading when MSFS absent
- [ ] **Flight phase engine** — single 14-phase state machine (ColdAndDark→…→Shutdown) fed by a
      `FlightDataSnapshot` seam; per-transition debounce; all features consume `PhaseChanged`
- [ ] **State stores** — observable, UI-agnostic stores consumed identically by Blazor circuits and
      any future surface (pattern proven in Prosim2GSX)
- [ ] **JSONL event log** — structured session record + replay data source (pattern from Prosim2FO)
- [ ] **Web shell** — layout/nav, connection status page, settings pages, LAN access with bearer
      token + QR onboarding, WPF-resident web server settings (lockout prevention)
- [ ] **Aircraft profiles** — profile matching on title/airline; per-profile feature settings

## Phase 2 — GSX ground automation (first feature pillar)

Port of the Prosim2GSX pillar onto the new foundation. Primary reference:
`docs/integrations/gsx.md` and Prosim2GSX `docs/00-shared-context.md` (locked Remote API decisions).

- [ ] Couatl Remote API v2 WebSocket client (hello/capabilities, state mirror, patches)
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

- [ ] Installer (Inno Setup; installs GSX aircraft profiles to `%APPDATA%\Virtuali\Airplanes`;
      verifies ProSimSDK.dll never ships)
- [ ] System tray icon + minimize-to-tray; single-instance mutex
- [ ] Themes (airline JSON themes in the web UI), light/dark
- [ ] Config migration importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`
- [ ] Docs site / user manual
