# Branch review — 2026-11-05

**What was reviewed.** `fix/code-review-2026-11-03` at `ee2c2ef0` against `main` at `0db90771`
(the merge-base). 45 commits, 391 code files, about 37,000 added lines of which 14,500 are two
flight recordings and about 3,000 are language-pack JSON. That is the whole unmerged November
work: the immersion batch (#194–#206), the leg-one fixes, rc.25/rc.26, the duty-day flow
(ADR-0019, #207–#223) and the rc.3 code-review fix batch (#224–#236).

**How.** Nine parallel reviewers, each with the shared brief (standards sources, Fowler smell
baseline, spec sources). Four on the Standards axis (by project), three on the Spec axis (by
spec), two modelling the runtime (services/loops, pathways). Every finding was verified by
opening the file; anything a reviewer could not prove is marked *unverified*. The three
top-ranked findings were re-checked by hand after the reports came in.

| Report | Axis | Scope |
|---|---|---|
| [standards-core.md](standards-core.md) | Standards | Core, Sim, Prosim, Audio |
| [standards-speech.md](standards-speech.md) | Standards | Speech |
| [standards-gsx-web-app.md](standards-gsx-web-app.md) | Standards | Gsx, Web, App, tools |
| [standards-tests-probes-packs.md](standards-tests-probes-packs.md) | Standards | tests, probe catalog, language packs, options↔UI rule |
| [spec-duty-day.md](spec-duty-day.md) | Spec | ADR-0019, audit 2026-11-02, #207–#223 |
| [spec-immersion.md](spec-immersion.md) | Spec | #194–#206, leg-one fixes, rc.25/26, ADR-0018, owner-report commits |
| [spec-fix-batch.md](spec-fix-batch.md) | Spec | docs/reviews/2026-11-03 plan, #224–#236 |
| [model-services-loops.md](model-services-loops.md) | Model | every service, timer, loop and subscription the branch adds or changes |
| [model-pathways.md](model-pathways.md) | Model | twelve end-to-end pathways with "where it can break" lists |

The two axes are kept apart on purpose: code can meet every standard and still do the wrong
thing, or do exactly what the issue asked and break a convention. Findings are not re-ranked
across axes.

---

## 1. Numbers

| | Hard | Judgement | Bugs noticed |
|---|---|---|---|
| Standards — Core/Sim/Prosim/Audio | 2 | 17 | 3 |
| Standards — Speech | 9 | 22 | 4 |
| Standards — Gsx/Web/App/tools | 4 | 12 | 2 |
| Standards — tests/probes/packs/options | 2 | 13 | 0 |
| **Standards total** | **17** | **64** | **9** |

| | Missing / partial | Scope creep | Looks wrong |
|---|---|---|---|
| Spec — duty day | 4 | 4 | 5 |
| Spec — immersion + fixes | 7 | 3 | 5 |
| Spec — rc.3 fix batch | 5 | 6 | 10 (3 real, 7 verified-OK notes) |
| **Spec total** | **16** | **13** | **13** |

Models: 15 services/loops inventoried (11 risks), 12 pathways mapped (10 ranked break points).

Clean, so nobody has to re-check: dependency direction; no `async void`; no `.Result`/`.Wait()`;
`ConfigureAwait(false)` throughout; no `ReadDataRef` polling (every new read is a subscription or
a store snapshot); no new aircraft write outside an allow-list (tracking LVARs through
`SimWriteGate`, `OhCallsMech` already on `ProsimWriteGate`, failure arming through
`ProsimFailureGate` = catalogue ∩ live list); no token or key logged; all 27 new option leaves
have `[OptionField]` + a settings-page control + a safe default; every branch text exists in all
seven packs at HEAD; xunit + Moq only; composition root untouched; no WPF settings UI; no
catch-all `@page`; the probe-catalog guard really derives event names from the source tree.

---

## 2. Standards

### 2.1 Hard (documented-standard breaches)

**"key" identifier ban — 5 new sites (owner Snyk rule 2026-10-13).** M.
`Web/Layout/SettingsSearch.cs:32,209,217` (`GroupKey`, `ItemKey`, `itemKey`);
`Web/wwwroot/js/app.js:698` (`FOLDS_KEY`); `Speech/SayIntentions/FlightJsonFile.cs:48,57`
(`_lastGoodApiKey`); `Speech/SayIntentions/FoRadiosService.cs:232` (`var key`);
`Core/Flight/FlightStateSnapshotService.cs:365` (`boundaryKeys`). Pre-existing BCL/interface
members (`p.Key`, `WAKE_KEY`) are grandfathered; these are new.

**Fire-and-forget without observing the fault.** L→S at exit.
`Speech/Scenarios/ScenarioService.cs:211,731,876` — `Task.Run(() => ClearAllAsync/ClearOursAsync)`
with no catch; `_writes.WaitAsync` throws once `_shutdown` is cancelled or the semaphore is
disposed, so the exception is unobserved. Same shape at `TransitionReminderService.cs:177`,
`SpokenChecklistEngine.cs:350`, `UtteranceRouter.cs:494`. `FoRosterVoiceFeature.Speak` shows the
house-safe form (`ContinueWith(OnlyOnFaulted)`).

**CancellationToken dropped on new public API.** L. `TechLogDialogueService.RaisePrefilledAsync`
(called from `WalkaroundService.cs:536` with `_shutdown` in hand); `FoRosterVoiceFeature.cs:283`
(`StyleAsync` + `EnqueueAsync` with no token on a startup `Task.Run`).

**Silent blanket catches.** L. `Speech/Briefings/BriefingQuestionsPhrases.cs:104`
(`catch (Exception) { }`); `Core/Configuration/OptionsListBinding.cs:161,191` (return null, no
logger); `Prosim/Failures/SdkFailureInjector.cs:182`; `Web/Components/Diagnostics/EcamStatusPage.razor:126`
(catches everything, shows `ex.Message`, no log).

**XML docs on public API.** L. Misplaced: `FoRadiosComposer.cs:94–100` (`<param>` block sits above
`IsBroadcast`, `Compose` has none); `UtteranceRouter.cs:449–454` (`SkipWhileFoAway` has two
summaries, `HandleIdleMiss` none). Missing: `GateDispatchPlanner.cs:77`, `ScenarioModels.cs:150–166`,
`ScenarioCommandHandlers.cs:30`, `WalkaroundFindings.cs:144`, `SolarPosition.cs:19`,
`FoRosterService.cs` public members, `DutyDaySignals.cs:23`, `LegBoundaryService.cs:59`,
`FmsPlanWatcher.cs` (`Origin`/`Destination`).

**Probe rule (flight-verification.md).** M. **#231 has no probe** (commit 7d09a69a: "the pushback
suggestion loop survives an iteration failure"). A log-signature probe on its warning line would do.

**Localisation rule per commit.** L at HEAD. The six package commits (#194–#199) wrote their new
ids into all six packs as copied English; `e6466012` translated them afterwards. Net clean, but
the "same change" rule was not followed per commit.

**Line endings.** M. `Core/Notifications/NotificationSignalSource.cs` was rewritten with CRLF
(every other Core file is LF, no `.gitattributes`), so an 810-line diff hides a 54-line change
and the file will flip back on the next editor save.

### 2.2 Judgement calls worth acting on (M)

- **Divergent Change — two god shells.** `ScenarioService.cs` (964 lines: scheduler shell, SDK write
  path, LLM decision turn, diversion, voice feature, status projector) and
  `CruiseSmallTalkService.cs` (717: scheduler shell, streamed tier, offer dialogue, layover listen,
  coffee follow-up, context builder). Both *do* have pure cores (`ScenarioScheduler`,
  `SmallTalkCore`), so the CLAUDE.md rule is met; the shells are the problem. Cut
  `ScenarioDecisionService` (~90 lines, six optional deps exist only for it) and
  `SmallTalkContextSource` (60 lines, five optional deps).
- **Duplicated Code — the "user JSON bank" shape ×5 and the "state file" shape ×3.**
  `WalkaroundFindingBank`, `ScenarioCatalogueBank`, `FoRosterService:271`, `PhraseBank`,
  `RegionFactBank` all re-type last-write-time → parse → `ConfigProblemStore` → keep-last-good.
  `FoRosterStateFile`, `ScenarioStateFile`, `DayStateFile` all re-type lock → read → corrupt→`.bak`
  → temp-then-move. The commit message itself calls it "the fo-roster-state.json pattern".
- **Duplicated Code — `WalkaroundService.RingInterphoneAsync` (:574)** re-types
  `GroundCrewUpcallService.RunUpcallAsync` line for line (MECH press, same catch pair, same log
  texts). **`GsxAutomationService.OnDutyDayEnded` / `OnOfpChanged`** (:203, :255) repeat the same
  six-step departure reset. **`FoRadiosService`** builds the "unanswered ATC line" event three times
  (:323, :357, :503) when `RecordDrop` exists.
- **Primitive Obsession — substring sniffing decides behaviour.** `ScenarioService.cs:461` and
  `ScenarioScheduler.cs:86` pick the SDK arm condition / the EFATO-vs-RTO switch by
  `AbnormalId.Contains("after-v1")` / `Id.Contains("efato")`; `AbnormalBurstGuard.cs:48` by
  `Id.Contains("fire")`; `WalkaroundService.Outcome()` returns `"deice"|"ask"|"raise"|"cosmetic"|"none"`
  strings and switches on them. A renamed catalogue entry silently flips which switch gates it.
  ADR-0018 §5 says the catalogue is the only source of what is armed — give it the field.
- **Temporal coupling — `GroundOpsSignals.LastFlightCycleResetKind`** (:40, :113) is set just before
  `FlightCycleReset` fires and read by handlers "on the raising thread". Two resets racing from
  different threads (GSX arrival edge vs SimBrief poller) read the wrong kind. The event argument
  should carry it.
- **Dead code.** `GsxServiceLifecycleTracker.AnyObservedProgress` (:228, no production caller);
  `CruiseSmallTalkService.cs:288` both arms return `(null, "llm")`; `SimLocalTime.cs:94` the
  day-366 guard never fires; `TransitionReminderInput.Enabled` only ever receives `true`.
- **Tests.** `MicOwnershipTests.cs:75–89` is a wall-clock test of a wall-clock window (900 ms sleep)
  — a `TimeProvider` seam would make #201's rule deterministic. `TransitionReminderService` (272
  lines, the DFD memo, QNH source and "no DFD → silent" rule) has no shell test. 9cbd82bc
  (restart-after-boarding → Departure, a phase-engine change) ships a 22-line unit test and the
  EHAM sidecar does not visibly exercise a post-restart `Preflight → Departure` — *unverified*
  whether it is replay-proven.
- **Pack guard weaker than claimed.** `ShippedLanguagePackGuardTests.cs:210–238` fails only when a
  text is English in *every* non-en pack. `flightStatus.aircraftState.pass` is `"cold and dark ✓"`
  in en/de/es/fr/nl and passes because ja/ko differ. Per-language copies at HEAD: de 166, es 112,
  fr 138, nl 203 (mostly one-word labels inherited from main; 9–15 per language are 4+ words).

The full per-file lists, including the L nits (member order, fully-qualified names, two clocks in
one gate table, `Give()` naming, the `IsEmpty` sentinel re-spelled, half-`GeneratedRegex`
`Phraseology`), are in the four standards reports.

### 2.3 Bugs the standards reviewers noticed in passing (S)

1. `Prosim/Failures/SdkFailureInjector.cs:113–122` — `ClearAsync` removes the handle from `_ours`
   *before* `Reset()` runs; if the session is gone or the call throws, the failure stays armed in
   ProSim and `scenario.clear <id>` can never clear it again (only clear-all).
2. `SdkFailureInjector.cs:101` — `ArmedAsync` identifies "ours" by `ReferenceEquals` against
   `getArmedFailures()` items. If the SDK returns fresh wrappers per call, every arm reads
   "not-verified" and is cleared 5 s later: scenario mode dead on the rig with a clean log.
   *Unverified — needs one live SDK check.* A name-based fallback closes it either way.
3. `Speech/Persona/CruiseSmallTalkService.cs:706` — `FirstSentence` cuts at the first `.`, so a
   model reply "about 1.5 hours to Amsterdam" becomes "about 1." and `NumberVerifier` judges the
   wrong figure.
4. `ScenarioService.cs:264 vs :552` — `Dispose` disposes `_writes` while an arm/clear may hold it;
   `finally { _writes.Release(); }` then throws inside a fire-and-forget task (see §4.1 for the
   shutdown-order version of this).
5. `ScenarioService.cs:633` — `OnAbnormalEnded` latches `_decisionAsked = true` *before* the
   `"session-ended"` early return, so a session reset during a fired scenario means the decision
   turn can never be raised for that plan.
6. `WalkaroundService.cs:480` — in the `"ask"` branch `_pendingFinding` is overwritten before
   `_raise.Ask(...)`; if `Ask` is refused, the earlier standing question's answer consumes the new
   finding and the captain's "yes" logs the wrong defect.
7. `Gsx/Automation/GsxAutomationService.cs:244–258` + `NewPlanCycleReset.cs:37–55` — nothing pins
   `_lastResetPlanId` when a cycle *starts*; `OfpStore.Set` raises `Changed` unconditionally; so a
   re-import of the *same* plan while parked before boarding resets the departure side and forgets
   the seeded refuel. *Unverified* — needs a "same RequestId re-imported mid-cycle" test.
8. `Core/State/GroundOpsSignals.cs:113` — the reset-kind race above can make an arrival reset read
   as NewPlan and skip dropping the old leg's snapshot.
9. `Gsx/Sync/CompanionTrackingLvars.cs:150` — `InvalidOperationException` is treated as "not
   connected" and queued, but `ISimVars.WriteAsync` throws the same for "not on the allow-list";
   an unlisted name would re-queue on every connected edge with only a Debug line. Latent.

---

## 3. Spec

### 3.1 Duty day — ADR-0019, audit 2026-11-02, #207–#223

**Headline.** All ten ADR sections have code; all five owner answers (619949b9) are in the ADR
text and the code; all 14 silent misses and 7 wrong displays in the audit have a fix to point
at; the two ADR defaults are the class defaults and the settings-page defaults; the replay
re-derives Deboarding Completed. What is wrong sits at the edges.

| Finding | Rating | Where |
|---|---|---|
| **A day re-opens after end of duty on the flown plan.** `autoStart` runs whenever `day is null \|\| !day.IsOpen` — an ended day is `!IsOpen`. The next ColdAndDark → Preflight edge (power cycle, next sim morning before the OFP) calls `TryStartDay("auto")` and seeds leg 1 from `_ofp.Current`, the flown plan. ADR §8 wants the OFP, not the Preflight edge, to open a day after an end. | S | `Speech/Day/CompanyDayService.cs:496,528,682` |
| **`DayEnded` drops the boundary `PlanId`** while `Airborne` and the GSX side deliberately keep theirs. A forced re-import of the flown plan after end of duty reads as "first plan of the session" → `LegBegan` → new day on the flown plan, cross-check re-armed, purser latch reset. ADR §2 "the same OFP loaded twice is one leg". | M | `Core/Flight/LegBoundaryService.cs:195` |
| **New-plan reset runs on a boarded aircraft** when the cycle is complete: `return cycleComplete \|\| !boardingObserved`. A regenerated OFP after boarding resets pax target, latches, gate monitor, walkaround, loadsheet editions. ADR §6's bar is boarding, full stop. | M | `Gsx/Automation/NewPlanCycleReset.cs:57` |
| **FMGC cross-check `_armed = true`** is never derived from the restored `LegBoundaryStore`; a restart inside TURNAROUND with the flown plan in the box speaks "INIT page: EGLL to EHAM" again — the B3 symptom by another door. | S (unverified live) | `Speech/FoSide/FmgcCrossCheckService.cs:55` |
| **ADR §8 names the end-of-duty button on "Flight Status and Duty Day cards"**; only `Day.razor` has it. `Home.razor` has the leg chip and no day control. | M, hard | `Web/Pages/Home.razor` |
| **Roster pins only once a leg has landed** (`OpenDayLegsCompleted > 0`); an open leg-1 day restarted after a sim-date rollover redraws — mid-day by #219's definition. | M | `Core/Persona/FoRosterTriggerCore.cs:75` |
| `OnFmsCleared` demands `Data.IsValid` + parked; with SimConnect down and ProSim up the IOS Reset FMS is recorded `accepted:false` and the chip never leaves TURNAROUND. | L | `LegBoundaryService.cs:217` |
| `LegBoundaryService` constructor feeds `PlanLoaded` before the snapshot restore (RestoreOrder 5); a new plan arriving in that window has its id overwritten by the restored flown one and never fires `LegBegan`. Timing window; noted. | L | `LegBoundaryService.cs:79–88,147` |

Scope creep (all L, all defensible, all should be a line in the ADR): `LegBoundaryCore.Airborne`
("a take-off ends any boundary state"); `DayOptions.EndOfDutyOfferMinutes` (ADR fixes 10 min; a
knob was added, with its control); `CompanyDayEngine.DropPhantomLeg` + `day.leg.dropped`
(migration for the owner's own daystate.json); gate-monitor B2 hole closed by seat occupation
rather than request-id adoption (stronger evidence, but the hole remains when `CabinEmpty == null`).

Per-issue: #207 #208 #209 #210 #211 #214 #215 #216 #218 #222 #223 implemented; #212 #220 #221
implemented with one wrong edge each (rows above); #213 #217 #219 partial.

### 3.2 Immersion batch, leg-one fixes, rc.25/26, ADR-0018

**Scenario mode (#197 / ADR-0018) is partial and has the most serious findings on the branch.**
Combined with the model reports (§4), four independent mechanisms each stop it working:

| Finding | Rating | Where |
|---|---|---|
| **The startup clear-all cannot reach ProSim.** `ClearAllAsync("startup")` runs from `Start()` inside `StartupModuleHost.StartAsync`, before `ProsimConnectionService.RunSessionsAsync` has set `_connection`. The injector answers "no SDK session", the STATUS row says "could not clear", nothing retries on the connected edge. ADR §6 "a persisted Armed/Fired runs one clear-all" is met in letter, not in effect. | M, hard | `ScenarioService.cs:209–213`, `ProsimConnectionService.cs:202` |
| **The exit clear cannot run either.** Hosted services stop in reverse registration order; `StartupModuleHost` (registered last) disposes `ScenarioService` first (`_writes.Dispose()`), then `ScenarioService.StopAsync` calls `ClearOursAsync("exit")` → `_writes.WaitAsync` → `ObjectDisposedException`. The next-start path above was the safety net — and it cannot reach ProSim. | S | `ScenarioService.cs:241–267`, `App/ServiceComposition.cs:83` |
| **No scenario on any turnaround leg.** `TryPlan` runs only on the Preflight edge, which in a turnaround comes 30 s after deboarding, before the new OFP. The OFP then raises `FlightCycleReset(NewPlan)` and `OnFlightCycleReset` clears `_plan` without reading the kind; nothing re-plans. Every leg after the first is scenario-free. | S | `ScenarioService.cs:282,655–663` |
| **Re-arm storm when ProSim refuses.** `ProcessTick` spawns `Task.Run(ArmPlanAsync)` on every 1 s tick while the plan is still `Planned`; the plan leaves `Planned` only after a successful verify. A refused arm → an `arming`/`armFailed` pair every ~3 s and an unbounded semaphore queue for the rest of the phase. | S | `ScenarioService.cs:376–392,431–524` |
| **"First live Preflight" never re-tries.** `TryPlan` returns without planning when `!_flight.IsLive`; a Preflight edge that lands before the live gate (restart at the gate) means that flight is never planned, silently. | M | `ScenarioService.cs:280–293` |
| **Runway-phase floor hole.** The AGL floor applies only in the airborne phases; `tick.Sterile` is false in TakeoffRoll/LandingRollout by design. A user-edited entry whose `phases` lists the roll arms instantly with no floor and no sterile hold; the manual `scenario.arm` path reaches the same branch from any phase. ADR §4. | M, hard | `Core/Scenarios/ScenarioScheduler.cs:209,231` |
| **Manual arm ignores `phases`.** `scenario.arm brakes-hot` (catalogue: TaxiIn only) arms in the cruise. ADR §5. | M | `ScenarioService.cs:747–790` |
| Verify-by-reference (standards bug 2 above). | S, unverified | `SdkFailureInjector.cs:101` |
| `techLogAfter` is carried on the entry/plan but nothing outside `Scenarios/` reads it; a refused scenario is logged, never named on the Config page (`ConfigProblemStore` not taken). | L | `ScenarioService.cs:321–327` |

Everything else in the batch verifies as asked (details in the report): ADR-0018 off-by-default,
consent window, allow-list, replay arms nothing, clear at cycle reset; #196 restart rule as
superseded by ADR-0019 §9; #195 sterile gating on every line; #194 sim calendar day; #198 PFD
dataref not FMS; the dialogue-hold fix cannot self-deadlock (every borrowing dialogue awaits its
prompt before listening); 20 s radio-clear is a constant (no control owed), 4 s check-in has its
control; #199–#203, #123, #151, #156/#102, #204, #145, #206, #177, #139, #138, #190–#193, MSA.

Remaining partials: **#205** is seed data only — a bare "one eight seven" still misses the
checklist line and falls to the read-back; and the three seeded checklist edits (#107/#205/#193)
ride ADR-0007 keep-user-edits, so the owner's user-edited `taxi.json` on the sim PC will not
receive them (the changelog does not say so). **#196** abandons on *any* loss of readiness and
always speaks "the refuel's been called", which is then untrue for boarding-called / prep-flip /
live-blip; `walkaround.abandoned` carries no reason.

### 3.3 rc.3 fix batch — docs/reviews/2026-11-03 plan, #224–#236

All 12 confirmed bugs in README §2 have a fixing commit (table in the report). Package 1 (bugs)
and package 8 (docs) are done; packages 2–7 untouched as planned. Partials and concerns:

- **#224 CHANGELOG backfill not done** — 15 tags still have no header (v0.3.0-beta.30 …
  v0.5.0-rc.1). M.
- **#227** — four `checks` prose strings still name `speech.recognized`
  (`verification-probes.json:3485,3519,3618,3712`); the lint checks `machine.type` only. M.
- **#236** — guard accepts English in de/es/fr/nl when ja/ko differ (§2.2). M.
- **#228 lost per-definition isolation** — the typed filter at `FailureMonitor.cs:749` is what the
  issue asked, but any other exception from the subscribe path now aborts `ProcessTick`'s loop
  for *every remaining abnormal that tick*, logged at Error every 500 ms. The bare catch at least
  isolated definitions. A per-definition catch-all (log once, continue) keeps both properties. S.
- **#225 mirror-release window** — the latch drops the moment the mirror reads not-Active/Completed;
  a transient intermediate state mid-retraction re-opens the window to the 2 s slot spacing.
  Not provable from code; the probe `gsx-toggle-retraction-once` is the instrument. M, unverified.
- **Unasked:** the `fixed-unverified` probe state (21 probes, new workflow vocabulary, no issue);
  the 90 s `RetractionRepeatGuard` with no unit test (owner comment: "needs a clock seam"); the
  crew's "retract jetway" changed from a watched slot call to a NoConfirm toggle; `RunLoopAsync`
  made public static with injected delegates; `gsx-cycle-reset` gains a `trackingLvars` field.
- **#233** — the `SessionEvents` constants class was not built; the lint derives names by regex
  (works; a computed event name is invisible to it). L.

---

## 4. Models

### 4.1 Services and loops — [model-services-loops.md](model-services-loops.md)

Fifteen entries, each with lifetime, trigger, inputs, outputs (every event name), concurrency,
cancellation, degrade path, pure core and test file. The loop map:

| Loop | Cadence | Overlap guard |
|---|---|---|
| `TransitionReminderService.Tick` | 1 Hz | `Interlocked` |
| `WalkaroundService.Tick` | 1 Hz | `Interlocked` + `_inFlight` |
| `ScenarioService.Tick` | 1 Hz | `Interlocked`; writes under `SemaphoreSlim` |
| `CruiseSmallTalkService.Tick` | 30 s | `Interlocked` + `_inFlight` |
| `CompanyDayService.TimerTick` | 60 s | **none** (lock inside) |
| `NotificationSignalSource` settle | one-shot 15 s | idempotent |
| `FoRadiosService.WaitForCom1Async` | 500 ms ≤ 60 s per hand-off | none — hand-offs may overlap |
| `MicOwnership.ListenAsync` | 250 ms slices, 60 s hard cap | per listen |
| `CabinCrewService.ReportCabinEmptyAsync` | 3 × 30 s | `Interlocked` per leg |
| `PushbackSuggestionService.RunLoopAsync` | 15 s, 5 s back-off | sequential |

Three new 1 Hz timers + one 30 s + the existing 60 s, all pool threads, no shared lock. Only
`TransitionReminderService` (DFD lookup per OFP pair) and `CompanyDayService` (state save on
offer) do IO in a tick. Thread picture: the SDK callback thread runs `FmsPlanWatcher` →
`LegBoundaryService` → five Speech subscribers synchronously; the heaviest is
`CompanyDayService.HandleLegBegan` (session rotation + state save under its lock).

Risk register (full in the report): the two ScenarioService S items above; the jetway
mirror-release window; the small-talk layover listen holding the mic for 10 s and so deferring
every non-exempt prompt app-wide (M); tick exceptions at Debug in Walkaround/Scenario (L);
`CompanyDayService.TimerTick` with no overlap guard (L); `DutyDaySignals` multicast stopping at
the first throwing subscriber (L); `LegBoundaryCore.Airborne` clearing flags without a publish (L).

Shell test coverage gaps: `TransitionReminderService`, `CruiseSmallTalkService` (tick → claim →
deliver, offer, coffee), `GsxAutomationService` new-plan / end-of-duty / seeded-restore paths,
`FoRadiosService` hand-off wait, `SdkFailureInjector` (needs the SDK).

### 4.2 Pathways — [model-pathways.md](model-pathways.md)

Twelve pathways drawn trigger → services → stores → events → outputs with their gates
(phase, sterile, dialogue hold, voice gate, option switch) and a "where it can break" list each;
then a cross-pathway section: who consults which gate and whether the same way, and stores
written by more than one pathway. The cross-pathway table is the most useful page in this review
for the next design pass. Three things it shows:

- **"Flight live" is checked four different ways** (small talk, walkaround, scenario, transition
  each early-return on their own copy; roster uses `InPreflight`; leg boundary uses
  `Data.IsValid` + parked; day and radios do not check at all).
- **"A new leg" is decided twice from the same `OfpPlanIdentity`**: `LegBoundaryService` raises
  `LegBegan` for Speech; GSX runs its own `NewPlanCycleReset.ShouldReset` with different extra
  conditions (parked, boarding). The snapshot service compares `RequestId` directly — a third
  spelling of "same plan".
- **"Boarding started" has three readers with three definitions after a restart** (lifecycle
  `ObservedProgress`, gate monitor `staleClose`, walkaround diagnostics-board stage). For the EHAM
  restart case the gate monitor resets, the walkaround refuses, and the departure-cycle reset for
  the new plan depends on which wins.

Ranked break points: (1) the turnaround scenario miss (S); (2) a walkaround done between
Shutdown-finalised and the next OFP is not persisted — the part is not `PersistsAcrossLegs` and
writes are suspended — so a restart repeats it (S/M, unverified); (3) roster fallback-date stamp
then sim-date stamp = two draws and two intros on one day (M, unverified); (4) the three
"boarding started" readers (M); (5) walkaround readiness read from a diagnostics *string*
(`GroundPrep.Stage == "Complete"`), not `DepartureCycleState` (M); (6) one radio hand-off
serialises every later read-back for up to ~64 s behind `_transmitGate` (M); (7) the `fo.`
exempt prefix lets the FMS reminder and the cross-check talk over a captain's-question listen
(M); (8) "ready for pushback" re-sent after a restart when the resync lands outside the 15 s
notification hold (M, unverified); (9) settings search silently drops cards of any pack root
missing from `Roots` (M).

---

## 5. Suggestions for improvement

Ordered by what bites first. Each package is one branch-sequential commit set per the branching
rule; behavioural fixes carry a probe.

### P1 — Scenario mode: make it actually run, and only when allowed (before anyone flies it)

1. `OnFlightCycleReset`: keep the plan on `NewPlan`; re-plan on `LegBegan` (one source of "new
   leg") rather than the Preflight edge; re-try `TryPlan` on the first *live* tick in Preflight.
2. `ProcessTick`: an `_arming` flag; after a refused arm mark the plan `Failed` (or back off with
   a logged reason) instead of re-queuing every second.
3. Shutdown order: do the exit clear from `Dispose` before cancelling, or stop disposing `_writes`;
   run the startup clear-all on the ProSim *connected* edge, not in `Start()`.
4. `SdkFailureInjector.ClearAsync`: remove the handle only after a successful `Reset()`;
   `ArmedAsync`: fall back to matching by failure name. Then one rig check of verify-after-arm.
5. `ScenarioScheduler`: apply the AGL floor and the sterile hold in every phase (the roll
   included); honour `entry.Phases` on the manual arm; replace the `"after-v1"`/`"efato"` substring
   sniffs with a catalogue field.
6. Probe: `scenario-turnaround-leg-planned` (assert-present on leg ≥ 2).

### P2 — Duty-day edges (ADR-0019)

1. Limit the Preflight auto-start to "no day has existed this session" (`_engine.Day is null`);
   after an end of duty only the OFP opens a day (`HandleLegBegan` already does this).
2. `DayEnded`: clear the boundary state and flags, keep the `PlanId` as the baseline.
3. `NewPlanCycleReset.ShouldReset`: boarding observed → never reset, cycle complete or not.
4. `FmgcCrossCheckService`: start disarmed when the restored boundary store is
   Turnaround/AwaitingPlan.
5. End-of-duty button on the Flight Status card (ADR §8); pin the roster while a day is open at
   all, not only after a completed leg.
6. Record the four creep items as ADR lines (take-off ends a boundary, offer-minutes knob,
   phantom-leg migration, seat-occupation evidence).

### P3 — One definition each for "reset kind", "new leg", "boarding started", "flight live"

1. `GroundOpsSignals`: raise `FlightCycleReset(FlightCycleResetKind)` as the event argument and
   delete `LastFlightCycleResetKind`. Every consumer that ignores the kind today (scenario,
   walkaround, notifications, gate monitor, jetway latch, transition, question stores) then has to
   say so explicitly.
2. GSX subscribes to `LegBegan` for its departure-cycle reset and keeps only its extra gate
   (boarding not observed); drop the second `OfpPlanIdentity` spelling and the snapshot's raw
   `RequestId` compare.
3. One `BoardingEvidence` reader (lifecycle-owned) that the gate monitor and the walkaround
   consume; the walkaround stops reading the diagnostics board string.
4. A `FlightGate.IsLiveFor(sample)` helper used by the four early-return copies.

### P4 — Speech hygiene

1. `FirstSentence`: split on a sentence end that is not between digits.
2. Walkaround: `Outcome` as an enum; `walkaround.abandoned` carries a reason; the abandon line
   matches it; restore `_pendingFinding` when `Ask` is refused.
3. Dialogue hold: replace the `fo.` prefix exemption with an explicit exempt tag set (the FMS
   reminder and the cross-check are not dialogue lines); consider whether a small-talk layover
   listen deserves full hold power.
4. Radios: release `_transmitGate` before `WaitForCom1Async`, or queue the read-back of an
   instruction that arrives during the wait rather than serialising it.
5. `TechLogDialogueService.RaisePrefilledAsync` and `FoRosterVoiceFeature.Speak` take and pass the
   token; the three `ScenarioService` clear paths and the three other fire-and-forgets observe
   faults the way `FoRosterVoiceFeature.Speak` does.

### P5 — Standards sweep (mechanical, one commit)

The five "key" identifiers; the CRLF file; the misplaced/missing XML docs; the four silent catches
(log at Debug or narrow); `AnyObservedProgress`, the dead `(null, "llm")` branch, the day-366
guard, `TransitionReminderInput.Enabled`; the five ctor "assign-before-validate" shapes; the
duplicated `OnRosterChanged`/`OnPersonaOptionsChanged` bodies; `EcamStatusPage` catch → log +
filter OCE like its sibling.

### P6 — Shared shapes (the redundancy families this branch added; pair with README-2026-11-03 §4)

1. `JsonStateFile<T>` for `FoRosterStateFile`, `ScenarioStateFile`, `DayStateFile`.
2. `UserConfigFileBank<T>(parse)` for the five banks; one `Text(JsonElement, name)` / `seen` /
   `entry {index}` parser helper for the three model parsers.
3. `InterphoneRing` helper shared by `WalkaroundService` and `GroundCrewUpcallService`.
4. `GsxAutomationService.ResetDepartureCycle(reason, …)` called from both handlers.
5. Cut `ScenarioDecisionService` out of `ScenarioService` and `SmallTalkContextSource` out of
   `CruiseSmallTalkService`; the six and five optional ctor parameters go with them.

### P7 — Verification debt

1. Probe for #231; fix the four `speech.recognized` prose refs and extend the lint to quoted event
   names inside `checks`/`signature` strings.
2. Pack guard per language with a per-language loanword allow-list (or "no 4+-word English
   sentence in a Latin pack").
3. `TimeProvider` seam in `MicOwnership`; shell tests for `TransitionReminderService` and the
   small-talk tick → claim → deliver path; a `GsxAutomationService` test for the new-plan /
   end-of-duty / seeded-restore handlers and for "same RequestId re-imported mid-cycle".
4. A replay that exercises 9cbd82bc's post-restart `Preflight → Departure`; a checked-in recording
   behind the 300 s parked grace (#206).
5. CHANGELOG backfill for the 15 tags.
6. Decide the `fixed-unverified` probe state in an issue (keep and document, or revert to
   `closed-regression-watch`).

---

## 6. One-line summary

**Standards:** 17 hard / 64 judgement findings; worst is the five new "key" identifiers (owner
rule) plus the unobserved fire-and-forget clears in `ScenarioService`.
**Spec:** 16 missing-or-partial / 13 creep / 13 looks-wrong; worst is scenario mode, which four
independent mechanisms (turnaround reset, re-arm storm, shutdown order, startup clear before
the session) each stop from working as ADR-0018 describes.
