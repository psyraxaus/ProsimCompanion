# Code-quality audit — `src/ProsimCompanion.Core`

Date: 2026-11-03. Branch: `feature/settings-hub-usability` @ e5d86287 (1.0.0-rc.2). Read-only.
Scope: 420 `.cs` files, 53,799 lines, 936 public types. Methods: full-file reads of the
suspects, one-pass `grep -rwoF` reference counts over `src/`, `tests/`, `tools/` (type names
and member names, defining file excluded), per-pattern greps for convention drift.
All paths below are relative to `src/ProsimCompanion.Core/` unless stated.

Size by folder (lines / files): Configuration 8,078/75 · Flight 7,364/32 · Aircraft 5,942/47 ·
State 5,147/65 · Airports 2,331/13 · Diagnostics 2,216/23 · Weather 2,213/16 · Commands 1,725/19 ·
Day 1,556/11 · Speech 1,432/9 · Checklists 1,292/5 · Debrief 1,063/4 · Gate 1,051/6 ·
Notifications 901/3 · Hosting 893/8 · Logbook 886/3 · Geo 878/5 · TechLog 867/3 · Persona 848/8 ·
Scenarios 787/5 · Display 669/3 · Boarding 651/3 · (rest < 520 each).

---

## TOP 15 FINDINGS (by payoff)

| # | Finding | Effort | Lines removable |
|---|---|---|---|
| 1 | JSON state-file persistence copy-pasted 7× (corrupt-rescue + atomic write + path resolve) | M | ~450 |
| 2 | 143 Core types consumed only by `Speech`; whole FO-only folders live in Core | L | 0 removed, ~4.5k moved |
| 3 | Options sprawl: 47 sections / 799 props; 22 JSON-only knobs; 15 legacy fields without migration; 1 dead option | M | ~150 |
| 4 | `FlightStateEngine` (969 lines) — pause subsystem + ground filter are clean seams; 3 ctors | M | ~0 (−250 from file) |
| 5 | `FlightStateSnapshotService` (748 lines, 13 ctor params) hosts a SimBrief fetch | M | ~100 moved |
| 6 | `ProsimDataRefNames`: 261 of 554 constants referenced nowhere (whole blocks) | S | ~530 (policy call) |
| 7 | Hand-rolled stores beside `SnapshotStore<T>`; `DayStatusStore` notifies on every write | S | ~90 |
| 8 | Approach-verdict rule triplicated (Logbook / Day engine / Debrief template) | S | ~30 |
| 9 | Hot-reload-by-write-time triplicated (roster, walkaround bank, scenario catalogue) | S | ~60 |
| 10 | 24 private `JsonSerializerOptions`; 8 are the same camelCase/enum/ignore-null shape `SettingsJson` already defines | S | ~100 |
| 11 | Dead/unused API: `PlaceholderSubsystemService`, `IFlightPhaseSource.LiveChanged`, `DepartureCycleState.CycleReset`, `SpeechOptions.ListeningTone` | S | ~80 |
| 12 | Composition root is one 436-line method; 15× repeated captain's-question registration pair | S | ~40 |
| 13 | Five clock styles; `%LOCALAPPDATA%\ProsimCompanion` composed in 10 places although `UserDataPaths` exists | S/M | ~30 |
| 14 | Snyk "key" rule: `IFlightStatePart.Key` forces every new part to add a `Key` member; 9 other `*Key*` identifiers | S | 0 |
| 15 | Convention drift is small but real: 11 un-`ConfigureAwait`ed awaits, 6 silent catches, 2 loops that die silently on a non-cancel exception | S | 0 |

### 1. JSON state-file persistence duplicated 7× — M, ~450 lines

Same algorithm (read → deserialize → on any exception move the file to `.corrupt-<stamp>.bak`,
log, start clean; write → `Directory.CreateDirectory` → temp file → `File.Move(overwrite)`;
`ResolvePath()` = options path or `%LOCALAPPDATA%\ProsimCompanion\<name>.json`):

| Copy | Lines | Notes |
|---|---|---|
| `Day/DayStateFile.cs` | 19–153 (whole class) | most hardened: `_io` lock, GUID temp name, empty-file detection (the 0-byte daystate bug), `TryDelete`, per-write size log |
| `Persona/FoRosterStateFile.cs` | 20–113 | GUID temp |
| `Gate/ArrivalGateStateFile.cs` | 34–121 | plain `.tmp` |
| `Scenarios/ScenarioModels.cs` (`ScenarioStateFile`) | 201–338 | GUID temp |
| `Flight/FlightStateSnapshot.cs` (`FlightStateSnapshotStore`) | 203–300 | plain `.tmp` |
| `Logbook/LogbookService.cs` | `Load` 524–556, `Save` 558–575, `ResolvePath` ~510–522 | timestamp `{DateTime.UtcNow:yyyyMMdd-HHmmss}` (culture-sensitive interpolation, unlike the others) |
| `TechLog/TechLogService.cs` | `Load` 536–568, `Save` 570–585, `ResolvePath` 522–534 | uses `_time.GetUtcNow()` — the only copy with an injectable clock |

