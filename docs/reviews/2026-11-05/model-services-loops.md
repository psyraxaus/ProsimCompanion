# Services & loops model — `fix/code-review-2026-11-03` (main 0db90771 → HEAD ee2c2ef0)

Method: `git diff main...HEAD -- src` grepped for `BackgroundService|IHostedService|Timer|Task.Delay|while|Channel<|Subscribe(|+=|Task.Run|_ =|SemaphoreSlim|lock|Interlocked|ProcessTick|Tick(`; every owning class then read in full (new files) or as its diff plus surrounding context (modified files). Lifetimes come from the four `*ServiceCollectionExtensions` diffs. `AddStartupModule<T>` = singleton + resolved at host start by `StartupModuleHost` (`Core/Hosting/StartupModules.cs:31-41`); `Start()` runs only when the type implements `IStartupModule`, otherwise construction is activation; modules are disposed in reverse order by `StartupModuleHost.StopAsync` (`:95-112`). All hosted services are registered before `StartupModuleHost` (`App/ServiceComposition.cs:64-83`), so at shutdown the module host stops first.

Ratings: S = silent misbehaviour / bug, M = standards breach likely to bite, L = nit. "By reading" = not executed.

---

## 1. Inventory — services, loops and subscriptions the branch ADDS

### A. `FmsPlanWatcher` — Core/Aircraft/FmsPlanWatcher.cs:24
| Field | Value |
|---|---|
| Lifetime / registration | singleton, `AddCoreServices` (CoreServiceCollectionExtensions.cs:150); not a startup module — constructed on first resolution (by `LegBoundaryService`, `FmsPlanReminderService`, `ProsimDiagnosticsCollector`). |
| Trigger | push: `IDataRefSubscription.ValueChanged` on `aircraft.fms.origin` + `aircraft.fms.destination` (`:38-42`); `aircraft.fms.flightPhase` is subscribed read-only (witness). |
| Inputs | the three cached datarefs; pure `FmsClearDetector` (`FmsClearDetector.cs:109-151`). |
| Outputs | event `Cleared(FmsClearedEventArgs)`; one Information log line. No session event here (the consumer records `fms.cleared`). |
| Concurrency | `lock(_gate)` around the detector; event raised outside the lock, subscriber faults caught (`:78-85`). Runs on the ProSim SDK callback thread. |
| Cancellation / absence | `Dispose` unhooks and disposes the three subscriptions (`:88-95`), container-owned. With ProSim absent the refs never change → never fires. A subscription drop (null) disarms rather than reading as a clear (`FmsClearDetector.cs:128-136`). |
| Pure core / tests | `FmsClearDetector` — `tests/.../Aircraft/FmsClearDetectorTests.cs`. |
| Risks | none found. L: the watcher is resolved lazily; if nothing in the graph depends on it the FMS-clear path would never exist — today three consumers do. |

### B. `LegBoundaryService` — Core/Flight/LegBoundaryService.cs:32
| Field | Value |
|---|---|
| Lifetime | `AddStartupModule` (CoreServiceCollectionExtensions.cs:152), also exposed as `ILegBoundaryEvents`, `IFlightStatePart`, `ILegBoundaryRecovery` (`:153-158`). Construction is activation (no `Start()`). |
| Trigger | `GroundOpsSignals.ArrivalCompleted`, `FmsPlanWatcher.Cleared`, `OfpStore.Changed`, `IFlightPhaseSource.PhaseChanged` (TakeoffRoll/InitialClimb/Climb only), `DutyDaySignals.DayEnded` (`:90-97`). Also `Restore(JsonNode)` from the snapshot service (`RestoreOrder = 5`, `PersistsAcrossLegs = true`). |
| Inputs | `OfpPlanIdentity.PlanId(ofp)`, `IFlightPhaseSource.Snapshot().Data` for the parked test (`:216-217`). |
| Outputs | `LegBoundaryStore.Update`; events `leg.boundary` (every accepted edge + restored/adopted state, `:284-294`, `:326-336`) and `fms.cleared` (every clear, accepted or not, `:218-225`); `DiagEventLog` lines TURNAROUND / AWAITING PLAN / NEW LEG / LEG BOUNDARY CLEARED; CLR event `LegBegan` (only when `change.LegBegan && PlanId != null`, `:350-360`); `Changed` for the snapshot. No aircraft writes. |
| Concurrency | `lock(_gate)` around every `_core` mutation; publishing outside the lock. Runs on whichever thread raises the edge (SDK callback, GSX thread, flight-state timer). Fault in a `LegBegan` subscriber is caught (`:352-359`). |
| Cancellation / absence | `Dispose` unhooks all five (`:363-373`). ProSim absent → machine never leaves None. No token needed (no async). |
| Pure core / tests | `LegBoundaryCore` (`LegBoundary.cs:92-196`) — `Flight/LegBoundaryCoreTests.cs`; the service with a real `FmsPlanWatcher` — `Flight/LegBoundaryRestoreTests.cs`; replay — `Flight/FlightReplayTurnaroundTests.cs`. |
| Risks | **L**: `LegBoundaryCore.Airborne` (`LegBoundary.cs:176-188`) clears `Deboarded/FmsCleared` when `State == None` but returns `null`, so the service skips `Publish` → store/snapshot not refreshed until the next edge; `Capture()` keeps returning a non-null node for the stale flags. Harmless in practice (a take-off with flags set but state None is itself an edge case). |

### C. `DutyDaySignals` — Core/Day/DutyDaySignals.cs:173
Singleton seam (`AddCoreServices` :222). One event `DayEnded(DutyDayEnded)`; raised by `CompanyDayService.TryEndDay` (`CompanyDayService.cs:754`) **after** the day's own records are final. Subscribers: `LegBoundaryService` (→ None), `FlightStateSnapshotService` (→ `Invalidate(..., keepBoundary:false)`), `GsxAutomationService` (departure cycle reset). Fires on the raiser's thread; the raiser wraps in try/catch so one faulting subscriber kills the others' delivery (`Action` multicast stops at the first throw) — **L**.

