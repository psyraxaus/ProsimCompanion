# Standards review — slice Core / Sim / Prosim / Audio (main 0db90771 … HEAD ee2c2ef0)

Scope: `git diff main...HEAD -- src/ProsimCompanion.Core src/ProsimCompanion.Sim src/ProsimCompanion.Prosim src/ProsimCompanion.Audio` (102 files, +5681/−518), read hunk by hunk with the full files opened where needed. Standards read first: CLAUDE.md Conventions, dotnet-best-practices SKILL.md, ARCHITECTURE.md dependency/resilience rules, ADR-0018, ADR-0019, the owner "key"/Snyk rule.

Checked and clean (no finding): dependency direction (Core references only Core; `IFailureInjector`, `IScenarioControl`, `ILegBoundaryEvents`, `DutyDaySignals` are Core seams implemented by Prosim/Speech; Prosim/Sim/Audio reference Core only); no `async void`; no `.Result`/`.Wait()`; every `await` in library code carries `ConfigureAwait(false)`; `CancellationToken` passed down through `IFailureInjector` → `SdkConnection.RunAsync`; no `ReadDataRef` polling (all new refs are `Subscribe` once: `FmsPlanWatcher`, `SimLocalTime`, `GateMonitorService`, `ProsimFlightDataSource`); every new options property has a safe default and an `[OptionField]`; no credential/token logging; all log calls use structured placeholders; `SdkFailureInjector` writes only through `ProsimFailureGate.EnsureAllowed` (catalogue ∩ live list) inside the SDK lambda, before `ArmedFailure.Create`, and clear-all including IOS failures is what ADR-0018 §3 specifies; `DayStateFile`/`FoRosterStateFile`/`ScenarioStateFile` never throw and degrade.

## src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs

`[HARD] [M] src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs:365` — new local identifier contains the banned substring "key" — owner Snyk rule (brief §9: "NEVER the substring 'key' in any new identifier; use name/id/textId"). `p.Key`/`Parts.Keys` are pre-existing BCL/interface members and are fine; the local is new.
```csharp
var boundaryKeys = _parts.Where(p => p.PersistsAcrossLegs).Select(p => p.Key).ToHashSet(StringComparer.Ordinal);
return snapshot.Parts.Keys.All(boundaryKeys.Contains);
```

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs:100` — field assigned before the `ThrowIfNull` block that the rest of the ctor follows — SKILL "match the surrounding file's style".
```csharp
_dutyDay = dutyDay;
ArgumentNullException.ThrowIfNull(parts);
```

## src/ProsimCompanion.Core/Notifications/NotificationSignalSource.cs

`[JUDGEMENT] [M] src/ProsimCompanion.Core/Notifications/NotificationSignalSource.cs:1` — commit 1b8c570a rewrote the file with CRLF endings (verified: `git show HEAD:` blob is `\r\n`, main's is `\n`, every other Core `.cs` is LF; no `.gitattributes`/`end_of_line` rule exists) so the 810-line diff hides a 54-line change (`git diff -w`) and the tree now has one CRLF island — not a documented standard, but it defeats blame/review and will flip back on the next editor save.

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Notifications/NotificationSignalSource.cs:64–66` — member order broken by the rewrite: two fields now sit after a public property, and `Settle` is a public settable knob that exists only for tests (Speculative Generality — an `internal` setter or ctor parameter like the existing `clock` would do).
```csharp
public TimeSpan Settle { get; set; } = RestoreSettle;
private bool _holdoverWarned;
private FlightTimesSnapshot _lastTimes = FlightTimesSnapshot.Empty;
```

## src/ProsimCompanion.Core/Configuration/OptionsListBinding.cs

