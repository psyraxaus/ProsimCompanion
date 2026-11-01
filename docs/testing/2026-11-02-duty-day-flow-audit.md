# Duty-day flow audit — 2026-11-02

Yardstick: `docs/decisions/ADR-0019-duty-day-and-leg-boundary.md` and CONTEXT.md "Duty day".
Measured: the code at HEAD `e7a58590` (branch `feature/settings-hub-usability`) and the two legs
flown 2026-11-01 (EGLL→EHAM, turnaround at EHAM with five app restarts, EHAM→EGLL, end of duty
at EGLL). Every gap below is a WHAT, not a HOW; each approved row becomes one GitHub issue
(ADR §10).

## 1. Scope and method

- Code read at HEAD; every file:line below is HEAD. The ADR's Context claims (written at
  f1a4653f) were each re-checked — two are wrong in detail (§3 rows B4, A3).
- Recordings copied read-only from `//10.0.1.22/c/Users/johncarlo.FLIGHTSIM-PC/AppData/Local/ProsimCompanion/`
  (nine session files 2026-10-31 22:38Z → 2026-11-01 05:25Z, `ProsimCompanion-20261101.log`,
  `daystate.json`, `logbook.json`, `fo-roster-state.json`, `day-20261101-002148.summary.txt`)
  into an empty scratch folder and read with grep/sed only. Session timestamps are wall-clock
  UTC; the CMTrace log is local (UTC+11, marked `L`); the sim clock ran ~6 h 39 min ahead of
  wall UTC (off-blocks sim 06:59:43 at wall 00:20:43Z). **All times below are wall UTC unless
  marked.**
- Phase-engine claims come from `FlightReplay.RunFile` over every session (scratch console
  project referencing `ProsimCompanion.Core`, appendix). The replay cannot reproduce the
  `Shutdown → Preflight` edge: `FlightReplay.cs` re-derives Boarding signals from `gsx-service`
  events (L158, L309–312) but not Deboarding Completed, so both arrivals "diverge" at that
  edge by construction. That edge is therefore cited from the recorded `phase-changed` + the
  engine log line, not a replay — a tooling gap for the probes ADR §Consequences names.
- The live builds (bd25101 → 7c019d7) predate HEAD; where HEAD replays differently the row says
  so. Not verifiable from this flight: the FMS-cleared-before-deboarding order (owner's
  RESETFMS = off; the FMS was cleared *after* deboarding, by the pilot — §2 row 01:51–02:59),
  the §3 reminder (the FMS already held the new route when the OFP landed), the §8 FO offer
  (the aircraft never went cold and dark), end-of-duty clearing (the app was closed 12 s after
  "End duty day"), §6 after partial progress (the only reset ran with nothing called).

## 2. Timeline of the EHAM turnaround as recorded

