# Full code review — 2026-11-03

Branch `feature/settings-hub-usability` at `e5d86287` (1.0.0-rc.2). Read-only review; nothing was changed.
Eight area reports sit beside this file and carry the file:line evidence. This file is the summary and the plan.

| Area report | Covers |
|---|---|
| [audit-core.md](audit-core.md) | Core: redundancy, layering, options, stores/event table |
| [audit-speech.md](audit-speech.md) | Speech: voice pipeline map, 20 gates, redundancy, seams |
| [audit-gsx.md](audit-gsx.md) | GSX: departure cycle, gate select, resync maps, write safety |
| [audit-web.md](audit-web.md) | Web: routing, settings write, live update, pages, packs, assets |
| [audit-small-and-di.md](audit-small-and-di.md) | App, Prosim, Sim, Audio, Skalarki, tools, DI map, build warnings, repo hygiene |
| [audit-tests.md](audit-tests.md) | Test run, shape, duplicated fakes, timing smells, untested areas |
| [audit-probes-docs.md](audit-probes-docs.md) | Probe catalog health, event blind spots, docs drift |
| [sweep-report.md](sweep-report.md) | Mechanical sweep: unreferenced types, clones, event names, options usage, "key" identifiers |

## 1. Numbers

| Thing | Value |
|---|---|
| Production code | 984 files, ~171 k lines (Core 54 k, Speech 55 k, Web 25 k, Gsx 20 k, rest 18 k) |
| Tests | 433 files, 75 k lines, **6187 pass / 0 fail / 0 skip**, 32 s test phase |
| Build | 0 errors, 77 warnings (CA1859 ×21, CA1305 ×18, CA1001 ×6, NU1510 ×3, CA2254 ×1) |
| Dead types | **0** fully dead; 110 over-exposed (public but file-local); 53 test-only cores (intended) |
| TODO/FIXME | 0 |
| Convention drift | 0 `async void`, 0 blocking on async, 0 interpolated log strings, 0 `ReadDataRef` polling, token never logged |
| Hosted services at boot | 26 (+ Kestrel); ~60 timers, ~95 wake-ups/s idle |
| Options | 47 sections, 799 properties; 3 unread, 22 JSON-only (exempted in a test), 15 legacy fields without a migrator step |
| Probes | 208; 43 `open` on closed issues; 1 machine probe checks an event name that does not exist; 71 emitted event types no probe mentions |
| Language packs | 3,921 ids × 7, structurally perfect; **94 texts still English in all six non-en packs** |

Verdict: the codebase is healthy. No dead subsystems, no layering violations, write safety holds. The cost is in
**repetition** (the same 10–20 line shape copied many times) and **a few very large files**, not in bloat.

## 2. Confirmed bugs (fix first; each is S)

