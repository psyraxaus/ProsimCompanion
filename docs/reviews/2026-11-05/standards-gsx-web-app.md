# Standards review — Gsx / Web / App (non-pack) / tools slice

Branch `fix/code-review-2026-11-03` vs `main` (0db90771…ee2c2ef0). Diff read hunk by hunk; full files opened where a hunk needed context. Standards cited: CLAUDE.md Conventions, `dotnet-best-practices/SKILL.md`, ADR-0001/0002/0010/0014/0016, `docs/agents/localisation.md`, `docs/ARCHITECTURE.md`, the owner's Snyk "no `key` in a new identifier" rule.

## What is clean (so the caller need not re-check)

- **Write safety.** Every LVAR write in the slice goes through `ISimVars.WriteAsync`, which is gated by `SimWriteGate` (`CompanionLvarNames.Prefix` is on `AllowedPrefixes`). `CompanionTrackingLvars` writes only `CompanionLvarNames.*` names, values are 0/1 or edition numbers, the queue is latest-value-per-LVAR under a lock, flushed on the disconnected→connected edge only, every failure is caught inside `Task.Run` so the fire-and-forget callers never see an unobserved exception. The jetway latch (`GsxJetwayStairsService.TryReserveRetraction`) is reserved before the slot wait, released on throw / non-dispatch, cleared on the four evidence edges (mirror no longer connected, re-connect, new gate, SID change, flight-cycle reset) and has a 90 s valve — idempotent against the four senders. `GsxTriggerSlot`'s duplicate rule (`occupied.Watched != request.NoConfirm`) is correct for all four pairings.
- **Composition root.** App code changes are three `<Content>` items in the csproj; `Program.cs`, the WPF shell and the DI root are untouched. New registrations sit in `GsxServiceCollectionExtensions` and `WebServiceCollectionExtensions` (ADR-0003 / CLAUDE.md "per-project extension methods"). ADR-0001 holds: no WPF settings UI.
- **Dependency direction.** Gsx still references only Core (+Analyzers); the new `FoAvailabilityStore`, `DutyDaySignals`, `ILegBoundaryRecovery`, `IFlightDataSource` collaborators are Core types, resolved as optional ctor params that MS DI nulls when unregistered.
- **Options → controls.** Every new option property in the Core diff has an `OptionField` in this slice (`PauseGraceParkedSeconds`, `EndOfDutyOfferMinutes`, `AskForQuestions`, `Walkaround.*`, `SmallTalk.*`, `Scenario.*`, `Roster.Mode/GateIntro`, `Confidence`, `Transition.*`, `FoRadios.CheckInDelaySeconds`). JSON-edited files are named in visible hints (`walkaround-findings.json`, `scenarios.json`, `Roster.FilePath`).
- **Localisation.** Every new Razor text in SpeechSettings, SettingsLayout, Section, Day, Home, Monitor, Setup goes through `@T[…]`. No catch-all `@page`. No blocking on async in any Blazor handler; the two `async void`-equivalents (`OnFoldsChanged`, `OnRosterChanged`) wrap `InvokeAsync` with catch inside. The web token is never logged.
- **Reduce tool.** No new file-API or console path; `machine.Key` / `lastByKey` are pre-existing identifiers. Raw bundle payload quoted into evidence lines is pre-existing behaviour, not introduced here.

## src/ProsimCompanion.Web/Layout/SettingsSearch.cs

- `[HARD] [M] SettingsSearch.cs:32, :209, :217` — three new identifiers contain `Key` — localisation.md "(rule, owner 2026-10-13) Never use the word `key` in any identifier, not even compound"; ADR-0014 §6; Snyk rule. `SettingsNavItem.Key` is pre-existing, these are new.
  ```csharp
  public static readonly IReadOnlyList<(string Root, string RouteBase, string? GroupKey, string? ItemKey)> Roots =
  ...
  if (entry.GroupKey is not null)
  ...
  var itemKey = entry.ItemKey
      ?? (BlockSections.TryGetValue(block, out var mapped) ? mapped : null)
  ```
  Rename to `GroupId`/`ItemId`/`itemId` (or `SectionName`).

