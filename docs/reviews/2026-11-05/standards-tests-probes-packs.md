# Standards review — tests, probes, language packs, options↔UI

Branch `fix/code-review-2026-11-03` (HEAD ee2c2ef0) against `main` 0db90771. Standards cited: CLAUDE.md "Conventions", `~/.claude/skills/dotnet-best-practices/SKILL.md`, `docs/agents/localisation.md`, `docs/agents/flight-verification.md`, ADR-0008, ADR-0014, owner rules in the brief. Scripts and raw outputs are in the scratchpad `review/` folder (`packs.ps1`, `retrans.ps1`, `longcopies.ps1`, `probe-diff.ps1`, `guard-tests.log`). The guard tests named below were run (`dotnet test … --filter Localization|Verification|OptionFieldCompleteness|FlightReplay`): 84 passed, 0 failed.

## 1. TESTS

**Framework rule (CLAUDE.md "Tests: xunit + Moq only").** The whole `tests/` diff (80 files, 300 added test methods) contains no NUnit, MSTest, FluentAssertions or Shouldly usage. Pass.

**Timer-driven tests (CLAUDE.md "favour extracting pure ProcessTick-style cores … testable without timers").** Added lines that sleep:

- [JUDGEMENT] M — `tests/ProsimCompanion.Core.Tests/Speech/MicOwnershipTests.cs:75-89` `Listen_DoesNotCountTime_WhileTheFoIsSpeaking`: `ListenAsync([], 400 ms)`, `await Task.Delay(900)`, `Assert.False(listen.IsCompleted)`, then `Assert.InRange(sw.ElapsedMilliseconds, 200, 3000)`. New on the branch (main had no `Task.Delay` in this file). `MicOwnership.cs:109-132` measures with `DateTimeOffset.UtcNow` and `Task.Delay(wait)`, so this is a wall-clock test of a wall-clock window; a loaded CI box turns the 900 ms sleep into a false "window elapsed". A `TimeProvider` seam would make #201's pause-while-speaking rule deterministic.
- [JUDGEMENT] L — `Flight/FlightStateSnapshotTests.cs:88-131` (new test): three `await Task.Delay(FlightStateSnapshotService.WriteDelay + 400 ms)` against the service's real `System.Threading.Timer` (`FlightStateSnapshotService.cs:48,70,403`), ~6 s per test. Same pattern as the 11 pre-existing sleeps in that file, so consistent with the file, but it is the exact shape the convention asks to avoid.
- [JUDGEMENT] L — `Gsx/GsxSimbriefReloadServiceTests.cs:63` and `Notifications/NotificationTests.cs:75,80`: `Task.Delay(200)` then a negative assertion ("nothing fired yet"). Pre-existing style in both files (6 and 10 on main); a negative timing assertion can only ever be weak evidence.
- `Gsx/CompanionTrackingLvarsTests.cs:44` and `Gsx/GsxTriggerSlotTests.cs:15` are deadline-bounded poll helpers; `Persona/FoRosterTests.cs:182` `Thread.Sleep(20)` is belt-and-braces beside an explicit `SetLastWriteTimeUtc(+5 s)`. Acceptable.
- BackgroundService test-race (owner memory 2026-10-25): no added test calls `StartAsync`/`ExecuteAsync` on a hosted service. Pass.

**Every new service has a test (59 new `src` files, 31 new test files).** Checked by type name, not file name. Untested:

- [JUDGEMENT] M — `src/ProsimCompanion.Speech/Callouts/TransitionReminderService.cs` (272 lines, 30 branch points vs 18 in `TransitionReminderCore`, which *is* tested). The host owns the DFD once-per-OFP memo, the QNH source and the "no DFD → log once, stay silent" rule — behaviour the Core cannot see.
- [JUDGEMENT] L — `src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs` (241 lines; SDK-bound, the `ProsimFailureGate` it installs into is tested), `src/ProsimCompanion.Core/Commands/Handlers/ScenarioCommandHandlers.cs` (56 lines), `src/ProsimCompanion.Core/Crew/WalkaroundFindingBank.cs` (96 lines, file re-read on mtime; the `WalkaroundFindingsFile` parser is tested). The rest of the zero-hit list is records/stores (`WalkaroundRaiseStore`, `SmallTalkOfferStore`, `FoRosterModels`, `ScenarioModels`), an interface (`IOptionNormalize`), or covered indirectly (`OfpPlanIdentity` via `"req:"` assertions in three tests; `RowsValidity` via `SettingsPagesRenderTests.Validity.cs:25`).

**Smells.** Duplicated fixtures: none — across the 31 new files no helper method name and no `Fake*`/`Harness` class is defined twice, and the one shared fake (`Speech/FakeLegBoundaryEvents.cs`) was extracted rather than copied. Mysterious name: [JUDGEMENT] L — `Speech/FmgcCrossCheckAutoDueTests.cs:23` `TheOldRules_StillHold` (five unrelated `Assert.False` rows; the name says nothing about which rules).

