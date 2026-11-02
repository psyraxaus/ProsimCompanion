# Audit: src/ProsimCompanion.Gsx (read-only, 2026-11-03)

Scope: 89 files / ~20 k lines on branch `feature/settings-hub-usability` (HEAD e5d86287).
Every file in Automation/, Gate/, Menu/, Mirror/, Protocol/, Services/, Sync/, Diagnostics/,
Transport/ and the root was read in full; Profiles/ (PyLiteral, Ini/Py parsers, locator,
parking source) was read in part. Cross-references were confirmed with ripgrep across `src/`
and `tests/`. Nothing was edited, built or committed.

---

## A. Pathway maps

### A1. Departure cycle: ground prep → departure complete

Two cooperating hosts own the cycle; they only talk through `Core.State.DepartureCycleState`
(`Started`, `Complete`, `PrepComplete`, `IsTurnaround`) and the shared
`Automation.IGsxTriggerSlot`.

**Stage 0 — Readiness and assessment (gates both hosts)**

| Gate | Decided by | Evidence |
|---|---|---|
| GSX `Ready` | `GsxRemoteApiClient.UpdateReadiness` | hello `gsxRunning` + capabilities `gate`+`handlerData` + protocol 1 |
| MSFS session | `Core SimSessionStore` (consumed by `PrepStageMachine.Next`, `GsxStartupResyncService.TryAssess`, `GsxServiceControl`) | camera/pause events, never the socket |
| Startup resync assessed | `GsxStartupResyncService.TryAssess` → `GsxResyncState.MarkAssessed` | companion `L:PROSIMCOMPANION_*` LVARs + FOB/seat-string/EFB datarefs; 90 s `SessionWindow` timeout |

Both `DepartureAutomationCore.Evaluate` (line 109) and `PrepStageMachine.Next` (line 95) hold
until `ResyncAssessed`; the assessment self-times-out so neither can deadlock.

**Stage 1 — Ground preparation (`Sync.GsxGroundPrepCoordinator`, 5 s timer)**

Pure decision: `PrepStageMachine.Next` → `PrepCommand` (None/Hold/Reset/RunStage/AdvanceFromSettling).
Stage order (`GsxPrepStage`): `Idle → Reposition → Settling(12 s) → AnchorGate → GroundEquipment → JetwayStairs → Complete`;
a turnaround jumps `Idle → GroundEquipment` (coordinator line 250).