## src/ProsimCompanion.Web/wwwroot/js/app.js

- `[HARD] [M] app.js:698` — new identifier `FOLDS_KEY` — same owner rule (Snyk Code scans the JS too). `WAKE_KEY` at :32 is a pre-existing precedent, which does not license a new one.
  ```js
  var FOLDS_KEY = "prosimCompanion.settingsFolds";
  ```

## src/ProsimCompanion.Web/Components/Diagnostics/EcamStatusPage.razor

- `[JUDGEMENT] [M] EcamStatusPage.razor:126-140` — broad catch that neither logs nor narrows, and surfaces `ex.Message` on the display — SKILL.md "A blanket `catch (Exception)` that swallows silently is a defect; if you catch broadly, log it". The sibling `EcamGsxPage` GPU box injects `ILogger` and filters `OperationCanceledException`.
  ```csharp
  catch (Exception ex)
  {
      _clearNote = ex.Message;
  }
  ```
- `[JUDGEMENT] [L] EcamStatusPage.razor:85-98` — four new avionics literals inline (`SCENARIO`, `FAILURES`, `CLEAR ALL`, `SDK FAILURE API N/A`) — ADR-0016 §6 says tokens "are avionics tokens in `EcamTokens`, not pack texts"; the GSX page's `GPU` / `PUSHBACK` already sit inline, so this follows precedent rather than the letter. Tooltip correctly uses `T[...]`.
- `[JUDGEMENT] [L] EcamStatusPage.razor:71-72, :119` — `Services.GetService<ScenarioStore>()` / `GetService<IScenarioControl>()` resolved from `IServiceProvider` on every render (service-locator, Hidden Dependency); precedent in `EcamGsxPage`, and it is the degrade-not-fail path for a Speech-pillar type, so acceptable.

## src/ProsimCompanion.Web/Pages/SpeechSettings.razor

- `[JUDGEMENT] [L] SpeechSettings.razor:1200-1220` — Duplicated Code: `OnRosterChanged` and `OnPersonaOptionsChanged` have identical bodies.
  ```csharp
  private void OnRosterChanged()
      => _ = InvokeAsync(() => { if (!_persona.IsDirty) { _persona.Reload(); } StateHasChanged(); });
  private void OnPersonaOptionsChanged(PersonaOptions changed)
      => _ = InvokeAsync(() => { if (!_persona.IsDirty) { _persona.Reload(); } StateHasChanged(); });
  ```
- `[JUDGEMENT] [L] SpeechSettings.razor (1,406 lines, +201)` — Divergent Change: one file now carries 16 sections for three rail groups (ADR-0010 amendment §9 split the *rail*, not the page). Also `_personaChange` is declared at :1385 between methods rather than with the other fields. No documented size rule, so judgement only.

## src/ProsimCompanion.Gsx/Automation/GsxAutomationService.cs

- `[JUDGEMENT] [M] GsxAutomationService.cs:203-215 and :255-272` — Duplicated Code: `OnDutyDayEnded` and `OnOfpChanged` repeat the same six-step departure reset (ResetCycle(ArrivalSide) → BeginTurnaroundCycle → paxTarget.Reset → slot.Reset → ClearDepartureTracking → `gsx-cycle-reset` event). Extract `ResetDepartureCycle(reason, …)`.
  ```csharp
  _lifecycle.ResetCycle(NewPlanCycleReset.ArrivalSide);
  _cycle.BeginTurnaroundCycle();
  _paxTarget.Reset();
  ...
  _slot.Reset("new flight plan — departure cycle reset");
  var trackingLvars = ClearDepartureTracking();
  ```
- `[JUDGEMENT] [L] GsxAutomationService.cs:108-111` (also `DeparturePumpGatherer.cs:89`, `GsxSimbriefReloadService.cs:80-81`, `GsxServiceControl.cs:80-84`) — the new collaborator is validated and assigned *before* the existing validate-all-then-assign block — SKILL.md "Match the surrounding file's style".
  ```csharp
  ArgumentNullException.ThrowIfNull(tracking);
  _tracking = tracking;
  _dutyDay = dutyDay;
  ArgumentNullException.ThrowIfNull(api);
  ```