**Count: 7 findings (0 HARD; 2 M, 5 L).**

## 2. PROBES

Catalog: 179 → 212 probes, 33 added, 0 removed, ~95 state flips (#233 hygiene). The 45 commits claim 70 distinct issue numbers; every behavioural one has a probe by `issue` field or by text (e.g. #157 ×10, #175 ×4, #177 ×6, #203/#204/#205 by text, #209 by the "paxBoarded equal to the previous leg's total" clause inside `gate-monitor-states`). Exceptions:

- [HARD] M — **#231 has no probe** (`flight-verification.md` "Every behavioural fix ships with a probe … in the same change"). Commit 7d09a69a: "the pushback suggestion loop survives an iteration failure (#231)". No probe carries `issue: 231` and no probe text mentions the suggestion loop; a log-signature probe on the loop's warning line would do.
- [JUDGEMENT] L — #208 (nested option lists no longer get defaults prepended) and #226 (radio-tune note timer released) ship with unit tests only. Neither is telemetry-observable, so a probe would be hollow; `requires-human-checklist` does not name them either. Docs/hygiene issues (#224, #227, #233, #235, #236), UI-only (#199, #234) and the non-fix mention of #190 ("evidence shows … cover it; comment only") are correctly probe-less.

**Phase-engine fixes additionally ship with a replayed recording (flight-verification.md; ADR-0008 §6).** Three commits touch `FlightStateEngine`/`FlightPhaseRules`:

- 077015ee (#138 taxi-out waits for the pushback flag) ships `lkpr-egll-2026-10-31-0.8.0-rc24.jsonl` + sidecar in the same commit. Compliant.
- [JUDGEMENT] M — 9cbd82bc (`FlightStateEngine.cs`: `_groundOps.BoardingCompleted += NotifyBoardingStarted`, "restart after boarding … sat in Preflight instead of Departure") ships a 22-line unit test only. The EHAM recording arrived four commits later (6724259e), and its sidecar `egll-eham-2026-11-01-1.0.0-rc1.expected.txt` runs `Preflight -> Departure, Departure -> Unknown, Unknown -> TaxiOut` — it does not visibly exercise a post-restart `Preflight -> Departure`, so whether the fix is replay-proven is **unverified**.
- [JUDGEMENT] L — 1b8c570a (#206 `PauseGraceParkedSeconds = 300`) adjusts the synthetic replay in `FlightReplayTests.cs` (envelope 100→340 s). A synthetic replay satisfies "replayed", but the 300 s figure is cited from EGLL 2026-11-01 without a checked-in recording; ADR-0008 §6 says "never a guessed constant".
- [JUDGEMENT] L — `egll-eham-2026-11-01-1.0.0-rc1.expected.txt` is the only sidecar with no provenance header (build, session id, what was trimmed/synthesised); every other sidecar has one.

**Guard test** (`tests/ProsimCompanion.Core.Tests/Verification/ProbeCatalogGuardTests.cs`). It genuinely derives event names from the source tree: `EmittedEventNames()` scans `src/**/*.cs` for the first argument of `eventLog.Record(` / `RecordEvent(` / `RecordEventEffect(` (literals anywhere in the expression plus `const`/`static readonly string` resolution), `"speech." + SpeechEventKind` by reflection, `EventName: "…"` tables in `*Phrases.cs`, and `SessionLogEventFormatter`'s switch; nine canaries and a `> 250` floor guard the derivation itself. Not a hand list. Limitation (by design, documented in the class summary): only `type` strings inside `machine` blocks are checked; prose `signature` strings — most probes — are not.

**Count: 6 findings (1 HARD M; 1 JUDGEMENT M, 4 L).**

## 3. LANGUAGE PACKS

Script `packs.ps1` flattens every `{code}/{page}.json`, compares id sets against `en`, and flags byte-identical values after stripping brand/acronym tokens (the guard's list plus ICAO/IATA/MSFS/ATC/UTC/…).

| lang | ids | missing | extra | copied (whole pack) | copied (branch ids) |
|---|---|---|---|---|---|
| de | 3922 | 0 | 0 | 166 | 2 (`Walkaround`, `Name`) |
| es | 3922 | 0 | 0 | 112 | 2 (`Roster` ×2) |
| fr | 3922 | 0 | 0 | 138 | 3 (`Patience` — real French, `Roster` ×2) |
| ja | 3922 | 0 | 0 | 15 | 0 |
| ko | 3922 | 0 | 0 | 15 | 0 |
| nl | 3922 | 0 | 0 | 203 | 1 (`Walkaround`) |

`en` gained 122 ids and changed 13 on the branch (135 total, 130 needing translation). **At HEAD every branch text exists in all seven packs and the only copies are loanwords. Compliant.** The whole-pack copies are overwhelmingly single-word labels (`Host`, `Port`, `Token`, `Backend`, `App`, `Panel`) inherited from main (main: de 171 / es 119 / fr 144 / ja 30 / ko 28 / nl 209); ja/ko's 15 are exactly the guard's `InvariantIds` plus Latin-less strings.

- [HARD] L — **Rule breached per commit, repaired by the branch.** `localisation.md` / owner rule 2026-10-13: every new text ships translated *in the same change*. The package commits f5c957bb (#194), 59d35201 (#195), 328a34ef (#196), ffe03909 (#197), bebeca20 (#198), 46b3e7f0 (#199) wrote their new ids into de/es/fr/ja/ko/nl as copied English; e6466012's de hunk shows `-"title": "Small Talk"` / `-"hint": "Now and then in the cruise the FO says…"` in `de/speechSettings.json`. e6466012 then translated 112–121 lines per language (its claim is accurate: the lines were branch-new ids, which is why a main↔HEAD "re-translation" count finds only 7–15 per language). Net result is clean; the per-commit rule was not followed.
- [JUDGEMENT] M — **The new pack guard catches only "pasted six times".** `ShippedLanguagePackGuardTests.cs:210-238` `EveryText_IsTranslated_InSomeLanguage_AndNeverLeftEnglishInJapaneseOrKorean` flags a text only when *all* non-en packs equal English (`unchanged.Count == packs.Count`), plus ja/ko individually. A text copied in de/es/fr/nl but translated in ja/ko passes. Verified live: `flightStatus.aircraftState.pass` is `"cold and dark ✓"` in en, de, es, fr and nl while ja/ko carry `コールド＆ダーク ✓` / `콜드 앤 다크 ✓` — the guard passes it. `longcopies.ps1` counts 13/11/9/15 (de/es/fr/nl) copied texts of four or more words at HEAD (`init.pageTitle = "EFB INIT - ProsimCompanion"`, `speechSettings.briefing.ask.flyingOver.label` outside its own InvariantIds effect, …). A per-language check with a per-language loanword allow-list, or at minimum "any Latin language keeps a 4+-word English sentence", would close the hole the commit message implies is closed ("checks words, not only ids").

**Count: 2 findings (1 HARD L; 1 JUDGEMENT M).**

## 4. OPTIONS ↔ UI

Properties **added** to `*Options.cs` on the branch (changed defaults excluded — `DayOptions.Enabled` false→true #216, `AutoCloseIdleMinutes` 90→300, `FoRadiosOptions.CheckInDelaySeconds` 8→4 are edits of existing fields; `SpeechOptions.ListeningTone` was removed, #232, and its `NotOnAPage` exemption dropped with it):

| Options class | Leaf properties added | `[OptionField]` | Web binding |
|---|---|---|---|
| `BriefingOptions` | `AskForQuestions` | yes | `SpeechSettings.razor` |
| `DayOptions` | `EndOfDutyOfferMinutes` | yes | `SpeechSettings.razor` |
| `FlightStateOptions` | `PauseGraceParkedSeconds` | yes | `FlightPhaseSettings.razor` |
| `WalkaroundOptions` (via `GroundCrewOptions.Walkaround`) | `Enabled, MinMinutes, MaxMinutes, FindingChance, AllowNoGo, OnInterphone` | 6/6 | `SpeechSettings.razor` |
| `PersonaOptions` / `PersonaRosterOptions` | `Confidence`; `Roster.Mode, Roster.GateIntro` | 3/3 | `SpeechSettings.razor` |
| `ScenarioOptions` (new section) | `Enabled, PerFlightChance, AllowMajor, TakeoffScenarios, MinAltitudeFt, ShowPlanned` | 6/6 | `SpeechSettings.razor` |
| `SmallTalkOptions` / `SmallTalkKindsOptions` (new section) | `Enabled, MaxPerFlight, MinQuietMinutes; Kinds.Offer/View/Layover/Crew` | 7/7 | `SpeechSettings.razor` |
| `TransitionReminderOptions` (via `SopOptions.Transition`) | `Enabled, DelaySeconds` | 2/2 | `SpeechSettings.razor` |

All 27 leaves carry `[OptionField]` and are bound on a settings page. The repo already fences this rule: `OptionFieldCompletenessTests` walks every section in `CoreRegistry()` (the two new sections are registered at `CoreServiceCollectionExtensions.cs:86,88`), requires a description or a reasoned `NotOnAPage` entry, and resolves each label/hint id in the `en` pack — it passed in the run above. Safe defaults: every added property has an initialiser or a value-type default (`Confidence = "steady"`, `Mode = ModeOff`, containers `= new()`); no `= null!`, no `required`. Lists/dictionaries: the roster and scenario catalogue are user files, and the page tables name them (`speechSettings.persona.roster.columns.*`).

**Count: 0 findings.**

---

**Totals: 15 findings — 2 HARD (1 M: #231 without a probe; 1 L: packages shipped copied English, fixed later on the branch), 13 JUDGEMENT (4 M, 9 L). No blocking standards breach at HEAD; the two M-rated judgement items worth acting on before merge are the per-language pack guard and the wall-clock `MicOwnership` test.**
