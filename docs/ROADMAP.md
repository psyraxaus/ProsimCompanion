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

> **Round-8 smoke test: provisionally passed** (owner-run quick session ~2026-08-03, reported
> working; logs not yet reviewed). The "Unverified live" markers below are downgraded to
> *provisionally verified* — if issues surface in later sessions they are diagnosed from the
> decision log / CMTrace logs as usual. Deboarding counter semantics (double-logged) remain
> unconfirmed until a log review.

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
- [x] Departure services — Prosim2GSX ordering system (`gsx.departureServices`, replacing the
      round-1 order/concurrent/boardingAfter keys via settings migration v2): ordered steps
      with per-service activation (Skip / Manual / AfterCalled / AfterRequested / AfterActive /
      AfterPrevCompleted / AfterAllCompleted) and leg constraints (always / first-leg-only /
      turnaround-only), cursor semantics, once-per-cycle re-trigger guard, reorderable editor
      on /settings. **Unverified live — round-8 smoke test evaluates**
- [x] Trigger dispatch discipline: one `service.trigger` in flight at a time, confirmed against
      the mirror before the next goes out, ~10 s timeout + retry, truthful "Called" on the
      status board (round-7 smoke-test fix: GSX silently dropped 4 of 5 simultaneous triggers
      and the board wedged on "Called"). **Unverified live**
- [x] INT/RAD force-next (the predecessor's smart button, `S_ASP(2)_INTRAD` = 0 during the
      departure phase) + "Call next service now" button on /gsx: calls the current step ahead
      of its activation rule, including Manual entries; never bypasses the OFP gate.
      **Unverified live**
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
      keystroke — deferred), company-hub service constraints + per-service minimum flight
      duration (deferred from the ordering system — needs hub lists and OFP duration plumbing)

## Phase 2.5 — Web UI foundation (pulled forward from Phase 7)

Recreates the Prosim2GSX web EFB design language in Blazor before Phase 3 adds more pages, so
migrating Prosim2GSX users land in a familiar UI and every later page is built into the final
frame instead of retrofitted. Reference inventory: the Prosim2GSX `Prosim2GSX.Web` React app
(design tokens in `src/styles/theme.css`, derivation rules in `src/theme/applyTheme.ts`).

- [x] Design-token stylesheet (`wwwroot/css/app.css` in the Web RCL, copied to the App output —
      no static-web-assets pipeline in a WinExe host) with the Prosim2GSX token set and the
      default navy EFB palette
- [x] Airline JSON themes (Dark/Light/Delta/Finnair/Lufthansa/Qantas + `config/themes/*.json`
      user themes), C# port of the `applyTheme` derivation rules (input/button surface shifts,
      alpha borders, `--text-on-dark` split), live theme switch from settings — Qantas
      light-theme derivation verified against a running instance 2026-08-02
- [x] Component library: Section card, Bool/Number/Text/Select field rows, buttons, status
      pills, indicator dots, DirtyBar (save/discard), split-flap display (client-side JS custom
      element — nothing animates over the circuit), section left-rail nav
- [x] Layout: Prosim2GSX-style header (wordmark + WEB badge, split-flap FLT NO/UTC/DATE,
      connection dot) + horizontal top tab bar replacing the sidebar; responsive breakpoints
      (540/720 px); styled circuit-reconnect overlay; favicon. NOTE: no catch-all 404 page on
      purpose — a Blazor catch-all route match makes endpoint-aware `UseStaticFiles` skip every
      static file (blazor.web.js served as HTML)
- [x] Flight Status dashboard (phase card with 7-block progress bar, connection/GSX dot cards,
      per-service pills, live log box) replacing the placeholder Home page
- [x] GSX Settings page: left-rail sections in the Prosim2GSX arrangement, full `GsxOptions`
      coverage (every implemented property — ends hand-editing settings.json), draft/baseline
      dirty tracking with DirtyBar
- [x] App Settings page (ProSim connection/SDK, web server, theme picker, logging)
- [x] Restyle /gsx diagnostics + /logs with the shared components
- [x] `GsxDiagnosticsStore.Changed` event — retired the GSX polling timers (Logs still polls:
      the log buffer deliberately has no per-event fan-out)

## Phase 3 — Flight data & EFB

- [x] SimBrief OFP fetch (MCDU-triggered; identity from `efb.simbrief.id`) — delivered early in
      Phase 2. Typed OFP model (`OfpData`/`OfpStore`), fetch retry, polymorphic-alternate parse
      and the manual force-fetch button on /flight added with the loadsheet pipeline