- `[JUDGEMENT] [L] GsxAutomationService.cs:77` — `_lastResetPlanId` is a mutable field declared between `readonly` ones and is read on the `OfpStore.Changed` thread (:249) while written on the resync callback (:335) and the duty-day event (:211) with no lock — unverified race.

## src/ProsimCompanion.Gsx/Services/GsxServiceLifecycleTracker.cs

- `[JUDGEMENT] [M] GsxServiceLifecycleTracker.cs:228-238` — Speculative Generality / dead code: `AnyObservedProgress(IReadOnlySet<string> exempt)` has no production caller (only `GsxServiceLifecycleTrackerTests`); the #213 change replaced it with `ObservedProgress(serviceId)`.

## src/ProsimCompanion.Gsx/Gate/GateDispatchPlanner.cs

- `[HARD] [L] GateDispatchPlanner.cs:77` — public API without an XML doc — CLAUDE.md "XML doc comments on public API"; the sibling `NoAirportResendBackoff` above it is documented.
  ```csharp
  public const int MaxSendsAfterPick = 5;
  ```

## src/ProsimCompanion.Gsx/Sync/GsxGateAnchorService.cs

- `[JUDGEMENT] [L] GsxGateAnchorService.cs:25-36, :136-144` — takes the concrete `Core.EventLog.JsonlEventLog?` while `GsxAutomationService` writes the same event stream through `IGsxDecisionLog.RecordEvent`; two paths to one log (Divergent Change), and a concrete-class dependency where the repo's convention is a narrow `I` interface.

## src/ProsimCompanion.Gsx/Sync/GsxSimbriefReloadService.cs

- `[JUDGEMENT] [L] GsxSimbriefReloadService.cs:166-176` — `IsNextLegPlan` (which takes `DepartureCycleLatches._gate`) is now called inside this class's own `lock (_gate)`; a new lock-nesting order whose reverse I did not find, but it is unverified.

## src/ProsimCompanion.Gsx/Sync/CompanionTrackingLvars.cs

- `[JUDGEMENT] [L] CompanionTrackingLvars.cs:150-160` — `InvalidOperationException` is treated as "MSFS not connected" and queued, but `ISimVars.WriteAsync` documents the same exception for "not on the sim write allow-list". Pre-existing logic moved from `GsxStartupResyncService`, and every name written here is under the allow-listed prefix, so it is latent — but the class now advertises itself as "the one writer", so a future name would queue and retry forever instead of failing loudly.

## Bugs noticed

- `[S, unverified] GsxAutomationService.cs:244-258 / NewPlanCycleReset.cs:37-55` — `NewPlanCycleReset.ShouldReset`'s doc says `lastResetPlanId` is "the plan the last reset ran for (or the plan the cycle was started with)", but nothing pins `_lastResetPlanId` when a cycle *starts* (only after a reset, after end-of-duty, and in `RestoreCompletedCycleFromSeeds`). `OfpStore.Set` raises `Changed` unconditionally. So a re-import of the *same* plan while parked, engines off, before boarding has been observed (e.g. the resync seeded Refueling, the pilot refetches the OFP) satisfies `id != null && id != lastResetPlanId(null)` → resets the departure side, forgets the seeded refuel and zeroes its tracking LVAR. Needs a test with "same RequestId re-imported mid-cycle"; if intended, the doc comment is wrong.
- `[S, latent] CompanionTrackingLvars.cs:150` — the allow-list/not-connected ambiguity above: an unlisted name would be re-queued on every connected edge with only a Debug line.

## Counts

hard: 4 (SettingsSearch `GroupKey/ItemKey/itemKey`; app.js `FOLDS_KEY`; `MaxSendsAfterPick` undocumented — the first two are one rule, M) / judgement: 12 / bugs: 2 (both S, one unverified, one latent)
