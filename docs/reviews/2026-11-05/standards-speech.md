# Standards review — slice `src/ProsimCompanion.Speech` (main 0db90771 … HEAD ee2c2ef0)

Sources read: CLAUDE.md Conventions, `~/.claude/skills/dotnet-best-practices/SKILL.md`, docs/agents/localisation.md, ADR-0018, ADR-0019, ARCHITECTURE.md dependency rules, the owner "key" rule. Dependency direction is enforced by the csproj (Speech references Core only) — nothing in the slice reaches Gsx/Prosim/Sim/Audio types; skipped as tooling-enforced. The `_ticking`/`Interlocked` tick guard appears in 17 files on `main` already; the four new copies follow house style and are not reported as duplication.

## Scenarios/ScenarioService.cs (new, 964 lines)

A pure core does exist — `Core/Scenarios/ScenarioScheduler` (`Plan`, `Evaluate`, `Allowed`, `DecisionLine`) — and the service exposes `ProcessTick(DateTimeOffset)` / `BuildTick`, so the CLAUDE.md "ProcessTick-style core" rule is met. The size comes from the shell owning five unrelated reasons to change.

- [JUDGEMENT] [M] ScenarioService.cs:36 — one class is the scheduler shell, the serialised SDK write path, the LLM decision turn, the diversion declaration, the voice feature and the status-store projector — Divergent Change. The decision turn (`RaiseDecisionAsync` + `OnDecisionAnswered`, ~90 lines, needs `_llm/_llmHealth/_facts/_persona/_diversion/_ofp`) is a self-contained `ScenarioDecisionService` waiting to be cut out; the six optional ctor parameters exist only for it.
  ```csharp
  public sealed class ScenarioService : Core.Hosting.IStartupModule, IHostedService, IScenarioControl, IVoiceFeature, IDisposable
  ```
- [JUDGEMENT] [M] ScenarioService.cs:461 — the SDK arm condition is chosen by sniffing the abnormal id for the substring `after-v1` — Primitive Obsession; the catalogue entry already carries `Takeoff` and should carry the after-event too (ADR-0018 §5 says the catalogue is the only source of what is armed).
  ```csharp
  var condition = plan.Takeoff
      ? new FailureArmCondition(plan.AbnormalId.Contains("after-v1", StringComparison.OrdinalIgnoreCase) ? FailureAfterEvent.V1 : FailureAfterEvent.Takeoff, 0)
      : null;
  ```
- [HARD] [L] ScenarioService.cs:211, :731, :876 — fire-and-forget `Task.Run(() => ClearAllAsync(...))` / `ClearOursAsync(...)` with no catch: `_writes.WaitAsync` throws `OperationCanceledException` once `_shutdown` is cancelled and `ObjectDisposedException` after `Dispose` releases the semaphore; the exception is unobserved. SKILL.md "Catch only what you can act on… if you catch broadly, log it" and CLAUDE.md "A failed subsystem logs". `ArmPlanAsync` does catch; the three clear paths do not.
  ```csharp
  _ = Task.Run(() => ClearAllAsync("startup", _shutdown.Token));
  ```
- [JUDGEMENT] [L] ScenarioService.cs:264 — `Dispose` disposes `_writes` and `_shutdown` but never `_armCancel` (SKILL.md resource rule); also `_lastHold` is written in `ProcessTick` and `OnFlightCycleReset` on different threads outside `_gate` — benign race, but the lock exists.
- [JUDGEMENT] [L] ScenarioService.cs:797 — the manual arm forges the phase dwell (`tick with { PhaseSinceUtc = Now().AddHours(-1) }`) to bypass the scheduler's "held: phase too young" rule. Works, but a `ScenarioScheduler.Evaluate(..., ignoreDwell: true)` would say what is meant.

## Persona/CruiseSmallTalkService.cs (new, 717) + SmallTalkCore.cs + SmallTalkBank.cs

Pure core present (`SmallTalkCore.Due/Claim/Release`, `SmallTalkBank` pure); `ProcessTick`/`BuildTick` exposed. The shell is long because it carries three delivery tiers (streamed narration, composed offer, listen-and-follow-up).

