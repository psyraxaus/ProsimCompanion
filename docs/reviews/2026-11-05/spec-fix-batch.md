# Spec review — code-review fix batch 1.0.0-rc.3 (fix/code-review-2026-11-03, HEAD ee2c2ef0)

Spec: `docs/reviews/2026-11-03/README.md` §2/§4–§9, issues #224–#236 (bodies + the #224 owner comment), CHANGELOG rc.3 header. Every fix below was opened in code, not taken from the commit message.

## (a) Asked for, missing or partial

- **[M] [HARD]** README §9 pkg 8 "CHANGELOG backfill" (§8: "15 tags without entries") — `CHANGELOG.md` — `f11b267b` adds only the rc.3 header (+23 lines). On HEAD 15 tags still have no header: v0.3.0-beta.30, v0.4.0-beta.1…7, v0.4.0-rc.1…4, v0.5.0-beta.1/2, v0.5.0-rc.1. #224 body lists "CHANGELOG" under the docs refresh; the rc.3 header says the docs are "caught up here". Partial.
- **[M] [HARD]** #227 "Also found by the same sweep … `speech.heard` (3 probes)… Fix: correct the names" and the commit message "`speech.recognized` → the CMTrace `ASR heard` trail line" — `docs/agents/verification-probes.json:3485, 3519, 3618, 3712` still name `speech.recognized` as the trigger in `checks` prose (`"the pilot says 'wind check' (speech.recognized)"`). The new lint (`ProbeCatalogGuardTests.MachineEventTypes_AreEmittedByTheCode`) checks `machine.type` only, so prose stays unguarded — the issue's "ideally every quoted event name in `checks`" is not done. Partial.
- **[M] [JUDGEMENT]** #236 "extend the guard with an 'equals en' check" / owner rule "all seven packs" — `tests/.../Localization/ShippedLanguagePackGuardTests.cs` `EveryText_IsTranslated_InSomeLanguage_…` only fails when a text is English in **every** non-en pack (or Latin in ja/ko). Measured on HEAD: ids byte-identical to English — de 187, es 133, fr 157, nl 224, ja 37, ko 38; 40 ids are English in all four western packs while ja/ko differ (`flightStatus.aircraftState.pass` = "cold and dark ✓", `fuel.plan.extra` "Extra", `monitor.lamps.beacon` "Beacon", `speechSettings.options.experience.junior` "Junior"…). Loanwords are a real reason, but the guard as written cannot catch the next text pasted into de/es/fr/nl. The 33 ids identical in all six are all genuine invariants (brand names, "C/L Menu", paths) — that part holds.
- **[L] [JUDGEMENT]** #233 "a `SessionEvents` constants class so the emitted set is derivable" — not built; `ProbeCatalogGuardTests.cs:32-75` derives the set by regex over `eventLog.Record(`/`RecordEvent(` literals and `const string`s. Works today; a computed event name is invisible to it.
- **[L]** #234 cites GsxSettings "~L152-170/274/297" — the operator-preference and company-hub lists (L274/297) got no `RowsValidity`. At rc.2 they never painted `invalid` either (0 hits), so the issue's premise was wrong there; the Audio tables were left by stated design (#224 comment). Not a gap, noted for the owner's veto.
- **§4 redundancy families**: none of R1–R20 is addressed as such — consistent with §9 (packages 2–7 are later). Incidental touches: pkg 4's "LVAR writer" is effectively done by `CompanionTrackingLvars` (#230); R7 (`TransientNote`) explicitly deferred in #226; R1 gets a local guard only (#231, as the issue asked).
- **§9 packages**: 1 (bugs) done bar the two partials above; 8 (docs) done except the CHANGELOG backfill (ARCHITECTURE/CLAUDE.md eleven projects, ROADMAP Phases 11.5/13/14, CONTEXT.md +4 sections, `parity-backlog.md` deleted, `claude/*.md` → `docs/integrations|plans`, `out/`+`.superdesign/` ignored, screenshots retaken — all verified). 2–7 untouched. §5/§6/§7 untouched (expected).

## (b) Not asked for (scope creep)

