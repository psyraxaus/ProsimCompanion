# Pathway model — branch fix/code-review-2026-11-03 (main 0db90771 → HEAD ee2c2ef0)

Scope: the pathways this branch adds or materially changes. The pre-branch app is mapped in
docs/reviews/2026-11-03 (voice pipeline audit-speech §A, GSX departure cycle audit-gsx §A1–A3,
Core store/event table in audit-core); this document extends those maps, it does not repeat
them. Vocabulary is CONTEXT.md's (leg boundary, duty day, end of duty, roster, gate intro,
walkaround, scenario, dialogue hold, phraseology, trigger slot, cycle latches, probe). Ratings:
S = silent misbehaviour / bug, M = likely to bite, L = nit. "unverified" = not confirmed by
reading code or a recording.

Speech priorities below are the arbiter's four bands (Low / Normal / High / Critical); every
spoken line enters through `ISpeechArbiter.EnqueueAsync` or `SpeakAsync` and passes the rule
chain SterileCockpitRule → QuietCockpitRule → DialogueHoldRule (SpeechArbiterService.cs:100),
then `Phraseology.Normalize` (SpeechArbiterService.cs:396) → TTS → playback.

---

## 1. Duty day & leg boundary (ADR-0019; #210 #211 #212 #213 #217 #218 #220 #222)

### 1a. The leg-boundary machine

Trigger sources → `LegBoundaryService` (Core/Flight/LegBoundaryService.cs) → pure `LegBoundaryCore` (Core/Flight/LegBoundary.cs:92).