| # | Where | What | Evidence |
|---|---|---|---|
| B1 | `src/ProsimCompanion.Gsx/Automation/GsxTriggerSlot.cs:109-111` | Duplicate guard is off for NoConfirm sends. Jetway/stairs **retract** is a NoConfirm toggle with four senders (beacon effect, on-final hook, after-departure effect, voice). A second toggle inside the 2 s spacing re-connects the jetway. | audit-gsx A1 #5, finding 1 |
| B2 | `src/ProsimCompanion.Web/Pages/Home.razor:516,650,875-884` | `_tuneNoteTimer` is never disposed in `Dispose()`; a stray `_tuneNoteTimer?.Dispose()` sits inside `ConfirmFuel()` at line 650 (wrong timer, wrong method). | audit-web A3 |
| B3 | `docs/agents/verification-probes.json:4682` | Probe `gsx-simbrief-reload-new-plan-only` (#223) asserts on `gsx.simbriefReload`; code emits `gsx-simbrief-reload` (`GsxSimbriefReloadService.cs:466`). Assert-absent passes vacuously → false green on an open rc.2 fix. | audit-probes-docs 2 |
| B4 | `src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:747` | Bare `catch { condHit = false; }` — a malformed abnormal condition never fires and nothing is logged. | audit-speech 8 |
| B5 | `src/ProsimCompanion.Speech/Persona/CruiseSmallTalkService.cs:624` | `morning = TotalHours < 12` → 00:00–03:59 is "morning"; Core `TimeOfDayGreeting` says otherwise (the 2026-10-29 "Good afternoon at 04:50" family). | audit-speech 9 |
| B6 | `src/ProsimCompanion.Gsx/Automation/GsxAutomationService.cs:285` | New-plan LVAR clear bypasses the MSFS-down pending-write queue used by the resync service; the clear is silently lost when MSFS is down. | audit-gsx 5 |
| B7 | `src/ProsimCompanion.Core/.../PushbackSuggestionService.cs:78` | `_loop = Task.Run(LoopAsync)`; the loop catches only `OperationCanceledException`, any other exception kills pushback suggestions for the session, silently. | audit-core 15 |
| B8 | `src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:479` | `ListeningTone` has zero production readers and no UI control (exempted in `OptionFieldCompletenessTests.cs:59`). Delete or wire it. | audit-core 11, sweep 5 |
| B9 | `docs/agents/verification-probes.json` | 43 probes are `state: open` while their GitHub issue is closed (list in audit-probes-docs finding 1); 2 have `issue: null`; 3 have empty `sources`; `airport-common-names` is `feature-pending` on a shipped issue. | audit-probes-docs 1 |
| B10 | Web raw row editors | AudioSettings, CockpitSettings, DisplaySettings, GsxSettings, SetupSettings tables paint `invalid` but never call `Validity.Report` → Save stays enabled with red cells (ADR-0010 gap). | audit-web 3 |
| B11 | `tools/ProsimCompanion.Reduce` ProbeEvaluator | `watch` probes with a `machine` block are never evaluated (7 probes) — they go to the LLM as prose. | audit-probes-docs 3 |
| B12 | Packs | 94 ids byte-identical English in de/es/fr/ja/ko/nl (66 in `speechSettings.json`) — the "always translate GUI" rule is breached silently because the guard test checks ids, not content. | audit-web 1 |

## 3. Pathway maps (where to read them)

- **Voice pipeline, 22 steps + 20 gates**: audit-speech §A. Overlap: sterile cockpit is computed three ways (arbiter rule, router idle-miss with a hard-coded 1000 ft, router clarifier with an option) plus six hand-built `SpeechContext`s.
- **GSX departure cycle, gate select, startup resync**: audit-gsx §A1–A3. Trigger slot is the single `service.trigger` builder (analyzer + guard test); all aircraft writes pass `SimWriteGate` / `ProsimWriteGate`.
- **Web request routing, settings write path, live-update path**: audit-web §A1–A3. Every page unsubscribes except B2.
- **DI / composition root / hosted-service start order**: audit-small-and-di §A–B.
- **Core state stores and event buses (34 rows)**: audit-core "EVENT / STATE PATHWAY TABLE". Five competing sources of truth: `OfpStore.IsDiverted` vs `DiversionStore`; `DepartureCycleState.CycleReset` vs `GroundOpsSignals.FlightCycleReset`; "boarding started" held in four places; block times in three files (FlightTimes, DayState, Logbook); `SimSessionStore` vs `ConnectionStatusStore["SimConnect"]`.
- **Session event names**: 315 distinct, list in `sweep-report.md §4`. Naming is split kebab-case vs dotted.

## 4. Redundancy (the real payoff)

| # | Copies | Shape | Keep / replace with | Effort | Lines |
|---|---|---|---|---|---|
| R1 | 21 (+7 variants) | Speech timer "tick shell" (guard, try, catch at random level) | `GuardedTimer` / `PolledService` base | M | 350–420 |
| R2 | 7 | Core JSON state-file read / move-aside `.corrupt` / temp-then-move write | `JsonStateFile<T>` from `DayStateFile` (only one that handles a 0-byte file) | M | ~450 |
| R3 | 28 fakes + 16 mocks | Test ProSim dataref fakes over three interfaces | one `FakeDataRefs` | M | ~550 |
| R4 | 9 + 62 Moq setups | Test `IOptionsMonitor` fakes although `FixedOptionsMonitor<T>` exists in Core | use it | S | ~250 |
| R5 | 2 files, 137 identical lines, 21 shared methods | `Landing.razor` ↔ `Takeoff.razor` | `PerfPageBase` + Core `PerfRunwayMath` (testable) | M | ~280 |
| R6 | 16 | Gsx private `RecordDecision` helpers (3 shapes) | `GsxDecisions.Record` (keep logger category; probes match text) | S–M | ~90 |
| R7 | 11 | Web transient-note / armed-action timer | `TransientNote` on `WatchSet` | S | ~120 |
| R8 | 9 `FakeGsxApi` + 4 mocks | Test GSX API fakes, 55-line tail triplicated | one `RecordingGsxApi` | S | ~180 |
| R9 | 4 + 3 | parked-at-stand predicate with three different speed thresholds; `IsValidIcao` twice | `ParkingConflictGate.IsParkedEnginesOff`, `FmsPlanRules` | S | ~25 |
| R10 | 4 parsers + 12 `ToDigits` + 7 tonnes formatters (two roundings) | spoken numbers / units | one `SpokenNumber` + units helper | M | 200–250 |
| R11 | 9 tables | captain's-question yes/no phrase tables | factory + `ConfirmVocabulary.Classify` | S | ~180 |
| R12 | 5 | MCDU/FCU cancel-window + preconditions ladder (`McduArrivalChanger` ↔ `McduRadNavTuner` is a sustained clone pair) | `CancelWindow` + `McduPreconditions` base | M | 150–200 |
| R13 | 3 | approach verdict rule (Logbook, CompanyDayEngine, DebriefTemplate) | `DebriefFacts.OverallApproach()` | S | ~30 |
| R14 | 3 | hot-reload-by-write-time with silent IOException | one loader | S | ~60 |
| R15 | 8 of 24 | private `JsonSerializerOptions` identical to `SettingsJson.FileOptions` | reuse | S | ~100 |
| R16 | 6 + 1 per send | hand-built `HttpClient`s | one Core factory helper | S | ~40 |
| R17 | 3 | ACP ring-and-await (GroundCrewUpcall, Walkaround, CrewHail) | `IAcpChannel.RingAndAwaitAsync` | S | 50–60 |
| R18 | 13 handler fakes, 9 `RepoRoot()`, 8 deadline polls, 4 clocks over 3 abstractions | test helpers | `ScriptedHttpHandler`, `TestPaths`, `Eventually()`, `ManualClock` | S | ~300 |
| R19 | 2 (Home, Monitor) | weather card rendered twice over different presenters | one `WeatherCardTile` | S | ~60 |
| R20 | 2 pipelines | Audio ACP→mixer: local VoiceMeeter vs remote mixer agent, mute rules and settings models twice | one mapping core, two transports | M | ~150 |

Sum of the confident removals: **≈ 3,000 production lines + ≈ 1,500 test lines**, roughly 2–3 % of the code. Small in
percent, large in "places a fix has to be applied".

## 5. Bloat and structure

**Files > 500 lines**: 24 in Speech, 6 in Gsx, 11 in Web, ~8 in Core. Each area report lists the seam. The ones worth
doing first because they also have no tests:

| File | Lines | Seam |
|---|---|---|
| `Gsx/Gate/GsxGateSelectionService.cs` | 1332 | host + AirportPickWalker + SuffixVariantChooser + PositionMenuAnswerer + GateSelectLadder |
| `Speech/SayIntentions/SayIntentionsService.cs` | 1080 | transmit / flight.json poll / comms hand-over; two methods duplicated verbatim in `FoRadiosService` |
| `Speech/Abnormals/FailureMonitor.cs` | 977 | `FailureDetector` (pure) + `EcamDialogueRunner` |
| `Prosim/Loadsheet/LoadsheetService.cs` | 924 (+221 Lmc) | 15 deps, 17 subscriptions, 0 tests, writes fuel/pax/cargo to ProSim → read model + pure `PrelimTriggerCore` + publisher |
| `Web/Pages/SpeechSettings.razor` | 1406 | one child component per rail section + `PttBindingEditor` |
| `Web/Pages/Home.razor` | 885, 29 injects | tile components + code-behind |
| `Core/Flight/FlightStateEngine.cs` | 969 | `PauseTracker` (~130 lines), ground-contact filter, heartbeat |
| `Core/Flight/FlightStateSnapshotService.cs` | 748, 13 params | SimBrief plan-only fetch does not belong here |
| `Core/Configuration/GsxOptions.cs` | 673, 101 flat props | nest like `SopOptions` |

**Layering (Core is too fat)**: 143 Core types are consumed only by Speech (Day, Scenarios, Crew, Geo, Persona,
Debrief, Fuel, KokoroVoices, FoFactSheet…), 30 only by Gsx, 22 only by Prosim. Four `HttpClient` users, Win32
file dialogs and X509 loading also live in Core. Rule of thumb from the Core report: keep the `SnapshotStore` +
record + control interface in Core, move the engine to the feature. ~4.5 k lines would move. L effort, do it in
slices.

**Constructor arity**: CruiseSmallTalkService 27 params, BriefingService 24, ScenarioService 22, WalkaroundService
20, UtteranceRouter 19, 18 more at ≥ 12. A `VoiceFeatureHost` value object (arbiter + eventLog + phase + signals)
takes four off ~40 constructors.

**Dead-ish data**: 261 of 554 `ProsimDataRefNames` constants are referenced nowhere (all 72 `Cdu2Key*`, 36 `Oh*`,
27 `Efis*`…; list in the Core report's `drdead.txt`). The smart-button picker reads the embedded CSV, not this
class. Policy call: trim, or keep as a catalogue and say so.

**Idle load**: five phase-blind 100 ms timers (TurbulenceMonitor, TouchdownRecorder, CalloutsEngine, EfatoService,
RejectedTakeoffService) + ~20 one-second timers reading the same snapshot. One shared tick source or phase gating.

**Repo hygiene**: untracked `claude/` holds `mixer-protocol.md` and `fov-tool-design.md` referenced by 11 tracked
files → move to `docs/`. `out/` (74 MB), stray `level` file, `.superdesign/` and `.vs/` not ignored. Root
`Prosim_B38M/B738_Dataref.csv` (432 KB) referenced by nothing; `Prosim_A322_Dataref.csv` is an embedded resource
reached via `..\..\` from `Core.csproj`. `ROADMAP.md:1108` links a git-ignored scratchpad file.
`docs/parity-backlog.md` is entirely obsolete.

## 6. Dead code (small)

`Core/Hosting/PlaceholderSubsystemService.cs` (never registered); `IFlightPhaseSource.LiveChanged` (no production
subscriber); `DepartureCycleState.CycleReset` (tests only); `IProsimGateway.CancelBoardingAsync` and
`GetFailuresAsync`; `IMixerClient.Unwatch`; `GsxGateResolver.ResolveCanonical` / `IsUnambiguousNearestMatch` /
`ResolveAnchorToken`; `GsxMenuControl.WaitForAsync`; `Aviation.TryParseSpoken`; `CompanyChannelService.RequestLoadsheet`;
`AbnormalDefinition.ContextFlags` (deserialised from ~20 JSON files, read by nothing);
`PredecessorConfigImporter` (470 lines, one call site — retire with the predecessors). ≈ 300 lines total.

## 7. Tests

6187 green in 32 s. Coverage ratio 0.44 overall. Zero-test areas: `Sim/Input` (vJoy relay — the winmm crash
history), `Prosim/Loadsheet` (82 % untested), `Audio/` root, `Prosim/Gateway`, `Speech/Playback`; 22 of 26 hosted
services are never constructed in a test. 13 tests start a real `BackgroundService` with `StartAsync → Task.Delay(100)`
(the .NET 10 race). `FlightStateSnapshotTests` waits the real 1.5 s write delay ten times. `ReduceTests` runs the
whole 22 MB recordings folder three times. The PCGSX001 analyzer is literal-only (a `const` bypasses it) and has no
Roslyn tests. `verification-probes.json` has no guard test — which is why B3 exists.

## 8. Docs drift

`ARCHITECTURE.md` and the CLAUDE.md "Solution layout" describe 6 projects; there are 11. "Feature projects never
reference each other" is false (Skalarki.Sdk → Skalarki by ADR-0017; Gsx → Analyzers build-only). `ROADMAP.md` has
no phase for 0.8.0-rc.9–rc.26 or 1.0.0 (ADR-0016/17/19 unmentioned; "Duty Day default off" is stale). CHANGELOG:
15 tags without entries, 9 entries without tags. CONTEXT.md has no terms for Diagnostics/ECAM, Skalarki, cockpit
switches, tech log, Radios, mixer, Screen & FOV, SayIntentions, Stream Deck, themes/packs, scenarios, telemetry/probes.
`feature-inventory.md` has 6 stale lines. README + manual spot check of 19 claims: no mismatches.

## 9. Suggested order of work

Each package is one branch-sequential commit set per the branching rule; fixes ship with probes/tests.

1. **Bugs (B1–B12)** — all S. Ship together as one fix batch. B1 and B6 need a slot/LVAR-writer test; B3/B9 need
   the probe lint test (also closes the hole for next time).
2. **Test helpers (R3, R4, R8, R18)** — pure test code, zero product risk, −1,500 lines, removes the timing flakes.
3. **Core `JsonStateFile<T>` (R2) + Speech `GuardedTimer` (R1)** — the two biggest production clones; mechanical.
4. **Gsx small set (R6, R9, LVAR writer, `GsxMenuTitles`)** and **Web small set (R7, R19, `WatchOptions`)**.
5. **Sterile predicate + spoken numbers (R10, sterile)** — behaviour-adjacent, needs a flight.
6. **Big-file splits with pure cores**: LoadsheetService, FailureMonitor, GsxGateSelectionService, SayIntentionsService.
7. **Core slimming (move Speech-only engines out)** — L, do per folder; `Day/` and `Scenarios/` first.
8. **Docs**: ARCHITECTURE/CLAUDE.md project list, ROADMAP phases since rc.9, CHANGELOG backfill, CONTEXT.md terms,
   delete `parity-backlog.md`, move `claude/*.md` into `docs/`.

Not recommended: a flattening of the 110 "over-exposed" public types (cheap to do, little value), renaming the
72 `Cdu2Key*` dataref constants (they mirror ProSim names), making the voice FO's spoken English into packs
(docs/agents/localisation.md says spoken phrases stay English — the real gap is ~40 reusable lines that belong in
`PhraseBank`).