### D. `FoRosterService` — Core/Persona/FoRosterService.cs:17
Request-driven singleton (`:363-366`), no loop. Re-reads `fo-roster.json` by last-write time on every `Pool`/`Current`/`Pin` access under `lock(_gate)` (`:270-317`) — a `File.GetLastWriteTimeUtc` per access; `Current` is read by the arbiter on every utterance (`SpeechArbiterService.FirstOfficerVoiceOrNull`), by `PersonaService.Effective` and by the small-talk tick. Writes `fo-roster-state.json` through `FoRosterStateFile` (`lock(_io)`), flips `persona.roster.mode` via `SettingsWriter` (IO exceptions caught, `:253-268`). Events: `persona.rosterDraw`, `persona.rosterPin`, `persona.rosterRelease`. Tests: `Persona/FoRosterTests.cs`. **L**: a stat() per utterance/tick is cheap but is a hidden file poll by another name; acceptable (the `PhraseBank` pattern the comment cites).

### E. `CompanionTrackingLvars` — Gsx/Sync/CompanionTrackingLvars.cs:20
| Field | Value |
|---|---|
| Lifetime | singleton, `AddGsxServices` (GsxServiceCollectionExtensions.cs:121). |
| Trigger | request-driven writers (`MarkServiceDoneAsync`, `MarkPrepDoneAsync`, `ResetForArrivalAsync`, `ClearAllAsync`, `ClearDepartureSideAsync`) + `ConnectionStatusStore.Changed` for the SimConnect disconnected→connected flush (`:187-220`). |
| Outputs | **LVAR writes** via `ISimVars.WriteAsync` for `CompanionLvarNames.*` only (nine `ServiceDone` + Turnaround, PrepDone, LoadsheetPrelimEdition, LoadsheetFinalSent). These are the companion's own tracking LVARs, written through the Sim project's `SimWriteGate` (pre-existing allow-list; unchanged on this branch) — verified the names are the same set the old `GsxStartupResyncService.WriteLvarAsync` wrote. |
| Concurrency | every write batch is `Task.Run` (`:130-152`), sequential inside; queue is `Dictionary` under `_pendingGate`, latest value per LVAR; `_simConnectWasConnected` via `Volatile`. Every public method is fire-and-forget-safe (never throws). |
| Cancellation | none — the `Task.Run` bodies take no token; `Dispose` only unhooks the status event. A flush racing a shutdown just fails inside `TryWriteAsync` (caught). |
| Tests | `Gsx/CompanionTrackingLvarsTests.cs`. |
| Risks | **L**: ordering between the connected-edge flush (`:213-219`) and a direct write issued at the same moment is not serialised (two independent `Task.Run`s) — a queued `boarding=0` from a new-plan clear could land after a live `boarding=1`. Needs MSFS to reconnect in the same second a service completes. |

### F. `SdkFailureInjector` / `ProsimFailureGate` — Prosim/Failures/*.cs
Singleton (`ProsimServiceCollectionExtensions.cs:37-38`), request-driven, no loop. Every SDK call goes through `SdkConnection.RunAsync` = `Task.Run` on the live `ProSimConnect` (`SdkConnection.cs:107-119`); no session → `unavailable`, logged once (`SdkFailureInjector.cs:192-205`). **Write safety**: `ArmAsync` calls `ProsimFailureGate.EnsureAllowed(name, liveNames)` inside the SDK lambda (`:65-70`) — allow-list = catalogue's *verified* names ∩ ProSim's live `getFailures()`; `InstallCatalogue` is `internal` (`ProsimFailureGate.cs:267`). No dataref path. `ProsimConnectionService` gains `internal Current` (`:27`) and is now registered as itself + hosted (`:33-34`). Tests: `Scenarios/ScenarioTests.cs` covers the gate; the injector itself is not constructed by any test (SDK type) — **unverified at runtime on this branch**. **L**: `ArmedAsync` matches handles by `ReferenceEquals` on `getArmedFailures()` items (`:101`) — relies on the SDK returning the same object instances it created; if it rebuilds the list, every verify fails ("not-verified" → clear) — unverified against the SDK.