- [JUDGEMENT] [M] CruiseSmallTalkService.cs:39 — like ScenarioService, one class owns the scheduler shell, the streamed-model tier, the offer dialogue, the layover listen, the purser coffee follow-up and the context builder (place, sun, weather, roster). The `BuildContext` block (60 lines, five optional dependencies) is a `SmallTalkContextSource` on its own.
- [JUDGEMENT] [L] CruiseSmallTalkService.cs:288 — dead branch: both arms return `(null, "llm")`.
  ```csharp
  if (result.Preempted || result.Outcome != SpeechOutcome.Suppressed)
  {
      return (null, "llm");
  }
  return (null, "llm"); // suppressed
  ```
- [JUDGEMENT] [L] CruiseSmallTalkService.cs:200 vs :252 — `OnSpeechObserved` stamps `DateTimeOffset.UtcNow` while the tick uses the injected `nowUtc`; two clocks in one gate table (the ScenarioService has `Now` injectable for exactly this).
- [JUDGEMENT] [L] CruiseSmallTalkService.cs:666 — `Give(kind, nowUtc, reason)` is a Mysterious Name for "release the claimed kind back to the core"; `ReleaseKind` reads.
- [JUDGEMENT] [L] SmallTalkOfferPhrases.cs:77 — `CommandVocabularyNormalize` is a one-line Middle Man around `CommandMatcher.Normalize`; the sibling tables call it directly.

## Crew/WalkaroundService.cs (new, 597) + WalkaroundCore.cs

Pure core present and well documented; `ProcessTick`/`BuildSample` exposed.

- [JUDGEMENT] [M] WalkaroundService.cs:574 (`RingInterphoneAsync`) — re-types the ground-upcall delivery shape line for line (MECH press with the same catch pair, then `AwaitReceiveAsync(Intercom, grace)`, same log texts) that already lives in `GroundCrewUpcallService.RunUpcallAsync` (lines 177–207 on disk). Duplicated Code; the summary even says "exactly as the ground upcalls arrive". Extract an `InterphoneRing` helper both call.
  ```csharp
  await _dataRefs.PressMomentaryAsync(ProsimDataRefNames.OhCallsMech, _shutdown.Token).ConfigureAwait(false);
  ...
  _logger.LogInformation("INT channel not selected within grace — playing the walkaround report anyway");
  ```