`[JUDGEMENT] [M] src/ProsimCompanion.Core/Configuration/OptionsListBinding.cs:161,191` — two blanket `catch (Exception)` that return silently — SKILL "a blanket catch that swallows silently is a defect; if you catch broadly, log it". The class is static with no logger; a failed `Activator.CreateInstance` or a serializer throw means the repair silently did not happen.
```csharp
catch (Exception)
{
    return null;
}
```

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Configuration/OptionsListBinding.cs:174–186` — item equality by `JsonSerializer.Serialize` string comparison on every post-configure pass (Primitive Obsession: the list item types are records; a `SequenceEqual` on records, or an explicit comparer, is the honest shape). Also `IsOurs` walks every Core class reachable from an options object, not only option sections — a wider reflection surface than the stated need.

## src/ProsimCompanion.Core/Flight/LegBoundaryService.cs

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:84 (and 7 more)` — `DateTimeOffset.UtcNow` read directly in eight places while the sibling `FlightStateSnapshotService`, `NotificationSignalSource` and `GateMonitorService` take an injectable clock — CLAUDE.md "favour … cores … testable without timers" is met by `LegBoundaryCore`, but the service's event stamps are untestable.
```csharp
_core.PlanLoaded(current, DateTimeOffset.UtcNow);
```

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:90–96` — the constructor subscribes to four hubs and may publish; it is registered via `AddStartupModule` but does not implement `IStartupModule`, so activation-is-start is implicit. Works (the generic has only `where T : class`), but every other startup module does its wiring in `Start()`.

## src/ProsimCompanion.Core/Day/DutyDaySignals.cs

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Day/DutyDaySignals.cs:25` — subscriber invocation unguarded: one throwing subscriber (the snapshot service does file IO in `Invalidate`) stops the later ones (the leg-boundary reset) and propagates into the Speech end-of-duty path — CLAUDE.md "degrade, not fail". `FmsPlanWatcher` and `LegBoundaryService.Publish` wrap their raises; `GroundOpsSignals` does not, so this matches the older hub — flagged as a nit.
```csharp
DayEnded?.Invoke(ended);
```

## src/ProsimCompanion.Core/State/GroundOpsSignals.cs

`[JUDGEMENT] [M] src/ProsimCompanion.Core/State/GroundOpsSignals.cs:40,113` — the reset "kind" travels as mutable shared state set just before the event fires and read by handlers "on the raising thread" — a temporal coupling that the event argument should carry (`event Action<FlightCycleResetKind>`); two resets racing from different threads read the wrong kind.
```csharp
public FlightCycleResetKind LastFlightCycleResetKind { get; private set; } = FlightCycleResetKind.Arrival;
…
LastFlightCycleResetKind = kind;
Volatile.Write(ref _boardingUnderway, 0);
FlightCycleReset?.Invoke();
```

## Duplicated code across the new banks / state files (Core/Crew, Core/Persona, Core/Scenarios)

`[JUDGEMENT] [M] src/ProsimCompanion.Core/Crew/WalkaroundFindingBank.cs:48, src/ProsimCompanion.Core/Scenarios/ScenarioCatalogueBank.cs:47, src/ProsimCompanion.Core/Persona/FoRosterService.cs:271` — the "PhraseBank pattern" (`GetLastWriteTimeUtc` → compare → absent/parse/`ConfigProblemStore` report, `catch … JsonException or IOException`) is now copied a fifth time (also `Speech/Persona/PhraseBank.cs`, `Speech/Immersion/RegionFactBank.cs`) — Duplicated Code; one generic `UserConfigFileBank<T>(parse)` would hold the shape once. Same for the three parsers (`Text(JsonElement, name)`, `seen` set, `entry {index}:` problems, `NullIfBlank`) at `FoRosterModels.cs:130`, `WalkaroundFindings.cs:133`, `ScenarioModels.cs:132`.
```csharp
catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException)
{
    _logger.LogWarning(ex, "walkaround-findings.json unreadable — keeping the last good bank ({Count} findings)", _findings.Count);
```

`[JUDGEMENT] [M] src/ProsimCompanion.Core/Persona/FoRosterStateFile.cs:69, src/ProsimCompanion.Core/Scenarios/ScenarioModels.cs:261, src/ProsimCompanion.Core/Day/DayStateFile.cs` — three copies of "lock, read, empty = none, corrupt → `.corrupt-<stamp>.bak`, temp-then-move save, delete temp on failure" — Duplicated Code (the commit message itself calls it "the fo-roster-state.json pattern"). One `JsonStateFile<T>` would also fix the next bug uniformly.

`[JUDGEMENT] [L] same three banks` — the reload catches only `JsonException`/`IOException`; an `UnauthorizedAccessException` on a user-config file escapes the property getter (`Findings`, `Entries`, `Pool`) into whichever feature touched it — CLAUDE.md "degrade, not fail".

## src/ProsimCompanion.Core/Scenarios

`[JUDGEMENT] [M] src/ProsimCompanion.Core/Scenarios/ScenarioScheduler.cs:86` — EFATO-vs-RTO is inferred by substring-sniffing the id/abnormal id instead of a catalogue field — Primitive Obsession / Mysterious Name (a renamed entry silently flips which switch gates it).
```csharp
var isEfato = entry.AbnormalId.Contains("after-v1", StringComparison.OrdinalIgnoreCase)
    || entry.Id.Contains("efato", StringComparison.OrdinalIgnoreCase);
```