### G. `TransitionReminderService` — Speech/Callouts/TransitionReminderService.cs:26
| Field | Value |
|---|---|
| Lifetime | `AddStartupModule` (SpeechServiceCollectionExtensions.cs:46), `IStartupModule`. |
| Trigger | `System.Threading.Timer`, due 2 s, **period 1000 ms** (`:107`), fixed. Subscribes `GroundOpsSignals.FlightCycleReset` and `IFlightPhaseSource.SessionReset` → `_core.Reset()`. One dataref subscription: the seat-mapped EFIS baro-STD gate, chosen once at ctor from `speech.pilotSeat` (`:90-91`). |
| Inputs | `sop.transition.*` (read per tick), `IFlightDataSource.Sample().SpokenAltitudeFt` (the pilot's PFD, issue #198), `IFlightPhaseSource`, `OfpStore` (TA/TL re-lookup per origin/destination pair via `DfdNavDataProvider.Lookup`, synchronous, once per pair `:179-221`), `HeroWeatherStore` for destination QNH. |
| Outputs | speech `EnqueueAsync(Normal, TTL 20 s, Tag "transition.reminder")` fire-and-forget (`:173`); event `transition.reminder`. No writes. |
| Concurrency | `Interlocked` tick guard (`:254`), `lock(_gate)` around the core; a tick cannot overlap. |
| Cancellation | `Dispose` unhooks + disposes timer and subscription (`:110-116`); no CTS (nothing awaited). Disabled option → silent, logged once; no DFD → logged once, never a guessed figure. |
| Pure core / tests | `TransitionReminderCore` — `Speech/TransitionReminderCoreTests.cs`. The service shell (timer, nav refresh, QNH selection) has **no test**. |
| Risks | **L**: `DfdNavDataProvider.Lookup` runs on the timer thread (SQLite read) once per OFP pair — fine. Re-speak on restart: the core resets on `SessionReset`/`FlightCycleReset`; after an app restart mid-climb the "set standard" reminder can be given again if the pilot is still below TA on QNH — by design (not a once-per-flight latch; not snapshot-persisted). |

### H. `WalkaroundService` — Speech/Crew/WalkaroundService.cs:33
| Field | Value |
|---|---|
| Lifetime | `AddStartupModule` + `IFlightStatePart` (SpeechServiceCollectionExtensions.cs:359-365). |
| Trigger | Timer due 1 s, **period 1000 ms** (`:159`); events `FlightCycleReset`, `WalkaroundRaiseStore.Answered`; `Restore(JsonNode)`. |
| Inputs | `groundCrew.walkaround.*`, `IGroundFactsSource.Facts`, `GsxDiagnosticsStore.Snapshot()` (prep stage, refuel/boarding rows, `:317-345`), flight phase/live, weather (`HeroWeatherStore`), `ISimLocalTime`, `WalkaroundFindingBank` (user JSON). |
| Outputs | `FoAvailabilityStore.SetAway/Clear` (gates the router and the refuel pump); speech: announce (Low, tag `walkaround.announce`), report (Normal, `walkaround.report`, role `FirstOfficerOnInterphone` when `OnInterphone`), abandon (Low), no-go line (Normal, `walkaround.nogo`); **aircraft write**: `IProsimDataRefs.PressMomentaryAsync(OhCallsMech)` (`:575`) — allow-listed in `ProsimWriteGate.cs:44` (pre-existing, the ground-upcall path); `ITechLogService.RaiseDefect` (no-go) / `TechLogDialogueService.RaisePrefilledAsync` (deferrable, after the yes); `DeiceRequestStore.Publish(Ask…)` for an icing finding; events `walkaround.started/reported/abandoned/finding/noted/techLogRaised/restored`. |
| Concurrency | `Interlocked` tick guard; `lock(_gate)` for core state; a single `_inFlight` task (announce/report/abandon) started with `Task.Run(..., CancellationToken.None)` — the tick returns `None` while it runs (`:229-232`); the raise follow-up is a second fire-and-forget `Task.Run` (`:534-545`). |
| Cancellation | own `_shutdown` CTS cancelled in `Dispose` (`:162-169`), passed into every await; `_inFlight` is not awaited at dispose. |
| Degrade | every optional collaborator is nullable; diagnostics read wrapped (`:323-342`); MECH press failure logged at Debug and the report still speaks; disabled → logged once. |
| Pure core / tests | `WalkaroundCore` + `WalkaroundFindingDraw` — `Crew/WalkaroundTests.cs` (also constructs the service). |
| Risks | **L**: `Tick` logs a failing tick at **Debug** (`:198`) — a persistently throwing tick is invisible at the default level (TransitionReminder uses Error, SmallTalk Error, Scenario Debug — inconsistent). **L**: the `FoAvailabilityStore.Clear()` in `OnFlightCycleReset` runs while `_inFlight` may still be in `ReportAsync`, which then calls `Clear()` again and records `walkaround.reported` for a cycle that was reset — cosmetic. |

### I. `CruiseSmallTalkService` — Speech/Persona/CruiseSmallTalkService.cs:38
| Field | Value |
|---|---|
| Lifetime | `AddStartupModule` (SpeechServiceCollectionExtensions.cs:354). |
| Trigger | Timer, due and **period 30 s** (`:43`, `:189`), fixed; events `PhaseChanged` (→ core), `ISpeechArbiter.Observed` (quiet clock, `:212-218`), `SmallTalkOfferStore.Answered`. |
| Inputs | `smallTalk.*`, `speech.sterileCockpitCeilingFt`, `briefing.llmEnabled`, flight view, `FlightProgressStore`, `HeroWeatherStore`, `TurbulenceStore`, every `ICaptainsQuestionStore` (`questionOpen`), `FailureMonitor.ActiveAbnormalTitles`, `RegionFactService.NextDueUtc` (stands aside 3 min), `PersonaService.Chattiness/Effective`, `IFoRosterService.Current`, `IPlaceLookup`, `ISimLocalTime.LocalDate/ZuluTimeOfDay` → `SolarPosition`, `FoFactSource.Build`. |
| Outputs | speech Low priority, TTL 90 s, tag `smalltalk.{kind}`, `IsStillValid: phase == Cruise` (`:665-668`); model tier through `StreamingNarrator` with `NumberVerifier`; Offer kind → `SmallTalkOfferStore.Ask(text)` (the shared captain's-question dialogue voices it); Layover kind → **borrows the mic for 10 s** (`:479-496`) and may speak one follow-up; a yes to the offer → `CabinCrewService.AnnounceAsync("cabin.smalltalk.coffee", …)` after a 2–4 min `Task.Delay`; events `smalltalk.spoken`, `smalltalk.reply`. No writes. |
| Concurrency | `Interlocked` tick guard; `lock(_gate)` around the core; one `_inFlight` delivery (`Task.Run(..., CancellationToken.None)`), tick skips while it runs; `CoffeeInFlight` replaced per answer (only the latest tracked). |
| Cancellation | `_shutdown` CTS cancelled in `Dispose`; LLM calls get a linked 20 s `ModelBudget` (`:385-386`); `OnFirstSpeech` lifts the budget. |
| Degrade | LLM down/unconfigured/unhealthy → bank line restyled; every optional read try/caught. |
| Pure core / tests | `SmallTalkCore` + `SmallTalkBank` — `Persona/SmallTalkCoreTests.cs` (mentions the service type; by name only). The shell (tick → claim → deliver, offer flow, coffee delay) is not exercised by a test. |
| Risks | **M** (judgement): the Layover listen borrows the mic with `IsListening = true`, so for up to 10 s the new `DialogueHoldRule` defers every Normal-priority tagged prompt from other features (purser questions, gate monitor…) — a small-talk line now has the same hold power as a minima capture. Not wrong, but worth knowing. **L**: `OnSpeechObserved` is invoked for *every* arbiter event on the arbiter's thread — a plain `DateTimeOffset` store, fine. |

### J. `FoRosterVoiceFeature` — Speech/Persona/FoRosterVoiceFeature.cs:25
Startup module + voice feature (SpeechServiceCollectionExtensions.cs:299-300). Event-driven: `PhaseChanged` (Preflight edge), `DayStatusStore.Changed`, `ILegBoundaryEvents.LegBegan`, `FoRosterChangeStore.Answered` (`:134-137`); `Start()` evaluates once for a mid-day start (`:144`). Pure rule `FoRosterTriggerCore.Evaluate` under `lock(_gate)`; the draw writes the state file and raises `Changed`; intro speech via `Task.Run` (`IntroInFlight`, Low, TTL 5 min, tag `persona.gateIntro`), styled first. Events `persona.foOnDuty` (on Preflight/leg-began edges), `persona.gateIntro`. `Speak()` observes faults via `ContinueWith(OnlyOnFaulted)` (`:420-422`). Dispose unhooks four. Tests: `Persona/FoRosterVoiceFeatureTests.cs`, `Persona/FoRosterTests.cs` (trigger core). **L**: `_dayStore.Changed` fires on every 60 s day tick (`CompanyDayService.ProcessTick` → `_store.Update`), so `Evaluate` (file stat + state copy) runs once a minute while a day is open — cheap. Double-speak on restart: prevented by `IntroDone` in the persisted roster state (`FoRosterService.MarkIntroSpoken`).

### K. `FmsPlanReminderService` — Speech/FoSide/FmsPlanReminderService.cs:43
Startup module (`:113`). Single subscription `LegBegan` (`:482`); pure `ShouldRemind` (`:493-508`); speaks once (Normal, TTL 2 min, tag `fo.fmsReminder`) via fire-and-forget `SpeakAsync` with its own CTS; event `fo.fms-reminder`. Once per leg by construction (the edge fires once per plan id). Test: `Speech/FmsPlanReminderTests.cs`. No risks found.

### L. `ScenarioService` — Speech/Scenarios/ScenarioService.cs:34
| Field | Value |
|---|---|
| Lifetime | `AddStartupModule` + `AddHostedService(same instance)` + `AddVoiceFeature` + `IScenarioControl` (SpeechServiceCollectionExtensions.cs:367-371). |
| Trigger | Timer due 1 s, **period 1000 ms** (`:233`); events `PhaseChanged` (Preflight edge → `TryPlan`), `FailureMonitor.FailureDetected`, `FailureMonitor.AbnormalEnded` (new), `FlightCycleReset`, `ScenarioDecisionStore.Answered`; requests from voice / API / Stream Deck via `IScenarioControl`; `IHostedService.StopAsync` for the exit clear. |
| Inputs | `scenario.*`, `abnormals.rtoEnabled/efatoEnabled`, `speech` (sterile), `briefing.llmEnabled`, flight view (`AltitudeAglFt`, phase, since), `FailureMonitor.ActiveAbnormalTitles/DialogueActive/HasAbnormal`, `ITechLogService.OpenDefects` (MEL A = no-go), `ScenarioCatalogueBank`, `ScenarioStateFile` (plan persisted, resumed within 6 h). |
| Outputs | **ProSim failure writes** through `IFailureInjector.ArmAsync/ClearAsync/ClearAllAsync` (allow-listed, §F); `ScenarioStore` for the STATUS page; `ScenarioDecisionStore.Ask` (pick-one captain's question); `DiversionStore.Declare` on return/divert; speech via `_arbiter.SpeakAsync` (High for the clear-all result, Normal for "planned?"); events `scenario.planned/arming/armCancelled/armFailed/armed/fired/decision/decided/cleared/clearFailed/clearAllRequested/failuresListed`. |
| Concurrency | `Interlocked` tick guard; `lock(_gate)` for plan state; **`SemaphoreSlim _writes(1,1)`** serialises arm / clear / clear-all (`:433`, `:687`, `:829`); `_armCancel` CTS for the 3 s window; a one-shot `_transientTimer` for `ClearAfterSeconds`. |
| Cancellation | `_shutdown` CTS; `Dispose` unhooks five events, disposes both timers, cancels the CTS and **disposes `_writes`** (`:255-267`). |
| Degrade | injector unavailable → `ArmAsync` returns null → "stays planned"; `ClearAllAsync` returns −1 → warning; tick failures logged at **Debug** (`:358`). |
| Pure core / tests | `ScenarioScheduler` (Plan/Evaluate/Allowed/DecisionLine) — `Scenarios/ScenarioTests.cs`, which also constructs the service with a fake injector. |
| Risks | **S (by reading): re-arm storm when ProSim refuses.** `ProcessTick` spawns `Task.Run(ArmPlanAsync)` on *every* tick while `decision.Arm` is true and the plan is still `Planned` (`:376-392`); the plan only leaves `Planned` after a successful verify (`:492-498`). Each `ArmPlanAsync` queues on `_writes`, then spends ≥3 s in the cancel window before the injector call. If the arm fails (name not in the live list, SDK without the failure API, "not-verified") the plan stays `Planned`, the scheduler keeps saying Arm (the only gate is `PhaseSinceUtc ≥ FireAfterSeconds`, `ScenarioScheduler.cs:204`), and every subsequent tick adds another queued task: a new `scenario.arming` + `scenario.armFailed` pair every ~3 s and an unbounded semaphore queue for the rest of the phase. Fix shape: an "arming" flag checked in `ProcessTick`, and after a failed arm either mark the plan failed or back off. **S (by reading): the exit clear cannot run.** Hosted services stop in reverse registration order; `StartupModuleHost` (registered last, `App/ServiceComposition.cs:83`) disposes the activated `ScenarioService` first — `_writes.Dispose()` — and only then does `ScenarioService.StopAsync` (`:241-253`) call `ClearOursAsync` → `_writes.WaitAsync` → `ObjectDisposedException` (unhandled in `StopAsync`). An armed failure survives an app exit despite the doc comment; the next start's `LeftArmed` path (`:209-213`) is the actual safety net. **L**: `_lastHold` reset only on cycle reset; fine. |

### M. New `ICaptainsQuestionStore` adapters (Core/State, Core/Persona, Core/Crew, Core/Scenarios)
`EndOfDutyOfferStore` (yes/no, MaxAsks 1, TTL counts as no, final for cycle — `EndOfDutyOfferStore.cs:17-23`), `BriefingQuestionsStore`, `FoRosterChangeStore`, `SmallTalkOfferStore`, `WalkaroundRaiseStore`, `ScenarioDecisionStore`: all `CaptainsQuestionStore<T>` subclasses registered as both the concrete type and `ICaptainsQuestionStore` (CoreServiceCollectionExtensions.cs:216-248). Request-driven; voiced by the pre-existing `CaptainsQuestionDialogue` from the phrase tables registered in Speech. `FoAvailabilityStore` is a plain snapshot store (`SetAway/Clear`), read by `UtteranceRouter`, `DeparturePumpGatherer`, `WalkaroundService`. The `BriefingQuestionsPhrases.Interpret` action fires `_ = FollowUpAsync(...)` (`BriefingQuestionsPhrases.cs:52`) — fire-and-forget inside the dialogue, exceptions swallowed by design (`:101-109`). No loops, no writes.

### N. `DialogueHoldRule` — Speech/Arbiter/DialogueHoldRule.cs:25
Suppression rule wired into the arbiter with `() => mic is { IsBorrowed: true, IsListening: true }` (`SpeechArbiterService.cs` diff). Defers (not drops) Normal/Low tagged requests outside the exempt prefixes while a dialogue *listens*. Pure `Holds()`; test `Speech/DialogueHoldRuleTests.cs`. Interacts with every new listening path (small-talk Layover, briefing questions, end-of-duty offer, walkaround raise, scenario decision).

### O. Web: `SectionFoldStore` (scoped, per circuit, `lock(_lock)`, `Persist` event → `SettingsLayout` writes localStorage via `_ = InvokeAsync(...)`), `SettingsSearchService` (singleton index over the packs). No loops; the JS load is guarded for prerender/disconnect (`SettingsLayout.razor` diff). Not reviewed further.

---

## 2. Loop map — every periodic loop the branch adds or touches

| Loop | Cadence | Thread / mechanism | Option-controlled? | Overlap guard | Added/changed |
|---|---|---|---|---|---|
| `TransitionReminderService.Tick` | 1 Hz (due 2 s) | `System.Threading.Timer` (thread pool) | cadence fixed; work gated by `sop.transition.enabled` | `Interlocked` | **new** |
| `WalkaroundService.Tick` | 1 Hz | Timer | fixed; gated by `groundCrew.walkaround.enabled` | `Interlocked` + `_inFlight` | **new** |
| `ScenarioService.Tick` | 1 Hz | Timer | fixed; `scenario.enabled` gates planning only | `Interlocked`; writes under `SemaphoreSlim` | **new** |
| `CruiseSmallTalkService.Tick` | every 30 s | Timer | fixed | `Interlocked` + `_inFlight` | **new** |
| `CompanyDayService.TimerTick` | every 60 s | Timer | fixed; `day.autoCloseIdleMinutes` (default 300) and `day.endOfDutyOfferMinutes` gate the work | **none** (relies on `lock(_gate)` inside `ProcessTick`) | changed (idle rules, offer) |
| `NotificationSignalSource._settleTimer` | one-shot, 15 s after `Start()` | Timer | `Settle` property (tests only) | `lock(_gate)`, idempotent flush | **new** |
| `ScenarioService._transientTimer` | one-shot, `ClearAfterSeconds` | Timer | catalogue entry | `_writes` | **new** |
| `MicOwnership.ListenAsync` slice | 250 ms poll while a dialogue listens | `Task.Delay` loop, hard cap timeout + 60 s | — | per listen | changed |
| `FoRadiosService.WaitForCom1Async` | 500 ms poll, ≤ 60 s, per hand-off | `Task.Delay` loop inside fire-and-forget `RespondAsync` | — | none (two hand-offs could overlap) | **new** |
| `FoRadiosService` hand-off grace | 3 s floor after read-back | `Task.Delay` | fixed | — | **new** |
| `CabinCrewService.ReportCabinEmptyAsync` | 3 tries × 30 s | `Task.Delay` | — | `Interlocked` latch per leg | **new** |
| `CruiseSmallTalkService.CoffeeInFlight` | one-shot 2–4 min | `Task.Delay` | — | — | **new** |
| `ScenarioService.ArmPlanAsync` verify | 500 ms poll ≤ 5 s + 3 s cancel window | `Task.Delay` | — | `_writes` | **new** |
| `PushbackSuggestionService.RunLoopAsync` | existing 15 s `PeriodicTimer`; new 5 s back-off after a faulted iteration | `PeriodicTimer` | — | sequential | changed (#231) |
| `FailureMonitor` 2 Hz tick | pre-existing | — | — | — | changed: burst hold re-judged each tick, condition-fault logged once per definition |
| `SayIntentionsFrequencyMonitor` poll | pre-existing | — | — | — | changed: failure warnings every 20th, `sayintentions.atc-line` per line |
| `FlightStateSnapshotService._writeTimer` | pre-existing debounce | — | — | — | changed: boundary-only writes while suspended |

Cumulative new timer load: three new 1 Hz timers + one 30 s + the existing 60 s, all on the thread pool, each a few store reads. The three 1 Hz ticks are not aligned to the existing 250/500/100 ms pollers and run on separate pool threads; no shared lock between them. The only tick doing IO is `TransitionReminderService` (DFD lookup once per OFP pair) and `CompanyDayService` (state-file save when the offer fires).

---

## 3. Dependency sketch — who subscribes to what

```mermaid
flowchart LR
  subgraph Core
    FPW[FmsPlanWatcher<br/>datarefs fms.origin/destination]
    LBS[LegBoundaryService]
    LBStore[(LegBoundaryStore)]
    GOS[GroundOpsSignals<br/>ArrivalCompleted / FlightCycleReset(kind)]
    OFP[(OfpStore.Changed)]
    DDS[DutyDaySignals.DayEnded]
    FSS[FlightStateSnapshotService]
    NSS[NotificationSignalSource]
    FRS[FoRosterService]
    FAS[(FoAvailabilityStore)]
    QS[(captain's-question stores:<br/>EndOfDuty / BriefingQ / RosterChange /<br/>SmallTalkOffer / WalkaroundRaise / ScenarioDecision)]
  end
  subgraph Gsx
    CTL[CompanionTrackingLvars]
    GAS[GsxAutomationService]
    GSR[GsxStartupResyncService]
    GJS[GsxJetwayStairsService<br/>IGsxJetwayRetraction]
    GSC[GsxServiceControl]
    DPG[DeparturePumpGatherer]
    CSS[(ConnectionStatusStore)]
  end
  subgraph Speech
    CDS[CompanyDayService 60s]
    CCS[CabinCrewService]
    FCC[FmgcCrossCheckService]
    FPR[FmsPlanReminderService]
    FRV[FoRosterVoiceFeature]
    WAS[WalkaroundService 1Hz]
    CST[CruiseSmallTalkService 30s]
    TRS[TransitionReminderService 1Hz]
    SCS[ScenarioService 1Hz]
    FM[FailureMonitor]
    ARB[SpeechArbiter + DialogueHoldRule]
    MIC[MicOwnership.IsListening]
  end
  subgraph Prosim
    SFI[SdkFailureInjector + gate]
  end
  FPW -->|Cleared| LBS
  GOS -->|ArrivalCompleted| LBS & CCS & FCC & CDS & NSS
  OFP -->|Changed| LBS & GAS
  LBS -->|LegBegan| CDS & CCS & FCC & FPR & FRV
  LBS --> LBStore
  LBS -->|IFlightStatePart turnaround| FSS
  WAS -->|IFlightStatePart walkaround| FSS
  CDS -->|RaiseDayEnded| DDS
  DDS --> LBS & FSS & GAS
  GAS -->|RaiseFlightCycleReset(NewPlan)| GOS
  GOS -->|FlightCycleReset| NSS & GJS & WAS & TRS & SCS & FSS & GSR
  GSR -->|AdoptDeboardedFromEvidence| LBS
  GAS & GSR -->|LVAR batches| CTL
  CSS -->|Changed| CTL
  GSC -->|RetractAsync| GJS
  FAS --> DPG & WAS
  WAS --> FAS
  FRS --> FRV & ARB & CST
  CST & CDS & WAS & SCS -->|Ask| QS
  FM -->|FailureDetected / AbnormalEnded| SCS
  SCS -->|Arm/Clear| SFI
  MIC --> ARB
```

Threads: SDK callbacks (FmsPlanWatcher → LegBoundaryService → five Speech subscribers, synchronously), the GSX client thread (ArrivalCompleted → six subscribers), the flight-state 250 ms timer (PhaseChanged → LegBoundaryService/ScenarioService/FoRosterVoiceFeature/CruiseSmallTalk), and the five new pool timers. Every subscriber reached from `LegBegan` either does trivial work or fires a `Task.Run`/fire-and-forget (`CompanyDayService.HandleLegBegan` is the heaviest: event-log session rotation + state-file save + `_store.Update`, on the SDK/OFP thread under `lock(_gate)`).

---

## 4. What changed in existing services

- **`NotificationSignalSource`** (Core/Notifications, rewrite in 1b8c570a, #151): milestones raised within `Settle` (15 s) of `Start()` are **held** in `_pending` until `Restore()` lands or the one-shot settle timer fires (`:370-424`); the flush re-enters `Raise` so the restored once-per-cycle set drops duplicates. New `Func<DateTimeOffset>` clock. Timer disposed in `Dispose`. Subscriptions unchanged. Tests: `Notifications/NotificationTests.cs`. Risk **L**: `FlushPending` is the only thing that sets `_restoreApplied`; a snapshot with no `notifications` part never calls `Restore` → the 15 s backstop is the normal path for most starts (a "held until restored" Information line per milestone in the first 15 s — expected noise).
- **`GsxAutomationService`** (Gsx/Automation): subscribes `OfpStore.Changed` (new-plan reset via pure `NewPlanCycleReset.ShouldReset`, raises `FlightCycleReset(NewPlan)`, records `gsx-cycle-reset` with `trackingLvars = cleared|queued`) and `DutyDaySignals.DayEnded` (same reset, plan pinned as baseline); `RestoreCompletedCycleFromSeeds` after the resync marks a fully-seeded cycle complete and raises `DepartureServicesCompleted` (`gsx-departure-complete`). Tracking writes now `_ = _tracking.ClearDepartureSideAsync()` (fire-and-forget, safe by contract). Both handlers wrapped in try/catch on the raising thread. No test constructs the service with these paths (tests cover the pure helpers: `NewPlanCycleResetTests`, `ResyncCycleRestoreTests`).
- **`GsxStartupResyncService`**: the LVAR writer, the MSFS-down queue and the connection-status subscription moved out to `CompanionTrackingLvars` (−70 lines); the arrival reset is skipped when `LastFlightCycleResetKind == NewPlan`; `BetweenLegs` → `ILegBoundaryRecovery.AdoptDeboardedFromEvidence`; resync rule: "assume the rest done" only once boarding is proven, and an empty cabin under `completed` is not this leg's boarding. Tests: `Gsx/GsxStartupResyncTests.cs`.
- **`GsxJetwayStairsService`** (#225): implements `IGsxJetwayRetraction`; per-service `_retractionSentAt` latch reserved *before* the slot wait, released on non-Dispatched outcomes or exception, cleared on mirror evidence (`OnMirrorUpdated`, service no longer Active/Completed), SID change, new gate, connect, `FlightCycleReset`, and a 90 s safety valve. New subscriptions `Mirror.Updated`, `FlightCycleReset`, unhooked in `Dispose`. `GsxServiceControl` routes the crew's "retract jetway" through it (NoConfirm instead of the old watched call). Tests: `Gsx/GsxJetwayStairsRetractionTests.cs`, `Gsx/GsxTriggerSlotTests.cs`. Risk **M (unverified against GSX)**: the latch is released as soon as the mirror reads the service as *not* Active/Completed — if GSX reports a transient intermediate state (e.g. Requested/Bypassed) right after the toggle while the jetway is still moving, a second sender inside that window is let through to the slot, whose own spacing (`duplicateToggle`) is then the only protection. Depends on the mirror's real state sequence during a retraction.
- **`GsxTriggerSlot`**: a toggle (NoConfirm) behind the same toggle inside its spacing is now a duplicate → `Busy` with a recorded decision (`:108-142`).
- **`GsxServiceLifecycleTracker`**: `Seeded` flag per cycle; `IsSeededOnly/Unseed` (crew call overrides a seed), `AnyObservedProgress/ObservedProgress`, `ResetCycle(keep)`.
- **`GsxSimbriefReloadService`**: only reloads for a next-leg plan (`IsNextLegPlan` against the arrival baseline, #223). **`GsxGateAnchorService`**: parked at the stand → anchored by GSX's own session, no `gate.select` (#156), `gsx-gate-not-found` event. **`DeparturePumpGatherer`**: refuel held while `FoAvailabilityStore.IsAway`. **`DepartureSequencer`**: a cycle never reads "all done" behind a closed OFP gate. **`GsxDeiceRequestService`**: a verdict still owed when the cycle completes in the same pump (#204).
- **`CompanyDayService`** (Speech/Day): the next leg now starts on `LegBegan` (session rotation + `day.leg.started` + "Leg N. New sector, A to B"), never on the Preflight edge; `ArrivalCompleted` → `MarkDeboarded` (`day.idle.started`); `DropPhantomLeg` at start (`day.leg.dropped`); the 60 s tick now runs `DayIdleRules.ShouldAutoClose/ShouldOffer` (`:859-911`) and raises the one-time offer (`day.end-offer.raised` → `EndOfDutyOfferStore.Ask`); `TryEndDay` speaks the debrief with tag `day.debrief` (exempt from the hold) and raises `DutyDaySignals.DayEnded` last. `_coldAndDarkSinceUtc` tracked from the phase edge. Tests: `Day/CompanyDayServiceTests.cs`, `Day/DayIdleRulesTests.cs`. Risk **L**: `TimerTick` (`:840-850`) has no overlap guard; `ProcessTick` holds `_gate` and `TryEndDay` does file + speech + signal fan-out outside it — a 60 s cadence makes overlap practically impossible, but it is the one loop in this batch without the `Interlocked` pattern the Threading-model section prescribes.
- **`CabinCrewService`**: "Cabin's empty, captain." once per leg on `ArrivalCompleted` (Interlocked latch re-armed by `LegBegan` or a take-off), Low priority with 3×30 s retries; `AnnounceAsync` gains a priority parameter. **`FmgcCrossCheckService`**: auto trigger armed per leg (`ArrivalCompleted` disarms, `LegBegan` re-arms, #221).
- **`FoRadiosService`**: hand-off = read-back → `RadioClearGate.WaitForClearAsync` → ≥3 s grace → retune → `WaitForCom1Async` (500 ms poll ≤ 60 s on the cached `IRadioTuner.ActiveKhz`, not a `ReadDataRef`) → skip the check-in if ATC already spoke on the new frequency; all inside the fire-and-forget `RespondAsync` (`:338`), so the monitor's poll is never blocked. Departure-runway geometry looked up once per (airport, runway) in a `Task.Run` guarded by `Interlocked _geometryLookupBusy`. Every dropped line is a `sayintentions.readback` with `kind: "dropped"`; `RadioClearGate.MaxWait` 8 → 20 s. No test constructs the service (composer tests only). Risk **L**: two ATC lines inside one 60 s COM1 wait run two concurrent `RespondAsync`s; the radio gate serialises transmits but `_lastAtcLineUtc` is shared.
- **`MicOwnership`**: `ListenAsync` is now a 250 ms-sliced loop that only counts time while nothing is playing (`SpeechStatusStore.NowPlaying is null`), hard cap timeout + 60 s; exposes `IsListening`; `finally` resets listening/grammar/window (`:141-147`). Tests: `Speech/MicOwnershipTests.cs`.
- **`SpeechArbiterService`**: `DialogueHoldRule` added to the rule list; the roster FO's voice rides the role-voice slot (`FirstOfficerVoiceOrNull`, try/caught); `Phraseology.Normalize` replaces `AviationSpeech.Normalize` (also in `TtsPrewarmService`); `spoken` payload on the Spoken event.
- **`FailureMonitor`**: `AbnormalBurstGuard.HoldUntil` re-judged each 2 Hz tick on the ground with engines off (`failure.burst-held` once); `AbnormalEnded` event raised outside the lock; `DialogueActive`, `HasAbnormal`; a throwing trigger condition logged once per definition (#228) instead of swallowed.
- **`FlightStateEngine`**: `BoardingCompleted` also counts as boarding started (restart after boarding → Departure); `PauseGrace` uses the parked grace when paused on the ground (#206). **`FlightStateSnapshotService`**: per-part handler dictionary (unsubscribable), boundary-only documents while writes are suspended (`_boundaryDirty`), `DayEnded` drops the file entirely, a NewPlan reset leaves the file alone, `SaveOrClear` deletes an empty document. Tests: `Flight/FlightStateSnapshotTests.cs`, `Flight/FlightStateEngineTests.cs`.
- **`GateMonitorService`**: one more dataref subscription (`PaxSeatOccupationString`) feeding the existing `OnEvidenceChanged` (fires per seat change during boarding — `SeatMap.Parse` per change, cheap); `gate.std-aligned` once per (OFP STD, aligned) pair. **`PushbackSuggestionService`**: `RunLoopAsync` survives a faulted iteration with a 5 s back-off (#231; test `Airports/PushbackSuggestionLoopTests.cs`). **`SimLocalTime`**: three more SimVar subscriptions (local year, day-of-year, zulu seconds), all disposed. **`ProsimFlightDataSource`**: two more dataref subscriptions (captain/FO indicated altitude) → `SpokenAltitudeFt` consumed by callouts, cabin, purser PA, FO radios, transition reminders. **`UtteranceRouter`**: skips features not `AvailableWhileFoAway` while the FO is away and answers with the one away line (fire-and-forget enqueue). **`SpokenChecklistEngine`**: "checklist cancelled" line fire-and-forget. **`TechLogDialogueService.RaisePrefilledAsync`**: same one-dialogue lock as the voice raise.

---

## 5. Risk register (sorted)

| # | Rating | Where | Finding |
|---|---|---|---|
| 1 | **S** (by reading) | `ScenarioService.ProcessTick` :376-392 + `ArmPlanAsync` :431-524 | No "arming in progress" flag: while an arm is pending or after it fails, every 1 s tick queues another `ArmPlanAsync` on `_writes`; a persistently refused failure (name not live, SDK without the API, verify timeout) produces an unbounded queue and an `arming`/`armFailed` pair every ~3 s until the phase changes or the cycle resets. |
| 2 | **S** (by reading) | `ScenarioService.StopAsync` :241-253 vs `Dispose` :255-267, `App/ServiceComposition.cs:69,83` | Reverse-order shutdown disposes the module (`_writes.Dispose()`) before `StopAsync` runs; the documented "cleared at app exit" path throws `ObjectDisposedException` when a failure is armed/fired. The next-start `LeftArmed` clear is the real safety net. Fix: make the exit clear part of `Dispose`-safe state (don't dispose `_writes`, or clear in `Dispose` before cancelling). |
| 3 | **M** (unverified vs GSX) | `GsxJetwayStairsService.OnMirrorUpdated` (diff) | Latch released on any mirror state other than Active/Completed; a transient intermediate state during the retraction would re-open the window for a second toggle, leaving only the slot's spacing as protection. Verify the mirror's state sequence on a live retraction before trusting the latch alone. |
| 4 | **M** (judgement) | `CruiseSmallTalkService.ListenOnceAsync` :479-496 + `DialogueHoldRule` | A Layover small-talk line holds the mic (`IsListening`) for 10 s, deferring every non-exempt Normal/Low prompt app-wide. Consider exempting small talk's borrow from the hold or shortening the window. |
| 5 | **L** | `WalkaroundService.Tick` :198, `ScenarioService.Tick` :358 | Tick exceptions logged at Debug (TransitionReminder/SmallTalk use Error). A tick that throws every second is invisible at Information. |
| 6 | **L** | `CompanyDayService.TimerTick` :840-850 | Only loop in the batch without an `Interlocked` overlap guard (60 s cadence; `lock(_gate)` inside). |
| 7 | **L** | `GroundOpsSignals.LastFlightCycleResetKind` (diff) | Set-then-raise with no lock; an Arrival reset (GSX thread) and a NewPlan reset (OFP thread) overlapping would let a handler read the wrong kind. Both resets are seconds apart in practice. |
| 8 | **L** | `CompanionTrackingLvars` :130-152 vs :213-219 | Flush and a concurrent direct write are two unordered `Task.Run`s. |
| 9 | **L** | `LegBoundaryCore.Airborne` :176-188 | State None with flags set is cleared without a publish. |
| 10 | **L** | `SdkFailureInjector.ArmedAsync` :101 | Handle ↔ SDK object match by reference; unverified that `getArmedFailures()` returns the created instances. Runtime-unverified on this branch (no SDK in tests, not flown). |
| 11 | **L** | `DutyDaySignals.RaiseDayEnded` | `Action` multicast: the first throwing subscriber stops delivery to the rest (raiser catches once). Subscribers are all try/caught internally today. |

Write-safety summary: the only new aircraft/sim writes on the branch are (a) the companion tracking LVARs through `CompanionTrackingLvars` → `ISimVars` (same names as before, `SimWriteGate` unchanged), (b) `OhCallsMech` momentary press from the walkaround (already on `ProsimWriteGate.cs:44`), and (c) ProSim **failure** arming through `ProsimFailureGate` (catalogue-verified ∩ live list, no dataref path). No new dataref writes, no cockpit switch actuation. `ReadDataRef` polling: none introduced — every new read is a cached subscription (`FmsPlanWatcher`, `GateMonitorService`, `SimLocalTime`, `ProsimFlightDataSource`, `TransitionReminderService`) or a store snapshot.

## 6. Test coverage of the new runtime shells

| Service | Pure core tested | Shell (timer/events) tested |
|---|---|---|
| LegBoundaryService | LegBoundaryCoreTests, FlightReplayTurnaroundTests | LegBoundaryRestoreTests (with real FmsPlanWatcher) |
| TransitionReminderService | TransitionReminderCoreTests | **no** |
| WalkaroundService | WalkaroundTests | WalkaroundTests (ProcessTick-driven) |
| CruiseSmallTalkService | SmallTalkCoreTests | **no** (service named, not constructed) |
| ScenarioService | ScenarioTests (scheduler, gate) | ScenarioTests (fake injector) — the re-arm loop (#1) and exit order (#2) are not covered |
| FoRosterVoiceFeature / FoRosterService | FoRosterTests | FoRosterVoiceFeatureTests |
| FmsPlanReminderService | FmsPlanReminderTests | FmsPlanReminderTests |
| CompanyDayService | DayIdleRulesTests | CompanyDayServiceTests |
| CompanionTrackingLvars | — | CompanionTrackingLvarsTests |
| GsxJetwayStairsService latch | — | GsxJetwayStairsRetractionTests, GsxTriggerSlotTests |
| GsxAutomationService new-plan / end-of-duty / seeded restore | NewPlanCycleResetTests, ResyncCycleRestoreTests | **no** |
| NotificationSignalSource settle/hold | — | NotificationTests |
| FoRadiosService hand-off wait | FoRadiosComposerTests | **no** |
| SdkFailureInjector | ProsimFailureGate (ScenarioTests) | **no** (needs SDK) |