- **[M] [JUDGEMENT]** New probe state **`fixed-unverified`** — `verification-probes.json` (21 probes, all rc.1/rc.2 issues #200–#223), `docs/agents/flight-verification.md` (new rule text), `ProbeCatalogGuardTests.State_IsOneTheWorkflowDefines` enshrines it. #233 asked to flip 43 probes to `closed-regression-watch`; nobody asked for a third state, and the #224 comment says the new probes "carry `state: open`" (they do — the new state was applied to older probes instead). Vocabulary change to the agent workflow without an issue.
- **[M] [JUDGEMENT]** #225 — `GsxJetwayStairsService.RetractionRepeatGuard` (90 s re-arm, `TryReserveRetraction`) is unrequested behaviour with no unit test (owner comment admits "needs a clock seam"); and `GsxServiceControl.RetractAsync` (L563-585) changes the crew's "retract jetway" from a **watched** slot call (confirm + retry-once) to a NoConfirm toggle through the latch. Both are defensible, both are more than "pass the latch first".
- **[L]** #231 — `PushbackSuggestionService.RunLoopAsync` made `public static` with injected `waitForTick`/`iteration` delegates; the issue said "only needs the local guard". Testability seam, mild speculative generality.
- **[L]** #230 — `gsx-cycle-reset` gains a `trackingLvars` payload field (new public event shape; the probe depends on it). Fine, but it is a contract change.
- **[L]** #234 — GsxSettings adds a new rule (negative `MinimumFlightMinutes` paints red, L171) that no page painted before.
- **[L]** Docs beyond pkg 8 in `08f1e31a`: de-ice LVAR "observed sequence" table in `docs/integrations/gsx.md`, ADR-0006 edit, first-flight checklist "superseded", voice-ground-ops note moved to `docs/plans`. Harmless.

## (c) Implemented, but check the implementation

- **[S] [JUDGEMENT]** #228 "catch the concrete exception types" — `FailureMonitor.cs:749` filters `ArgumentException | InvalidOperationException | ObjectDisposedException`. `Read` → `Subscription` → `_dataRefs.SubscribeDynamic(dataref, …)` (L975). Any other type a backend throws from that path (unverified which — `ConditionEvaluator` itself throws only `ArgumentNullException`) now escapes `EvaluateTrigger`, aborts `ProcessTick`'s `foreach (var definition in _definitions)` (L300) for **every remaining abnormal that tick**, and is logged at Error every 500 ms by `Tick()` (L993). The bare catch at least isolated definitions from each other. Spec-conformant, but the per-definition isolation was lost; a per-definition catch-all at L341 (log once, continue) would keep both properties.
- **[M] [JUDGEMENT]** #225 latch release — `GsxJetwayStairsService.OnMirrorUpdated` drops the latch the moment the mirror reads the service as not Active/Completed. If GSX's mirror passes through an intermediate state before the retraction visibly completes, the only remaining guard is the slot's 2 s spacing; a sender that re-reads Active/Completed (the "mirror lag" the issue names) inside the 90 s could toggle again. Not provable from code — the probe `gsx-toggle-retraction-once` (80 s window) is the right instrument. Sender coverage is otherwise closed: all four paths (`RequestRemovalAsync` via `RetractJetwayStairsEffect`, `RemoveStairsAfterDepartureAsync`, crew `RetractAsync`) funnel into `DispatchRemovalAsync`; no other `OperateJetways/OperateStairs` trigger sender exists in `src/`. Slot guard verified: `occupied.Watched != request.NoConfirm` refuses toggle-behind-toggle (`GsxTriggerSlot.cs:116`), mixed pairs wait; 4 new slot tests + 8 retraction tests.
- **[L]** #225 — `GsxServiceControl.RetractAsync` maps any `Busy` naming the same service to "retraction already under way". A watched *connect* of that service still in flight past the 10 s `SlotWait` returns Busy with the same id → the crew is told a retraction is under way while a connect is. Edge case.
- **[L] [JUDGEMENT]** #230 — verified: latest-value-per-LVAR queue, flush snapshots and clears `_pending` before writing (exactly once per connected edge; a re-failure re-queues), `ClearDepartureSideAsync` leaves TURNAROUND/PREP_DONE (test `…LeavesTheArrivalSideAlone`). "Stale plan": the queue carries no plan id — a queued new-leg clear is applied whatever plan is current at connect; a later arrival reset overrides per LVAR, so the end state is right. Two warts: every `Run` is its own `Task.Run` (`CompanionTrackingLvars.cs:130`), so a clear and a `MarkServiceDoneAsync` issued in the same instant race on thread-pool order, not call order (pre-existing shape); and `GsxAutomationService.ClearDepartureTracking` reads `IsSimConnected` **after** firing the write, so the event can say "cleared" while the write queued on a flap (documented "best effort").
- **#226** — verified closed: `Home.razor:539` disposes the previous timer on re-arm, `:880` in `Dispose()`; the stray line in `ConfirmFuel` is gone. Only two exit paths exist.
- **#231** — verified: two consecutive failures → two Warning lines, two 5 s backoffs, loop continues; `waitForTick` false → exit; OCE during backoff → exit; `using var timer` outlives the loop so no disposed-timer re-loop. [L] a persistently throwing `waitForTick` logs a Warning every 5 s forever (no breaker).
- **#232** — verified: `if (version < 4)` ladder (`SettingsMigrator.cs:64`) — a file at 4 is untouched (no explicit "already 4" test, but the ladder guarantees it); importer no longer copies the key; manual line repointed to the mic pill. [L] a v4 file hand-edited with `listeningTone` keeps it — by design.
- **#233/#235** — mental run of `ProbeEvaluator` on the seven watch probes: `signature` watch → `("watch", hits, count, 0)` → `AsWatch` → `"watch: N hit(s) of '<type>'"` + hits; `required-fields` watch with zero events → inner `untested` → `"watch: 0 '<type>' event(s), 0 violation(s)"` + `"no '<type>' event in the bundle"`; violations → fail evidence quoted under the summary; clean → summary only. Correct. [L] a malformed watch block (unknown kind / missing type) also surfaces as `watch: 0 … 0 violation(s)` with the error as line two — the lint keeps the catalog well-formed, so acceptable. Catalog on HEAD: 212 probes, 0 null issues, 0 empty sources, 0 `feature-pending`, 0 signature blocks without `signature`; `gsx-simbrief-reload-new-plan-only` is now text-only with the old name explained in `notes` — correct per the issue's reasoning.
- **#234** — verified on all four claimed pages: `RowsValidity.OnParametersSet` → `Validity.Report(Name, Blocking)`; placed outside the "no rows" branch; `FieldValidity.Report` removes the entry when the row is fixed; render tests in `SettingsPagesRenderTests.Validity.cs`. [L] `Name` is the card title — two tables sharing a title on one page would collide (none today).
- **#229** — verified: `IsMorning` = `Word(t) == Morning`, small talk/briefing/guard compose through `Phrase`; `FoRosterVoiceFeature` already took `TimeOfDayGreeting.Word` (its "Morning captain" form is not a "Good {word}" — the issue's L299 claim was off). Band tests present.

## README §2 → fix

| Bug | Fixed by |
|---|---|
| B1 trigger-slot NoConfirm duplicate | `078d7204` `GsxTriggerSlot.cs:116`, `GsxJetwayStairsService.cs` latch, `IGsxJetwayRetraction` (+ unrequested 90 s re-arm) |
| B2 `_tuneNoteTimer` | `e6466012` `Home.razor:880`, stray line removed |
| B3 `gsx.simbriefReload` probe | `8711ea72` probe rewritten text-only; 4 `speech.recognized` prose refs remain (partial) |
| B4 bare catch | `7d09a69a` `FailureMonitor.cs:749` (typed filter; isolation concern above) |
| B5 small-talk morning | `7d09a69a` `SimLocalTime.cs` `IsMorning/Phrase`, `CruiseSmallTalkService.cs:631` |
| B6 new-plan clear bypasses queue | `078d7204` `CompanionTrackingLvars.cs`, automation + resync routed through it |
| B7 pushback loop dies | `7d09a69a` `PushbackSuggestionService.RunLoopAsync` |
| B8 `ListeningTone` | `7d09a69a` deleted, importer, migrator v4 |
| B9 probe states/fields | `8711ea72` 43 flipped, issue 0, `human` source, lint (+ new `fixed-unverified` state, unasked) |
| B10 row editors never block Save | `e6466012` `RowsValidity` on 4 pages (Audio left by design) |
| B11 watch probes skipped | `8711ea72` `ProbeEvaluator.AsWatch`, `ReportModels` |
| B12 copied-English packs | `e6466012` 112–121 texts/lang + guard (guard weaker than asked) |

## Issues → status

| Issue | Status |
|---|---|
| #224 docs | partial — CHANGELOG backfill not done; everything else verified |
| #225 | implemented (+ unrequested 90 s re-arm, crew call semantics change; mirror-release window unverified) |
| #226 | implemented |
| #227 | partial — machine block fixed; 4 prose refs to `speech.recognized` remain; `checks` not linted |
| #228 | implemented — per-definition isolation regression risk (c) |
| #229 | implemented |
| #230 | implemented — event field is best-effort, thread-pool ordering wart |
| #231 | implemented |
| #232 | implemented |
| #233 | implemented — regex derivation instead of `SessionEvents`; new state unasked |
| #234 | implemented (Audio tables excluded by stated design) |
| #235 | implemented |
| #236 | partial — translations done; guard accepts English in de/es/fr/nl when ja/ko differ (187/133/157/224 identical ids remain) |