Build per session: 223816 `bd25101`, 000225/015033 `af9dab8`, 030022 `1b8c570`, 033438
`7f56c58` (#207), 035706 `a03094a`, 041057 `9cbd82b`, 045246/052423 `7c019d7`.

| UTC | Session | Event | What the app did / said |
|---|---|---|---|
| 10-31 23:27:05 | 223816 | ColdAndDark | `power-off`; 23:28:47 voice "commence ground services" (before Preflight). |
| 23:29:42 | 223816 | Preflight | Roster drew Hanna `sim:2026-10-30` ("sim-day" → day mode off), gate intro, walkaround started. No `day.started` (day.enabled false). |
| 23:32:19 | 223816 | `ofp.loaded` 191935630 EGLL→EHAM BAW432 | GSX SimBrief reload; leg 1 begins per ADR §1 — nothing marks it. |
| 23:39:44 / 23:50:36 | 223816 | Departure (boarding) / departure complete | ready-for-pushback + boarding-complete notifications. 00:02:15 app stopped. |
| 11-01 00:02:26 | 000225 | **Restart 1** (at the gate, boarded) | Snapshot restored (notifications, ofp, departureCycle, cabin; 10 min old). Resync seeded Refueling/Catering/Boarding/Water (LVARs), Cleaning/Lavatory (assumed), prep fast-forwarded. Phase Preflight live; HEAD replay → Departure 00:02:30. Walkaround restarted 00:02:31 (abandoned 00:07:15). 00:14:42 pilot forced Departure (web). |
| 00:21:48 | 000225 | `day.started` (web, during pushback) | "Duty day started." Leg 1 route seeded from OFP. |
| 01:37:25 | 000225 | TaxiIn → Shutdown | Arrival: cycles reset, `FlightCycleReset`, `day.leg.completed` 1, "That's leg 1 — 77 minutes block" (01:37:40), tech-log offers, debrief. 01:37:27 snapshot "invalidated: shutdown finalised — not written again until the next leg". |
| 01:38:05 → 01:50:03 | 000225 | Deboarding Requested → Active → Completed | `notify.sent deboarding-complete`; log "Arrival complete (deboarded) — turnaround armed". **No crew line.** |
| 01:50:33 | 000225→015033 | Shutdown → Preflight (`turnaround-arrival-complete`, 30 s) | Session rotated; `day.leg.started` 2; `day.leg.route` **EGLL→EHAM BAW432** (the flown plan); 01:50:35 "Leg 2. New sector."; 01:50:58 FO cross-check "INIT page: EGLL to EHAM". |
| 01:51 → 02:59 | 015033 | FMS cleared by the pilot (inferred) | No event. Cross-check held EGLL→EHAM at 01:50:58; log 03:00:24Z "MCDU has no plan", 03:01:17Z `fmsOrigin='' fmsDestination=''`. 02:59:06 app stopped. |
| 03:00:23 | 030022 | **Restart 2** (empty cabin, no plan) | `day.resumed` leg 2, "Continuing the duty day — leg 2 of 2" (03:00:34). **Nothing to restore** (snapshot gone at shutdown). 03:00:26 Preflight; roster drew **Sam** `sim:2026-10-31` + gate intro (FO changed mid-day). Resync 03:00:28: Deboarding (LVAR), **Boarding (EFB status 'completed', cabin empty)**, Cleaning/Lavatory/Refueling/Catering/Water assumed; turnaround from LVARs. |
| 03:01:01 → 03:03:54 | 030022 | Voice "commence ground services" | Gate monitor ClosedAfterBoarding 119/119 (leg 1's count, "Doors closed 09:40Z"); 03:03:54 "all departure services completed or skipped" with nothing called → **ready-for-pushback notification (false)**. |
| 03:03:55 / 03:05:06 | 030022 | FMS plan EHAM→EGLL entered / `ofp.loaded` 191949682 EHAM→EGLL BAW433 | `day.leg.route` corrected to EHAM→EGLL. No `gsx-cycle-reset` (build predates #207). Gate monitor **unchanged** to 03:34:19 (one `gate-status-changed` in the file). 03:11:59 Refueling Completed. 03:18:44 / 03:19:26 "boarding / catering has already completed this turnaround". 03:34:19 app stopped. |
| 03:34:39 | 033438 | **Restart 3** (#207 build) | Snapshot (written 03:12Z) restored: notifications "ready-for-pushback, refuel-complete already sent", ofp, departureCycle "turnaround", cabin, walkaround → `walkaround.skipped` restart (state Idle). 03:34:41 `gsx-cycle-reset` new flight plan req:191949682 `cycleWasComplete:false`. Gate monitor Open. Resync: Deboarding (LVAR), Refueling (5100 ≥ 3356 kg), turnaround. |
| 03:35:11 → 03:56:23 | 033438 | Voice start → Catering, Water, Lavatory, Cleaning, Boarding | Gate FinalCall 03:41:32 at 119/129 (stale count; #209 fixed in a03094a); Departure 03:43:52 (`boarding`); 03:56:23 departure complete, gate Closed 129/129, **boarding-complete notification only — no ready-for-pushback**. 03:56:50 app stopped. |
| 03:57:07 | 035706 | **Restart 4** (boarded) | Snapshot (1 min) restored; first mirror read fired Boarding Completed; resync seeded all seven + Deboarding; phase **Preflight for 13 min** (HEAD replay → Departure 03:57:12). 04:10:46 app stopped. |
| 04:10:58 | 041057 | **Restart 5** (boarded) | Same seeds; 04:11:04 `gsx-departure-complete` "restored by the startup resync"; phase Preflight (HEAD replay → Departure 04:11:02); 04:15:18 PushbackAndStart (leg 2 off-blocks); 04:25:45 airborne. 04:52:32 app stopped in descent. |
| 04:52:47 | 045246 | **Restart 6** (in flight) | Snapshot restored (flightTimes, fuelLog, minima, chrono, …); Unknown → Descent 04:52:54; 05:04:05 landing. |
| 05:09:38 | 045246 | Shutdown at EGLL | `FlightCycleReset`, `day.leg.completed` 2. Debrief deferred (`dialogueHold`, expired 05:19:40 — the stuck hold fixed in HEAD 33dba762). **No turnaround line** (leg 2 has no block minutes, §3 B6). 05:09:59 → 05:23:53 Deboarding; notification only, no crew line. |
| 05:24:23 | 045246→052423 | Shutdown → Preflight | `day.leg.started` **3**, route **EHAM→EGLL** (the flown plan); "Leg 3. New sector." deferred; 05:24:24 GSX SimBrief reload "new OFP" for the flown plan at EGLL; 05:24:54 FO cross-check "INIT page: EHAM to EGLL". |
| 05:25:03 | 052423 | `day.ended` (web, legs 2) | Summary file + logbook day record (2 legs, block 77) written; spoken summary **deferred by the dialogue hold and lost** (app closed 05:25:15). `daystate.json`: state ended, `currentLegIndex: 3`, leg 3 EHAM→EGLL `landed:false`. |

## 3. Findings, ranked

Kind A = silent miss (the app does not do what the ADR says); B = wrong display or wrong line.
Rows are ordered A first, then by how often a pilot meets them.

| # | Kind | ADR | What the ADR says | What the code does (HEAD) | What the recording shows | Proposed issue title |
|---|---|---|---|---|---|---|
| A1 | A | 2, 4 | Three pilot-visible leg-boundary states (Turnaround, Awaiting plan, new-leg Preflight): a pill under the phase, the arrival monitor, one session event per change. | No leg state exists. `FlightStatusPresentation.cs` carries only the phase; no event family; the Flight Status change log reads `pill "PREFLIGHT" (was "ARRIVAL")` on the deboarding edge (log 16:24:23L). | 01:50:03 deboarded, 01:50:33 PREFLIGHT, nothing between; the FMS clear (01:51–02:59) is invisible; 05:24:23 PREFLIGHT at EGLL with no next plan. | Leg-boundary states: Turnaround / Awaiting plan pill, board row and session events |
| A2 | A | 2 | Awaiting plan = `aircraft.fms.origin` AND `.destination` go value → empty while parked, engines off; event `fms.cleared`; `aircraft.fms.flightPhase` logged as witness. | Both datarefs are subscribed (`GsxFlightPlanMonitor.cs:57-58`, `FlightStateSnapshotService.cs:117-118`, `GsxStartupResyncService.cs:317-318`) but nobody watches the value→empty edge. `FmsFlightPhase` is a const only (`ProsimDataRefNames.cs:315`), never subscribed. `control.resetFMS` is write-only (CSV). | FO cross-check 01:50:58 "INIT page: EGLL to EHAM" (015033); log 14:00:24L "the MCDU has no plan", 14:01:17L `fmsOrigin='' fmsDestination=''`. The clear happened live with RESETFMS off and left no trace. | FMS-cleared watcher: `fms.cleared` event, FMGC phase witness |
| A3 | A | 1, 5 | The Day engine starts the next leg on the new OFP; the Preflight edge only closes the arrival; the leg's route comes from the plan that began it. | `CompanyDayService.cs:328-339`: `Preflight when Turnaround` rotates the session, `StartNextLeg()`, seeds the route from `_ofp.Current` (L337) — the plan just flown. (The ADR Context says "no plan loaded"; in fact the OLD plan is loaded and becomes the new leg's route.) | 01:50:33 `day.leg.started 2` + `day.leg.route EGLL→EHAM BAW432`; corrected only at 03:05:06. 05:24:23 `day.leg.started 3` EHAM→EGLL after the final landing; `daystate.json` ended with `currentLegIndex 3`, leg 3 `landed:false` — a phantom third leg on the Duty Day page. | Day engine: leg starts on the new OFP, never on the Preflight edge |
| A4 | A | 6 | A different plan at the same gate resets everything the arrival resets (`FlightCycleReset`): gate monitor, loadsheet, upcalls, notifications. | `GsxAutomationService.OnOfpChanged` (L193-230) resets lifecycle, cycle, pax target, slot, tracking LVARs (L236-258). `RaiseFlightCycleReset` only at Arrival (L511). Left stale for the new plan: `NotificationSignalSource` latches, `LoadsheetService` editions, `GsxGroundOpsSignalRelay` once-per-cycle memory, `FuelConfirmationStore`/`FuelDecisionStores`, de-ice stores and `GsxDeiceRequestService`, `PaPromptStore`/`PaPromptService`/`PurserPaService`, `TurbulenceCabinService`, `TransitionReminderService`, `CaptainsQuestionStore`, `DiversionStore`, `RunwayConditionStore`, `WalkaroundService`, `EfbInitOverridesService`, `GateMonitorService` (grep `FlightCycleReset +=`). | Leg 2's "ready-for-pushback" was spent by the false completion at 03:03:54 (030022 `notify.sent`), the latch came back from the snapshot at 03:34:41 (log "ready-for-pushback, refuel-complete already sent"), and the real completion at 03:56:23 (033438) sent **boarding-complete only**. | New-plan cycle reset raises FlightCycleReset for a different plan |
| A5 | A | 6 | …"when boarding has not started". | `NewPlanCycleReset.ShouldReset` (L63-70) refuses when **any** departure service outside deboarding/GPU/jetways was called, active or completed (`_lifecycle.AnyObservedProgress`, `GsxAutomationService.cs:199`) — refuel or catering alone blocks the reset. | Not exercised (the only reset ran in a fresh process with nothing called). | New plan after refuel/catering but before boarding still starts the new leg |
| A6 | A | 2 | Turnaround: the purser reports once, Low: "Cabin's empty, captain." | `ArrivalCompleted` subscribers: `FlightStateEngine.cs:231` (latch) and `NotificationSignalSource.cs:110` (push notification) only. | 01:50:03 and 05:23:53: `notify.sent deboarding-complete`, no `speech.*` between the parking checklist (01:40:51) and "Leg 2. New sector." (01:50:35). | Purser "cabin's empty" line at deboarding complete |
| A7 | A | 3 | New OFP loaded, cabin empty, FMS still holds the previous plan → FO says once "Captain, the FMS still holds the previous flight plan." | No such line anywhere (grep "previous flight plan", "end of our duty": none). | Not exercised: the FMS received EHAM→EGLL at 03:03:55 (log `FMS plan: "EHAM" → "EGLL"`), 71 s before the OFP. | FO reminder: FMS still holds the previous plan (once) |
| A8 | A | 7 | The Duty Day feature is on by default from 1.0 (auto-start on the first Preflight). | `DayOptions.cs:18` `Enabled` defaults false (`AutoStart` L22 true). Settings page control exists. | No `day.started` at the 23:29:42 Preflight; owner pressed web Start at 00:21:48 mid-pushback; roster drew by "sim-day" (= day mode off). | Duty Day on by default (settings default + manual) |
| A9 | A | 8 | FO's one-time offer "Captain, is that the end of our duty day?" after cold and dark, parked, no plan for 10 min. | No code. End only by voice/web (`CompanyDayService.cs:646-679`) or 90-min idle (L622-637). | Not exercised (ended by web at 05:25:03 one minute after Preflight; never cold and dark). | FO end-of-duty offer |
| A10 | A | 8 | On end of duty: roster FO released, flight-state snapshot and departure cycle cleared, ground preparation held until a new OFP. | `TryEndDay` (L535-576) closes the day, folds the logbook, writes and speaks the summary — nothing else; no other component subscribes to the day ending (grep `day.ended`, `IDayControl`: only the service). | Not exercised (app closed 12 s after). | End of duty clears restart memory, departure cycle, holds ground prep |
| A11 | A | 8, CONTEXT | A plan loaded after the end starts a new duty day **with the same FO**; the duty day holds the FO on duty. | With day mode on, a new day id draws a new FO (`FoRosterTriggerCore.cs:63-73`, `FoRosterVoiceFeature.cs:247`; #194 "a different First Officer each duty day"). With day mode off the draw is per sim calendar day at any Preflight edge (L75-84). | Day mode off as flown: Hanna (`sim:2026-10-30`, 23:29:42) was replaced by Sam (`sim:2026-10-31`, 03:00:26, 030022 `persona.rosterDraw` reason sim-day) in the middle of the duty day, with a second gate intro. | Roster: one FO per duty day, same FO on the day after end-of-duty |
| A12 | A | 9 | During Turnaround / Awaiting plan the snapshot keeps a turnaround part (deboarded, FMS cleared, last plan id); never Preflight-with-nothing. | Snapshot cleared and writes suspended at shutdown finalise (`FlightStateSnapshotService.cs:454-472`); resumed only on a new request id (L344-354) or push/taxi (L430-437). Parts: ofp, minima, departureCycle, flightTimes, fuelLog, notifications, cabin, chrono, walkaround — no turnaround part. | Log 12:37:27L "invalidated: shutdown finalised"; Restart 2 had nothing to restore (no `flight-state-restored` in 030022) and read PREFLIGHT with no plan; the resync then seeded leg 1's boarding from the stale EFB status (14:00:28L) → false completion 03:03:54 (that seed is fixed in 7f56c58, `GsxStartupResync` L144-150; the missing part is not). | Turnaround snapshot part (deboarded, FMS cleared, last plan id) |
| A13 | A | 9 | Never a revived old leg. | Restore identity passes on the EFB's `planImported` flag alone (`FlightStateSnapshot.cs:124-127`), not the request id; `OfpSnapshotPart.Restore` (`FlightStateParts.cs:48`) then overwrites the store with the snapshot's plan. A plan imported in the EFB while the app is down would still restore the previous plan's snapshot. | Not exercised (every restart restored the matching plan). | Snapshot restore compares the request id, not the EFB flag |
| A14 | A | 9 | A restart is invisible. | Walkaround: `WalkaroundCore.cs:104` needs `!BoardingDone`; every restart inside a cycle skips the walk (`WalkaroundService.cs:277-301`, #196 "never after an app restart"). | Leg 2 had no walkaround at all: blocked by the false boarding seed (03:00:28), then `walkaround.skipped restart` at 03:34:41 (state Idle), 03:57:09, 04:11:00, 04:52:48. | Walkaround survives a restart (or the owner confirms #196's rule) |
| B1 | B | 1, 5 | "The FO announces the new sector" on the new OFP. | "Leg {n}. New sector." is spoken on the Preflight edge (`CompanyDayService.cs:339`), before any plan. | 01:50:35 "Leg 2. New sector." with the flown plan as route; 05:24:23 "Leg 3. New sector." after the final landing (deferred). | FO sector announcement on the new OFP, with the route |
| B2 | B | 6 | Gate monitor resets for a different plan at the gate. | `GateMonitorCore.cs:76-85` resets on a different request id **only when one was remembered**; after a restart with no OFP the first plan is adopted without a reset. The at-gate auto-reset (L104-113) needs `!paxReported`, defeated by GSX's stale count. #173 shipped the id reset (rc.5, "unverified live"); this is the remaining hole. | 030022: ClosedAfterBoarding 03:01:13 (119/119, leg 1's) stayed through the new OFP 03:05:06 until the restart — one `gate-status-changed` in the file. | Gate monitor: first OFP after a restart resets a restored ClosedAfterBoarding |
| B3 | B | 1 | The leg (and its cross-check) belongs to the new OFP. | FMGC cross-check auto trigger: at the gate, once per cycle, when origin/destination/V1 are settled (`FmgcCrossCheckService.cs:148-179`) — the flown plan satisfies it on the arrival's Preflight edge. | 01:50:58 "INIT page: EGLL to EHAM" after landing at EHAM; 05:24:54 "INIT page: EHAM to EGLL" after landing at EGLL. | FMGC cross-check waits for the new leg's plan |
| B4 | B | 8 | A short spoken debrief: legs, block time, fuel, tech-log entries. | Spoken in code (`CompanyDayService.cs:567`, Low) — the ADR Context's "nothing is spoken" is wrong. Content (`DaySummaryComposer.cs:29-70`): sectors, block, duty, delay, punctuality, stabilised approaches, abnormals, drills, defects raised/cleared — **no fuel**, defects as counts not entries. | 05:25:03 summary enqueued, `speech.deferred` (dialogueHold), app closed 05:25:15 — never spoken. Summary file says "Total block 1 hour 17 minutes" (see B6). | End-of-duty debrief: fuel and tech-log entries; spoken even under a hold |
| B5 | B | — | `gsx-cycle-reset` records `cycleWasComplete`. | `_cycle.BeginTurnaroundCycle()` (`GsxAutomationService.cs:207`, `DepartureCycleState.cs:102-113` sets `_complete=false`) runs before the field is read (L219). | 03:34:41 `cycleWasComplete:false`. | gsx-cycle-reset reads cycleWasComplete before the reset |
| B6 | B | 8, 9 | Block time per leg; a restart is invisible. | Leg facts come from the finalised session file (`DebriefFactExtractor.cs:163` off-block = first push/taxi edge in that file); a session that began in flight has none. | Leg 2 (session 045246, started 04:52:47 in descent) has no `blockMinutes` in `daystate.json` or `logbook.json`; the day summary and logbook day record read block 77 = leg 1 only (leg 2 was 54 min: `flight-times` off 10:54:18 on 11:48:38 sim). No turnaround line at 05:09:38 (needs block). | Leg block/flight minutes survive an in-flight restart |
| B7 | B | 2 | Ground preparation begins on a *different* OFP. | `GsxSimbriefReloadService.cs:141` retries the pending "new OFP" reload on every phase edge. | 05:24:24 GSX SimBrief reload "new OFP" req 191949682 at **EGLL** for the plan just flown (052423). | GSX SimBrief reload only for a plan other than the one flown in |

## 4. What already matches the ADR

- Shutdown → Preflight only after GSX Deboarding Completed + 30 s hold, parked, beacon off
  (`FlightPhaseRules.cs:127-131`; latch `FlightStateEngine.cs:264-274`, cleared on leaving
  Shutdown L539-542; relay `GsxGroundOpsSignalRelay.cs:104-108`). Recorded 01:50:03 → 01:50:33
  and 05:23:53 → 05:24:23; replay of every session matches the live engine up to TaxiIn →
  Shutdown (see §1 for the harness gap).
- Plan identity = SimBrief request id, route as fallback, flight number ignored
  (`NewPlanCycleReset.cs:29-44`). Same plan twice resets nothing: `ShouldReset` L58; `OfpEventRecorder.cs:71-79`
  de-dupes `ofp.loaded`; `SimbriefImportService.cs:267` reuses the pax roll; the snapshot is kept
  on an unchanged id (`FlightStateSnapshotService.cs:344`). Recorded: six `ofp.loaded` for
  191949682 across restarts, one `gsx-cycle-reset`.
- A different plan at the gate resets the departure side — service cycles, pax target, trigger
  slot, tracking LVARs (`GsxAutomationService.cs:193-258`). Recorded 03:34:41; catering, water,
  lavatory, cleaning and boarding then ran fresh for leg 2 (033438 03:35–03:56).
- Turnaround auto-start needs an OFP other than the arrival's (`DeparturePumpGatherer.cs:239-258`,
  `DepartureAutomationCore.cs:128-146`); a resync-recovered turnaround sets the baseline to null
  (`GsxAutomationService.cs:266`).
- Cabin crew re-arm on Shutdown/TaxiIn → Preflight (`CabinCrewCore.cs:143-144`).
- Day: resumes across restarts (`CompanyDayService.cs:172-206`; five `day.resumed`, "Continuing
  the duty day — leg 2 of 2"); the state file is never written empty (`DayStateFile`, every
  "Day state saved (N bytes)" non-zero — probe `day-state-empty-file` clean this run); ends by
  voice/web (L646-679) with the 90-min idle backstop (L622-637); summary file (L578-594) and
  logbook day fold (L556) — recorded 16:25:03L.
- Restart after boarding reaches Departure (`9cbd82b`: `FlightStateEngine.cs:796-813` plus the
  tracker's first-read Completed edge): HEAD replays of 000225, 035706 and 041057 commit
  Preflight → Departure within 2 s of start where the live builds sat in PREFLIGHT (00:02–00:14
  manual override; 03:57–04:10; 04:11–04:15). Fixed-unverified live.
- Resync seeds boarding only with a non-empty cabin and assumes nothing without a proven boarding
  (`7f56c58`, `GsxStartupResync` L88-150) — the 03:00:28 false seed would not recur. A fully
  seeded cycle is restored complete (`GsxAutomationService.cs:280-298`; recorded 04:11:04).
- The stuck dialogue hold that swallowed the EGLL debrief, the chocks upcall, "Leg 3" and the day
  summary (05:09:40 → 05:25:03) is fixed in HEAD `33dba762` (unflown).

## 5. Open questions for the owner

1. **90-min backstop vs a real turnaround.** Under ADR §5 the day would have sat in Turnaround
   from 01:37:25 (shutdown) to 03:05:06 (new OFP) — 88 min, two short of `AutoCloseIdleMinutes`
   (90, `CompanyDayService.cs:622-627`); today the idle clock is also reset by every phase edge
   (L297) and every restart (L180). Should the backstop count from deboarding complete, be
   longer, or pause while the FMS is being programmed?
2. **How was the FMS cleared at EHAM?** RESETFMS is off, yet the FMS was empty by 03:00:24
   after holding EGLL→EHAM at 01:50:58. IOS "Reset FMS", MCDU INIT re-entry, or a ProSim
   restart? A ProSim restart drops the subscription (value → null) rather than value → empty,
   and the `fms.cleared` rule must know which edges count.
3. **Walkaround after a restart:** #196 says "never after an app restart"; ADR §9 says a restart
   is invisible. Which wins (A14)?
4. **Roster rule:** #194 "a different First Officer each duty day" (per day id) vs ADR §8 "a new
   duty day after the end keeps the same FO (per sim calendar day)" (A11). Which is the rule, and
   is a mid-day FO change ever acceptable?
5. **Final station display:** with §5 in force the day stays in Turnaround at EGLL with no leg 3
   row. Should the Duty Day page show "Turnaround — no plan" there, or nothing?

## 6. Appendix — commands

Copy (read-only share → empty scratch folder; `S` is the scratch path, `SRC` the share):

```bash
mkdir -p "$S/simpc-2026-11-01" && cd "$S/simpc-2026-11-01"
SRC="//10.0.1.22/c/Users/johncarlo.FLIGHTSIM-PC/AppData/Local/ProsimCompanion"
for f in session-20261031-223816.jsonl session-20261101-000225.jsonl session-20261101-015033.jsonl \
  session-20261101-030022.jsonl session-20261101-033438.jsonl session-20261101-035706.jsonl \
  session-20261101-041057.jsonl session-20261101-045246.jsonl session-20261101-052423.jsonl \
  day-20261101-002148.summary.txt; do cp "$SRC/sessions/$f" .; done
cp "$SRC/logs/ProsimCompanion-20261101.log" "$SRC/daystate.json" "$SRC/logbook.json" "$SRC/fo-roster-state.json" .
```

Event extraction (one line per event, time + type + payload head):

```bash
grep -E '"type":"(session-started|session-ended|session-rotated|phase-changed|gsx-cycle-reset|gsx-departure-complete|gsx-departure-started|ofp\.loaded|day\.[a-z.]+|flight-state-restored|walkaround\.[a-z]+|persona\.[a-zA-Z]+|notify\.sent|gate-status-changed|gsx-simbrief-reload|flight-times)"' session-*.jsonl \
 | sed -E 's/^session-([0-9-]+)\.jsonl:\{"timestamp":"2026-1[01]-[0-9]+T([0-9:]{8})\.[0-9]+\+00:00","type":"([^"]+)","payload":/\1 \2 \3 /' | cut -c1-330
grep -E '"type":"(gsx-decision|gsx-service)"' session-20261101-0[0-4]*.jsonl | grep -iE 'resync|turnaround|deboard|cycle|complete'
grep -E '"type":"speech\.(spoken|deferred|expired)"' session-20261101-05*.jsonl
grep -c '"type":"gate-status-changed"' session-20261101-030022.jsonl   # = 1
```

CMTrace log (local time UTC+11):

```bash
grep -iE "snapshot invalidated|Day state saved|Duty day|Company day|resync|Arrival complete|Boarding started|Gate monitor|FO roster|walkaround|flight plan detection|MCDU has no plan|FMS plan" ProsimCompanion-20261101.log \
 | sed -E 's/<!\[LOG\[//; s/\]LOG\]!><time="([0-9:.]+)\+660" date="[^"]*" component="([^"]*)"[^>]*>/ | \1L | \2/'
```

Replay (scratch console project outside the repo, `net10.0`, `ProjectReference` to
`src/ProsimCompanion.Core/ProsimCompanion.Core.csproj`; `Program.cs` = `foreach (var p in args)
Console.WriteLine(FlightReplay.RunFile(p).Describe());`), run from the project folder:

```bash
dotnet build -c Release && dotnet run -c Release --no-build -- "$S/simpc-2026-11-01/session-20261101-000225.jsonl" ... "$S/simpc-2026-11-01/session-20261101-052423.jsonl"
```

Code checks: `grep -rn "FlightCycleReset +=" src --include=*.cs` (subscriber list, A4);
`grep -rn "ArrivalCompleted +=" src` (A6); `grep -rn "end of our duty\|previous flight plan" src`
(A7, A9); `grep -rn "FmsFlightPhase" src` (A2); `grep -rn "public string Key =>" src` (snapshot
parts, A12); issue #173 read via the GitHub MCP (B2).