Differences are accidental, not semantic: 3 temp-name schemes, 3 timestamp spellings, only one
copy handles the empty-file case, only one logs writes, only one has a lock. **Keep** the
`DayStateFile` variant generalised as `JsonStateFile<T>(path resolver, JsonSerializerOptions,
ILogger, TimeProvider)`; each of the seven becomes a 10–20-line adapter (Logbook/TechLog keep
their domain methods and delegate `Load/Save`). The three `ReloadIfChanged` banks (#9) would
sit on the same helper.

### 2. Layering: Core hosts the FO feature's domain — L

From the reference sweep (consumer project combinations, Core/Core.Tests ignored):

| Consumers | Core types |
|---|---|
| Core-only / tests-only | 183 |
| Speech only | **143** |
| Speech + Web | 114 |
| Web only | 93 |
| Gsx only | 30 |
| Prosim only | 22 |
| Audio only | 5 · Skalarki only 4 · Sim only 1 |

Speech-only types by Core folder: Configuration 17, State 16, **Day 12**, Airports 10,
**Scenarios 9, Speech 9**, Flight 8, Aircraft 7, **Crew 6**, Checklists 5, Gate 5, **Geo 5,
Persona 5, Debrief 4**, Diagnostics 4, Radios 4, Fuel 3, Weather 3, Company 2, Logbook 2,
Sessions 2. (Full list: `single-feature.txt` beside this report.)

Concrete smells (feature/provider logic, not seams):
- `Speech/KokoroVoices.cs` (208 lines) — a TTS vendor's voice sheet with quality grades, in Core.
- `Speech/FoFactSheet.cs` (474) — `FoFactSheetBuilder` reads ~15 stores to build the LLM fact
  sheet; consumed only by Speech.
- `Day/CompanyDayEngine.cs` (371), `Day/DaySummaryComposer.cs` (225), `Debrief/DebriefLlm.cs`
  (209), `Debrief/DebriefTemplate.cs` (194), `Fuel/FuelDecisionAdvisor.cs` (230),
  `Persona/FoRosterTriggerCore.cs`, `Scenarios/ScenarioScheduler.cs` (244),
  `Crew/WalkaroundFindings.cs` (181) — pure engines with a single consumer (Speech).
- `Geo/WikipediaSummaryClient.cs` (143, HttpClient) — "where are we" answers, Speech-only.
- Also Gsx-specific: `State/GsxResyncState.cs`, `State/GsxServiceIds.cs`,
  `Configuration/DepartureServiceStep.cs`, `Profiles/ProfileGsxMirror.cs` + `ProfileGsxApplier.cs`
  (per-profile GSX settings write-back as an `IHostedService` in Core),
  `Checklists/Prosim2GsxChecklistSetLoader.cs` (240, predecessor file-format parser).
- Prosim-only: 19 types under `Aircraft/` (Gateway DTOs `AirportDtos.cs`/`PerformanceDtos.cs`,
  `SeatMap`, `PassengerManifestService`, `DoorStateReader`, `Setup/*`).
- Network-doing code in Core (`HttpClient`): `Geo/WikipediaSummaryClient`,
  `Notifications/NotificationDispatcher`, `Updates/UpdateCheckService`,
  `Weather/ActiveSkyWxProvider`. The Notifications folder (901 lines: ntfy/Discord/JSON webhooks,
  templating, send loop) is a complete outbound feature — a `ProsimCompanion.Notifications`
  project with only the milestone events (`GroundOpsSignals`) left in Core.
- Infra in Core: `Hosting/NativeFilePicker.cs` (198, Win32 dialogs), `Hosting/WebListenerPlan.cs`
  (112, X509 loading for Kestrel).

The "Web references only Core" rule legitimately pulls *stores and snapshot records* into Core;
it does not require the *engines* to be there. Natural move: engines → Speech, leaving the
`SnapshotStore` + record + control interface in Core (the pattern `DayStatusStore`/`IDayControl`
already uses).

### 3. Options sprawl — M, ~150 lines

Census (`Configuration/`): 47 `*Options.cs` sections, **799 settable properties**, 98 classes in
the folder (51 nested option classes). Biggest: `GsxOptions` 101 props / 673 lines / **one flat
class** with 11 `// ----` sections (lines 38, 158, 172, 179, 291, 455, 482, 587, 625, 641, 657);
`SpeechOptions` 94/530 (3 classes); `SopOptions` 89/406 (14 classes — the model to follow);
`CabinOptions` 51/278; `BriefingOptions` 44/225.

Unread / JSON-only:
- `SpeechOptions.ListeningTone` (`SpeechOptions.cs:479`) — **zero production readers**; the only
  mention is the exemption `"no control yet"` in
  `tests/ProsimCompanion.Core.Tests/Web/OptionFieldCompletenessTests.cs`. Dead option.
- 22 properties are explicitly exempted as `"no control yet"` in that test (lines ~50–75):
  `SpeechOptions.{AsrTimeoutMs,VadMinSpeechMs,VadPreRollMs,VadMaxUtteranceMs,NoSpeechCeiling,
  ConfidenceFloor,ListeningTone}`, `HumanizeOptions.{GapJitter,HoldJitter,ThinkPauseChance,
  ThinkPauseMinMs,ThinkPauseMaxMs}`, `GsxOptions.{VoiceControlEnabled,AnchorDepartureGate}`,
  `FlightDataOptions.SimbriefFetchAttempts`, `ProsimOptions.{MomentaryPressHoldMs,
  MomentaryPressGapMs,SdkStallSeconds,PushSilenceWarnSeconds}`,
  `CommandApiOptions.RequireTokenOnLoopback`, `SopOptions.CalloutPollIntervalMs`,
  `FlowMonitorOptions.{Enabled,RateLimitSeconds,FlapsCleanAboveAglFt}` — each is an owner-rule
  (2026-08-14) violation carried as a test exemption. Plus whole families: `CalloutSetting.*`,
  `SpeedCalloutSetting.*`, `RadioCalloutSetting.*`, `AltitudeCallout.*`, `OneThousandToGoSetting.*`,
  `SopWeatherOptions.*`, `BriefingFmsDatarefs.*` ("no card yet").
- Every other property has ≥1 production reader (sweep of 799 names outside `Configuration/`
  and the Web settings pages; lowest counts were 1–2 and all verified as real reads, e.g.
  `SopOptions.IcingTatMaxC` → `Speech/Monitoring/FlowMonitor.cs:171`).

Legacy leftovers with no `SettingsMigrator` step (migrator is at v3; v1→v2 and v2→v3 exist):
- `SpeechOptions.PttKey`, `PttJoystickDevice`, `PttJoystickButton`, `PttJoystickDeviceName`,
  `AtcMuteKey`, `AtcMuteJoystickDevice/Button/DeviceName` (`SpeechOptions.cs:393–406`, 8 fields,
  "LEGACY … ignored once PttBinding is set").
- `BriefingOptions.LlmWakeOnLan` (`:145–149`, superseded 2026-10-08 by `WakeTargetsOptions`).
- `MixerOptions.Host/Port/Token/PanelStrips/PanelBuses` + `HasLegacyAgent`/`LegacyName` lifting
  (`MixerOptions.cs:20–57, 74–132`, pre-#178 single agent).
- `GsxOptions.AutoPca` (`:357–363`, behind `PcaMode`).
- `SayIntentionsOptions.DepartureGatingEnabled/DepartureGateDistanceNm` (`:27–35`, "legacy
  departure gate … never tripped").
Each is "migrated lazily by a page visit". A v4 migration step (PTT → `PttBinding`, wake → targets
list, mixer → `Agents[0]`, `AutoPca` → `PcaMode`) lets ~15 properties and their runtime fallbacks go.

Defaults vs docs: 12 documented defaults spot-checked (`autoCloseIdleMinutes` 300,
`delayToleranceMinutes` 15, `gateFinalCallPaxPercent` 90, `endOfDutyOfferMinutes` 10,
`todLeadMinutes` 10, `weatherRefreshMinutes` 10, `reportBeforeStdMinutes` 60 …) — all agree.
Five have no doc mention at all (`finalDelayMin/MaxSeconds` 90/150, `updateCheck.intervalHours` 24,
`postFlightAllowanceMinutes` 15, `prelimStdOffsetMinutes` 30). No contradiction found.

### 4. `FlightStateEngine` — M

`Flight/FlightStateEngine.cs` 969 lines, 26 public members, 20+ private fields. Seams:
- **Pause subsystem** (issue #168): `PauseGrace` 631, `HandleOutOfSession` 649, `BeginPause` 680,
  `TryResolvePause` 696, `EndPause` 731, `RaisePauseChanged` 740, `IsSameFlight` 755 + fields
  `_pausedAt/_pausedSnapshot/_lastLiveSnapshot/_resetNotified` → a `PauseTracker` (~130 lines).
- **Ground-contact agreement filter** `ApplyGroundContactFilter` 841–873 + `_committedOnGround/
  _groundDisagreeSamples` → sits naturally beside `GroundFactsTracker` (already extracted at
  `Flight/GroundFacts.cs:101`).
- `Heartbeat` 874–900 (diagnostic logging) → own small class.
- `ProcessTickLocked` 383–532 is 150 lines; after the two extractions it is the rule-table walk.
- Three constructors (196, 201, 212); DI uses only the 5-arg one; the others exist for tests —
  replace with the `FixedOptionsMonitor` already used in the first one at the call sites.

### 5. `FlightStateSnapshotService` — M

748 lines; ctor (`:85–98`) takes 13 collaborators, 5 optional. `MaybeFetchPlanOnly` (`:599`) and
`FetchPlanOnlyAsync` (`:636–688`, fire-and-forget `_ = Task.Run(...)` at `:633`) fetch a SimBrief
plan when the FMS already holds a route the snapshot does not — OFP acquisition, not snapshot
persistence. It also owns 4 dataref subscriptions (`:116–121`) only for that. Move to
`Aircraft/Ofp/` (beside `OfpEventRecorder`) as a `PlanOnlyFetcher` startup module; the service
then needs neither `ISimbriefImporter` nor `IProsimDataRefs`.

### 6. `ProsimDataRefNames` — 261 of 554 constants unreferenced — S (policy)

`Aircraft/ProsimDataRefNames.cs` 1,123 lines, 554 `public const string` / `DataRef<T>` members.
One-pass sweep over `src/`, `tests/`, `tools/` (`.cs`, `.razor`, `.json`): **293 referenced,
261 never referenced** — and the wire strings behind the sampled ones are not used as literals
either. Whole dead blocks: all 72 `Cdu2Key*`, 36 `Oh*` (overhead switches), 27/37 `Efis*`,
14 `Mip*`, 9 `Fc*`, 9 `Ecam*`, 8 `Fcu*`, 7 `Hyd*`, 5 `Fuel*Capacity/Act*`, 4 `Aoc*`, 4 `Apu*`
(full list: `drdead.txt` beside this report). A catalog is justified in principle (the class
doc says it is the ProSim reference), but the smart-button picker reads the embedded CSV, not
this class (csproj comment), so nothing needs the unused names at runtime. Keep the class as
the one place names live, but trim to what is used, or mark the file explicitly as a catalog
in CLAUDE.md so the next audit stops flagging it. ~2 lines per member → ~530 lines.

### 7. Hand-rolled stores beside `SnapshotStore<T>` — S, ~90 lines

`State/SnapshotStore.cs` (campaign #86) is "the one snapshot-store implementation" with
equality-gated notification. Classes that still carry their own lock + `Snapshot()` + `Changed`:
- `Day/DayStatusStore.cs:38–66` — `Update()` raises `Changed` on **every** write (no equality
  check), the exact re-render storm `ConnectionStatusStore` fixed on 2026-09-20. Straight
  `SnapshotStore<DaySnapshot>` → −50 lines, gains gating.
- `State/SimSessionStore.cs:59–133`, `State/ConnectionStatusStore.cs`,
  `State/DepartureCycleState.cs` (own `Mutate` + two events), `Gate/ArrivalGateCoordinator.cs:271`,
  `Deice/DeiceHoldoverService.cs`, `Checklists/ChecklistRunner.cs`.
- `State/CaptainsQuestionStore.cs:76` relays `Observe(...)` into a second `Changed` event
  (`_changedRelay = Observe(_ => Changed?.Invoke())`) — two notification APIs for one fact; the
  base class also exposes `QuestionRaised` and `Answered`. Pick `Observe` (what Web uses) and
  drop `Changed` on the 13 question stores.

### 8. Approach-verdict triplicated — S, ~30 lines

Identical rule "any gate `unstable` → unstable; else any `stable` → stable; else indeterminate"
over `DebriefFacts.Gates`:
- `Logbook/LogbookService.cs:392–410` `OverallApproach` (returns string)
- `Day/CompanyDayEngine.cs:357–370` `OverallStabilized` (returns `bool?`)
- `Debrief/DebriefTemplate.cs:61–67` (inline `FirstOrDefault` pair)
Keep one `DebriefFacts.OverallApproach()` on the record in `Debrief/DebriefModels.cs`; the string
vocabulary `"stable"/"unstable"/"indeterminate"` already lives in `LogbookModels.cs:60`.

### 9. Hot-reload-by-write-time triplicated — S, ~60 lines

`_loadedWriteTimeUtc` + `File.GetLastWriteTimeUtc` + silent `catch (IOException) return` +
reparse: `Crew/WalkaroundFindingBank.cs:22–65`, `Scenarios/ScenarioCatalogueBank.cs:23–64`,
`Persona/FoRosterService.cs:32, 270–287`. `Company/DelayCodes.cs` and `Day/FtlTable.cs` are the
same shape without the reload. One `HotReloadedJsonFile<T>` (or a `Reload` mode on the #1 helper).

### 10. `JsonSerializerOptions` ×24 — S, ~100 lines

24 private static instances; 8 declare the identical camelCase + `JsonStringEnumConverter(CamelCase)`
+ `WhenWritingNull` (+ indent) shape that `Configuration/SettingsJson.cs` already publishes as
`FileOptions`: `Day/DayStateFile.cs:19`, `Day/RotationLoader.cs:41`, `Gate/ArrivalGateStateFile.cs:34`,
`Logbook/LogbookService.cs:67`, `Persona/FoRosterStateFile.cs:20`, `Scenarios/ScenarioModels.cs:203`,
`TechLog/TechLogService.cs:78`, `Profiles/ProfileGsxApplier.cs:19`. The wire-specific ones
(`AcarsMessage.Wire` lowercase keys, `LoadsheetEnvelope.Wire`, `NotificationMessage.Json`) are
legitimately different and should stay.

### 11. Dead / unused API — S, ~80 lines (high confidence)

- `Hosting/PlaceholderSubsystemService.cs` (45 lines): 0 references in `src/`, `tests/`, `tools/`.
  Every placeholder registration has been replaced; delete.
- `IFlightPhaseSource.LiveChanged` (`Flight/FlightStateEngine.cs:40–44` decl, `:304` event):
  raised by the engine, **zero production subscribers** (only `Core.Tests` ×2); all 46 consumers
  read `IsLive` on their own tick. Either drop it or make the FO modules use it (the interface doc
  says they "reset on the false edge" — they don't via this event).
- `DepartureCycleState.CycleReset` (`State/DepartureCycleState.cs:75`): only tests subscribe;
  production resets on `GroundOpsSignals.FlightCycleReset`. Two events for one boundary.
- `SpeechOptions.ListeningTone` (see #3).
- Over-public, own-file-only members (make `private`/`internal`): `TurbulenceDetector.CalibrationSamples/
  HysteresisFactor`, `FlightProgress.DecelerationAllowanceNm/MinGroundSpeedKt`, `GreatCircle.EarthRadiusNm`,
  `FlightReplay.LastSampleAt`, `ArrivalGateCoordinator.LastStandLookup`, `FlightStateEngine.LastTransitionRuleId`,
  `PushbackAdvisor.LowConfidenceSeparationDeg`, `FuelLogSnapshot.MaxStampLag`, `LanguagePack.MetaMember`,
  `PlaceLookup.NotableMinimumPopulation/ReferenceMinimumPopulation`, `LogbookService.OtpToleranceMinutes`,
  `FlightStateSnapshotService.PlanFetchedEvent/RestoredEvent`, `GsxDiagnosticsStore.RecentCommandLimit`,
  `TechLogService.WearProbability`.
- Types referenced only from tests (not dead — used inside their own file — but public for no
  consumer): `MetarValues`, `FtlRow`, `FlightReplayResult`, `HotWindow`, `FoRosterTriggerDecision`,
  `LatLng`, `PlaceSummary`, `UserConfigSeedResult`.
- `Logging/CmTraceParser.cs` (94): used by `tools/ProsimCompanion.Reduce` and tests only — belongs
  in the tool.
- `Configuration/PredecessorConfigImporter.cs` (470): one production call site (one-shot import
  of Prosim2GSX/Prosim2FO configs). Retire with the predecessors or move to `tools/`.
- Not dead despite zero by-name hits (verified): `FlightPhaseExtensions` (50 call sites via
  extension syntax), `WxProviderExtensions.GetAsync` (3), `OfpDiversion.WithDiversion` (4), all
  `*Snapshot`/`*Request` records (used inside their store/handler file).

### 12. Composition root — S, ~40 lines

`DependencyInjection/CoreServiceCollectionExtensions.cs` is one 436-line `AddCoreServices`
registering ~190 services; CLAUDE.md asks for per-area extension methods and "no god-object
composition root". Lines 200–285 repeat the pair
`AddSingleton<XStore>(); AddSingleton<ICaptainsQuestionStore>(p => p.GetRequiredService<XStore>())`
**14 times** → `AddCaptainsQuestionStore<T>()`. Same for the 4× `IFlightStatePart` forwarders
(157, 170, 184, 294). Split by folder (`AddDayServices`, `AddCaptainsQuestions`, `AddFlightState`…).

### 13. Clocks and paths — S/M

Five clock styles in Core: `TimeProvider` (2 files, e.g. `TechLogService`), `Func<DateTimeOffset>`
(5: `FlightStateSnapshotService`, `NotificationSignalSource`, …), `ISimClock` (6, sim time —
legitimately distinct), raw `DateTimeOffset.UtcNow` (38 files), raw `DateTime.UtcNow` (9 files,
incl. the corrupt-backup stamps). Standardise wall-clock on `TimeProvider`.
`Environment.GetFolderPath(LocalApplicationData)` composed in 10 places although
`Configuration/UserDataPaths.cs` (22 lines) and `UserConfigPaths.cs` exist: `Day/DayStateFile.cs:149`,
`Logbook/LogbookService.cs:519`, `TechLog/TechLogService.cs:531`, `Weather/ActiveSkyWxProvider.cs:193`,
`Airports/Parking/MsfsPackageRoots.cs:25,97,118`, `Configuration/PredecessorConfigImporter.cs:30`.

### 14. Snyk "key" identifiers — S

Owner rule 2026-10-13: never the word "key" in a new identifier. Pre-rule roots that keep forcing
new violations: `IFlightStatePart.Key` (`Flight/FlightStateSnapshot.cs:22`, blamed 2026-10-11) —
every new part implements it, so `NotificationSignalSource.Key` (`:312`, **2026-11-01**) and
`LegBoundaryService.Key` (`:105`, **2026-11-02**) are post-rule additions. Others:
`RunwayLookupCache.Key(...)` (`:23`), `ParkingIdentity.PyGroupKey`/`ParsePyGroupKey` (`:62`, `:182`),
`NotificationDispatcher.Key(target)` (`:267`), `FuelLogSnapshot.PlanKey` (`Flight/FuelLog.cs:38ff`),
`NarrationSection.Key` (`Speech/NarrationSection.cs`), `SpeechOptions.PttKey`/`AtcMuteKey`
(legacy, see #3), `PredecessorConfigImporter` `fromKey/toKey` params (`:436–460`). Rename the
interface member to `Name`/`PartId` first — it is the one that propagates.

### 15. Convention drift — S

Clean: 0 `async void`; 0 blocking-on-async (`.Result` hits are record properties named `Result`);
0 interpolated log templates (both grep hits build an *argument* string); every ctor that takes
`ILogger<T>` also has `ArgumentNullException.ThrowIfNull`; 0 `ReadDataRef` polling.
Drift:
- `ConfigureAwait(false)`: 86 of 97 awaits. Missing in `Diagnostics/DiagnosticsBundleBuilder.cs`
  (3 of 13), `Gate/ArrivalGateCoordinator.cs` (1 of 4), `Theming/ThemeLogoStore.cs` (1 of 3).
- Silent catches: `Configuration/OptionsListBinding.cs:161,191` (`catch (Exception)` → return,
  no log), `Day/RotationLoader.cs:76–78` (empty `catch (IOException) {}`),
  `Persona/FoRosterService.cs:277`, `Crew/WalkaroundFindingBank.cs:55`,
  `Scenarios/ScenarioCatalogueBank.cs:54` (`catch (IOException) return;` — a permanently unreadable
  file never gets logged).
- Loops that die silently: `Airports/Parking/PushbackSuggestionService.cs:78` `_loop = Task.Run(LoopAsync)`
  where `LoopAsync` catches only `OperationCanceledException` — any other exception faults the
  stored task, is never observed, and the suggestion loop stops for the session.
  `Flight/FlightStateSnapshotService.cs:633` `_ = Task.Run(FetchPlanOnlyAsync)` (that one does
  catch `Exception` inside, so it is only an unobserved-task smell).
- `Flight/FlightStateEngine.cs` `_ticking` reentrancy guard + `Timer` — fine, but `Tick()` at
  `:926` swallows via `catch`? (not read in full; verify.)

---

## 6. EVENT / STATE PATHWAYS (Core)

Publisher/subscriber columns are feature projects (C = Core, G = Gsx, P = Prosim, S = Speech,
W = Web, A = Audio, T = Core.Tests). Subscriber counts are `+=`/`Observe` sites.

| Store / bus | Kind | Written by | Read / subscribed by | Note |
|---|---|---|---|---|
| `GroundOpsSignals` (State) | 12 events | G (all), C (TOD, arrival, boarding), S (CabinSecured), P (FlightCycleReset) | `FlightCycleReset`: C13 S14 G3 P2; `PhaseChanged`-like fan-out | the ground-ops hub; `FlightCycleReset` is the universal reset |
| `FlightStateEngine` / `IFlightPhaseSource` | hand-rolled + 5 events | C (tick) | `PhaseChanged`: S17 C10 G6 W3; `SessionReset`: S7 G1 C0; `GroundFactChanged`: C1 G1; `PauseChanged`: C1; **`LiveChanged`: 0 prod** | also `IGroundFactsSource`, `IFlightPhaseControl` |
| `SimSessionStore` | hand-rolled, 4 events | Sim pillar | `SessionEnded`: G2 C1; `SessionStarted`: C1; `SessionWindow` helper | session gate; overlaps `ConnectionStatusStore["SimConnect"]` |
| `ConnectionStatusStore` | hand-rolled, `Changed` | every pillar | W, App, P | subsystem dots |
| `DepartureCycleState` | hand-rolled, `Changed`+`CycleReset` | G, C (resync) | G2 (`_cycle.Changed`), T (`CycleReset`) | flags also persisted by `DepartureCycleSnapshotPart` and seeded by `GsxResyncState` |
| `GsxResyncState` | holder, `Assessed` | P (resync) | G1 P1 | startup-only |
| `GsxDiagnosticsStore` | SnapshotStore (15 view records) | G | W, C (`PushbackSuggestionService`), S | GSX page model in Core |
| `OfpStore` (Aircraft/Ofp) | hand-rolled, `Changed`, holds diversion overlay | P (SimBrief import), C (restore, plan-only fetch) | W11 C8 G3 S2 P1 | **`IsDiverted` duplicates `DiversionStore.Active`** |
| `DiversionStore` | SnapshotStore | S (declare/cancel) | S, W, C | calls `OfpStore.ApplyDiversion` |
| `LoadsheetStore` | SnapshotStore | P | C (notifications), W, S | + `ILoadsheetControl`, `IFmsInitSync` |
| `FlightTimesStore` + `FlightTimesTracker` | SnapshotStore + `IFlightStatePart` | C (phase edges) | C (progress, notifications), S, W | block times #1 |
| `DayStatusStore` (+`DayState` file) | hand-rolled, ungated `Changed` | S (`CompanyDayEngine`) | W, S | block times #2 (`DayLeg.ActualOff/On`) |
| `LogbookService` (file) | `Changed` | C (session finalizer) | W, S | block times #3 (`LogbookFlight.OffBlocks/OnBlocks`) |
| `LegBoundaryStore` + `LegBoundaryService` | SnapshotStore + `LegBegan` | C | `LegBegan`: S5 T1 | leg boundary; `DayState.State` is a second "between legs" state |
| `DutyDaySignals.DayEnded` | event | S | C2 G1 | end of duty |
| `FlightProgressStore` | SnapshotStore | C | W, S | raises `TodApproaching` via GroundOpsSignals |
| `GateStatusStore` (Boarding) | SnapshotStore | C (`GateMonitorService`) | W, C | derives pax counts from `GsxDiagnosticsStore` (same fact, two snapshots) |
| `WeatherStore` (SI) / `HeroWeatherStore` | SnapshotStore ×2 | S / C | W; composite provider reads SI store back | fine (raw vs card) |
| `CaptainsQuestionStore<T>` ×13 | SnapshotStore + `QuestionRaised`/`Answered`/`Changed` | feature that owns the question | `Answered`: S9 G2 P1; `QuestionRaised`: S1; all via `ICaptainsQuestionStore` list in W | TTL sweeper in Core |
| `DeiceRequestStore`/`DeiceActivityStore` | question store / SnapshotStore | G, S | G (`StepBecameNext`), W | |
| `FuelConfirmationStore`, `FuelDecisionStore`, `HoldingTimeStore` | SnapshotStores | G/S | S, W | |
| `TurbulenceStore` (`EpisodeEnded`) | SnapshotStore + event | C (`TurbulenceMonitor`) | S1 | |
| `SpeechStatusStore`, `VoiceSignalStore`, `UnmatchedUtteranceStore`, `LlmHealthStore` | SnapshotStores | S | W | voice page models |
| `AudioStatusStore`, `MixerStatusStore` | SnapshotStores | A | W | |
| `ConfigProblemStore`, `WebListenerStatus`, `UpdateStore` | stores | C/App | W | banners |
| `JsonlEventLog` (`SessionRotated`) | append log | everyone | C (`SessionFinalizer`, `DebriefFactExtractor`, `FlightReplay`) | the session's unit of truth |
| `FlightStateSnapshotStore` + `IFlightStatePart` ×7 | file | C | C | restart memory; parts: OFP, minima, cycle, times, fuel log, leg boundary, notifications |
| `TechLogService` (`Changed`, `WearRaised`) | file | S, W, C | S1 W1 | `ISessionFinalizationStep` |
| `DataRefSubscriptionTable` | 4 events | P | P, C (`FlightDataSource`) | the ProSim read model |

Competing sources of truth worth a decision: (a) `OfpStore.IsDiverted` vs `DiversionStore`;
(b) `DepartureCycleState.CycleReset` vs `GroundOpsSignals.FlightCycleReset`; (c) "boarding started"
lives as `GroundOpsSignals.BoardingStarted`, `FlightStateEngine._boardingStarted` (via
`NotifyBoardingStarted` `:247`), `DepartureCycleState.Started` and `GateStatusSnapshot.State`;
(d) block times in three files; (e) `SimSessionStore` vs `ConnectionStatusStore["SimConnect"]`.

---

## APPENDIX — everything else, terse

**Redundancy (minor)**
- `Flight/FlightDataSnapshot` (raw sample) / `FlightStateView` (committed view) / `FlightSample`
  (recorded per-second) / `FlightStateSnapshotFile` (persisted boundary) — four shapes, four
  purposes, documented; acceptable.
- `Weather/MetarParser` (240) + `MetarConditions` (82) + `WxFacts` (39) + `SkyCondition` (175) +
  `TafParser` (378) — layered, not duplicated; `TafForecast` record is file-local.
- `Speech/NatoPhonetics` is the single NATO table; `Flight/GreatCircle` the single haversine. Good.
- `Aircraft/SimClock` / `SimLocalTime` / `SimClockFormat` — distinct (UTC instant / local / format).
- 8-line normalised window scan found only 3 exact cross-file duplicates besides #1/#8/#9:
  `WalkaroundFindings.cs:75` ≈ `FoRosterModels.cs:89` ≈ `ScenarioModels.cs:64` (JSON options block).
- Notification milestone list (`NotificationSignalSource`) re-derives "final loadsheet sent" from
  `LoadsheetStore` and "cabin secure" from `GroundOpsSignals` — fine, but it is also an
  `IFlightStatePart` (persisted latches) — 432 lines for a relay.

**Bloat / god classes (>500 lines)** — verdicts
- `Aircraft/ProsimDataRefNames.cs` 1,123 — catalog, justified; 47 % unreferenced (#6).
- `Flight/FlightStateEngine.cs` 969 — split (#4).
- `Flight/FlightStateSnapshotService.cs` 748 — split (#5).
- `Configuration/GsxOptions.cs` 673 — nest by its 11 sections (#3).
- `TechLog/TechLogService.cs` 634 — persistence (#1) + `BuildBrief`/`DaysPhrase` spoken-text
  (`:318–390`, FO wording in Core) are the seams; rest is domain.
- `Logbook/LogbookService.cs` 596 — persistence (#1) + CSV export (`LogbookCsv` already split).
- `Gate/ArrivalGateCoordinator.cs` 592 — 10 fields, optional GSX/SI/parking deps; `DescribeAsync`/
  `RefreshStandInfo` (`:94–178`, stand description) is a separable read model; `Restore()` 179–270
  already delegates to `ArrivalGateRestorePlan`. Borderline; leave.
- `Debrief/DebriefFactExtractor.cs` 579, 1 public member — JSONL fold; justified.
- `Checklists/ChecklistService.cs` 539 — loader (`Reload/LoadDefaultSet/LoadProsim2GsxSets` `:354–505`,
  150 lines) is a `ChecklistSetLoader` seam; runner already split.
- `Configuration/SpeechOptions.cs` 530 — nest by its 8 sections (Kokoro/Google/ElevenLabs/recognition/…).
- `Display/CamerasCfgEditor.cs` 485 — file editor with parse/describe/apply; justified.
- `Speech/FoFactSheet.cs` 474 — belongs in Speech (#2).
- `Configuration/PredecessorConfigImporter.cs` 470 — retire/move (#11).
- `DependencyInjection/CoreServiceCollectionExtensions.cs` 436 — split (#12).
- Collaborator counts (private readonly fields / ctor params): `FlightStateSnapshotService` 19/13,
  `FlightProgressService` 17/12, `NotificationSignalSource` 14/10, `PushbackSuggestionService` 12/9,
  `LegBoundaryService` 11/9, `ArrivalGateCoordinator` 10/8. None >15 params; the first two are the
  only ones over the "~15 collaborators" line.
- Public-member counts >25: `GsxOptions` 115, `SpeechOptions` 96, `SopOptions` 95 (options — fine),
  `OfpData` 52 (DTO), `GsxDiagnosticsStore.cs` 31 (15 records), `FlightStateEngine` 26.

**Layering — Web-only Core types (93)**: Configuration 23, Flight 16, State 14, Diagnostics 6,
Aircraft 5, Profiles 4, Display 4 (`FovCalculator`, `CamerasCfgEditor` — Screen & FOV page logic),
Localization 3, Checklists 3, … Mostly snapshots/presentation inputs; the Display and
Localization engines are UI-only logic that could sit in Web (Web → Core is allowed to hold them).

**Dead-ish by folder**: `Hosting/PlaceholderSubsystemService` (delete), `Collections/BoundedLog`
(2 prod refs — fine), `Logging/WireFrameDeduper` (1), `Diagnostics/Metrics/*` all referenced.

**Options — structure notes**: `OptionsListBinding` (212 lines) exists for the binder
list-append quirk; `OptionsDraft`/`IOptionsDraft`/`OptionField*` (Fields/, 7 files, ~740 lines)
are the settings-page metadata layer — Core-resident because the completeness test and Web both
read it; fine. `FixedOptionsMonitor` is production-used (FlightStateEngine ctor) and in 16 tests.

**Convention — other**: `Task.Run` long-lived loops: `JsonlEventLog:59`, `NotificationDispatcher:89`,
`SessionFinalizer:121` (stored in `LastRun`, awaited by tests), `ArrivalGateCoordinator:145`
(`LastStandLookup`, public, only own-file use). `DataRefSubscriptionTable:162` routes subscriber
exceptions to an optional sink (`_subscriberErrorSink?.Invoke`) — silent when no sink is wired.
`CaptainsQuestionStore` subscribes `GroundOpsSignals.FlightCycleReset` in its ctor and unsubscribes
in a `virtual Dispose` — 13 stores, DI never disposes singletons; harmless but the `IDisposable`
is decoration.

**Files produced beside this report**: `typerefs2.tsv` (per-type reference counts + consumer
projects), `single-feature.txt` (205 single-feature-consumer types), `optprops.tsv` (799 option
properties with external reference counts), `drdead.txt` (261 unreferenced dataref names),
`mrefs.tsv` (member-level counts for 44 big files), `dups.txt`.