- [x] In-house W&B/loadsheet pipeline (bit-exact ProSim formulas per
      `docs/integrations/prosim.md` §4): prelim on GSX refuel-active (via `GroundOpsSignals`),
      final after boarding-complete + 90–150 s dispatcher delay, EDNO increments/inheritance,
      REVISIONS/COMPLIANCE title with `//` flags, CG plausibility gate, JSON envelope to
      `efb.{prelim|final}Loadsheet` (3 s settle) then ACARS uplink to slots 01/02, cycle reset
      on turnaround, manual generate/resend on /flight. **Unverified live**
- [x] FMS INIT B sync (`aircraft.fms.init.{zfw,zfwcg,block}`, tonnes conversion, source
      resolution final→prelim→live, optional auto-sync on final) — /flight button.
      **Unverified live**
- [x] EFB INIT page (/init): per-field overrides with the predecessor's exact writable set
      (zfwKg → fms.init.zfw in tonnes, fuelRampKg → fms.init.block rounded-up/tonnes, cargoKg →
      efb.plannedCargoKg, passengerCount → re-synthesized booked seat map + statistics), set
      writes immediately, clear restores the OFP figure, overrides reset on new OFP/turnaround;
      display-only OFP reference card. **Unverified live**
- [x] Live W&B page (/wnb): live weights/CG/zones/cargo + loadsheet echo, SVG CG envelope
      (indicative A320-family outline; ZFW/GW points with fuel-travel connector, out-of-envelope
      status colour), per-tank fuel bars (5 tanks, granular `aircraft.systems.fuel.*` refs),
      collapsible passenger manifest. **Unverified live**
- [x] Takeoff/landing performance (/perf): gateway `/efb/calculate/*` with the predecessor's
      exact wire scales (TOW tens-of-kg, MAC ×10, Break* misspellings, LdgW tonnes, 3600 m TORA
      cap, VRB→reciprocal on landing), runway+intersection pick, METAR autofill, LDR/LDR+15%/LDA
      margin with displaced threshold, FMS PERF TO uplink (flaps/flex/V-speeds/THS sign/shift
      rounded to 100 in the cockpit's display unit). **Unverified live**
- [x] Interactive ECAM-style checklists (/checklists): Prosim2FO-compatible JSON (the 16 shipped
      checklists carried over verbatim to config/checklists, hot-reload on save), exact
      condition-evaluator semantics (epsilon equals, inclusive between, and/or trees,
      fail-closed), NEW visual semantics — strict in-order gating with auto-complete cascade,
      retreat (a regressed condition reopens its line until the checklist completes), freeze
      (per-item `freeze: true`, manual lines always, completed checklists wholesale).
      **Unverified live**
- [x] Deice holdover-time card (Flight Data page): predecessor HOT matrix verbatim (sim-immersion
      figures, not certified), armed by the GSX deice-complete edge with fluid type from
      `FSDT_GSX_DEICING_TYPE` + configured concentration, crew-entered precip/OAT, live
      countdown, expiry alarm. Passenger manifest generator (W&B page): predecessor name pools,
      deterministic per booked map. **Unverified live**

## Phase 4 — Audio control

- [x] ACP knob/latch → Windows per-app volume (CoreAudio via NAudio.Wasapi) with per-ACP power
      gating: shared `AcpChannelFeed` (knob analogs 0–1024 + REC latches @ 250 ms), the full
      three-bus + audio-switching gate applied to BOTH backends (the predecessor gated CoreAudio
      on DC ESS at startup only), events suppressed while unpowered with re-emit on power
      restore, per-mapping coalesced writes (latest-value-wins), session volume save/restore,
      process-handle hygiene, /audio status + /settings/audio pages. **Unverified live**
- [x] VoiceMeeter backend: runtime-loaded VoicemeeterRemote64.dll (NativeLibrary +
      GetDelegateForFunctionPointer, user-configured path), idempotent login, strips/buses with
      −60…+12 dB mapping, live backend switch (suspend-not-logout; neutral 0 dB reset runs
      BEFORE unbind — fixing the predecessor's dead-code ordering), mapping validation
      (duplicate channel per ACP / duplicate target across ACPs) with Captain-only session
      fallback that never modifies the config. **Unverified live**
- [x] Device blacklist (device-name-prefix match), elevated-process detection (MainModule probe;
      flagged on /audio with run-as-admin guidance), ProSim native audio guard
      (aircraft.communication.windows.* cleared once per connection; PA deliberately untouched,
      predecessor parity). **Unverified live**

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
- [x] Themes (airline JSON themes in the web UI), light/dark — delivered in Phase 2.5
- [ ] Config migration importers from Prosim2GSX `AppConfig.json` and Prosim2FO `settings.json`
- [ ] Docs site / user manual