| Stage | Service | GSX evidence / action | Write path |
|---|---|---|---|
| Hold: unknown parking | `GsxParkingWakeService` (+ pure `ParkingWakePlan`) | `Mirror.GateContextKey == null` → `GsxMenuOpener.OpenAsync` (menu.open, then LVAR `L:FSDT_GSX_MENU_OPEN=1`); `menu.close` on root menu once `/parking` arrives | Remote API + SimWriteGate |
| Reposition | `GsxRepositionService.RunStepAsync` | intent chain "Activate Services at" → `^reposition aircraft` → positional pick index 0 on "Select Position at" (20 s verify) | `GsxMenuIntentExecutor` (menu.pick) |
| AnchorGate | `GsxGateAnchorService.RunStepAsync` | `gate.select` ladder from `GsxGateResolver.AnchorTokenLadder`; **skipped when parked** (`ParkedAtTheStand`) — so in practice it always answers Done without sending (gsx-remote-api.md §6 says this step is kept only for the owner's workflow) | own `gate.select` payload |
| GroundEquipment | `GsxGroundEquipmentService.RunPlacementStepAsync` | writes ProSim chocks / GPU / PCA | `GsxProsimWriter` → gateway → `ProsimWriteGate` |
| JetwayStairs | `GsxJetwayStairsService.RunStep` | picks OperateJetways unless `L:FSDT_GSX_JETWAY == 2`, else OperateStairs; confirms via slot watcher (20 s window), cross-service fallback once | `IGsxTriggerSlot` (watched) |
| Complete | coordinator `Advance(Complete)` → `DepartureCycleState.MarkPrepComplete()` | | `GsxStartupResyncService.Tick` latches `..._PREP_DONE=1` |

Voice gate: `PrepStageMachine` holds the whole chain while `GroundPrepActivation` is voice-gated
and `CycleStarted == false` (ADR-0006).

**Stage 2 — Departure pump (`Automation.GsxAutomationService`, 3 s timer + edge triggers)**

`Pump()` = `LockedPumpRunner.TryRun(PlanPump, applier)`:
`DeparturePumpGatherer.Read` → `DepartureAutomationCore.Evaluate` (pure) → `DeparturePumpEffectPlanner.Plan` (pure, ordered effects) → `DeparturePumpEffectApplier.Apply` (one collaborator call per effect; `MarkDeiceStepNextEffect` deferred past the lock).

Order of gates inside `Evaluate` (owner-specified, table-tested in `DepartureAutomationCoreTests`):
1. `AutomationEnabled && GsxReady`
2. `ResyncAssessed`
3. Start decision: auto-start (Preparation phase, not voice mode) / `voiceThenAuto` turnaround settle (`TurnaroundStartInputs`) / ask-first question / explicit `Start(source)` from web, voice, hail (`IGsxDepartureStarter.StartForServiceCallAsync`)
4. `CycleStarted && !CycleComplete && Phase in {Preparation, SessionStart}`
5. `PrepComplete` (from Stage 1)
6. Flight-plan gate (`GsxFlightPlanMonitor.FlightPlanAvailable`: FMS plan OR dataref-imported-AND-OfpStore-holds-one) — never bypassed by force
7. `DepartureSequencer.Next(...)`: cursor walk over `DeiceRequestPolicy.EffectiveSteps(options.DepartureServices)`; activation predicates `Manual/Voice/AfterCalled/AfterRequested/AfterActive/AfterPrevCompleted/AfterAllCompleted`; constraints `FirstLeg/TurnAround/CompanyHub/NonCompanyHub`; `MinimumFlightMinutes`; mirror `NotAvailable/Bypassed` → skip; `preSkip` (tankering via `RefuelCore.TankeringSkipReason`, declined de-ice) and `preHold` (fuel confirmation, FO walkaround, open de-ice question) injected by the gatherer; `awaitingConfirmation = slot.InFlightServiceId` holds the cursor.

Per-service evidence that advances the cursor comes from one place: `Services.GsxServiceLifecycleTracker.Process(mirror.Services)` (driven by `GsxBootstrapService` on every `/services` patch + a 5 s reconcile tick). Edges Requested/Active/Completed latch once per cycle; "return-to-available after active/called" = Completed. The pump re-runs on `ServiceEvent`, `Mirror.Updated("services")`, `slot.Changed`, `cycle.Changed`, fuel-confirmation / de-ice store observers, `SimbriefImported`, OFP change, phase change.

**The trigger slot** sits between the planner's `DispatchTriggerEffect` and the wire:
`GsxTriggerSlot.TryDispatchAsync(GsxTriggerRequest)` → the ONLY `service.trigger` payload builder
(`SendTriggerAsync`, enforced by analyzer PCGSX001 and `TriggerPathGuardTests`). It owns
exactly one in-flight trigger; confirmation = mirror state Requested/Active/Completed OR a
lifecycle edge, polled every 500 ms up to `TriggerConfirmTimeoutMs` (10 s); on confirm it calls
`lifecycle.MarkCalled`. Senders and their policies:

| Sender | Source string | RetryOnce | NoConfirm | SlotWait | ConfirmWindow |
|---|---|---|---|---|---|
| Sequencer (`DeparturePumpEffectApplier`) | `sequencer — …` | no (re-offers next pump) | no | 0 | default |
| `GsxServiceControl.DispatchAsync` (voice/web/API/hail) | `command` | yes | no | 3 s | default |
| `GsxArrivalService.TriggerDeboardingAsync` | `arrival` | yes | no | 3 s | default |
| `PushbackTickEffectApplier.CallPushbackAsync` | `pushback sequence` | yes | no | 3 s | default |
| `GsxJetwayStairsService.DispatchConnectAsync` | `jetway/stairs connect` | no (cross-service fallback) | no | 10 s | 20 s |
| `GsxJetwayStairsService.DispatchRemovalAsync` | `jetway/stairs removal` | no | **yes** (2 s spacing) | 10 s | — |

**Stage 3 — Completion.** `DepartureSequencer` reports `AllDone` when every step settled and the
plan gate is open → planner emits `MarkCycleCompleteEffect` + `gsx-departure-complete` event +
`RaiseDepartureServicesCompletedEffect` (`GroundOpsSignals` → "ready for pushback"). The
beacon-orchestrated push (`Sync.GsxPushbackSequenceService`, 1 s tick → `PushbackTickGatherer` →
`PushbackSequencer`/`PushbackTickCore` → `PushbackTickEffectPlanner` → `PushbackTickEffectApplier`)
arms on `IGsxAutomationPhaseView.DepartureComplete`; its only GSX trigger is `Departure`
(pushback) through the slot; its menu pick is "Confirm good engine start" when
`VEHICLE_PUSHBACK_STATE == 12`.

**Reset points** (all end the cycle): `OnFlightPhaseChanged(Arrival)` (lifecycle `ResetCycle()`,
`BeginTurnaroundCycle`, pax armer reset, latches `ResetForArrival`, `slot.Reset`,
`RaiseFlightCycleReset()`); `OnOfpChanged` via pure `NewPlanCycleReset.ShouldReset` (departure
side only, `FlightCycleResetKind.NewPlan`); `OnDutyDayEnded` (same as new-plan plus `Withdraw`).

**Places where two services can send the same trigger**

1. **Refueling**: sequencer (fuel-confirmation hold released) and `GsxServiceControl.ConfirmFuelAsync → RequestAsync`. Mitigated: the slot refuses a *watched* duplicate of the in-flight id immediately (`GsxTriggerSlot` lines 103-117, issue #157) and `ConfirmFuelAsync` records before `Confirm()` pumps. Safe.
2. **Deboarding**: `GsxArrivalService` (auto) and `GsxServiceControl.RequestDeboarding` (voice). Same duplicate guard. Safe.
3. **Departure (pushback)**: `PushbackTickEffectPlanner` can emit *two* `CallPushbackEffect`s in one tick (tug-attached hook `PlanHooks` + sequencer `ReadyForPush`). Both go through the slot with RetryOnce; the second answers Busy and `ClearTugPushbackCalled()` un-latches — a benign log line, not a double send. Acceptable, but the planner could suppress the second itself.
4. **OperateJetways/OperateStairs CONNECT**: `GsxJetwayStairsService.RunStep` (prep) / `RunDepartureStep` (pushback hook `CallJetwayStairsDuringDeparture`) / `RunArrivalStep` share one once-per-gate core — fine. `GsxServiceControl.RequestJetway` yields only while `GroundPrepOwnsJetwayStairs()` (prep window); during the departure hook or the arrival step a voice "connect jetway" can race it. The slot's duplicate guard covers the same-id case while the first is in flight; after the first confirms, the mirror reads Active and `RequestAsync` answers AlreadySatisfied. Low risk.
5. **OperateJetways/OperateStairs RETRACT (real risk)**: retraction is a *toggle* sent `NoConfirm`, and the slot's duplicate guard is explicitly disabled for NoConfirm sends (`duplicate = occupied.Watched && !request.NoConfirm …`). Four independent senders exist: beacon sequence `RetractJetwayStairsEffect`, `PushbackTickCore` on-final hook `RemoveJetwayStairsOnFinal` (only when the beacon sequence is off), `RemoveStairsAfterDepartureEffect` (fires on `DepartureComplete`, independent of the beacon sequence), and `GsxServiceControl.RetractJetway/RetractStairs` (voice). Each checks the mirror reads Active/Completed before sending, but within the 2 s spacing + mirror lag a second toggle re-connects what the first retracted. Example: `DepartureComplete` fires `RemoveStairsAfterDeparture` (mode `onlyJetway`, default) at the same moment the pilot says "retract stairs". Recommend: the slot should treat a NoConfirm send for the same id within `NoConfirmSpacing` as Busy, or `GsxJetwayStairsService` should hold a per-service "retraction sent at" latch.
6. **gate.select**: `GsxGateAnchorService` (prep, departure stand) and `GsxGateSelectionService` (arrival) build separate payloads. Different phases, anchor self-latched; no double send, but two payload builders (see E).

### A2. Gate-select path (`Gate.GsxGateSelectionService` + pure `GateDispatchPlanner` / `GsxGateResolver` / `GsxPositionMenuPlanner`)

Entry points: `IGsxGateControl.RequestGate(gate)` from `Core.Gate.ArrivalGateCoordinator`
(OFP/ATC/web/voice), `GsxAutomationService.ArmConfiguredArrivalGate` at `Flight` phase when
`gsx.arrivalGate` is set (only if Status is Idle/Failed), and `OnSidChanged` re-arm.

```
RequestGate ──► Status=Armed, reset counters ──► TryDispatchAsync()
   re-entered on: ReadinessChanged, Mirror.Updated(airport|handlerData), FmsDestination change,
                  PhaseChanged, SidChanged; menu updates → TryAnswerPositionMenuAsync
TryDispatchAsync (under _gate):
   skip if no request | dispatching | pendingVariants | Status in {Assigned,Confirmed,AssignedUnconfirmed}
   skip if Status==Failed && same gate && <10 s since fail            (FailedRetryBackoff)
   step = GateDispatchPlanner.Decide(ready, loadedIcao, destination, phase, data, now, pick stamps,
                                     walk stamps, noAirport stamp, sendsAfterPick)
     Wait          → stay armed
     TooLate       → (parked: ground, ≤1 kt, engines off, destination loaded) event gsx-gate-too-late, Fail(...)   [once]
     PickAirport   → PickAirportInFlightAsync(destination)  (cruise/descent/approach, loaded≠dest,
                      ≤3 walks/flight, 10 min walk backoff, 2 min pick backoff)
                      ─ already on "Select Position at <dest>" → treated as success
                      ─ executor: "" root → "^select airport" → page "Select airport"
                      ─ walk ≤ gsx.airportPickMenuPages pages (GsxMenuPaging.TurnPageAsync),
                        menu.search "<ICAO>" once if a Search row exists,
                        row regex \bICAO\b → verify loaded==dest OR Select-Position-at-dest page (20 s)
                      ─ failure → close menu if we opened it, stay armed
     Send          → skip if <60 s since a no_airport answer (NoAirportBackoff), else fall through
   Status=Dispatching → DispatchLadderAsync(requested)
DispatchLadderAsync:
   resolved = ResolveFromCatalogueAsync(requested)   (Core IAirportParkingCatalog: GSX .ini/.py profile + facility data)
   result   = resolved? SendResolvedLadderAsync (number int → GSX gate name → full name → default name;
                                                  ambiguous → candidate whose bgl/ui name matches; stop early on SuffixVariants)
                      : SendSelectAsync(typed token)
   ambiguous && SuffixVariants(candidates, requested) != null
        → ChooseSuffixVariantAsync: pilot suffix → lone jetway/Safedock (catalogue) → ask pilot (GateChoiceStore;
          Status=Armed, pendingVariants set; answer or TaxiIn default resumes via DispatchChosenVariantAsync)
          → else first variant; resend bglName; HandleFinalResult; return
   resolved && not_found|ambiguous → one plain send of the typed token
   !ok && !retriedOnce (ONE auto-retry total):
        not_found        → NumberFallback(mirror.Parkings) → resend integer
        ambiguous        → PickUniqueCandidate → resend bglName|uiName|gate, else Fail("ambiguous — no unique candidate")
        services_active  → resend revokeServices:true
        assigned_to_other→ resend force:true
   HandleFinalResult(requested, result); close menu if we opened it
HandleFinalResult:
   ok | prepared | already_selected | already_parked → _assigned = AssignedFrom(payload) (GSX's own uiName/number),
        event gsx-gate-assigned, Status=Assigned, StartConfirmationWindow(60 s), CheckReadback()
   no_airport     → NOT a failure: stamp _lastNoAirportAtUtc, ++_sendsAfterPick, Status=Armed
                    (planner re-sends every 60 s up to 5 times while airborne after a real walk;
                     an /airport patch naming the destination clears the stamp and re-dispatches)
   gsx_not_running | not_connected → Status=Armed (state change re-dispatches)
   not_found      → log mirror parkings count + NearestNames + SampleNames, event gsx-gate-not-found, Fail(...)
   other          → Fail("gate.select failed (code)")
Confirmation: SetGate_Name/Number/Suffix LVAR readback (GsxGateResolver.FormatReadback) == Normalize(requested)
   → Status=Confirmed (event gsx-gate-confirmed); window expiry → AssignedUnconfirmed (never a failure)
Side doors:
   TryAnswerPositionMenuAsync (gsx.answerPositionMenuWithArrivalGate, ≤2 attempts, never while executor IsDriving):
        GsxPositionMenuPlanner.Pick(entries, requested) → facility group row → positions page row → Status=Assigned
   TryAdoptFacility(facility) ← GsxQuestionCatalog "Change parking or service": same number, other suffix → adopt
   Fail(): stamps _lastFailedGate/_lastFailedAtTicks; event gsx-gate-failed
```

Refusal/not_found branches, summarised: (a) **TooLate** never sends; (b) **no_airport** keeps the
request armed (airport pending); (c) **not_found** after the ladder → Failed with diagnostics,
auto-retry at most once, re-armed only by a new `RequestGate` or a sid change; (d) **ambiguous**
without a unique candidate → Failed; with suffix variants → pilot question (never a guess).

### A3. Startup-resync path (`Sync.GsxStartupResyncService` + pure `GsxStartupResync`)

Forward direction (always on):
- `lifecycle.ServiceEvent(Completed)` for non-toggles → `L:PROSIMCOMPANION_SVC_DONE_<ID> = 1` (queued in `_pendingLvarWrites` when MSFS is down; flushed on the SimConnect connected edge).
- `GroundOpsSignals.FlightCycleReset` (arrival kind only; `NewPlan` kind is ignored) → `TURNAROUND=1`, `PREP_DONE=0`, loadsheet LVARs 0, all SVC_DONE 0; `GsxResyncState.MarkTurnaround()`.
- Post-assessment tick: `PrepComplete` → `PREP_DONE=1` once (re-armed on `SessionEnded`).

Startup direction (once per process; 5 s tick):
```
Tick → !IsAssessed → TryAssess():
  wait for SimSession.DataIsMeaningful (walkaround counts)          [logs once]
  worldKnown = Ready && mirror.Services.Count > 0
     !worldKnown && !timedOut(90 s SessionWindow) → return
     !worldKnown && timedOut → MarkAssessed(turnaround from LVAR, loadsheet LVARs) "no prior progress assumed"
  prosimKnown = any of FuelTotal/FmsOrigin/EfbSimbriefPlanImported has pushed; wait unless timed out
  doneLvars = SVC_DONE_* ≥ 1;  if LVARs claim progress and plan refs have not pushed → wait (unless timed out)
  evidence = ResyncEvidence(TurnaroundLvar, PrepDoneLvar, doneLvars, fuelTarget(kg → raw), FOB?, PaxBooked,
             PaxOccupied?, efbBoardingStatus, prelimEdition, finalSent, configured one-shots,
             FlightPlanLoaded? (null until plan refs pushed), ProsimDataAvailable)
  verdict  = GsxStartupResync.Assess(evidence):
     stale = prosim data available && (departure claimed) && (plan definitely absent || refuel contradicted by FOB
             || boarding contradicted by empty cabin)  → ignore all LVARs, clear them, fresh flow
     proofs: trusted LVARs; FOB ≥ target−100 kg → Refueling; occupied ≥ booked, or EFB status completed/ended
             with a non-empty cabin → Boarding; prelim EDNO > 0 → Refueling
     never-re-call: only once BOARDING is proven, every other non-toggle configured one-shot is assumed done
             (Deboarding/GPU exempt from counting as progress)
  effects: lifecycle.SeedCompleted(id) (event-silent) + backfill SVC_DONE LVAR; groundPrep.SeedComplete when
           PREP_DONE; ILegBoundaryRecovery.AdoptDeboardedFromEvidence when BetweenLegs; MarkAssessed(...)
Consumers of IsAssessed/Assessed: DepartureAutomationCore (hold), PrepStageMachine (hold),
  GsxAutomationService.OnResyncAssessed (adopt turnaround, RestoreCompletedCycleFromSeeds → cycle complete),
  AircraftStateCheckService (fresh-start vs turnaround), LoadsheetService (Core, restore prelim/final).
```

---

## B. Redundancy

| # | Duplicate | Evidence | Keep |
|---|---|---|---|
| B1 | "Parked at the stand" predicate, 6 variants with 3 different speed thresholds | `NewPlanCycleReset.cs:50` (`GroundSpeedKt >= 1`), `GateDispatchPlanner.cs:143-146` (`ParkingConflictGate.StoppedGroundSpeedKt`), `GsxGateAnchorService.cs:57` (`< 1`), `GsxRepositionService.cs:79-82` (`> 1`), `GsxSimbriefReloadService.cs:889` (`> 1`), `ArrivalCore.cs:549` (`< 2.0` + brake + beacon, legitimately stricter) | `Core.Flight.ParkingConflictGate.IsParkedEnginesOff` (already used by `GsxQuestionCatalog:369`, `GsxParkingWakeService:187`); replace the four local copies |
| B2 | `IsValidIcao` | `GsxStartupResyncService.cs:613` private copy vs `Core.Aircraft.FmsPlanRules.IsValidIcao` (and the pass-through `GsxFlightPlanMonitor.IsValidIcao:112`) | `FmsPlanRules.IsValidIcao`; delete the private copy, inline the wrapper |
| B3 | `WaitForAsync` 100 ms poll loop | `GsxMenuIntentExecutor.cs:359`, `GsxMenuOpener.cs:549`, `GsxMenuControl.cs:247` (**unreferenced**), `GsxParkingWakeService.cs:424`, plus `GsxGateSelectionService.WaitForRowAsync:505` | one `internal static class MirrorWait { Task<bool> ForAsync(Func<bool>, TimeSpan, CancellationToken) }` in Menu/ |
| B4 | `JetwayLvarNotPresent = 2` | `GsxServiceControl.cs:37`, `GsxDoorService.cs:29`, `GsxJetwayStairsService.cs:48`, `GsxGroundEquipmentService.cs:62` (`JetwayNotAvailable`) | one `GsxLvarNames.JetwayNotPresent` beside the `Jetway` descriptor (whose fallback is already 2.0) |
| B5 | Menu title literals | "Select Position" x4 (`GsxQuestionCatalog:174,359,412`, `GsxRepositionService:98,104`, `GsxGateSelectionService.PositionSelectTitle:74`), "Activate Services" x3 (`GsxParkingWakeService:266`, `GsxRepositionService:95`, `GsxSimbriefReloadService.GateMenuTitle:38`), "Change Facility" x2 (`GsxQuestionCatalog:289`, `GsxGroundPrepCoordinator:435`), `PushbackDirectionTitle` const defined at `GsxQuestionCatalog:35` but the literal is repeated at `:158` | one `GsxMenuTitles` static class in Menu/ |
| B6 | Private `RecordDecision` helpers | 16 copies (`GsxDeiceRequestService:326`, `GsxTriggerSlot:326`, `GsxQuestionCatalog:786`, `GsxServiceControl:312`, `GsxArrivalService:454`, `GsxBoardingSync:472`, `GsxDoorService:480`, `GsxGateAnchorService:158`, `GsxGroundEquipmentService:398`, `GsxGroundPrepCoordinator:485`, `GsxJetwayStairsService:355`, `GsxRefuelSync:257`, `GsxRepositionService:127`, `GsxStartupResyncService:683`, `ProsimNativeGsxGuard:151`, `PushbackTickEffectApplier:193`) in three shapes (log+diag, log+diag+`gsx-decision` event, fixed-action) while `IGsxDecisionLog` already exists for the automation | a static `GsxDecisions.Record(ILogger, GsxDiagnosticsStore, action, reason, JsonlEventLog? events = null)` that keeps the caller's `ILogger<T>` category and message template (probes match on text), or widen `IGsxDecisionLog` with a category |
| B7 | "Close the menu we opened" | `GsxGateSelectionService.CloseIfWeOpenedAsync:546`, `GsxSimbriefReloadService.cs:715-719`, `GsxParkingWakeService.CloseRootMenuAsync:394` | one helper on `GsxMenuOpener` (`CloseIfOpenedByUsAsync(bool menuWasShown)`) |
| B8 | no_airport pacing in two classes | `GsxGateSelectionService.NoAirportBackoff` (60 s, line 60, applied in `TryDispatchAsync` to the `Send` branch) and `GateDispatchPlanner.NoAirportResendBackoff` (60 s, applied only to the post-walk branch) — the two constants agree by coincidence | fold the service's check into `GateDispatchPlanner.Decide` (it already receives `LastNoAirportUtc`) and delete the service constant |
| B9 | Parallel service-state models | `Protocol.GsxServiceState` (mirror semantic), `GsxServiceLifecycleTracker.ServiceCycleSnapshot` (4 bools) and `Automation.DepartureCycleView` (4 bools, derived in `DeparturePumpGatherer.Read:355-363`), `Core.GsxServiceStage` (display latch), `Core.GsxServiceView.MappedState` (string copy of `GsxServiceState`) | `GsxServiceState` and `GsxServiceStage` are justified (wire vs display; Core cannot see Gsx.Protocol). `DepartureCycleView` is a pure projection of `ServiceCycleSnapshot`: replace it with two computed properties on the snapshot (`ReachedRequested`, `ReachedActive`) and delete the record + the lambda |
| B10 | Tracking-LVAR bulk writes, three writers | `GsxStartupResyncService.OnFlightCycleReset:404-414` (TURNAROUND=1, rest 0), `GsxStartupResyncService.ClearTrackingLvars:595-609` (all 0), `GsxAutomationService.ClearDepartureTracking:285-309` (departure one-shots + loadsheet editions 0, bypassing the resync's pending-write queue) | one `CompanionTrackingLvars` writer owning the queue with `ResetForArrival()`, `ClearAll()`, `ClearDepartureSide()` |
| B11 | Timeouts with the same meaning | `AssessmentTimeout` 90 s (`GsxStartupResyncService:225`) and `SettleTimeout` 90 s (`AircraftStateCheckService:152`, comment says "mirrors the resync's window"); `TickInterval` 1 s x4, 5 s x3; `PollInterval` 100 ms x4; `VerifyTimeout = 20 s` hard-coded in `GsxRepositionService:99,107` and `GsxGateSelectionService.AirportLoadTimeout:68` while `gsx.intentVerifyTimeoutMs` exists | a `GsxTimings` static class for the shared ones; leave per-module ticks alone |
| B12 | Deice service-id tolerance | `GroundOpsSignalRelayCore.Map:1123`, `GsxDeiceRequestService.OnServiceEvent:155`, `GsxDeiceActivityRelay.IsDeiceService:774` — three copies of `serviceId.Replace("-", "").Contains("deic")` | `GsxDeiceActivityRelay.IsDeiceService` (already public static) or move it to `Core.GsxServiceIds.IsDeice` |
| B13 | `SeatMap.Parse(...).Count(seat => seat)` | `GsxPaxTargetArmer:48`, `GsxArrivalService:252`, `GsxStartupResyncService:522,525`, `GsxBoardingSync:224,259` | a `SeatMap.CountOccupied(string?)` helper in Core |

GSX menu text parsing is **not** duplicated across intents: every pick goes through
`GsxMenuIntentExecutor.Resolve` (regex / positional). The paging walk is shared via
`GsxMenuPaging`. LVAR polling: there is no polling loop — every LVAR is a typed subscription
read from cache; the only loops are timers reading cached values.

---

## C. Dead code (confirmed by ripgrep over `src/` and `tests/`)

| Symbol | Where | Refs outside its file | Note |
|---|---|---|---|
| `GsxGateResolver.ResolveCanonical` (+ its doc) | `Gate/GsxGateResolver.cs:142-180` | src 0, tests 5 | superseded by the catalogue ladder (2026-10-04) |
| `GsxGateResolver.IsUnambiguousNearestMatch` + private `LeadingDesignator` | `:197-244` | src 0, tests 2 | the nearest-name substitution it guards no longer exists |
| `GsxGateResolver.ResolveAnchorToken` | `:365-366` | src 0, tests 6 | `AnchorTokenLadder` is the live one |
| `GsxGateResolver.TrimToken` | `:189` | src 0 outside the file (used internally), tests 2 | make private |
| `GsxMenuControl.WaitForAsync` | `Menu/GsxMenuControl.cs:247-260` | 0 | private, never called |
| `GsxMenuPaging.HasNextPage` (public) and `BackRow` | `Menu/GsxMenuPaging.cs:701-711` | `HasNextPage` only internal use; `BackRow` 0 | make `HasNextPage` private; delete `BackRow` |
| `GsxIniProfile.Empty` | `Profiles/GsxIniProfile.cs:850` | 0 | |
| `GsxDeiceStateMonitor.Label` public | `Sync/GsxDeiceStateMonitor.cs:677` | 0 outside | make private |
| `GroundOpsSignalRelayCore.HasRaised` | `Sync/GroundOpsSignalRelayCore.cs:1164` | src 0, tests 1 | test-only accessor |
| `GsxRemoteApiClient.Capabilities` property | `GsxRemoteApiClient.cs:84` | used by bootstrap only | fine, keep |
| `GsxGateSelectionService.PositionsPageTitle` | `:77` | file-internal | fine |

Options that are always-on / never read:
- `GsxOptions.Enabled` (line 13) — read only by `GsxRemoteApiClient.ExecuteAsync`; every automation module reads `AutomationEnabled`. Correct as designed (one is the connection, the other the automation), but the settings page should say so.
- `GsxOptions.VoiceControlEnabled` — 18 refs, all outside this project (Speech). Not a Gsx concern.
- `GsxOptions.ReconnectMaxIntervalMs`, `AircraftStateCheckAnnounce`, `RefuelFinishOnHose`, `AllowDefuel` — each read exactly once; live.
- `GsxLvarNames.DeicingState` — read once by `GsxDeiceStateMonitor` (diagnostics only, as documented). Live.
- No never-read option was found in `GsxOptions` from this project's side.

Estimated removable: ~130 lines of source + ~70 lines of tests that only exercise dead members
(`GsxGateResolverTests` cases for `ResolveCanonical`, `IsUnambiguousNearestMatch`, `ResolveAnchorToken`).

---

## D. Bloat: files over 500 lines

| File | Lines | Verdict | Natural seams |
|---|---|---|---|
| `Gate/GsxGateSelectionService.cs` | 1332 | **Split.** One class holds five concerns and 20 mutable fields under one lock. | (1) `AirportPickWalker` — `PickAirportInFlightAsync`, `WaitForRowAsync`, `RecordAirportPick`, `LogPickNotCompleted`, `AirportPickVerified` (~200 lines, already tested as `GsxGateSelectionAirportPickTests`/`GsxGateAirportPickPagingTests`); (2) `SuffixVariantChooser` — `ChooseSuffixVariantAsync`, `DescribeVariantsAsync`, `RecordSuffixChoice`, `OnGateChoiceChanged`, `DispatchChosenVariantAsync`, `DefaultPendingVariant`, `JoinOr` (~250 lines, `GsxGateSuffixVariantServiceTests`); (3) `PositionMenuAnswerer` — `TryAnswerPositionMenuAsync` (~100 lines, `GsxGatePositionMenuAnswerTests`); (4) `GateSelectLadder` — `DispatchLadderAsync`, `SendResolvedLadderAsync`, `ResolveFromCatalogueAsync`, `ParseCandidates`, `SendSelectAsync`, `AssignedFrom` (~300 lines); (5) the host: status, readback confirmation, event wiring, backoffs (~400 lines). The status machine (`GsxGateRequestStatus` transitions + stamps) would become a pure `GateRequestState` record the way `DepartureCycleLatches` did. |
| `Menu/GsxQuestionCatalog.cs` | 792 | **Split by concern, do not convert to data.** Five handlers are table-shaped (`AnswerIfAsync` rows: FollowMe, board crew, deboard crew, pushback confirm, revoke) but they key off typed, hot-reloaded options; a JSON table would need name-based option binding for five rows — negative payoff. The other handlers (tug, direction, ice warning, two fluid menus, legacy combined menu, parking conflict, Select Position) consult stores and are procedural. | `DeiceMenuAnswers` (`HandleIceWarningAsync`, `IceWarningAnswer`, fluid/concentration/legacy handlers, `FluidTypePattern`, `ConcentrationPattern`, `FluidTypeOf`, `ConcentrationOf` — ~230 lines, already tested as `DeiceMenuAnswerTests`); `ParkingQuestions` (`HandleParkingConflictAsync`, `HandleSelectPosition`, `PublishSelectPositionConflict`, `OnPhaseChanged`, `ExtractFacility` — ~170 lines, `ParkingChangeMenuAssignedGateTests`); `PushbackQuestions` (tug + direction + the choice-store observers in the ctor — ~130 lines). The catalog keeps `RegisterAll` and `AnswerIfAsync` (~200 lines). |
| `Automation/GsxAutomationService.cs` | 708 | **Mostly justified** (post-C1 shell: wiring + public surface), but ~250 lines are not pump wiring. | Extract `SmartButtonSubscription` (`SubscribeSmartButton`, `ReleaseSmartButton`, `DisposeSmartButton`, `OnSmartButtonChanged`, the two fields and lock — ~95 lines); move `ArmAutoSelectOperatorAsync` (~45 lines, `handler.set`, nothing to do with departure pumping) to `GsxBootstrapService` or a 60-line `GsxOperatorAutoSelect`; unify `OnDutyDayEnded` / `OnOfpChanged` / the Arrival branch of `OnFlightPhaseChanged` into one `ResetDepartureCycle(reason, kind)` (three copies of the same seven calls; ~40 lines saved). Result ≈ 480 lines. |
| `Sync/GsxStartupResyncService.cs` | 688 | **Split file, not class.** Lines 1-207 are the pure `ResyncEvidence`/`ResyncVerdict`/`GsxStartupResync` (tested as `GsxStartupResyncTests`); the service is 480 lines. | Move the pure part to `Sync/GsxStartupResync.cs`; move the tracking-LVAR writer (queue + flush + the three bulk writes, ~120 lines) to `CompanionTrackingLvars` (see B10). Service drops to ~350. |
| `Services/GsxServiceControl.cs` | 638 | **Justified as the one on-demand policy**, but the fuel sub-policy is a seam. | `RefuelCommandPolicy` — `ConfirmFuelAsync`, `RequestRefuelTopUpAsync`, `TankeringSkipReason`, `ConfirmCurrentFigure`, `EffectiveFigure`, `SourceLabel` (~140 lines, three dataref subscriptions move with it); `ServiceIdFor`/`DisplayNameFor` tables (~30 lines) could live on `GsxServiceAction` as extension methods in Core next to the enum. Result ≈ 450. |
| `Gate/GsxGateResolver.cs` | 637 | **Cohesive and pure; shrink by deleting dead members (C) → ~540.** | Optional: `GsxFacilityText` for `FacilityNamesGate`, `SuffixVariantIn`, `Words`, `IndexOfRun`, `StripFacilityPrefix`, `KnownFacilityPrefixes` (~160 lines, the "parse GSX's facility strings" half). |
| `Sync/GsxGroundPrepCoordinator.cs` | 515 | Justified: the shell around `PrepStageMachine`; 60 lines are the unknown-parking hold's diagnostics. | Leave. |

Catalog-as-data question: the only genuinely tabular data in this project are
`GsxDiagnosticsCollector.Services` (8 rows), `KnownIgnoredKeys` (15 strings) and
`GsxQuestionCatalog`'s five `AnswerIfAsync` rows. None justifies a resource file; the
`.ini`/`.py` profile parsers already read GSX's own data files.

---

## E. Write safety

**service.trigger**: one builder (`GsxTriggerSlot.SendTriggerAsync`), fenced by the in-repo
analyzer `PCGSX001` (`src/ProsimCompanion.Analyzers/TriggerSlotAnalyzer.cs`, referenced from the
Gsx csproj) and `TriggerPathGuardTests`. Six senders, all through `IGsxTriggerSlot` (table in A1).
Compliant.

**Other Remote API verbs** (all via `IGsxRemoteApi.SendCommandAsync`, which itself refuses
unless Ready):

| Verb | Senders |
|---|---|
| `gate.select` | `GsxGateSelectionService.SendSelectAsync` (1046) and `GsxGateAnchorService.RunStepAsync` (113) — **two payload builders**. Not unsafe (both are explicit allow-listed verbs), but the anchor should reuse a shared `GateSelectCommand` so the `revokeServices`/`force` flags have one definition. |
| `menu.pick` | `GsxMenuIntentExecutor` only (266). Compliant with locked decision 6 (text-resolved; positional only with TOCTOU text guard). |
| `menu.open` | `GsxMenuOpener` only (69/72, plus the LVAR rung). Compliant with #141. |
| `menu.close` | 6 sites (`GateSelection:550`, `MenuControl:205`, `QuestionCatalog:218/270/565`, `ParkingWake:271`, `SimbriefReload:279`). Harmless verb; consolidate per B7. |
| `menu.search` | `GsxGateSelectionService:467` (documented unverified). |
| `handler.set` | `GsxAutomationService.ArmAutoSelectOperatorAsync:654`. |
| `menu.toggle`, `state.get` | never sent. Compliant. |

**SimConnect writes** — every call is `ISimVars.WriteAsync`, which `SimVarService` guards with
`SimWriteGate.EnsureAllowed` (prefixes `L:FSDT_GSX_` and `L:PROSIMCOMPANION_`): `GsxPaxTargetArmer`
(NUMPASSENGERS + four CREW/PILOTS flags), `GsxMenuOpener` (MENU_OPEN), `GsxDoorService`
(DISABLE_DOORS_MSG), `GsxArrivalService` (NUMPASSENGERS), `GsxStartupResyncService` and
`GsxAutomationService.ClearDepartureTracking` (companion LVARs). Compliant; no cockpit switch
is actuated from this project.

**ProSim writes** — two paths, both gated by `ProsimWriteGate.EnsureAllowed`:
- SDK (`IProsimDataRefs.WriteAsync` → `ProsimDataRefService:82/90`): `FuelTotal` from `GsxRefuelSync:229` and `GsxArrivalService:442`.
- Gateway (`GsxProsimWriter` → `IProsimGateway.WriteDataRefAsync` → `ProsimGatewayClient:91`): doors, chocks, GPU, PCA, refuel power, seat occupation / booked strings, cargo amounts, EFB boarding status. `ProsimNativeGsxGuard` writes `efb.gsx.*` directly through the gateway (prefix allow-listed at `ProsimWriteGate:108`).

**Bypasses found: none.** Two observations: (1) `GsxArrivalService.ArmDeboardPaxTarget:260` and
`GsxDoorService.ReassertDoorsMsgSuppression:586` fire `WriteAsync` without awaiting or observing
the task — a failed write is silent (the gates still apply); (2) the NoConfirm toggle race in
A1 item 5 is a *semantic* write-safety gap (a retraction can become a connection), not an
allow-list gap.

---

## F. Convention drift vs CLAUDE.md

- `async void`: none.
- Blocking on async: none (`.Result` hits are record members, not `Task.Result`).
- Interpolated log strings: none; every `_logger.Log*` uses placeholders.
- `ReadDataRef` polling: none; every dataref/LVAR is a registered subscription. `AircraftStateCheckService` uses `SubscribeDynamic` for user-file names (documented escape hatch).
- Missing `ThrowIfNull`: optional collaborators are deliberately nullable (`GsxGateSelectionService` options/parkings/gateChoice, `GsxQuestionCatalog` stores, `GsxDiagnosticsCollector` refuel/automation/options). Required ones are all checked. `GsxDiagnosticsCollector` assigns `_options` before the null checks (cosmetic).
- `ConfigureAwait(false)`: present on every library await that was checked; the `GsxParkingWakeService`/`GsxMenuControl` poll loops include it.
- `key` identifiers (Snyk rule, owner 2026-10-13, applies to NEW identifiers): pre-existing offenders this project will keep tripping on when touched — `GsxStateMirror.GateContextKey` (public, 12 call sites), `PrepStageMachine.PrepInputs.GateKey/SessionGateKey`, `_handledGateKey` in `GsxRepositionService`/`GsxGateAnchorService`/`GsxJetwayStairsService`, `_currentGateKey`, `_sessionGateKey`, `GsxPatchFrame.Key`, `Mirror.Updated(string key)` parameter, `UnknownKeySeen`, `_reportedUnknownKeys`, `KnownIgnoredKeys`, `GsxIniStand.Get(string key)`, `ParsePyGroupKey`/`ParseStandKey`, `CouatlPortLocator` comments. Renaming `GateContextKey → ParkingContextId` is the one with blast radius (also Web/Speech consumers).
- Timers without a `ProcessTick` core: `GsxAutomationService` (core: `DepartureAutomationCore`), `GsxGroundPrepCoordinator` (`PrepStageMachine`), `GsxPushbackSequenceService` (`PushbackTickCore`/`PushbackSequencer`/planner), `GsxArrivalService` (`ArrivalCore`), `GsxRefuelSync` (`RefuelCore`), `GsxBoardingSync` (`BoardingCore`), `GsxStartupResyncService` (`GsxStartupResync.Assess`; the *wait* ladder in `TryAssess` — session, worldKnown, prosimKnown, planKnown — is not pure), `AircraftStateCheckService` (`AircraftStateCheck.Assess`; same caveat for the settle ladder), `GsxDoorService.ReassertDoorsMsgSuppression` (trivial compare-and-write, fine), `GsxBootstrapService` reconcile (calls the tracker; fine), `GsxGateSelectionService` confirmation timer (trivial).
- Fire-and-forget `Task.Run`: 9 sites, all with catch blocks except the two unobserved `WriteAsync` calls noted in E.
- Options rule "every option ships with a web control": not auditable from this project; `GsxOptions` lives in Core and the controls in Web.
- Logging never logs secrets: confirmed (no token/credential handling in this project).

---

## G. Test seams (cross-checked against tests/ProsimCompanion.Core.Tests/Gsx, 59 files, 12.5 k lines)

Pure cores with tests: `DepartureSequencer`, `DepartureAutomationCore`, `DeparturePumpEffectPlanner`,
`DeparturePumpGatherer`, `DeparturePumpEffectApplier`, `LockedPumpRunner`, `NewPlanCycleReset`,
`ResyncCycleRestore`, `SmartButtonRule`, `DeiceRequestPolicy`, `GateDispatchPlanner`,
`GsxGateResolver`, `GsxPositionMenuPlanner`, `PrepStageMachine`, `ParkingWakePlan`
(`GsxParkingWakeTests`), `ArrivalCore`, `BoardingCore`, `RefuelCore`, `PushbackSequencer`,
`PushbackTickCore`, `PushbackTickEffectPlanner`, `GsxStartupResync`, `AircraftStateCheck`,
`GsxServiceLifecycleTracker`, `GsxServiceStageProjector`, `GsxStateMirror`, `GsxFrame`,
`GsxReconnectBackoff`, `CouatlPortLocator`, `GsxOperatorMatcher`, `PushbackDirectionDecider`,
`PushbackDirectionResolver`, `GsxQuestionDispatcher`, `GroundOpsSignalRelayCore`,
`GsxAutomationPhaseMapper`, `GsxIniProfileParser`/`GsxPyProfileParser`/`PyLiteral`
(`GsxProfileParserTests`), `GsxFlightPlanMonitor.IsFlightPlanAvailable`,
`GsxArrivalService.DecideFobRestore` + `ReadInputs`, `GsxDoorService` catering-door learning,
`GsxGateSelectionService` internals (`AirportPickVerified`, `TryDispatchAsync`, `TryAnswerPositionMenuAsync`
with fakes), `GsxMenuIntentExecutor`, `GsxMenuControl`, `GsxServiceControl`, `GsxTriggerSlot`,
`GsxSimbriefReloadService`, `GsxGroundEquipmentService`, `GsxGroundOpsSignalRelay`.

Services **without** a pure core or a test file named after them:
- `GsxJetwayStairsService` (360) — `RunConnectStep` is a decision function in disguise (gate change, pending, handled, jetway LVAR, target choice, connected check) interleaved with `StartTrigger`; extract `JetwayStairsCore.Decide(gateKey, services, jetwayLvar, pending, handled) → (Status, Action)`.
- `GsxRefuelSync` / `GsxBoardingSync` shells — cores exist; the shells' event-to-core wiring is untested (acceptable).
- `GsxGateAnchorService` (163) — small; `ParkedAtTheStand` is pure and tested via `GateFixesTests`? (not confirmed) — the ladder loop is not.
- `GsxRepositionService` (132) — straight-line; fine.
- `GsxStartupResyncService` — the `TryAssess` wait ladder (lines 447-508: five early returns depending on session, readiness, pushed refs, timeout) is the part that bit twice (#30 regression, #33) and has no table test; extract `ResyncReadiness.Decide(...) → Wait|AssessNow|TimeOut`.
- `GsxDeiceStateMonitor`, `GsxDeiceActivityRelay`, `ProsimNativeGsxGuard`, `GsxBootstrapService`, `GsxDepartureDecisionLog` — thin adapters; low value.
- `GsxDoorService` LVAR edge handling (`OnLvarChanged`, `HandleCargoExit`, `HandleStairs`) — the edge rules (rising toggles, loader-finished delay, stairs-docked door choice) are an obvious pure `DoorEdgeCore` candidate; today only the catering-door learning is tested.
- `GsxQuestionCatalog` handlers other than de-ice: `HandleTugQuestionAsync`, `HandleParkingConflictAsync`, `HandleSelectPosition` are tested only indirectly (`ParkingChangeMenuAssignedGateTests`).

---

## Top 12 by payoff (S/M/L effort, estimated removable lines)

1. **Fix the NoConfirm toggle race in `GsxTriggerSlot`** (A1 item 5) — treat a NoConfirm send for the same service inside `NoConfirmSpacing` as Busy (one extra condition at lines 103-108) and give `GsxJetwayStairsService` a per-service "retraction sent" latch. S; +10 lines, removes a path that can re-connect a jetway during the beacon sequence. Ship with a `GsxTriggerSlotTests` case.
2. **Split `GsxGateSelectionService`** into host + `AirportPickWalker` + `SuffixVariantChooser` + `PositionMenuAnswerer` + `GateSelectLadder`, with a pure `GateRequestState` record for the status/stamps (the existing tests already target those seams). L; 0 net lines, 1332 → five files under 400; the one lock-guarded 20-field state becomes one record.
3. **Delete dead `GsxGateResolver` members** (`ResolveCanonical`, `IsUnambiguousNearestMatch` + `LeadingDesignator`, `ResolveAnchorToken`; make `TrimToken` private) and their tests; delete `GsxMenuControl.WaitForAsync`, `GsxMenuPaging.BackRow`, `GsxIniProfile.Empty`. S; ~130 src + ~70 test lines.
4. **One parked-at-stand predicate** (`ParkingConflictGate.IsParkedEnginesOff`) replacing the four local copies with three different speed thresholds (B1), and one `IsValidIcao` (B2). S; ~25 lines, and the thresholds stop drifting.
5. **`CompanionTrackingLvars` writer** owning the pending-write queue and the three bulk resets (B10), so `GsxAutomationService.ClearDepartureTracking` stops bypassing the resync's MSFS-down queue (today a new-plan reset while SimConnect is down silently loses the clear). M; ~60 lines removed, one correctness fix.
6. **`GsxDecisions.Record` helper** replacing 16 private `RecordDecision` copies while preserving each caller's `ILogger<T>` category and message template (B6). S-M; ~90 lines.
7. **`GsxMenuTitles` + `GsxLvarNames.JetwayNotPresent` + `MirrorWait.ForAsync`** — the literal/constant/poll-loop consolidation (B3, B4, B5). S; ~70 lines, and the menu-title contract (`docs/integrations/gsx.md` §1 anchors) gets one home.
8. **Move no_airport pacing into `GateDispatchPlanner`** (B8) — the planner already receives `LastNoAirportUtc`; the service's duplicate `NoAirportBackoff` check and constant go. S; ~10 lines, one owner for the gate dispatch timing (which is the whole point of that planner).
9. **Shrink `GsxAutomationService`**: extract `SmartButtonSubscription`, move `ArmAutoSelectOperatorAsync` to the bootstrap, unify the three cycle-reset sequences (D). M; ~180 lines out of the host, ~40 removed outright.
10. **Split `GsxQuestionCatalog`** into de-ice / parking / pushback question classes (D) — not a data file. M; 0 net, 792 → four files ≤ 230; de-ice answers already have pure helpers and tests.
11. **Pure cores for the two untested decision ladders**: `JetwayStairsCore.Decide` out of `GsxJetwayStairsService.RunConnectStep` and `ResyncReadiness.Decide` out of `GsxStartupResyncService.TryAssess` (G). M; 0 net, closes the two seams with the worst flight history (#30/#33 regressions, jetway-less stand acks).
12. **Fold `DepartureCycleView` into `ServiceCycleSnapshot`** (B9) and `SeatMap.CountOccupied` (B13). S; ~30 lines, one fewer service-state model to explain.

Appendix items not ranked: `GsxServiceControl` fuel sub-policy extraction (D), `GsxFacilityText`
split (D), `GsxStartupResync` pure part to its own file (D), `GsxTimings` (B11), deice-id
tolerance helper (B12), `CloseIfWeOpenedAsync` helper (B7), second `gate.select` payload
builder in `GsxGateAnchorService` (E), the two unobserved `WriteAsync` calls (E), `key`
identifier renames (F), `DoorEdgeCore` (G), suppressing the duplicate `CallPushbackEffect`
inside `PushbackTickEffectPlanner` (A1 item 3), and the `GsxOptions.Enabled` vs
`AutomationEnabled` wording on the settings page (C).