1. **Deboarding complete.** GSX mirror `Deboarding` lifecycle `Completed` → `GsxGroundOpsSignalRelay.OnServiceEvent` (Gsx/Sync/GsxGroundOpsSignalRelay.cs:103–107) → `GroundOpsSignals.RaiseArrivalCompleted()` → `LegBoundaryService.OnArrivalCompleted` (:203) → `LegBoundaryCore.Deboarded` (LegBoundary.cs:97): state → **Turnaround** (or stays AwaitingPlan with an order warning if the FMS clear came first). Same signal also feeds `FlightStateEngine.NotifyArrivalComplete` (Core/Flight/FlightStateEngine.cs:264 — arms the `turnaround-arrival-complete` rule FlightPhaseRules.cs:127), `CabinCrewService.OnArrivalCompleted` (purser "cabin's empty", Low, once per leg via `_cabinEmptyReported`, re-armed on `LegBegan` — Speech/Cabin/CabinCrewService.cs:223–240, event `cabin.empty`, #214), `CompanyDayService.OnArrivalCompleted` (idle backstop clock, below) and `NotificationSignalSource.OnDeboardingCompleted` (§11).
2. **FMS cleared.** `FmsPlanWatcher` (Core/Aircraft/FmsPlanWatcher.cs) holds three Infrequent subscriptions (`FmsOrigin`, `FmsDestination`, `FmsFlightPhase`); `FmsClearDetector.Observe` on each push; value→empty on BOTH raises `Cleared` (:63–80; a drop to null is not a clear by the detector — unverified, detector not read). `LegBoundaryService.OnFmsCleared` (:214) records `fms.cleared {origin, destination, fmgcPhase, parked, accepted}` ALWAYS, and only counts the step when `Data is {IsValid, OnGround, !AnyEngineRunning} && GroundSpeedKt < 1` (:216–217) → `LegBoundaryCore.FmsCleared` → **AwaitingPlan**.
3. **Plan loaded.** `OfpStore.Changed` → `OnOfpChanged` (:247) → `OfpPlanIdentity.PlanId` (request id, else route) → `LegBoundaryCore.PlanLoaded` (LegBoundary.cs:131): same id → nothing; different id → state **None**, `LegBegan=true`, with a warning when it arrived in Turnaround (FMS not cleared), in AwaitingPlan without deboarding, or with no boundary at all ("a different plan replaced X at the gate").
4. **Take-off.** `PhaseChanged` into TakeoffRoll/InitialClimb/Climb → `Airborne` (:176) clears any boundary state and re-arms both step flags.
5. **End of duty.** `DutyDaySignals.DayEnded` → `OnDutyDayEnded` (:184) → machine back to Empty, published as adopted (no LegBegan).

Outputs per accepted edge (`Publish`, :307): `LegBoundaryStore` snapshot (Flight Status pill `fs-leg` Home.razor:120–125 and the monitor board via FlightMonitorPresentation), session event **`leg.boundary`** `{state, previous, reason, warning, planId, legBegan, deboarded, fmsCleared}`, a Diagnostics RECENT line (TURNAROUND / AWAITING PLAN / NEW LEG / LEG BOUNDARY CLEARED), `Changed` (snapshot part dirty), and on `LegBegan` the Core event `ILegBoundaryEvents.LegBegan(planId, previousPlanId, from)` (:350).

`LegBegan` subscribers: `CompanyDayService` (next leg), `FoRosterVoiceFeature` (draw opportunity), `FmsPlanReminderService` (#215: "Captain, the FMS still holds the previous flight plan." — only when `From == Turnaround`, the box holds a valid pair, and that pair is not the new plan's; Normal, tag `fo.fmsReminder`, TTL 2 min, event `fo.fms-reminder` — Speech/FoSide/FmsPlanReminderService.cs:70–85, :133), `FmgcCrossCheckService` (#221, re-arms the cross-check), `CabinCrewService` (re-arms the cabin-empty latch).

### 1b. Restart: the `turnaround` snapshot part

`LegBoundaryService` is an `IFlightStatePart` with `Key="turnaround"`, `RestoreOrder=5`, `PersistsAcrossLegs=true` (:105–112). `Capture` returns null when nothing is between legs (:128). `FlightStateSnapshotService`:
- after Shutdown is finalised (`SessionFinalizer.Finalized` → `OnShutdownFinalized`, FlightStateSnapshotService.cs:548) and on a ColdAndDark commit (:509) the file is cleared and re-written **boundary-only** (`Invalidate(keepBoundary:true)` :556–600); writes stay suspended for the flight parts until PushbackAndStart/TaxiOut/TakeoffRoll (:513–520) or a new OFP request id (:380–390), only boundary parts are written meanwhile (:393–404);
- at the next start a boundary-only document is validated by `FlightStateSnapshotRules.Validate(..., boundaryOnly:true)` (Core/Flight/FlightStateSnapshot.cs:146–166): same aircraft, young enough, and no DIFFERENT request id already in `OfpStore` → Restore; the EFB "imported" flag and the FMS pair are ignored for it; a flight (non-boundary) document is still discarded when the aircraft is cold and dark (:273–276) but a boundary-only one survives the power-down;
- `ApplyRestore` walks parts by `RestoreOrder` then `Key` (:110) — `notifications` (5) and `turnaround` (5) before `ofp` (10) so the flown plan id is in the machine before the OFP store fires (LegBoundaryService.cs:107–109);
- end of duty → `Invalidate(keepBoundary:false, suspend:true)` (:553).

Fallback with no snapshot: `GsxStartupResyncService` (Gsx/Sync/GsxStartupResyncService.cs:549–553) → `ILegBoundaryRecovery.AdoptDeboardedFromEvidence("startup resync: tracking LVARs")` only when `GsxStartupResync.BetweenLegs(verdict, evidence)` and the machine is still Empty (LegBoundaryService.cs:157–182).

Replay: `FlightReplay` re-derives `DeboardingCompleted` from `gsx-service {service:"Deboarding", event:"Completed"}` lines (Core/Flight/FlightReplay.cs:212–218) and raises `GroundOpsSignals.RaiseArrivalCompleted()` at its stamp (:327–328) — so the engine's turnaround rule fires in the replay; the replay constructs no `LegBoundaryService`, so `leg.boundary` is not replayed (by design, ADR-0018 §6 shape).

### 1c. The Duty Day engine (`CompanyDayService`, Speech/Day/CompanyDayService.cs; `CompanyDayEngine`, `DayIdleRules`)

Defaults changed: `day.enabled=true`, `autoStart=true`, `autoCloseIdleMinutes=300`, `endOfDutyOfferMinutes=10` (Core/Configuration/DayOptions.cs:20–43).

- **Leg begins on the OFP**: `OnLegBegan → HandleLegBegan` (:337): day in `Turnaround` → `_eventLog.StartNewSession()` FIRST (:357, the only rotation in the app), `StartNextLeg` (state OnLeg, `IdleSinceUtc=null`), `SeedRouteFromOfpLocked`, events `day.leg.started` + `day.leg.route`, speech "Leg n. New sector, A to B." (Low, tag `day`), company message in Planned mode. No day open → `TryStartDay("ofp")` when enabled+autoStart (speech "Duty day started." Normal). Day `OnLeg` → route re-seed only (`OnOfpChanged`).
- **Preflight edge** (`HandlePhase` :423): only auto-starts a day when none is open; never touches legs. Off-blocks stamp on PushbackAndStart/TaxiOut; `CompleteLeg` on Shutdown (`day.leg.completed`, state Turnaround, `IdleSinceUtc` = on-blocks — CompanyDayEngine.cs:139–140).
- **Idle backstop**: `OnArrivalCompleted → HandleDeboarded` → `MarkDeboarded` restarts `IdleSinceUtc` (event `day.idle.started {from:"deboarding complete"}`); 60 s `ProcessTick` (:857) → `DayIdleRules.ShouldAutoClose` (Core/Day/DayIdleRules.cs:35: Turnaround, now − IdleSince ≥ 300 min) → `TryEndDay("idle")`.
- **The FO's offer**: `DayIdleRules.ShouldOffer` (:50): Turnaround, `_coldAndDarkSinceUtc` set (ColdAndDark edge, or process start while already ColdAndDark — CompanyDayService.cs:225), not yet offered, ≥ 10 min since the later of idle start and the dark edge → `MarkEndOfDutyOffered` (persisted, once per day) → event `day.end-offer.raised` → `EndOfDutyOfferStore.Ask(prompt)` (captain's question: YesNo, MaxAsks 1, StandardTtl counts as no, AnswerIsFinalForCycle — Core/State/EndOfDutyOfferStore.cs:18–24). Voiced by `CaptainsQuestionDialogue` (mic owner `endOfDuty`, prompt High via `SpeakAsync`, listens) or answered on the web card. `OnOfferAnswered` → yes → `TryEndDay("offer")`.
- **End of duty** (`TryEndDay` :699): `EndDay` → `day.ended {dayId, legs, source}` → `_offer.Withdraw()` → logbook day record → debrief text (`IDaySummaryComposer.Compose` with fuel + tech-log titles) → file `{dayId}.summary.txt` beside the session logs → **speech Low, tag `day.debrief`** (exempt from the dialogue hold, DialogueHoldRule.cs:30) → **`DutyDaySignals.RaiseDayEnded`** raised last (:745–752).
- `DayEnded` consumers (grep): `FlightStateSnapshotService` (file cleared, boundary dropped, writes suspended), `LegBoundaryService` (machine → None), `GsxAutomationService.OnDutyDayEnded` (Gsx/Automation/GsxAutomationService.cs:202: lifecycle `ResetCycle(ArrivalSide)`, `BeginTurnaroundCycle`, pax target, `ResetForArrival` latches, `_lastResetPlanId = current plan`, `Withdraw` the turnaround-start question, slot reset, `ClearDepartureTracking()`, event `gsx-cycle-reset {reason:"end of duty"}`, `Pump()`). It does NOT raise `FlightCycleReset`; the roster is not a subscriber (ADR §8's "released for the next sim day" is the trigger core's date rule, §2).

### Where it can break (pathway 1)

- **[M] Session rotated before the leg is known to start.** `HandleLegBegan` calls `_eventLog.StartNewSession()` (:357) before `StartNextLeg()` (:358); `StartNextLeg` returns null when the day is not open/Turnaround — it is guarded by the `day.State == Turnaround` branch, so today it cannot return null, but a future guard in the engine silently rotates the log with no leg. L today, noted for the order.
- **[M] Scenario plan is lost on the normal turnaround order** — see §5; the `LegBegan`/new-plan reset interplay.
- **[S/M, unverified] A walk done between Shutdown-finalised and the next OFP is not persisted.** The `walkaround` part is not `PersistsAcrossLegs`; after `OnShutdownFinalized` writes are suspended for every non-boundary part until a new request id or PushbackAndStart (FlightStateSnapshotService.cs:393–404, :513–520). Ground prep has no plan gate (audit-gsx §A1 stage 1), so a turnaround walk can run before the plan lands; a restart then finds `walkaround` absent → `AdoptRestart(null)` = StillPossible → a second walk this turnaround (WalkaroundCore.cs:136–146). Reproducible only if prep completes and the walk window elapses before the plan — needs a recording.
- **[M] Route-identity plans.** `FlightStateSnapshotRules.Validate` only discards a boundary-only document on a differing `currentRequestId` (FlightStateSnapshot.cs:150–159); a plan with no request id (route identity) already in `OfpStore` at restore time is overwritten by `LegBoundaryService.Restore` (:139–148) with the flown plan id — the leg-began edge for that plan never fires (the store will not change again). L in practice (SimBrief plans carry a request id).
- **[M] `OnFmsCleared` trusts `Data.IsValid` rather than flight-live** (:216). ProSim pushes plausible cold-and-dark data with no MSFS session (CONTEXT.md "Flight live"); an IOS Reset FMS with the sim down is counted as the step. Arguably right for a turnaround at a powered-down sim; recorded as `accepted:true` either way.
- **[L] Two `RestoreOrder=5` parts** (`notifications`, `turnaround`) are ordered by key — fine, but implicit.
- **[L] `_coldAndDarkSinceUtc` at process start** is "now" when the phase is already ColdAndDark (:225); a restart in the dark re-waits the full 10 min. Intended per the comment.
- **[L] `TryEndDay("idle")` speaks the debrief at Low** — `QuietCockpitRule` drops Low while "quiet please" is latched (audit-speech G12); the day still ends. Deferred by the hold is not an issue (exempt tag).

---

## 2. FO roster (#194 narrowed by #219)

Services: `FoRosterService` (Core/Persona/FoRosterService.cs) over `fo-roster.json` (user config) + `fo-roster-state.json`; `FoRosterTriggerCore` (Core/Persona/FoRosterTriggerCore.cs, pure); `FoRosterVoiceFeature` (Speech/Persona/FoRosterVoiceFeature.cs, startup module + voice feature, precedence 305, gated by `persona.roster.mode`).

1. Triggers → `Evaluate(preflightEdge, legBegan)` (:176): `Start()` once (mid-day start), every `DayStatusStore.Changed` (the day's 60 s tick refreshes the store — so this runs at least once a minute while a day is open), the committed Preflight edge, `LegBegan`.
2. Inputs (:245): roster active (persona on + mode daily/pinned), pinned, `OpenDayLegsCompleted` from `DayStatusStore`, `LegPlanned` = `LegBoundaryStore.PlanId != null && State == None`, `InPreflight`, `SimLocalDate` = ProSim sim clock UTC + (local − zulu) offset from `ISimLocalTime`, else the SimVar local date, else the wall UTC date logged once (:221–243, :252).
3. Rule (FoRosterTriggerCore.cs:64): opportunity = `LegBegan || (InPreflight && LegPlanned)`; `dayPinsFo` = open day with ≥1 leg completed and an FO; stamp `sim:yyyy-MM-dd` ≠ `DrawnFor` → `NewStamp`. Intro due when enabled, in Preflight, a stamp exists, intro pending, FO present.
4. Draw: pinned → `AdoptDay(stamp)`; else `FoRosterService.Draw(reason, now, stamp)` → `FoRosterDraw.Pick` (never the previous when the pool allows) → state file → event **`persona.rosterDraw`** `{id, reason, name, experience, formality, chattiness, confidence, hoursOnType, voice, previousId, drawnFor, at}` → `Changed`.
5. Stamp on the leg: `persona.foOnDuty {id, name, drawnFor}` on every Preflight edge / leg-began evaluation (:199–202).
6. Gate intro: `SpeakIntro` (:273): `MarkIntroSpoken` first, `TimeOfDayGreeting.Word` (one source, §3), `IntroText` → `StyledSpeechService.StyleAsync(Advisory)` → **Low, TTL 5 min, tag `persona.gateIntro`** (held by the dialogue hold, dropped by quiet/sterile); event `persona.gateIntro`.
7. Voice: `SpeechArbiterService.FirstOfficerVoiceOrNull()` reads `IFoRosterService.Current.Voice` and `RoleVoiceResolver.Resolve(role, voices, firstOfficerVoice)` puts it in the voice-override slot (SpeechArbiterService.cs:402–409); `PersonaService` reads the drawn record as the effective persona for every LLM composition (PersonaService.cs:57).
8. Voice commands: "who's flying with me today" (Normal, tag `persona.roster`); "new first officer" → `FoRosterChangeStore.Ask()` captain's question (mic owner `foRosterChange`) → yes → `Draw("voice")` + intro (`trigger:"change"`); nobody yet → draw outright.
9. Pin/Release (web Roster card on SpeechSettings.razor) also write `persona.roster.mode` through `SettingsWriter` (FoRosterService.cs:253–268); events `persona.rosterPin` / `persona.rosterRelease`.

### Where it can break

- **[M, unverified] Fallback-date stamp then sim-date stamp = two draws on one day.** When the ProSim clock is not live at the first opportunity, the stamp is the wall UTC date (:78, :266). `DayStatusStore.Changed` re-evaluates every minute; once the sim clock comes live with a different calendar date the stamp differs, `dayPinsFo` is false on leg 1, and `InPreflight && LegPlanned` still holds → a second `Draw` and a second intro. Depends on `ISimClock.SimUtcNow` being null before ProSim connects (not read).
- **[M] "Never mid-day" relies on the Day feature.** `dayPinsFo` needs an OPEN day with a completed leg (:75). With `day.enabled=false` (user choice) a sim-date rollover in a turnaround draws again at the next plan — the EHAM case the rule was written for. By design per ADR §8 ("one FO per sim calendar day"); worth a probe note.
- **[L] The intro is Low** behind every operational line and the dialogue hold; TTL 5 min can expire under a long briefing.
- **[L] `Evaluate` runs under `_gate` and calls `_roster.Draw` → `Changed` → subscribers (web, PersonaService) on the same thread.

---

## 3. Cruise small talk (#195; greeting band #229)

Shell `CruiseSmallTalkService` (Speech/Persona/CruiseSmallTalkService.cs, 30 s timer), core `SmallTalkCore` (SmallTalkCore.cs), bank `SmallTalkBank`.

1. `ProcessTick` (:243): `smallTalk.enabled` (default false) → `BuildTick` (:276): phase, `IsLive`, MSL altitude + validity, `QuestionOpen` = any `ICaptainsQuestionStore.Asking.QuestionStands`, `AbnormalOpen` = `FailureMonitor.ActiveAbnormalTitles.Count > 0`, turbulence level, `_lastSpeechUtc` (arbiter `Observed` Spoken/Segment of ANY speaker, :212–218), region facts' `NextDueUtc`, `PersonaService.Chattiness` (0 once "quiet please"), `Sterile = SterileCockpitRule.IsSterile(options, context)`.
2. `SmallTalkCore.Due` (:142): sequence drawn once at cruise entry from the enabled kinds (Crew only with a roster FO); every gate holds the line: enabled, live, Phase == Cruise, altitude valid and > `speech.sterileCockpitCeilingFt` (10 000) AND not sterile (belt and braces), no abnormal, no question, turbulence < Moderate, count < min(maxPerFlight 3, chattiness), quiet gap (`minQuietMinutes` 8) since cruise start AND since the last speech, ≥ 3 min clear of the next region fact, `_notBeforeUtc`, sequence non-empty.
3. `Claim` → `DeliverAsync` on a task: bank line as the floor; fact sheet + allowed numbers; LLM when configured/healthy; `NumberVerifier` on the model's sentence; `StyledSpeechService` for the bank line.
   - **Offer**: `SmallTalkOfferStore.Ask(spoken)` captain's question (mic owner `smallTalkOffer`, voiced by `CaptainsQuestionDialogue`); yes → after 2–4 min `_cabin.AnnounceAsync("cabin.smalltalk.coffee", PurserCoffeeLine, foAck)` (:572); event `smalltalk.reply {kind:"offer", heard, source, line}`.
   - **View / Crew**: one line, **Low, TTL 90 s, `IsStillValid: Phase == Cruise`, tag `smalltalk.{kind}`** (:665–668).
   - **Layover**: line, then `ListenOnceAsync` borrows the mic (`"small-talk"`, 10 s; refuses when borrowed) → one follow-up (`smalltalk.{kind}.reply`), floor "Good to know, captain."
4. Nothing spoken → `Give` → `SmallTalkCore.Release` (kind back to front, count returned, retry in 2 min).
5. Events: `smalltalk.spoken {kind, source, foId, text, phase, altitudeFt, chattiness, countThisFlight, town, destination}`, `smalltalk.reply`.
6. Reset: `OnPhaseChanged` → ColdAndDark, or Shutdown/TaxiIn → Preflight (SmallTalkCore.cs:111) — not `FlightCycleReset`.
7. **Greeting band (#229)**: `TimeOfDayGreeting` (Core/Aircraft/SimLocalTime.cs:135–160) is the one source — `Word` (morning 04–11:59, afternoon 12–17:59, evening otherwise), `IsMorning`, `Greeting`; consumers: `SmallTalkContext.Morning` (CruiseSmallTalkService.cs:593), the briefing (`BriefingService.cs:490`, `BriefingComposer.FixGreeting`), the gate intro (FoRosterVoiceFeature.cs:276), the radios check-in (FoRadiosService.cs:555). Probe `greeting-band-one-source`.

### Where it can break

- **[L] Three sterile readings** for one decision (own MSL check, `tick.Sterile`, and the arbiter's rule on the Low line) — intentional belt and braces; `SterileCockpitRule.IsSterile` is only true in the four airborne phases below the ceiling (SpeechSuppression.cs:105–111), so in Cruise only the MSL check bites.
- **[M] Phase-only reset** means a NewPlan `FlightCycleReset` at the gate does not re-draw the sequence — harmless (cruise only), but the once-per-flight count is a cabin latch the region facts reset the same way, so consistent.
- **[L] Offer and layover can both borrow**: the offer goes through the shared dialogue (borrow `smallTalkOffer`), the layover borrows `small-talk` itself — a dialogue hold from the offer defers other Low/Normal lines for its listen window only.
- **[L] `QuestionOpen` includes its own offer store** — a pending offer blocks the next kind until the TTL; intended.

---

## 4. Walkaround (#196, restart rule revised by ADR-0019 §9 / #220)

Shell `WalkaroundService` (Speech/Crew/WalkaroundService.cs, 1 s timer, `IFlightStatePart "walkaround"`), core `WalkaroundCore` (WalkaroundCore.cs), findings `WalkaroundFindings` / `WalkaroundFindingBank` (Core/Crew, `walkaround-findings.json`).

1. `BuildSample` (:317): phase, `IsLive`, ground facts `Parked`/`EnginesOff`, `prepComplete = DiagnosticsSnapshot.GroundPrep.Stage == "Complete"` (string), refuel row stage and boarding row stage from the diagnostics `ServiceBoard`, `BoardingObserved = BoardingStarted(stage)` (Called/Requested/Active/Completed — seeded Completed included, WalkaroundCore.cs:126).
2. `WalkaroundCore.Evaluate` (:172): ready = live ∧ `IsAtGate` (ColdAndDark/Preflight/Departure) ∧ parked ∧ engines off ∧ prep complete ∧ ¬boarding observed; the **ready edge** (`_lastReady == false && ready`) with `walkaround.enabled` (default false) → **Announce**: `FoAvailabilityStore.SetAway(WalkaroundReason)`, event `walkaround.started {plannedMinutes, phase}`, **Low, TTL 2 min, tag `walkaround.announce`** (:357–362). Away window min 6 / max 10 min.
3. While Departed: refuel Called/Requested/Active or ¬ready → **Abandon** (`_availability.Clear()`, `walkaround.abandoned`, Low tag `walkaround.abandon`); else at `ReportDueUtc` → **Report** (`ReportAsync` :394): `WalkaroundFindingDraw.Draw(bank, conditions, findingChance 0.15, allowNoGo)`; with `onInterphone` (default true) `RingInterphoneAsync`: MECH press `_dataRefs.PressMomentaryAsync(OhCallsMech)` (a cockpit write — pre-existing upcall path) and `IAcpChannel.AwaitReceiveAsync(Intercom, grace)`; **Normal, TTL 3 min, tag `walkaround.report`, Role FirstOfficerOnInterphone**; `_availability.Clear()`; event `walkaround.reported {durationMinutes, finding, severity, onInterphone, text}`.
4. `HandleFindingAsync`: cosmetic → `walkaround.finding {outcome}`; deferrable → `_raise.Ask(id, title, category)` captain's question → yes → `TechLogDialogueService.RaisePrefilledAsync` (borrows `techlog:<name>`, TechLogDialogueService.cs:173) → `walkaround.techLogRaised {confirmed}`; no-go → `ITechLogService.RaiseDefect` regardless + Normal line tag `walkaround.nogo`; icing → `DeiceRequestStore.Publish(Ask, …, DeicePrompt)` (the de-ice captain's question).
5. Gates on others: `FoAvailabilityStore.IsAway` → `UtteranceRouter.SkipWhileFoAway` (audit-speech G14) and the departure pump's refuel hold (`DeparturePumpGatherer.cs:210`).
6. Reset: `GroundOpsSignals.FlightCycleReset` (either kind) → `OnFlightCycleReset` (:171): core Idle with baseline "not ready", availability cleared if away for the walk.
7. Restart: `Capture` always writes a node (:268–278); `Restore → AdoptRestart` (WalkaroundCore.cs:136): Idle → StillPossible (baseline not-ready: the ready state found at startup IS the edge), Departed → Done (no report), Reported/Abandoned → Done; event `walkaround.restored {snapshotState, outcome, state}`. Boarding-complete refusal is the `BoardingObserved` term, restart or not.

### Where it can break

- **[M] Ready reads a diagnostics string.** `GroundPrep.Stage == "Complete"` and the service-board rows come from `DiagnosticsSnapshotService`'s once-per-tick snapshot (WalkaroundService.cs:324–337), not from `DepartureCycleState.PrepComplete` / the lifecycle tracker. A label change or a collector failure (amber XX → missing node) silently disarms the walk; the audit's "collectors read their own stores, never another feature's" rule is crossed in the other direction here.
- **[S/M, unverified] Walk not persisted in the turnaround gap** — §1 above.
- **[M] StillPossible after a restart means a startup walk.** Intended by ADR §9, but the 1 s tick evaluates before the GSX resync seeds the board; `BoardingObserved` becomes true only once the seeded Completed row is published. The core's comment says the first ticks read "not ready" (prep stage arrives later) so the edge waits for prep — the ordering of "prep Complete seeded" vs "boarding Completed seeded" within one `DiagnosticsSnapshot` publish is what protects it. Needs the `walkaround-after-restart` probe on a real restart-after-boarding.
- **[L] `ReportAsync` clears availability only after the enqueue resolves** (:416) — a 3-min TTL line stuck behind a hold keeps the FO "away" for the hold's length; queries get the away line meanwhile.

---

## 5. Scenario mode (#197, ADR-0018; #228)

`ScenarioService` (Speech/Scenarios/ScenarioService.cs; startup module, hosted service, `IScenarioControl`, voice feature), pure `ScenarioScheduler` (Core/Scenarios/ScenarioScheduler.cs), catalogue `ScenarioCatalogueBank` (`scenarios.json`), `SdkFailureInjector` (Prosim/Failures/SdkFailureInjector.cs) behind `IFailureInjector`, gate `ProsimFailureGate` (Prosim/Failures/ProsimFailureGate.cs).

1. **Plan**: `OnPhaseChanged` into Preflight (the only `TryPlan` caller, :282) with `scenario.enabled` (default false) and `IsLive` → entries whose `abnormalId` the `FailureMonitor` has loaded (`Known`, :322) → `ScenarioScheduler.Plan` (:109): roll ≥ `perFlightChance` 0.10 → none; `Allowed` filters `Verified`, weight > 0, phases, Major only with `allowMajor`, takeoff entries only with `takeoffScenarios` AND the matching RTO/EFATO switch; open tech-log `wearTitle` doubles the weight; phase and moment drawn (cruise window from the OFP enroute time). Persisted (`ScenarioStateFile`), event `scenario.planned {scenario, title, phase, afterSeconds, shown}`.
2. **Tick** (1 s, `ProcessTick` :368): `ScenarioScheduler.Evaluate` (:169) on `BuildTick` (:395): phase == plan phase (takeoff plans: TaxiOut/Departure/PushbackAndStart), `FireAfterSeconds` since the phase commit, airborne AGL ≥ `minAltitudeFt` 1500, no abnormal open, no drill/ECAM dialogue (`FailureMonitor.DialogueActive`), no open category-A defect, not sterile (`SterileCockpitRule.IsSterile`). Hold reasons logged once.
3. **Arm** (`ArmPlanAsync` :431, under `_writes`): state still Planned → event **`scenario.arming`** `{scenario, title, failure, source, cancelWindowSeconds:3}` → 3 s cancel window (`scenario.armCancelled`) → `_injector.ArmAsync(failureName, condition)`: `RefreshGate()` installs the catalogue's verified names (`ProsimFailureGate.InstallCatalogue`, internal), `getFailures()` live list, `EnsureAllowed` (catalogue ∩ live) → `ArmedFailure.Create(failure)` or `Create(FailureCondition{afterEvent V1/Takeoff/Landing}, failure)` on `SdkConnection.RunAsync`; null → `scenario.armFailed {reason:"refused-or-unreachable"}` → verify: `ArmedAsync` must list it within 5 s else `ClearAsync(handle)` + `armFailed {reason:"not-verified"}` → state **Armed** (persisted) → event `scenario.armed`; `clearAfterSeconds` starts the transient timer.
4. **Fire**: the real detection path — `FailureMonitor.FailureDetected(id, title)` from the E/WD text and system datarefs → `OnFailureDetected` (:526) matches `plan.AbnormalId` while Armed → **Fired** → `scenario.fired {scenario, title, failure, abnormalId, abnormalTitle}`. The FO's response is the existing ECAM dialogue / drills (audit-speech A.2).
5. **Decision**: `FailureMonitor.AbnormalEnded(id, reason)` while Fired, once → `RaiseDecisionAsync` → `ScenarioDecisionStore` captain's question (continue/divert; LLM line verified, template floor `ScenarioScheduler.DecisionLine`) → `scenario.decision` / `scenario.decided` → `IDiversionControl` on divert.
6. **Clear**: `FlightCycleReset` (either kind) → `ClearOursAsync("flight-cycle-reset")` (handle, else clear-all) + verify via `ArmedAsync` → `scenario.cleared` / `scenario.clearFailed`, plan null; `IHostedService.StopAsync` clears when Armed/Fired ("exit"); startup `LeftArmed` → `ClearAllAsync("startup")`; a Planned plan younger than 6 h is resumed (:214). `scenario.clearAll` (voice "clear all failures", web, API) → `scenario.clearAllRequested` → every armed failure ProSim reports, IOS's included.
7. **#228** is on the detection side: `FailureMonitor` now logs a trigger condition that throws ONCE per definition (`NoteConditionFault`, FailureMonitor.cs:755–758, :934) and treats it as not met (fail-closed) until an abnormals reload; probe `abnormal-condition-fault-once` is an assert-absent on duplicate `log.warning` lines.

### Where it can break

- **[S] The turnaround leg never gets a scenario.** `TryPlan` runs only on the Preflight edge (:282); in a turnaround that edge comes 30 s after deboarding, BEFORE the new OFP. The new OFP then raises `FlightCycleReset(NewPlan)` (GsxAutomationService.cs:277) and `ScenarioService.OnFlightCycleReset` (:655) clears `_plan` without looking at `LastFlightCycleResetKind`; nothing re-plans because Preflight does not re-commit. Every leg after the first (and any first leg whose plan lands after Preflight with ADR-0019 §6's reset conditions met) is scenario-free. `FlightStateSnapshotService` and `GsxStartupResyncService` already branch on the kind (FlightStateSnapshotService.cs:537, GsxStartupResyncService.cs:371) — the scenario service should too, or re-plan on `LegBegan`.
- **[M] App started in Preflight = no plan.** No edge → no `TryPlan`; only a persisted Planned plan < 6 h is resumed. Probe `scenario-fires-and-detects` will read "untested" on such flights.
- **[L] One `ArmPlanAsync` task per tick** while the first holds `_writes` through its 3 s window + verify (:380–386); each later one returns false at the state check. Bounded (~8 tasks), noisy only.
- **[L] `ClearAsync` on an SDK session rebuilt since the arm** uses the stale `ArmedFailure` object (SdkFailureInjector.cs:120) — the class comment promises a re-read; `ClearOursAsync` verifies through `ArmedAsync` afterwards and reports `clearFailed`, so it degrades visibly.

---

## 6. FO altitude from the PFD + transition reminders (#198)

- **Spoken altitude**: `ProsimFlightDataSource` subscribes `AltitudeIndicatedCaptain` / `AltitudeIndicatedFo` (Prosim/Flight/ProsimFlightDataSource.cs:132–133) and fills `IndicatedAltitudeCaptFt`, `IndicatedAltitudeFoFt`, `IndicatedAltitudeFt` (the pilot's side by seat, :252–255). `FlightDataSnapshot.SpokenAltitudeFt => IndicatedAltitudeFt > 0 ? IndicatedAltitudeFt : AltitudeFt` (Core/Flight/FlightDataSnapshot.cs:44). Consumers: `FoFactSheet`, `CabinCrewService`, `PurserPaService`, `CalloutsEngine`, `TransitionReminderService`, `FoRadiosService` (check-in, :559). The phase engine keeps `AltitudeFt`.
- **Transition reminder**: `TransitionReminderService` (Speech/Callouts/TransitionReminderService.cs, 1 Hz after 2 s) → `ProcessTick` (:119): `sop.transition.enabled` (default **false**, SopOptions.cs:161), `live = IsLive && sample.IsValid`, `RefreshNavData` (DFD transition altitude for the OFP origin, transition level for the destination; none configured → one warning, nothing said), `PilotOnStd` from the seat-mapped `Efis1BaroStdGate` subscription (null until read), destination QNH from the weather card → `TransitionReminderCore.Process(input, delay 30 s)` (TransitionReminderCore.cs:70): Standard due when climbing (InitialClimb/Climb/Cruise), not on STD, PFD > TA; QNH due when Descent/Approach, on STD, PFD < TL×100; each once per crossing after the patience delay; re-armed by the opposite phase family. Output **Normal, TTL 20 s, tag `transition.reminder`** (exempt from the dialogue hold), event `transition.reminder {kind, altitudeFt, thresholdFt, qnhHpa, delaySeconds, origin, destination, text}`. Reset on `FlightCycleReset`.

### Where it can break

- **[L] `IndicatedAltitudeFt <= 0` disarms the core** (TransitionReminderCore.cs:73); the fallback to true altitude covers unread PFD refs, but a true altitude ≤ 0 (EHAM −11 ft) is only on the ground, where no reminder is due.
- **[L] QNH from the destination METAR card** — with no card, "Transition level. QNH, please." (no figure); correct degrade.
- **[M, unverified] `PilotOnStd` is the pilot's seat-side STD gate**; the FO-side baro follow (CONTEXT "Baro follow") writes the FO's side — no shared write, but two readers of seat mapping; a wrong `rightSeat` option flips both.

---

## 7. FO works the radios (`FoRadiosService`, Speech/SayIntentions/FoRadiosService.cs; `ReadbackComposer` / `CheckInComposer` / `ClearedLevelParser` / `RadioSpeech` in FoRadiosComposer.cs; `RadioClearGate`)

Gates: `sayIntentions.enabled && foRadios.enabled` (both default false), `CaptainOwns` ("I have the radios"), SAPI key present, flight.json `IsActive`.

1. Trigger: `SayIntentionsFrequencyMonitor.AtcTransmissionObserved` (SAPI comms-history poll) → `OnAtcTransmission` (:289): stamps `_lastAtcLineUtc` for non-broadcasts, parses a cleared level, captain owns → `sayintentions.readback {kind:"dropped", reason}`.
2. `ReadbackComposer.Compose(text, callsign, station, ourAtisLetter)` (FoRadiosComposer.cs:100): broadcast ("All aircraft…") → null; the **ATIS question** clause is removed and answered from the held letter (`AtisAnswer`: "information X" / "negative, we have Y" / "negative, request X"); hand-off → `Readback(Handoff, station, khz)` "Contacting X on Y."; clauses split, noise/greetings/station names stripped, ATC's own intentions (`Advisory()`) dropped but remembered → no instruction left: ATIS answer alone, else **"Roger." `Acknowledge`** when an advisory was dropped, else null (nothing to read back) → `sayintentions.readback {kind:"none"}`.
3. `RespondAsync` (:364) under `_transmitGate` (one transmission at a time): `TransmitAsync` (:520): **radio-clear gate** `RadioClearGate.WaitForClearAsync` (RadioClearGate.cs:68: `L:SIAI_COM1_RECEIVING` and `L:SIAI_RADIO_PTT` both < 0.5 for 400 ms, poll 100 ms, **cap 20 s** then transmit anyway with a warning; stale values count as clear; no `ISimVars` → fixed 400 ms) → if `foSpeaksTransmission` the FO voice **Normal, TTL 30 s, tag `sayintentions.tx`** (awaited, exempt from the hold) → `SapiClient.SayAsAsync` → event `sayintentions.readback {payload{atc, kind, transmit}, spoken, radioWaitMs, radioBusyAtTx, sent}`.
4. Hand-off only: wait for clear again, then the 3 s `HandoffTuneGrace` measured from the read-back (:402–408) → `tuneOnHandoff` (default true) → `IRadioTuner.TuneCom1Async(khz, "handoff")` (standby → verify → swap; the one aircraft write on this path) → failure: "Could not tune …, the radios are yours." (Normal, `sayintentions.tx`) and stop.
5. Check-in (`checkIn` default true): `WaitForCom1Async` until COM1 active reads the frequency (≤ 60 s, else `sayintentions.checkin {sent:false, reason}`), `checkInDelaySeconds` (4, clamped 3–30), abort if the captain took the radios or a newer hand-off superseded (`_handoffSerial`), skip if ATC already spoke after the tune (:450) → `CheckInComposer.Compose(context)` with the greeting (`TimeOfDayGreeting.Greeting`), callsign, phase, `SpokenAltitudeFt`, cleared level, SID/STAR via `ISpokenText.Procedure(ident, ofpAirport)` (#202), departure-runway distance for "at the holding point" → `TransmitAsync` → `sayintentions.checkin {payload{station, khz, transmit}, …}`.
6. Tick (15 s): flight.json radio context (new flight clears `_clearedLevel`), `SIAI_COPILOT=0` re-asserted every 2 min via SAPI, departure runway geometry primed on the ground.

### Where it can break

- **[M] A hand-off serialises everything behind it for up to ~64 s** (`_transmitGate` held through `WaitForCom1Async` 60 s + delay, :381–479); an ATC instruction arriving meanwhile is read back only afterwards. The `_lastAtcLineUtc > tunedAt` skip (:450) protects the check-in, not the delayed read-back.
- **[M] Ordering assumption: the hand-off read-back precedes the retune by ≥ 3 s** — now enforced (:402–408); but when `foSpeaksTransmission` is on, the FO's local line is awaited BEFORE sayAs (:525–529), so the grace is measured from before the local playback — fine, it only lengthens the gap.
- **[L] `RadioClearGate.IsBusy` false on stale L:vars** — with MSFS down the FO never waits; intended degrade.
- **[L] `OurAtisLetter` on the ground reads the Local card** (:589) — before Descent the destination card is "weather at destination" (Second); ATC asking for the destination ATIS while on the ground at the origin gets the origin's letter. Rare.
- **[L] The `Acknowledge` "Roger" is sent for any dropped advisory even when the remaining clauses were pure noise** — by design (owner report 2026-11-01).

---

## 8. Briefing dialogues + dialogue hold (#201)

- **Hold rule**: `DialogueHoldRule` (Speech/Arbiter/DialogueHoldRule.cs) defers a request when `Priority < High`, it has a tag, the tag is not in `ExemptPrefixes` (`sayintentions.`, `fo.`, `briefing.`, `checklist.`, `ecam.`, `abnormal.`, `minima.`, `transition.`, `clarifier`, `confirm`, `day.debrief`; :29–30) and the accessor is true. **The self-deadlock fix**: the accessor is `mic is { IsBorrowed: true, IsListening: true }` (SpeechArbiterService.cs:100); `MicOwnership.IsListening` is 1 only inside `ListenAsync` (MicOwnership.cs:105, :143), so a dialogue that has borrowed the mic but is still speaking its own tagged Normal question (the clarifier of EGLL 2026-11-01) is never deferred by its own borrow. Deferred items are re-judged on every dequeue pass (`SpeechArbiterCore.ReadmitDeferred`, SpeechArbiterCore.cs:275: expired → Expired, `IsStillValid` false → Dropped, still suppressed → Suppressed, else readmitted to the tail).
- **Listen window**: `MicOwnership.ListenAsync` (:86) counts no time while `SpeechStatusStore.NowPlaying` is set (250 ms slices, hard cap timeout + 60 s), so the FO's own lines never eat the pilot's window.
- **Briefing dialogues** (`BriefingService`, Speech/Briefings/BriefingService.cs): after the narration (streamed or whole, :240–270; events `briefing.spoken`, `briefing.emergency`, `flight.route`) → `AskForThreats` (departure, emergency section present, `briefing.askForThreats`) → `ThreatsQuestionStore.Ask()` → `AskForQuestionsAfter` (:335): if threats were asked, subscribe once to `Answered` and raise the questions round after (with the "answered between raise and subscribe" check :352–357) → `BriefingQuestionStore.Ask(departure)`. Both questions are voiced by the one `CaptainsQuestionDialogue` (borrow by `MicOwner`, prompt **High via `SpeakAsync` untagged**, listen, repeat/unclear prompts High, unanswered phrase Normal — CaptainsQuestionDialogue.cs:140–221); the answers themselves ride `fo.*` tags (exempt).

### Where it can break

- **[M] Exempt-prefix taxonomy is string-typed and grows per incident** (audit-speech F already flags it); the branch added `transition.` and `day.debrief`. The FMS reminder `fo.fmsReminder` and the cross-check `fo.crossCheck` are exempt by the `fo.` prefix although they are not dialogue lines — they will talk over a captain's-question listen.
- **[L] `CaptainsQuestionDialogue` prompts are High and untagged** — never held, never deferred; a question raised DURING another dialogue's listen is serialised by the borrow (`Borrow` throws when held, MicOwnership.cs:73) — the dialogue runner must retry; not read here (unverified).
- **[L] `ThreatsQuestionStore.Answered` once-subscription** leaks if the store is reset by `FlightCycleReset` without an `Answered` (the TTL does answer; the cycle reset withdraws — unverified whether `Withdraw` raises `Answered`).

---

## 9. Phraseology (#200)

`Core/Speech/Phraseology.cs` (`Normalize` :169; passes: letters-in-context → numbers → abbreviations; idempotent). **Apply point**: `SpeechArbiterService.SynthesizeAndPlayAsync` (:390–418) — `spoken = Phraseology.Normalize(text)` BEFORE the TTS cache text and the provider call, for every render path (plain item :299, streamed sentence :502, :552); the arbiter's `Observed` event text is normalised the same way (:612) so the self-echo guard compares like with like. `TtsPrewarmService` normalises its phrases before warming (:133, :163) — consistent with the cache. Source-side renderers (`ISpokenText`, `NatoPhonetics`, `RadioSpeech.Spoken`) still render SIDs, fixes, ICAO codes before the net.

**Callers that bypass it** (text reaching a TTS provider without `Normalize`):
- `SpeechDiagnosticsService.cs:148` — the voice test sample (`provider.SynthesizeAsync(sampleText)`): intended, a fixed sample.
- The SayIntentions `sayAs` transmit text (`line.Transmit`, FoRadiosService.cs:529) goes to SI's own TTS un-normalised; the locally spoken copy is `RadioSpeech.Spoken` + the arbiter pass — by design (ATC's engine wants digits).
- Nothing else calls `SynthesizeAsync` (grep), so there is no FO line that skips the table.

### Where it can break

- **[L] English-context regexes** ("taxiway", "via", "hold short of") — the FO is English-only by ADR-0014, fine.
- **[L] Normalize runs on LLM sentences after `NumberVerifier`** — the verifier checks digits on the pre-normalised sentence; Normalize only re-spells, never changes a number, so the allowed set holds.

---

## 10. GSX (#225 #230 #207/#213 #209-family #210)

- **Jetway/stairs retraction latch (#225)**: `GsxJetwayStairsService` is the one `IGsxJetwayRetraction` (Gsx/Sync/GsxJetwayStairsService.cs:19–22); every retraction sender — the beacon sequence's `RetractJetwayStairsEffect`, the on-final hook, `RemoveStairsAfterDeparture` (:340), and the crew's `GsxServiceControl.RetractJetway/RetractStairs` (GsxServiceControl.cs:570 → `RetractAsync` :366) — passes `DispatchRemovalAsync` (:381): `TryReserveRetraction` per-service stamp (90 s `RetractionRepeatGuard` as the safety valve, :43, :428) → Busy naming the service when already sent → else trigger slot `TryDispatchAsync(NoConfirm=true)`; not dispatched → `ReleaseRetraction`. Latch released by evidence: mirror `services` update where the service no longer reads Active/Completed (:130–157), a connect re-arms it (:472–474), `FlightCycleReset` (either kind) and a GSX restart / new gate context clear it. The slot itself now refuses a same-service toggle inside `NoConfirmSpacing` 2 s (GsxTriggerSlot.cs:112–140). Probe `gsx-toggle-retraction-once`.
- **`CompanionTrackingLvars` (#230)**: the one writer of `L:PROSIMCOMPANION_*` (Gsx/Sync/CompanionTrackingLvars.cs): `MarkServiceDoneAsync`, `MarkPrepDoneAsync`, three bulk resets — `ResetForArrivalAsync` (TURNAROUND=1, rest 0), `ClearAllAsync` (stale clear, all 0), `ClearDepartureSideAsync` (departure one-shots + loadsheet editions 0; deboarding/GPU/turnaround/prep-done kept). A write that throws `InvalidOperationException` (no SimConnect) is queued latest-value-per-LVAR (:163–173) and flushed on the `ConnectionStatusStore` SimConnect disconnected→connected edge (:187–219). `IsSimConnected` lets the cycle-reset event say "cleared" vs "queued until MSFS connects" (GsxAutomationService.cs:294–297). Probe `gsx-new-plan-tracking-clear`.
- **New-plan departure-cycle reset (#207/#213/#223)**: `GsxAutomationService.OnOfpChanged` (:239) → `NewPlanCycleReset.ShouldReset(current, lastResetPlanId, cycleWasComplete (read BEFORE the reset, #223), boardingObserved = lifecycle `ObservedProgress(Boarding)` (seeds do not count), data)` (NewPlanCycleReset.cs:37: different plan id, parked with engines off, and complete-or-boarding-not-started) → lifecycle `ResetCycle(ArrivalSide)`, `BeginTurnaroundCycle`, pax target reset, slot reset, `ClearDepartureTracking()`, event `gsx-cycle-reset {reason:"new flight plan", planId, cycleWasComplete, flightCycleReset:true, trackingLvars}`, **`RaiseFlightCycleReset(NewPlan)`** (:277) → gate monitor, loadsheet editions, upcalls, notifications, walkaround, captain's-question stores, scenario (§5 problem), jetway latch, transition reminder all reset; `FlightStateSnapshotService` and the startup resync skip their arrival-only work for this kind.
- **Gate monitor boarded-count trust**: `GateMonitorCore` (Core/Boarding/GateMonitorCore.cs:92–163): a Completed boarding over an EMPTY cabin at the gate is `staleClose` (no evidence, undoes a latched close); a ClosedAfterBoarding strip at the gate over an empty cabin with no boarding running resets (:130–140, the restart hole); the boarded count becomes the **baseline** when boarding is Called/Requested (:153–158) and is trusted for the thresholds only in Active/Completed once it has moved off the baseline (:160–163). `CabinEmpty` comes from `PaxSeatOccupationString` (GateMonitorService.cs:85, :181); the code cites #213 for all three rules (the task's "#209" is not referenced in code — unverified mapping).
- **Restart after boarding → Preflight → Departure arming** (commit 9cbd82bc): `FlightStateEngine` now also subscribes `BoardingCompleted += NotifyBoardingStarted` (FlightStateEngine.cs:236, :247); after a restart the GSX mirror's first observation of Boarding `Completed` raises a lifecycle Completed edge (`GsxServiceLifecycleTracker.Process`, :131–137) → relay → `RaiseBoardingCompleted` → the `boarding` rule (FlightPhaseRules.cs:203) takes Preflight → Departure. The LVAR **seed** is event-silent (GsxServiceLifecycleTracker.cs:177–196), so this only works while GSX still reports Completed (not after it flipped back to Callable) — unverified live. Separately `RestoreCompletedCycleFromSeeds` (GsxAutomationService.cs:321–339) marks the cycle started+complete when every configured service is seeded, pins `_lastResetPlanId`, and raises `DepartureServicesCompleted` ("ready for pushback" notification §11, event `gsx-departure-complete {reason:"restored by the startup resync"}`).

### Where it can break

- **[M] Two readers of "boarding started" disagree after a restart.** `NewPlanCycleReset` reads lifecycle `ObservedProgress(Boarding)` (a mirror-first-observation Completed counts as observed), the gate monitor reads `staleClose` (empty cabin → not evidence), the walkaround reads the diagnostics board stage (seeded Completed counts as started). For the EHAM case (restart at the gate, GSX still showing leg one's Completed, cabin empty) the gate monitor resets, the walkaround refuses, and the new-plan reset is allowed only if `cycleWasComplete` (every configured service seeded) — otherwise the departure cycle is NOT reset for the new plan. Needs a restart recording with partial seeds.
- **[M] `RetractionRepeatGuard` 90 s vs mirror lag**: a retraction GSX silently ignored keeps the latch for 90 s while the mirror still reads Active; a legitimate second toggle within that window answers Busy. Documented safety valve; probe covers the opposite case only.
- **[L] `OnDutyDayEnded` does not raise `FlightCycleReset`**; notification/gate-monitor/question latches survive the end of duty until the next (different) plan's NewPlan reset. ADR §8 lists only the snapshot and the departure cycle, so by spec.
- **[L] The tracking-LVAR queue is latest-value-per-name across operations** — an arrival reset queued, then a new-leg clear queued, flush in dictionary order (not issue order); values coincide for overlapping names (0) except TURNAROUND, which only the arrival reset writes. Safe today.

---

## 11. `NotificationSignalSource` rewrite (#151/#167)

Signals consumed (Core/Notifications/NotificationSignalSource.cs:105–117): `GroundOpsSignals.RefuelCompleted` → `RefuelComplete` (fuel figure from `FuelTotal`), `BoardingCompleted` → `BoardingComplete`, `DepartureServicesCompleted` → `ReadyForPushback`, `CabinSecured` → `CabinSecure`, `TodApproaching` → `TopOfDescentApproaching`, `ArrivalCompleted` → `DeboardingComplete`, `FlightCycleReset` (either kind) → clears `_firedThisCycle`, `_lastFinalEdition`, `_holdoverWarned`; store observers: `LoadsheetStore` (final edition edge → `FinalLoadsheetSent`, re-armed per edition), `FlightTimesStore` (landing stamp edge → `Landed`, on-blocks edge → `OnBlocks` — edges, not levels), `DeiceHoldoverService.Changed` → `DeiceHoldoverExpiring` (once per holdover). Every milestone is once per cycle via `Raise` (:370) → `NotificationDispatcher.Enqueue` → the targets (ntfy / Discord / webhook). Snapshot part `notifications` (RestoreOrder 5): fired set, last final edition, holdover flag; milestones raised within the first 15 s (`RestoreSettle`) before the restore lands are **held** (`_pending`) and released by `Restore` or the settle timer (`FlushPending`, :404), each re-judged against the restored set — the EGLL 2026-11-01 double "boarding complete".

### Where it can break

- **[L] Boundary-only snapshot**: the `notifications` part is absent, `Restore` never runs, the settle timer releases the held milestones at 15 s with an empty set — correct (a new leg).
- **[L] NewPlan kind clears the fired set while the file keeps the old leg's set until the next part change** — `OnFlightCycleReset` raises `Changed` (:307) → `OnPartChanged` sees the new request id → file cleared and rewritten. Consistent.
- **[M] `ReadyForPushback` from `RestoreCompletedCycleFromSeeds`** (§10) raises `DepartureServicesCompleted` on every restart where all services are seeded — the once-per-cycle set restored from the snapshot is what stops a second notification; with no snapshot (boundary-only or discarded) it fires again. Covered by the 15 s hold only if the resync lands inside it (resync has a 90 s window — audit-gsx stage 0), so a late resync re-sends "ready for pushback" after a restart. unverified live.

---

## 12. Settings hub search (#199)

`SettingsSearch` (Web/Layout/SettingsSearch.cs) + `SettingsSearchService` (:264). **Yes, it indexes the language packs**: `Merged(pack)` = every English string overlaid by the language's strings (:284–304) from `LanguagePackCatalog`; `Build` (:118) adds every rail group/item by its translated label (`SettingsNav.Groups`) and one entry per **card** — a second-level pack block `root.block` (or a third-level block that has its own `.title`), haystack = title + every text of the block lower-cased; `Locate(block)` maps the block to its rail place through `Roots` (pack root → route base / group / item, :34–51) and `BlockSections` (block → section key, :55–109), else a section named like the block, else the page root; `SkippedBlocks` drops shared wording. `Find` (:222) requires every query word (≥ 2 chars) in the haystack, ranks nav label 0 < title match 1 < text match 2, then rail order, then title; max 12. Called from `SettingsLayout.razor:169/208` on each keystroke and on `UiLanguage` change; the per-language index is cached by `(code, LanguagePack instance)` and rebuilt when the catalogue rescans. A source-scanning test keeps `BlockSections` honest against the Razor `CurrentSection` branches (per the class comment; test not read).

### Where it can break

- **[L] Cache keyed by pack instance** (:267, :280) — every catalogue rescan adds a new index and never evicts the old; bounded by rescans, not by time.
- **[M] New pack root or page = silent no-hit.** A page whose pack root is missing from `Roots` has its cards dropped by `Locate` returning null (:167–170); the guard test covers sections of known roots only (unverified).
- **[L] Non-settings roots** (`flightStatus`, `layout`, …) are intentionally not indexed; a user searching a Flight Status term gets nothing — by the CONTEXT.md definition (settings rail only).

---

## Cross-pathway section

### Shared gates — who consults what, and whether the same way

| Gate | One source | Consumers on this branch | Same way? |
|---|---|---|---|
| Flight live (`IFlightPhaseSource.IsLive`) | engine | Small talk (`tick.Live`), walkaround (`sample.Live`), scenario (plan + tick), transition (`IsLive && sample.IsValid`), roster (no — uses `InPreflight`), leg boundary (no — `Data.IsValid` + parked), day (no), radios (no — flight.json `IsActive`) | Four early-return copies (audit-speech G13 pattern); the FMS-clear step and the day engine deliberately run without it |
| Sterile cockpit | `SterileCockpitRule.IsSterile(options, context)` static (SpeechSuppression.cs:105) | small talk (plus its own MSL ceiling), scenario, the arbiter rule on every line | Same static; small talk double-checks the ceiling by design |
| Dialogue hold | `DialogueHoldRule` with `IsBorrowed && IsListening` | every speaker through the arbiter; **exempt**: `transition.`, `fo.` (FMS reminder, cross-check, FO answers), `sayintentions.`, `day.debrief`, `briefing.`, untagged High prompts; **held**: `smalltalk.*`, `walkaround.*`, `persona.gateIntro`, `persona.roster`, `day` (sector/summary lines), GSX upcalls | One rule; the taxonomy is the only place a new tag is classified |
| Mic borrow (serialised dialogues) | `MicOwnership.Borrow` (throws when held) | `CaptainsQuestionDialogue` (end-of-duty, small-talk offer, roster change, threats, briefing questions, walkaround raise, de-ice, scenario decision), small talk layover (`small-talk`), tech-log raise (`techlog:*`) | Small talk catches the throw and skips the listen (CruiseSmallTalkService.cs:484–490); whether the shared dialogue retries a refused borrow is unverified |
| Phase edges | `IFlightPhaseSource.PhaseChanged` | Preflight edge: roster intro, scenario plan, day auto-start; Cruise: small talk; TakeoffRoll/InitialClimb/Climb: leg-boundary `Airborne`; ColdAndDark: day offer clock, snapshot boundary rewrite, small talk reset; Shutdown: day `CompleteLeg` | Each subscriber filters its own edge — no shared "Preflight edge" helper |
| `FlightCycleReset` + `LastFlightCycleResetKind` | `GroundOpsSignals` | Distinguish the kind: `FlightStateSnapshotService`, `GsxStartupResyncService`. Ignore it (reset on both): scenario (**bug**, §5), walkaround, notifications, gate monitor, jetway latch, transition reminder, captain's-question stores, small talk (phase-only, not subscribed) | Mixed; `LastFlightCycleResetKind` is a property read after the fact — a handler that runs on another thread could read a later kind (L) |
| `DutyDaySignals.DayEnded` | `CompanyDayService.TryEndDay` | snapshot (drop boundary), leg boundary (→ None), GSX automation (cycle reset, no `FlightCycleReset`) | Three subscribers, one raise; the roster, notifications, walkaround and gate monitor are not told |
| `LegBegan` | `LegBoundaryService` | day engine (next leg), roster (draw), FMS reminder, FMGC cross-check, cabin-empty latch | One Core event; GSX does NOT use it — it has its own plan-identity test (`NewPlanCycleReset.ShouldReset` with `_lastResetPlanId`), so "a new leg" is decided twice from the same `OfpPlanIdentity` with different extra conditions (parked, boarding) |
| Captain's question (`ICaptainsQuestionStore`) | Core stores + one dialogue | new stores: `EndOfDutyOfferStore`, `SmallTalkOfferStore`, `FoRosterChangeStore`, `ThreatsQuestionStore`, `BriefingQuestionStore`, `ScenarioDecisionStore`, walkaround raise | All reset on any `FlightCycleReset` (CaptainsQuestionStore.cs:74) — an end-of-duty offer standing when a new plan lands is withdrawn by the NewPlan reset; fine |

### Stores written by more than one pathway

- **`flight-state` snapshot file** — written by `FlightStateSnapshotService` for the parts `turnaround` (§1), `walkaround` (§4), `notifications` (§11), plus the pre-existing `ofp`, loadsheet, times, minima… parts; invalidated by: new request id (any part change), ColdAndDark commit, Shutdown finalised, `FlightCycleReset` (Arrival kind only), `DayEnded`. Five invalidation triggers with three different `(suspend, keepBoundary)` shapes — the matrix lives in one class but nothing tests the ordering Shutdown-finalised → DayEnded → new OFP end to end (recording-level check recommended).
- **`GroundOpsSignals.FlightCycleReset`** — raised by `GsxAutomationService` on the arrival phase edge (`Arrival`) and on a different plan (`NewPlan`); consumed by ≥ 14 files (audit-speech A.2) — the kind was added here; the §5 scenario miss is the one confirmed consumer that needed to care and does not.
- **`OfpStore`** — written by the SimBrief fetch and the in-flight plan-only fetch (`FlightStateSnapshotService.MaybeFetchPlanOnly`); read as the leg identity by the leg boundary, the day engine, the roster (`LegPlanned`), the GSX new-plan reset, the snapshot rules, the radios' SID/STAR airports, the scenario's enroute window — one identity function (`OfpPlanIdentity.PlanId`) in two places (`LegBoundaryService`, `NewPlanCycleReset`) is good; the snapshot service compares `RequestId` directly (FlightStateSnapshotService.cs:371–380), a third spelling of "same plan" that ignores the route fallback.
- **`FoAvailabilityStore`** — written only by the walkaround (`SetAway`/`Clear`), read by the router and the departure pump; cleared on `FlightCycleReset`, `Restore`, report, abandon and failure paths — five clears, one set; the reason string `WalkaroundReason` is the only ownership check.
- **`fo-roster-state.json`** — `FoRosterService` writes from the voice feature's draw/adopt/intro, the voice "new first officer", and the web Roster card's pin/release; pin/release also write `settings.json` (`persona.roster.mode`) — two files kept in step by one method pair (`WriteMode`).
- **`daystate.json`** — `CompanyDayService` only (leg start/complete/route/deboarded/offer/end); `ScenarioStateFile`, `FoRosterStateFile`, `FlightStateSnapshotFile` likewise single-writer. The event-log session file is rotated by exactly one caller (`HandleLegBegan`), and the Order-10/20/30/40 finalization steps read the retiring file — the rotation-before-`StartNextLeg` order (§1) is the one place that could rotate without a leg.
- **Tracking LVARs** — one writer now (`CompanionTrackingLvars`), three callers: the lifecycle Completed edge, the startup resync backfill, the three resets from `GsxAutomationService` / the resync's stale clear.

### Probes shipped for these pathways (docs/agents/verification-probes.json)
`leg-boundary-order` (#210), `fms-cleared` (#211), `day-no-phantom-leg` (#212), `new-plan-flight-cycle-reset` + `gate-monitor-restart-reset` (#213), `purser-cabin-empty` (#214), `fo-fms-still-holds` (#215), `day-end-offer` (#217), `day-end-clears` (#218), `roster-one-per-sim-day` (#219), `turnaround-restart` + `walkaround-after-restart` (#220), `fmgc-crosscheck-new-leg` (#221), `day-debrief-content` (#222), `leg-block-after-inflight-restart` + `gsx-simbrief-reload-new-plan-only` (#223), `gsx-toggle-retraction-once` (#225), `abnormal-condition-fault-once` (#228), `greeting-band-one-source` (#229), `gsx-new-plan-tracking-clear` (#230), `fo-roster-daily-draw` (#194), `cruise-small-talk` (#195), `fo-walkaround` (#196), `scenario-fires-and-detects` + `scenario-clear-on-exit` (#197), `transition-reminders` (#198), `phraseology-letters` (#200), `briefing-dialogue-hold-and-questions` (#201), `fo-radios-fix-names-region` (#202), `turnaround-new-plan-reset` (#207). No probe names the §5 turnaround-scenario miss (the `scenario-fires-and-detects` probe is assert-present and will simply read "untested" on every turnaround leg).

### Ranked summary of "where it can break"
1. **[S]** Scenario plan cleared by the NewPlan `FlightCycleReset` and never re-planned — no scenario on any turnaround leg (§5).
2. **[S/M, unverified]** A walkaround performed between Shutdown-finalised and the next OFP is not persisted (writes suspended, part not boundary-persisting) — a restart repeats it (§1/§4).
3. **[M, unverified]** Roster: fallback-date stamp before the sim clock is live, then a sim-date stamp → second draw and intro the same day (§2).
4. **[M]** Three readers of "boarding started" after a restart (lifecycle, gate monitor, walkaround) with three definitions; the departure-cycle reset for a new plan depends on which wins (§10).
5. **[M]** Walkaround readiness from a diagnostics string, not the departure-cycle state (§4).
6. **[M]** Radios: one hand-off serialises later read-backs for up to ~64 s (§7).
7. **[M]** `fo.` prefix exempts non-dialogue lines (FMS reminder, cross-check) from the hold (§8).
8. **[M, unverified]** "Ready for pushback" re-sent after a restart when the resync lands outside the 15 s notification hold (§11).
9. **[M]** Settings search drops cards of any pack root missing from `Roots` silently (§12).
10. **[L]** Session rotated before `StartNextLeg`; `RetractionRepeatGuard` 90 s; per-tick `ArmPlanAsync` tasks; search index cache growth; `LastFlightCycleResetKind` read-after-raise.