`[JUDGEMENT] [L] src/ProsimCompanion.Core/Scenarios/ScenarioModels.cs:31` — `ScenarioEntry` is a 13-positional-parameter record built by a 13-argument `new` in the parser (Data Clump / long parameter list; named or `init` properties would keep the parser readable).

## src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs

`[JUDGEMENT] [L] src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs:30–31` — `_unavailableLogged` / `_sdkMismatchLogged` are plain bools written from pool threads next to a `volatile _available`; harmless (a duplicate log line) but inconsistent.

`[JUDGEMENT] [L] src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs:182` — `catch (Exception)` with a comment and no log — SKILL "if you catch broadly, log it" (a `LogDebug` would do; the sibling `ClearAllAsync` does log).

## XML docs on public API

`[HARD] [L]` — CLAUDE.md "XML doc comments on public API" (not tooling-enforced: no `GenerateDocumentationFile`/CS1591): `ScenarioModels.cs:150–166` (`ScenarioPlan` properties bar two), `ScenarioCommandHandlers.cs:30` (class + `Register`), `WalkaroundFindings.cs:144` (`Draw`), `SolarPosition.cs:19` (`At` has params, no summary), `FoRosterService.cs` public members (`Changed`, `FilePath`, `IsActive`, `Pool`, `State`, `Current` — no `<inheritdoc/>`), `DutyDaySignals.cs:23` (`RaiseDayEnded`), `LegBoundaryService.cs:59` (`LegBegan`), `FmsPlanWatcher.cs` (`Origin`, `Destination`).

## Nits (JUDGEMENT L)

- `src/ProsimCompanion.Core/Aircraft/SimLocalTime.cs:46` — field declared after the constructor, unlike its three siblings above it.
- `src/ProsimCompanion.Core/Aircraft/SimLocalTime.cs:94` — the `catch (ArgumentOutOfRangeException)` for "day 366 of a non-leap year" never fires: `DateOnly.AddDays` rolls into the next year; the comment is wrong, the guard dead.
- `src/ProsimCompanion.Core/Boarding/GateMonitorService.cs:184` — a private field declared mid-class between methods.
- `src/ProsimCompanion.Core/Radios/Com1PanelService.cs:154` — public property appended after the private static helpers.
- `src/ProsimCompanion.Prosim/Flight/ProsimFlightDataSource.cs:128` — fully-qualified `Microsoft.Extensions.Options.IOptionsMonitor<Core.Configuration.SpeechOptions>` inline instead of `using`s.
- `src/ProsimCompanion.Core/Flight/FlightDataSnapshot.cs:46` — `IndicatedAltitudeFt == 0` doubles as "not pushed" and "sea level"; `SpokenAltitudeFt` falls back to true altitude for a legitimate 0/negative indication (only below-sea-level fields; Primitive Obsession on a sentinel).
- `src/ProsimCompanion.Core/Speech/Phraseology.cs:90,187` — half the table is `[GeneratedRegex]`, the other half runtime `new Regex` via `Rx()`, plus a static `Regex.Replace` with a string pattern per call; one style would do.
- `src/ProsimCompanion.Core/Aircraft/FmsClearDetector.cs:58` — `IsEmpty` re-spells the `"----"`/`"Null"` sentinels that `FmsPlanRules.IsValidIcao` already owns (Duplicated Code; expose one `FmsPlanRules.IsBlank`).

## Bugs noticed (S)

1. `src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs:113–122` — `ClearAsync` removes the handle from `_ours` **before** the SDK `Reset()` runs; if the session is gone or the call throws (`RunAsync` returns `default`), the failure stays armed in ProSim but the handle is lost, so `scenario.clear <id>` can never clear it again (only clear-all). Remove after success.
2. `src/ProsimCompanion.Prosim/Failures/SdkFailureInjector.cs:101` — `ArmedAsync` identifies "ours" by `ReferenceEquals` against objects returned by `getArmedFailures()`; if the SDK materialises new `ArmedFailure` wrappers per call (the XML doc only says "gets a list of all ArmedFailures currently in the system"), every app-armed failure reports as `ios-n` and the verify step after arming (ADR-0018 §3) never matches. **Unverified** — needs a rig check against the real SDK.
3. `src/ProsimCompanion.Core/State/GroundOpsSignals.cs:113` — see the finding above: `FlightStateSnapshotService.OnFlightCycleReset` keys its "NewPlan → keep the file" branch off `LastFlightCycleResetKind`; an arrival reset raised concurrently with a new-plan reset (GSX arrival edge vs an OFP landing on the SimBrief poller thread) can make the arrival reset read as NewPlan and skip the invalidation that drops the old leg's snapshot. Low likelihood, silent when it happens.

**Count: hard 2 / judgement 17 / bugs 3**