- [JUDGEMENT] [M] WalkaroundService.cs:475 — `Outcome()` returns the strings `"deice" | "ask" | "raise" | "cosmetic" | "none"` and `HandleFindingAsync` switches on them — Primitive Obsession for a four-valued domain decision; an enum costs nothing and the switch becomes exhaustive.
- [JUDGEMENT] [L] WalkaroundService.cs:552 — `CurrentConditions(WalkaroundSample sample)` never reads `sample` (0 uses); it reads `_flight.Snapshot()` instead. Either use the sample passed in (the core's input) or drop the parameter.
- [JUDGEMENT] [L] WalkaroundService.cs:541 — "night" hard-coded as `< 06:00 || >= 20:00`; `SolarPosition` is already used next door for the sun side.
- [HARD] [L] WalkaroundService.cs:536 — `_techLogDialogue.RaisePrefilledAsync(...)` and `TechLogDialogueService.RaisePrefilledAsync(string, MelCategory, string)` take no `CancellationToken`; the service has `_shutdown`. SKILL.md "Honour CancellationToken… pass it down rather than dropping it". (The existing `RunDialogueAsync` wrappers share the omission, so this follows local style — flagged because it is a new public API.)

## SayIntentions/FlightJsonFile.cs, FoRadiosService.cs, FoRadiosComposer.cs

- [HARD] [M] FlightJsonFile.cs:48, :57 — new identifier `_lastGoodApiKey` contains the banned substring "key" (owner Snyk rule 2026-10-13: never in any new identifier; CLAUDE.md memory "use name/id/textId"). The pre-existing `apiKey` local is grandfathered; the new static field is not. Also a mutable `static` cache field declared between two methods.
  ```csharp
  return _lastGoodApiKey;
  ...
  private static string? _lastGoodApiKey;
  ```
- [HARD] [M] FoRadiosService.cs:232 — new local `var key = icao + "/" + runway;` — same rule. `pair`/`runwayId` reads fine.
- [HARD] [L] FoRadiosComposer.cs:94–100 — the `<param name="ourAtisLetter">` block (and the three older `<param>`s) now sit above `IsBroadcast`'s `<summary>`, so `Compose` has no doc and `IsBroadcast` carries four foreign `<param>` tags. CLAUDE.md "XML doc comments on public API". Not tooling-enforced here (the build emits no CS157x).
- [JUDGEMENT] [L] FoRadiosService.cs:87 — `_ofp = ofp; _weather = weather;` assigned before the `ThrowIfNull` block; the same "new optional dep assigned first" appears in FoRosterVoiceFeature.cs:67 (`_clock = clock;`), FmgcCrossCheckService.cs:68, UtteranceRouter.cs:131, BriefingService.cs:127. Style only, but five new copies of a shape the rest of each ctor avoids.
- [JUDGEMENT] [L] FoRadiosService.cs:323, :357, :503 — the "unanswered ATC line" event is built three times as the same anonymous shape (`payload{atc,kind,transmit}, spoken:"", radioWaitMs:0, radioBusyAtTx:false, sent:false`) — Duplicated Code; `RecordDrop` already exists, the `kind:"none"` path should call it with its own kind.
- [HARD] [L] SayIntentionsFrequencyMonitor.cs:452 and FoRadiosService.cs:317/:343 — ATC text from the SayIntentions network reply is logged raw (`"ATC #{Id} ({Station}): \"{Text}\""`); the Snyk rule asks for CR/LF stripping on external strings before logging, and the same change added `NarrationPlan.OneLine` for exactly that on model text. Unverified whether Snyk flags this path.

## Day/CompanyDayService.cs

- [JUDGEMENT] [L] CompanyDayService.cs:764 — `DebriefTag` and :853 `EndOfDutyPrompt` are `public const` declared mid-class between private methods; the file's other constants sit at the top.
- [JUDGEMENT] [L] CompanyDayService.cs:778 — `DefectTitlesRaisedSafe` spells `System.Globalization.CultureInfo` / `DateTimeStyles` fully qualified inside a 10-line LINQ predicate; a `using` and a small `RaisedWithin(day, defect)` predicate would make the rule readable.

## Callouts/TransitionReminderService.cs (+Core)

Pure core, injectable `Clock`, `ProcessTick` — the pattern the brief asks for.

- [JUDGEMENT] [L] TransitionReminderService.cs:142 — passes the literal `true` for `TransitionReminderInput.Enabled` after already returning on `!transition.Enabled`; the core's `Enabled` field is Speculative Generality (its doc says "read per tick", but the shell never passes anything but `true`).
- [JUDGEMENT] [L] TransitionReminderService.cs:177 — `_ = _arbiter.EnqueueAsync(...)` fire-and-forget without observing faults; same in CompanyDayService.Speak (pre-existing), SpokenChecklistEngine.cs:350 (new), UtteranceRouter.cs:494 (new). `FoRosterVoiceFeature.Speak` shows the house-safe form (`ContinueWith(static t => _ = t.Exception, OnlyOnFaulted)`).

## Briefings/BriefingQuestionsPhrases.cs

- [HARD] [L] BriefingQuestionsPhrases.cs:104 — `catch (Exception) { }` swallows silently with a comment that "the question service logged its own failure"; SKILL.md: a blanket catch that swallows silently is a defect — log at Debug or narrow to `OperationCanceledException`.
  ```csharp
  catch (Exception)
  {
      // The question service logged its own failure; the round still closes properly.
  }
  ```
- [JUDGEMENT] [L] BriefingQuestionsPhrases.cs:84 — `NonePhrases.Select(CommandMatcher.Normalize)` is recomputed on every utterance; `FoRosterVoiceFeature` pre-normalises its tables (`NormalizedWho`) — be consistent.

## Recognition/UtteranceRouter.cs

- [HARD] [L] UtteranceRouter.cs:449–454 — the new `SkipWhileFoAway` summary was inserted *under* `HandleIdleMiss`'s existing summary, so `SkipWhileFoAway` has two `<summary>` elements and `HandleIdleMiss` has none. CLAUDE.md XML-doc rule.

## Abnormals/AbnormalBurstGuard.cs

- [JUDGEMENT] [L] AbnormalBurstGuard.cs:48 — `t.Id.Contains("fire")` decides "two fire loops" by substring of the abnormal id — Primitive Obsession; a definition flag (`IsFireLoop`) or the definition's group would survive an id rename.

## Persona/FoRosterVoiceFeature.cs

- [HARD] [L] FoRosterVoiceFeature.cs:283 — `_styled.StyleAsync(template, ..., $"roster-intro:{fo.Id}:{word}")` and the following `EnqueueAsync` pass no cancellation token although the service runs this on `Task.Run` at startup; every sibling shell passes `_shutdown.Token`. SKILL.md cancellation rule.

## Cross-cutting

- [JUDGEMENT] [L] Core-built English — `CommandResult` messages in `ScenarioService.ArmAsync/ClearAsync/ClearAllAsync/ListFailuresAsync` ("A scenario id is required.", "ProSim is unreachable — check the IOS.") reach the STATUS page and the API untranslated. localisation.md scopes the rule to Razor/UI text and the repo's known list of Core-built English texts already accepts this family, so judgement only; the spoken FO lines are English by design and are not findings.
- Options defaults / web-UI controls for the new options (`ScenarioOptions`, `SmallTalkOptions`, `GroundCrewOptions.Walkaround`, `SopOptions.Transition`, `DayOptions.EndOfDutyOfferMinutes`, `BriefingOptions.AskForQuestions`, `CommsHandover.HoldingPointDistanceNm`) live in Core/Web — outside this slice; the Core/Web reviewer should confirm each has a control.
- Logging: every new log line uses structured placeholders; no interpolated `_logger.Log*($"…")`, no `async void`, no `.Result/.Wait()` anywhere in the slice; `ConfigureAwait(false)` is consistent. No API key or token is logged (`FoRadiosService` logs only that the key is absent).

## Bugs noticed (S)

- [JUDGEMENT] [S] CruiseSmallTalkService.cs:706 `FirstSentence` cuts at the first `.`, so a model reply containing a decimal ("about 1.5 hours to Amsterdam") becomes "about 1." — the truncated digit then passes or fails `NumberVerifier` on the wrong figure and the FO speaks a fragment. Used for the offer line and the layover follow-up. Needs a sentence splitter that ignores a `.` between digits (and "Mr."-style abbreviations).
  ```csharp
  var end = text.IndexOfAny(['.', '!', '?']);
  return end > 0 && end < text.Length - 1 ? text[..(end + 1)].Trim() : text;
  ```
- [JUDGEMENT] [S] ScenarioService.cs:264 vs :552 — `Dispose` disposes `_writes` while an arm/clear task may still hold it; `finally { _writes.Release(); }` then throws `ObjectDisposedException` inside a fire-and-forget task (see the unobserved-exception finding) — on app exit only, after `StopAsync`'s own clear has run, so low likelihood but a real crash-at-exit path when `_shutdown` is cancelled mid-write.
- [JUDGEMENT] [S] ScenarioService.cs:633 `OnAbnormalEnded` latches `_decisionAsked = true` *before* the `reason == "session-ended"` early return; a session reset during a fired scenario means the decision turn can never be raised for that plan even if the plan survives (`Fired` is not persisted, so only within the process). Probably intended; flagged because the latch and the return are in the wrong order for the comment's "once".
- [JUDGEMENT] [S] WalkaroundService.cs:480 — in the `"ask"` branch `_pendingFinding` is overwritten before `_raise.Ask(...)`; if `Ask` returns false because an earlier walkaround-raise question still stands, that earlier question's `Answered` now consumes the *new* finding (`OnRaiseAnswered` reads whatever `_pendingFinding` holds) and the captain's "yes" logs the wrong defect. Restore the previous finding (or refuse the overwrite) when `Ask` is refused. Unlikely in practice (one walk per cycle) but the ordering is wrong.

## Count

hard 9 / judgement 22 / bugs 4
