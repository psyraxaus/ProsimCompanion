# ProsimCompanion mechanical cross-project sweep — 2026-11-03

Branch `feature/settings-hub-usability` at e5d86287 (1.0.0-rc.2). Scope: `src/**/*.cs` + `src/**/*.razor` (984 files), `tests/**` (433 files) and `tools/**` (10 files) as reference corpora; `obj/`/`bin/` excluded. Read-only; scripts and CSVs live in `C:\Users\johncarlo\AppData\Local\Temp\claude\c--Users-johncarlo-git-ProsimCompanion\3750cb80-805c-4856-88ca-2a1aedf6cbe9\scratchpad\sweep\` (`Sweep.cs` word index + clone detector, `s1_types.ps1` … `s678.ps1`, `*_all.csv` full tables, `s4_event_names_full.txt`).

## Summary

| # | Section | Headline numbers |
|---|---|---|
| 1 | Unreferenced types | 1834 public/internal types (Core 932, Speech 446, Gsx 216, Web 93, App 48, Audio 37, Prosim 31, Sim 19, Skalarki 10, Skalarki.Sdk 1, Analyzers 1). **Dead: 0.** File-local (public/internal but used only inside the declaring file): **110** (Core 48, Web 17, Gsx 15, Speech 15, App 14, Audio 1). Test-only (no production use outside the declaring file, referenced by tests): **53** (Core 24, Speech 16, Gsx 7, App 3, Web 2, Audio 1) — all 53 are also self-used in their file, i.e. extracted `*Core`/decider/enum types, the pattern CLAUDE.md asks for. 17 static classes have zero type-name hits but are reached through their methods (DI `Add*Services`, `Map*` endpoints, `FlightPhaseExtensions`, `WxProviderExtensions`, `OfpDiversion`, `CopilotControls`, `MetarConditionsLight`). |
| 2 | Unused interface members | 129 interfaces / 400 members (Core 266, Speech 72, Gsx 42, Skalarki 12, Prosim 3, Sim 3, Audio 2). **4 likely unused**: `IProsimGateway.CancelBoardingAsync`, `IProsimGateway.GetFailuresAsync`, `IMixerClient.Unwatch` (no test hits either), `ISkalarkiControl.StopCheck` (2 test hits). |
| 3 | Duplicate blocks | 12-line window: **src 7 families / 113 redundant lines** (Web 3, Speech 3, App 1); **tests 8 families / 186 lines** (all Core.Tests, mostly `FakeGsxApi` x4 and gate-test fakes x3). 8-line window on src: 25+ families; `McduArrivalChanger.cs` <-> `McduRadNavTuner.cs` is a sustained clone pair (21+11+8+8+8 lines), `Landing.razor` <-> `Takeoff.razor` another (27+15+9+9). |
| 4 | Session event names | Emitter `JsonlEventLog.Record(type, payload)`. **315 distinct names** = 276 literal call sites + 39 resolved through wrappers (`speech.<kind>` x11, captain's-question `table.EventName` x15, `log.warning/error/fatal` mirror x3, `sim-paused/resumed/pause-ended` x3, `IGsxDepartureDecisionLog.RecordEvent` x5, `scenario.cleared/clearFailed`). Plain list: `s4_event_names_full.txt`. No unresolved sites remain. Naming is split kebab-case vs dotted (`gsx-gate-*` vs `fo.*`). |
| 5 | Options property usage | 102 classes in `Core/Configuration` (86 knob classes), 940 public properties (865 knob). **Unread knobs: 3** (`SpeechOptions.ListeningTone`, `AudioOptions.DefaultAppMappings`, `SopOptions.DefaultAltitudeCallouts` — the latter two are static defaults). **Web-only knobs: 16**, of which the genuine "settings control exists, nothing reads it" cases are `GsxDeiceOptions.QuestionTimeoutMinutes` (only `GsxSettings.razor:339`) and `SkalarkiOptions.ReconnectBackoffMs` (only `SkalarkiSettings.razor:112`); `MixerOptions.HasLegacyAgent` is read only by `Web/Settings/AudioPageRules.cs`; the rest are UI-by-nature (`WebUiOptions.*`, `TileOrder`, `DeviceLabels`, `PanelStrips/PanelBuses`) or static default tables. |
| 6 | Log interpolation | **0 strict violations** (`Log<Level>($"...")` / `Log(LogLevel, $"...")`). 8 loose hits are interpolations passed as *arguments* to a structured template (`MixerClient.cs:346/406`, `GatewayAirportCoordinates.cs:62`, `DiagnosticsSnapshotService.cs:50`, `GsxGateSelectionService.cs:760`, `MovementMessageService.cs:239`, `DisregardVoiceFeature.cs:78`, `SayIntentionsService.cs:816`) — not violations. |
| 7 | "key" identifiers | **306 declaration sites / 167 distinct identifiers / 105 files**; 72 of them are the `Cdu2Key*` dataref-name constants in `ProsimDataRefNames.cs` (mirror ProSim's `S_CDU2_KEY_*`, not renamable). Excluding those: 234 sites — Speech 87, Core 70, Gsx 37, Web 27, Prosim 8, Audio 3, App 2; 97 are plain `key`/`keys` locals or parameters; 30 are the `ApiKey` family (SayIntentions/ElevenLabs/LLM); 99 sit on public surface (types, methods, public properties). Hot files: `PushToTalkService.cs` 16, `PredecessorConfigImporter.cs` 9, `SayIntentionsService.cs` 8, `SpeechOptions.cs` 8, `AudioSettings.razor` 7. |
| 8 | TODO/FIXME/HACK/XXX | **0** in src and tests (confirmed independently with ripgrep). Nothing to blame. |

## Method and known blind spots

Everything is name-based over a whole-word token index, not a semantic model. Type and member usage (1, 2, 5) counts occurrences of the bare identifier in files other than the declaring one (plus, for interfaces, outside the interface body and outside implementation files detected by base-list regex), so any identifier collision — a property called `Enabled`, a method called `Start`, an interface member with the same name as an unrelated method — counts as a use; the lists therefore **under-report** dead code and the small numbers in sections 1a, 2 and 5a are lower bounds, not estimates. Over-reporting is possible only where a symbol is reached without its name appearing in C#/Razor: Razor components used by tag (handled: `<Name` is a word match), routed pages, WPF XAML `x:Class`/converters and `*.json` configuration (a secondary scan of src `*.xaml/*.json/*.csproj/*.props/*.js` was applied to zero-hit types and found nothing), reflection-driven settings rendering (the `OptionField` renderer reads knob metadata by reflection, so a knob may be on a page without a Web hit), Roslyn-discovered analyzers, and extension/static classes (handled: static classes are credited when any of their public static methods is referenced). Static classes whose only references are in tests/tools are not separately listed. The clone detector normalises whitespace only (no identifier renaming), skips lines under 12 characters, braces, `using`, comments and preprocessor lines, and requires the same 12 (or 8) consecutive normalised lines in two *different* files, so renamed-variable clones and same-file repetition are invisible; families whose every copy lies inside a larger reported family are suppressed, but partially overlapping families can still appear twice. Event names (4) were taken from `Record("literal"`, `const string` resolution (case-sensitive, same-file or `Class.Const`), and manual tracing of the five wrapper paths; a new wrapper that forwards a caller-supplied type would be missed next time unless it is added to the trace. The `tools/` folder (Reduce, SkalarkiSpike) counted as a reference corpus for sections 1-2 but was not itself audited. `git blame` dates were not needed because section 8 found nothing.

---
## 1. Unreferenced types

Declared public/internal types in src/: 1834. Zero external hits (src outside declaring file(s) + tests + tools): 110, of which DEAD (no in-file use either): 0; FILE-LOCAL (used only inside the declaring file — over-exposed, not dead): 110. Test-only (0 in src outside declaring files, >0 in tests): 53. Zero-hit but explained (static class reached via its methods / razor / WPF / analyzer): 17.

Columns: self = references inside the declaring file(s) excluding the declaration line (ctor/self-use/cref); other-files = hits in src *.xaml/*.json/*.csproj/*.props/*.js.

### 1a. DEAD (no reference anywhere, not even in its own file)

### 1a'. FILE-LOCAL public/internal types (only the declaring file uses them; candidates for private nesting or file-scoped accessibility)

#### ProsimCompanion.App (14)
- `ControlsPosition` record — src/ProsimCompanion.App/Hosting/ControlsApiEndpoints.cs:15 (self=2)
- `ControlsSwitch` record — src/ProsimCompanion.App/Hosting/ControlsApiEndpoints.cs:22 (self=2)
- `ControlsView` record — src/ProsimCompanion.App/Hosting/ControlsApiEndpoints.cs:36 (self=2)
- `ControlsResponse` record — src/ProsimCompanion.App/Hosting/ControlsApiEndpoints.cs:40 (self=2)
- `SimDamageEntry` record — src/ProsimCompanion.App/Hosting/SimDamageEndpoints.cs:17 (self=5)
- `SimDamageResponse` record — src/ProsimCompanion.App/Hosting/SimDamageEndpoints.cs:26 (self=2)
- `StatusConnections` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:30 (self=2)
- `StatusServiceRow` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:32 (self=5)
- `StatusGsx` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:34 (self=3)
- `StatusChecklist` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:43 (self=3)
- `StatusVoice` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:47 (self=3)
- `StatusSwitch` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:51 (self=2)
- `TelemetrySummary` record — src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:18 (self=2)
- `SessionLogEventPayload` record — src/ProsimCompanion.App/Logging/SessionLogEventFormatter.cs:12 (self=3)

#### ProsimCompanion.Audio (1)
- `MixerInboundFrame` record — src/ProsimCompanion.Audio/Mixer/MixerFrame.cs:10 (self=8)

#### ProsimCompanion.Core (48)
- `DoorView` record — src/ProsimCompanion.Core/Aircraft/DoorStateReader.cs:4 (self=4)
- `IntersectionViewModel` class — src/ProsimCompanion.Core/Aircraft/Gateway/AirportDtos.cs:33 (self=1)
- `InfluenceFactor` class — src/ProsimCompanion.Core/Aircraft/Gateway/PerformanceDtos.cs:127 (self=3)
- `NotamHighlight` record — src/ProsimCompanion.Core/Aircraft/Ofp/OfpNotam.cs:27 (self=2)
- `PassengerManifestEntry` record — src/ProsimCompanion.Core/Aircraft/PassengerManifestService.cs:3 (self=5)
- `FinalDifferences` record — src/ProsimCompanion.Core/Aircraft/WeightAndBalance/LoadsheetFormatter.cs:189 (self=2)
- `PrelimTriggerDecision` record — src/ProsimCompanion.Core/Aircraft/WeightAndBalance/PrelimTriggerPolicy.cs:22 (self=1)
- `ControlAxisDefinition` class — src/ProsimCompanion.Core/Checklists/ChecklistModels.cs:123 (self=1)
- `CommandException` class — src/ProsimCompanion.Core/Commands/CommandExceptions.cs:9 (self=5)
- `ChecklistSelectRequest` record — src/ProsimCompanion.Core/Commands/Handlers/ChecklistCommandHandlers.cs:6 (self=2)
- `ChecklistItemRequest` record — src/ProsimCompanion.Core/Commands/Handlers/ChecklistCommandHandlers.cs:14 (self=4)
- `FmsSyncCommandResult` record — src/ProsimCompanion.Core/Commands/Handlers/LoadsheetCommandHandlers.cs:10 (self=1)
- `ScenarioIdRequest` record — src/ProsimCompanion.Core/Commands/Handlers/ScenarioCommandHandlers.cs:4 (self=2)
- `SpeechSpeakTestRequest` record — src/ProsimCompanion.Core/Commands/Handlers/SpeechCommandHandlers.cs:6 (self=2)
- `WindLimitsOptions` class — src/ProsimCompanion.Core/Configuration/InFlightMonitoringOptions.cs:26 (self=1, options)
- `FoRadiosOptions` class — src/ProsimCompanion.Core/Configuration/SayIntentionsOptions.cs:209 (self=1, options)
- `SmallTalkKindsOptions` class — src/ProsimCompanion.Core/Configuration/SmallTalkOptions.cs:34 (self=1, options)
- `TransitionReminderOptions` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:157 (self=1, options)
- `StabilizedOptions` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:207 (self=1, options)
- `WalkaroundRaiseSnapshot` record — src/ProsimCompanion.Core/Crew/WalkaroundRaiseStore.cs:10 (self=6)
- `RotationLegFile` class — src/ProsimCompanion.Core/Day/DayModels.cs:155 (self=1)
- `ApiClientView` record — src/ProsimCompanion.Core/Diagnostics/ApiClientRegistry.cs:6 (self=4)
- `BundleFileEntry` record — src/ProsimCompanion.Core/Diagnostics/DiagnosticsBundleBuilder.cs:50 (self=6)
- `BundleTruncatedEntry` record — src/ProsimCompanion.Core/Diagnostics/DiagnosticsBundleBuilder.cs:53 (self=4)
- `BundleSelection` record — src/ProsimCompanion.Core/Diagnostics/DiagnosticsBundleBuilder.cs:361 (self=2)
- `CamerasCfgEdit` record — src/ProsimCompanion.Core/Display/CamerasCfgEditor.cs:33 (self=3)
- `DisplayGeometry` record — src/ProsimCompanion.Core/Display/FovCalculator.cs:14 (self=4)
- `FovPlanRow` record — src/ProsimCompanion.Core/Display/FovCalculator.cs:29 (self=2)
- `ReplayCommit` record — src/ProsimCompanion.Core/Flight/FlightReplay.cs:11 (self=4)
- `RecordedCommit` record — src/ProsimCompanion.Core/Flight/FlightReplay.cs:18 (self=4)
- `DepartureCycleSnapshot` record — src/ProsimCompanion.Core/Flight/FlightStateParts.cs:107 (self=2)
- `LandingMinima` record — src/ProsimCompanion.Core/Fuel/FuelDecisionAdvisor.cs:47 (self=6)
- `NearbyTown` record — src/ProsimCompanion.Core/Geo/PlaceLookup.cs:16 (self=6)
- `SunPosition` record struct — src/ProsimCompanion.Core/Geo/SolarPosition.cs:5 (self=2)
- `PlaceholderSubsystemService` class — src/ProsimCompanion.Core/Hosting/PlaceholderSubsystemService.cs:12 (self=3)
- `NotificationRequest` record — src/ProsimCompanion.Core/Notifications/NotificationMessage.cs:101 (self=7)
- `FoRosterChangeSnapshot` record — src/ProsimCompanion.Core/Persona/FoRosterChangeStore.cs:6 (self=5)
- `SmallTalkOfferSnapshot` record — src/ProsimCompanion.Core/Persona/SmallTalkOfferStore.cs:6 (self=5)
- `ScenarioDecisionSnapshot` record — src/ProsimCompanion.Core/Scenarios/ScenarioDecisionStore.cs:6 (self=6)
- `ScenarioStateLoad` record — src/ProsimCompanion.Core/Scenarios/ScenarioModels.cs:192 (self=1)
- `FoFact` record — src/ProsimCompanion.Core/Speech/FoFactSheet.cs:23 (self=3)
- `BriefingQuestionsSnapshot` record — src/ProsimCompanion.Core/State/BriefingQuestionsStore.cs:5 (self=6)
- `DelayCodeSnapshot` record — src/ProsimCompanion.Core/State/DelayCodeStore.cs:9 (self=6)
- `FoAvailability` record — src/ProsimCompanion.Core/State/FoAvailabilityStore.cs:7 (self=5)
- `ThreatsQuestionSnapshot` record — src/ProsimCompanion.Core/State/ThreatsQuestionStore.cs:7 (self=6)
- `UnmatchedPhraseGroup` record — src/ProsimCompanion.Core/State/UnmatchedUtteranceStore.cs:29 (self=2)
- `VoiceSignalSnapshot` record — src/ProsimCompanion.Core/State/VoiceSignalStore.cs:20 (self=4)
- `TafForecast` record — src/ProsimCompanion.Core/Weather/TafParser.cs:15 (self=2)

#### ProsimCompanion.Gsx (15)
- `GsxPositionMenuPick` record — src/ProsimCompanion.Gsx/Gate/GsxPositionMenuPlanner.cs:9 (self=3)
- `GsxMenuOpenOutcome` enum — src/ProsimCompanion.Gsx/Menu/GsxMenuOpener.cs:7 (self=6)
- `GsxMenuOpenResult` record — src/ProsimCompanion.Gsx/Menu/GsxMenuOpener.cs:20 (self=1)
- `PushbackDecision` record — src/ProsimCompanion.Gsx/Menu/PushbackDirectionDecider.cs:19 (self=14)
- `PushbackDirectionPick` record — src/ProsimCompanion.Gsx/Menu/PushbackDirectionResolver.cs:4 (self=9)
- `GsxIniStand` record — src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:10 (self=5)
- `GsxIniProfile` record — src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:46 (self=4)
- `GsxProfileFile` record — src/ProsimCompanion.Gsx/Profiles/GsxProfileLocator.cs:9 (self=4)
- `GsxPyGroup` record — src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs:12 (self=5)
- `GsxResolvedName` record — src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs:15 (self=2)
- `PyNone` record — src/ProsimCompanion.Gsx/Profiles/PyLiteral.cs:37 (self=4)
- `TickPlan` record — src/ProsimCompanion.Gsx/Sync/BoardingCore.cs:32 (self=2)
- `ResyncVerdict` record — src/ProsimCompanion.Gsx/Sync/GsxStartupResyncService.cs:38 (self=3)
- `HookIntents` record — src/ProsimCompanion.Gsx/Sync/PushbackTickCore.cs:43 (self=2)
- `IPushbackTickEffectApplier` interface — src/ProsimCompanion.Gsx/Sync/PushbackTickEffectApplier.cs:9 (self=2)

#### ProsimCompanion.Speech (15)
- `RoleVoice` record struct — src/ProsimCompanion.Speech/Arbiter/RoleVoiceResolver.cs:12 (self=7)
- `MissedApproachLeg` record — src/ProsimCompanion.Speech/Briefings/DfdNavDataProvider.cs:46 (self=3)
- `MissedApproachProcedure` record — src/ProsimCompanion.Speech/Briefings/DfdNavDataProvider.cs:55 (self=4)
- `CruiseQueryResult` record — src/ProsimCompanion.Speech/Cabin/CabinCruiseQueryDialogue.cs:28 (self=5)
- `CrossCheckReadout` record — src/ProsimCompanion.Speech/FoSide/CrossCheckComposer.cs:19 (self=2)
- `NumberCheck` record — src/ProsimCompanion.Speech/Llm/NumberVerifier.cs:10 (self=2)
- `ReadbackRequest` record — src/ProsimCompanion.Speech/Monitoring/ReadbackCore.cs:23 (self=7)
- `ReadbackAnswer` record — src/ProsimCompanion.Speech/Monitoring/ReadbackCore.cs:35 (self=16)
- `FoAnswerOutcome` enum — src/ProsimCompanion.Speech/Questions/FoQuestionService.cs:17 (self=28)
- `AsrTranscript` record — src/ProsimCompanion.Speech/Recognition/AsrServerApi.cs:12 (self=6)
- `CommandMatch` record — src/ProsimCompanion.Speech/Recognition/CommandMatcher.cs:6 (self=3)
- `IdleMissResponse` record — src/ProsimCompanion.Speech/Recognition/IdleMissPolicy.cs:11 (self=3)
- `InterpretResult` record — src/ProsimCompanion.Speech/Recognition/UtteranceInterpreter.cs:29 (self=11)
- `AtcRequestDefinition` class — src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:19 (self=5)
- `TtsLastSynthesis` record — src/ProsimCompanion.Speech/Tts/TtsRouter.cs:314 (self=3)

#### ProsimCompanion.Web (17)
- `DrawingPoint` record struct — src/ProsimCompanion.Web/Components/Diagnostics/A320Anchors.cs:4 (self=19)
- `SkalarkiLayoutRow` record — src/ProsimCompanion.Web/Components/Diagnostics/SkalarkiLayoutCatalog.cs:12 (self=2)
- `HeroProgressItem` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:22 (self=3)
- `SequenceStep` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:26 (self=3)
- `RadioStationChip` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:107 (self=3)
- `RadiosTileView` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:111 (self=2)
- `FuelConfirmedPill` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:130 (self=2)
- `FuelChartPoint` record — src/ProsimCompanion.Web/Components/FuelLogPresentation.cs:23 (self=2)
- `FuelChartView` record — src/ProsimCompanion.Web/Components/FuelLogPresentation.cs:28 (self=2)
- `DepartureBoardView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:12 (self=2)
- `BoardRowView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:20 (self=2)
- `DeiceView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:24 (self=2)
- `DeiceQuestionView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:33 (self=3)
- `ServiceTableRow` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:36 (self=2)
- `DecisionRowView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:46 (self=2)
- `CommandRowView` record — src/ProsimCompanion.Web/Components/GsxPresentation.cs:49 (self=2)
- `SparkPoint` record — src/ProsimCompanion.Web/Components/LogbookPresentation.cs:22 (self=2)

### 1b. Test-only production types by project (0 with self=0 = production never touches them at all; 53 with self>0 = file-local types that tests also reach)

#### ProsimCompanion.App (3)
- `StatusServiceState` enum — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:20 (tests=12, self=10)
- `StatusResponse` record — src/ProsimCompanion.App/Hosting/StatusApiEndpoints.cs:53 (tests=1, self=3)
- `TelemetryFileView` record — src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:14 (tests=4, self=6)

#### ProsimCompanion.Audio (1)
- `MixerPongFrame` record — src/ProsimCompanion.Audio/Mixer/MixerFrame.cs:29 (tests=1, self=1)

#### ProsimCompanion.Core (24)
- `LatLng` class — src/ProsimCompanion.Core/Aircraft/Gateway/AirportDtos.cs:27 (tests=3, self=3)
- `AircraftStateGroup` class — src/ProsimCompanion.Core/AircraftState/AircraftStateDefinition.cs:26 (tests=3, self=1)
- `AircraftStateExpectation` class — src/ProsimCompanion.Core/AircraftState/AircraftStateDefinition.cs:35 (tests=6, self=2)
- `CockpitPressRequest` record — src/ProsimCompanion.Core/Commands/Handlers/CockpitCommandHandlers.cs:6 (tests=5, self=2)
- `CockpitSetRequest` record — src/ProsimCompanion.Core/Commands/Handlers/CockpitCommandHandlers.cs:16 (tests=2, self=2)
- `GsxRequestGateRequest` record — src/ProsimCompanion.Core/Commands/Handlers/GsxCommandHandlers.cs:7 (tests=4, self=2)
- `InputPressViewRequest` record — src/ProsimCompanion.Core/Commands/Handlers/InputCommandHandlers.cs:8 (tests=9, self=2)
- `MinimaSetRequest` record — src/ProsimCompanion.Core/Commands/Handlers/MinimaCommandHandlers.cs:8 (tests=4, self=2)
- `WakeOnLanOptions` class — src/ProsimCompanion.Core/Configuration/BriefingOptions.cs:22 (tests=2, self=1, options)
- `BriefingFmsDatarefs` class — src/ProsimCompanion.Core/Configuration/BriefingOptions.cs:40 (tests=1, self=1)
- `SpeedCalloutSetting` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:21 (tests=1, self=4)
- `RadioCalloutSetting` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:37 (tests=1, self=4)
- `AltitudeCallout` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:61 (tests=1, self=2)
- `OneThousandToGoSetting` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:70 (tests=1, self=1)
- `FlowMonitorOptions` class — src/ProsimCompanion.Core/Configuration/SopOptions.cs:125 (tests=3, self=2, options)
- `UserConfigSeedResult` record — src/ProsimCompanion.Core/Configuration/UserConfigSeeder.cs:12 (tests=2, self=3)
- `HttpsOptions` class — src/ProsimCompanion.Core/Configuration/WebUiOptions.cs:88 (tests=1, self=1, options)
- `FtlRow` record — src/ProsimCompanion.Core/Day/FtlTable.cs:11 (tests=1, self=6)
- `HotWindow` record struct — src/ProsimCompanion.Core/Deice/HotMatrix.cs:31 (tests=7, self=6)
- `FlightReplayResult` class — src/ProsimCompanion.Core/Flight/FlightReplay.cs:25 (tests=1, self=5)
- `FlightSampleRecorderCore` class — src/ProsimCompanion.Core/Flight/FlightSampleRecorder.cs:14 (tests=6, self=1)
- `PlaceSummary` record — src/ProsimCompanion.Core/Geo/WikipediaSummaryClient.cs:18 (tests=3, self=7)
- `FoRosterTriggerDecision` record — src/ProsimCompanion.Core/Persona/FoRosterTriggerCore.cs:43 (tests=3, self=4)
- `MetarValues` record struct — src/ProsimCompanion.Core/Weather/MetarParser.cs:10 (tests=1, self=4)

#### ProsimCompanion.Gsx (7)
- `AircraftStateCheckContext` record — src/ProsimCompanion.Gsx/Sync/AircraftStateCheckService.cs:20 (tests=1, self=2)
- `ArrivalTickOutcome` record — src/ProsimCompanion.Gsx/Sync/ArrivalCore.cs:40 (tests=2, self=2)
- `FobRestoreDecision` enum — src/ProsimCompanion.Gsx/Sync/GsxArrivalService.cs:321 (tests=8, self=11)
- `ParkingWakeAction` enum — src/ProsimCompanion.Gsx/Sync/GsxParkingWakeService.cs:28 (tests=12, self=10)
- `ParkingWakePlan` class — src/ProsimCompanion.Gsx/Sync/GsxParkingWakeService.cs:44 (tests=4, self=2)
- `ResyncEvidence` record — src/ProsimCompanion.Gsx/Sync/GsxStartupResyncService.cs:18 (tests=1, self=3)
- `EngineStartEvidence` record struct — src/ProsimCompanion.Gsx/Sync/PushbackTickGatherer.cs:13 (tests=4, self=2)

#### ProsimCompanion.Speech (16)
- `CabinAutoAnswerGate` enum — src/ProsimCompanion.Speech/Cabin/CabinAutoAnswer.cs:8 (tests=6, self=8)
- `AntiIceSystem` enum — src/ProsimCompanion.Speech/Callouts/AntiIceCallFeature.cs:13 (tests=11, self=4)
- `AntiIceCall` record — src/ProsimCompanion.Speech/Callouts/AntiIceCallFeature.cs:20 (tests=3, self=3)
- `EngineStartCall` record — src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:11 (tests=1, self=6)
- `EngineStartCalloutCore` class — src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:32 (tests=7, self=3)
- `AnnouncementKind` enum — src/ProsimCompanion.Speech/Callouts/PilotAnnouncementFeature.cs:46 (tests=5, self=8)
- `VoiceCommandStep` class — src/ProsimCompanion.Speech/Commands/VoiceCommandModels.cs:10 (tests=1, self=1)
- `AcpTransmitState` record struct — src/ProsimCompanion.Speech/Crew/AcpChannel.cs:39 (tests=1, self=4)
- `WeatherTrigger` enum — src/ProsimCompanion.Speech/Monitoring/DestinationWeatherWatchCore.cs:8 (tests=4, self=11)
- `EffectivePersona` record — src/ProsimCompanion.Speech/Persona/PersonaService.cs:28 (tests=1, self=4)
- `UtteranceEndReason` enum — src/ProsimCompanion.Speech/Recognition/Vad/UtteranceSegmenter.cs:19 (tests=5, self=5)
- `UtteranceReadyEventArgs` class — src/ProsimCompanion.Speech/Recognition/Vad/UtteranceSegmenter.cs:29 (tests=2, self=3)
- `UtteranceDroppedEventArgs` class — src/ProsimCompanion.Speech/Recognition/Vad/UtteranceSegmenter.cs:42 (tests=2, self=3)
- `VoiceReferenceRow` record — src/ProsimCompanion.Speech/Recognition/VoiceFeatureDeclaration.cs:105 (tests=1, self=1)
- `VoiceClaim` enum — src/ProsimCompanion.Speech/Recognition/VoiceFeatureRoster.cs:5 (tests=2, self=3)
- `PttEdgeTracker` class — src/ProsimCompanion.Speech/SayIntentions/SayIntentionsPttRelay.cs:12 (tests=9, self=5)

#### ProsimCompanion.Web (2)
- `EnginePill` record — src/ProsimCompanion.Web/Components/FlightStatusPresentation.cs:29 (tests=2, self=3)
- `CircuitView` record — src/ProsimCompanion.Web/Diagnostics/CircuitTracker.cs:22 (tests=2, self=6)

### 1c. Zero type-name hits but explained (static class used via methods, routed pages, XAML-bound, Roslyn-discovered)
- `AppBootEndpoints` class — src/ProsimCompanion.App/Hosting/AppBootEndpoints.cs:15 [static-used-via-methods] via: MapAppBoot(1)
- `LogbookApiEndpoints` class — src/ProsimCompanion.App/Hosting/LogbookApiEndpoints.cs:20 [static-used-via-methods] via: MapLogbookApi(1)
- `ThemeLogoEndpoints` class — src/ProsimCompanion.App/Hosting/ThemeLogoEndpoints.cs:16 [static-used-via-methods] via: MapThemeLogoApi(1)
- `AudioServiceCollectionExtensions` class — src/ProsimCompanion.Audio/AudioServiceCollectionExtensions.cs:10 [static-used-via-methods] via: AddAudioServices(1)
- `OfpDiversion` class — src/ProsimCompanion.Core/Aircraft/Ofp/OfpData.cs:274 [static-used-via-methods] via: WithDiversion(2)
- `CoreServiceCollectionExtensions` class — src/ProsimCompanion.Core/DependencyInjection/CoreServiceCollectionExtensions.cs:13 [static-used-via-methods] via: AddCoreServices(8)
- `OptionSectionServiceCollectionExtensions` class — src/ProsimCompanion.Core/DependencyInjection/OptionSectionServiceCollectionExtensions.cs:13 [static-used-via-methods] via: AddOptionSection(49)
- `FlightPhaseExtensions` class — src/ProsimCompanion.Core/Flight/FlightPhase.cs:63 [static-used-via-methods] via: IsAirborne(17) IsAtGate(21) IsBeforeTaxiOut(14)
- `MetarConditionsLight` class — src/ProsimCompanion.Core/Fuel/FuelDecisionAdvisor.cs:225 [static-used-via-methods] via: HasObservation(2)
- `StartupModuleServiceCollectionExtensions` class — src/ProsimCompanion.Core/Hosting/StartupModules.cs:25 [static-used-via-methods] via: AddStartupModule(79)
- `WxProviderExtensions` class — src/ProsimCompanion.Core/Weather/IWxProvider.cs:20 [static-used-via-methods] via: GetAsync(41)
- `GsxServiceCollectionExtensions` class — src/ProsimCompanion.Gsx/GsxServiceCollectionExtensions.cs:8 [static-used-via-methods] via: AddGsxServices(1)
- `ProsimServiceCollectionExtensions` class — src/ProsimCompanion.Prosim/ProsimServiceCollectionExtensions.cs:12 [static-used-via-methods] via: AddProsimServices(1)
- `SimServiceCollectionExtensions` class — src/ProsimCompanion.Sim/SimServiceCollectionExtensions.cs:6 [static-used-via-methods] via: AddSimServices(1)
- `SkalarkiServiceCollectionExtensions` class — src/ProsimCompanion.Skalarki/SkalarkiServiceCollectionExtensions.cs:6 [static-used-via-methods] via: AddSkalarkiServices(1)
- `CopilotControls` class — src/ProsimCompanion.Speech/Checklists/ControlSweepService.cs:12 [static-used-via-methods] via: IsWritable(2) TryResolve(17)
- `WebServiceCollectionExtensions` class — src/ProsimCompanion.Web/DependencyInjection/WebServiceCollectionExtensions.cs:15 [static-used-via-methods] via: AddWebServices(1)


## 2. Interface members with no caller outside the interface file and its implementations

Interfaces scanned: 129; members: 400; members with zero src hits outside interface+implementation files: 4 (of which also zero test hits: 3). Name-based: a member name that collides with any other identifier anywhere counts as used, so this list under-reports; it over-reports when the only caller is another implementation of the same interface (decorator/fake) or a Moq setup.

### 2a. Top 60 (most confident first: no test hits, then longest names)

| Interface | Member | Kind | Location | impls | test hits |
|---|---|---|---|---|---|
| `IProsimGateway` | `CancelBoardingAsync` | method | src/ProsimCompanion.Core/Aircraft/Gateway/IProsimGateway.cs:32 | 1 | 0 |
| `IProsimGateway` | `GetFailuresAsync` | method | src/ProsimCompanion.Core/Aircraft/Gateway/IProsimGateway.cs:51 | 1 | 0 |
| `IMixerClient` | `Unwatch` | method | src/ProsimCompanion.Core/State/IMixerClient.cs:206 | 1 | 0 |
| `ISkalarkiControl` | `StopCheck` | method | src/ProsimCompanion.Core/Hardware/Skalarki/ISkalarkiControl.cs:27 | 1 | 2 |

### 2b. Full list by project

#### ProsimCompanion.Core (4)
- `IMixerClient.Unwatch` (method) — src/ProsimCompanion.Core/State/IMixerClient.cs:206, impls=1, tests=0
- `IProsimGateway.CancelBoardingAsync` (method) — src/ProsimCompanion.Core/Aircraft/Gateway/IProsimGateway.cs:32, impls=1, tests=0
- `IProsimGateway.GetFailuresAsync` (method) — src/ProsimCompanion.Core/Aircraft/Gateway/IProsimGateway.cs:51, impls=1, tests=0
- `ISkalarkiControl.StopCheck` (method) — src/ProsimCompanion.Core/Hardware/Skalarki/ISkalarkiControl.cs:27, impls=1, tests=2


## 3. Duplicate code blocks

### 3a. src/ only

Clone families (>=12 consecutive non-trivial normalised lines, present in >=2 distinct files, maximal runs, contained families suppressed): 7. Redundant lines (sum of length x (copies-1)): 113.

| Projects involved | families | redundant lines |
|---|---|---|
| ProsimCompanion.Web | 3 | 54 |
| ProsimCompanion.Speech | 3 | 47 |
| ProsimCompanion.App | 1 | 12 |

Top 40 by block length:

1. **27 lines x 2 copies** — first line: `_wxBusy = false;`
   - src/ProsimCompanion.Web/Pages/Landing.razor:606-660
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:585-638
2. **21 lines x 2 copies** — first line: `ArgumentNullException.ThrowIfNull(procedures);`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:112-140
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:67-95
3. **15 lines x 2 copies** — first line: `<span class="perf-unit">hPa</span>`
   - src/ProsimCompanion.Web/Pages/Landing.razor:113-146
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:124-157
4. **14 lines x 2 copies** — first line: `private string? ResolveApiKey(SayIntentionsOptions options)`
   - src/ProsimCompanion.Speech/SayIntentions/SayIntentionsGateAssignService.cs:127-151
   - src/ProsimCompanion.Speech/SayIntentions/SayIntentionsWeatherService.cs:356-380
5. **12 lines x 2 copies** — first line: `private static IResult? Gate(`
   - src/ProsimCompanion.App/Hosting/DiagnosticsEndpoints.cs:109-128
   - src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:120-139
6. **12 lines x 2 copies** — first line: `SpokenAdvisories? advisories = null)`
   - src/ProsimCompanion.Speech/Crew/AircraftStateAdvisoryService.cs:39-55
   - src/ProsimCompanion.Speech/Crew/DroppedCallAdvisoryService.cs:30-46
7. **12 lines x 2 copies** — first line: `private WatchSet? _watches;`
   - src/ProsimCompanion.Web/Components/StoreObserverComponent.cs:15-41
   - src/ProsimCompanion.Web/Components/StoreObserverLayout.cs:13-34

### 3b. tests/ only

Clone families (>=12 consecutive non-trivial normalised lines, present in >=2 distinct files, maximal runs, contained families suppressed): 8. Redundant lines (sum of length x (copies-1)): 186.

| Projects involved | families | redundant lines |
|---|---|---|
| ProsimCompanion.Core.Tests | 8 | 186 |

Top 40 by block length:

1. **17 lines x 3 copies** — first line: `Commands.Add((verb, args));`
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateAirportPickPagingTests.cs:179-208
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGatePositionMenuAnswerTests.cs:146-173
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateSuffixVariantServiceTests.cs:281-310
2. **16 lines x 2 copies** — first line: `public List<(string Verb, JsonObject? Args)> Commands { get; } = [];`
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateAirportPickPagingTests.cs:170-202
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateSuffixVariantServiceTests.cs:272-304
3. **15 lines x 3 copies** — first line: `private sealed class FakeGsxApi : IGsxRemoteApi`
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateAirportPickPagingTests.cs:162-190
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGateSuffixVariantServiceTests.cs:264-292
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxSimbriefReloadServiceTests.cs:337-365
4. **15 lines x 2 copies** — first line: `private static IEnumerable<(string Absolute, string Relative)> ProductionSources()`
   - tests/ProsimCompanion.Core.Tests/Aircraft/DataRefCatalogGuardTests.cs:191-213
   - tests/ProsimCompanion.Core.Tests/Gsx/TriggerPathGuardTests.cs:60-82
5. **14 lines x 4 copies** — first line: `private sealed class FakeGsxApi : IGsxRemoteApi`
   - tests/ProsimCompanion.Core.Tests/Gsx/DeiceMenuAnswerTests.cs:242-266
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxGatePositionMenuAnswerTests.cs:131-155
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxMenuControlTests.cs:196-220
   - tests/ProsimCompanion.Core.Tests/Gsx/GsxMenuIntentExecutorTests.cs:511-535
6. **13 lines x 2 copies** — first line: `Dictionary<string, DepartureCycleView>? cycles = null)`
   - tests/ProsimCompanion.Core.Tests/Gsx/DepartureAutomationCoreTests.cs:29-42
   - tests/ProsimCompanion.Core.Tests/Gsx/DeparturePumpEffectPlannerTests.cs:37-50
7. **12 lines x 3 copies** — first line: `private static IEnumerable<(string Absolute, string Relative)> RazorFiles()`
   - tests/ProsimCompanion.Core.Tests/Configuration/OptionSectionGuardTests.cs:150-171
   - tests/ProsimCompanion.Core.Tests/State/StoreObserverGuardTests.cs:35-56
   - tests/ProsimCompanion.Core.Tests/Web/RazorAttributeEscapeGuardTests.cs:41-62
8. **12 lines x 2 copies** — first line: `private sealed class FakeSubscription(string name) : IDataRefSubscription`
   - tests/ProsimCompanion.Core.Tests/Aircraft/SimClockTests.cs:97-115
   - tests/ProsimCompanion.Core.Tests/Mixer/MixerMappingServiceTests.cs:234-255

### 3c. src/ at an 8-line window (supplementary, looser)

Clone families (>=8 consecutive non-trivial normalised lines, present in >=2 distinct files, maximal runs, contained families suppressed): 30. Redundant lines (sum of length x (copies-1)): 316.

| Projects involved | families | redundant lines |
|---|---|---|
| ProsimCompanion.Speech | 11 | 117 |
| ProsimCompanion.Web | 8 | 96 |
| ProsimCompanion.App | 3 | 35 |
| ProsimCompanion.Prosim | 3 | 27 |
| ProsimCompanion.Audio + ProsimCompanion.Gsx | 3 | 24 |
| ProsimCompanion.Audio + ProsimCompanion.Web | 1 | 9 |
| ProsimCompanion.Core | 1 | 8 |

Top 25 by block length:

1. **27 lines x 2 copies** — first line: `_wxBusy = false;`
   - src/ProsimCompanion.Web/Pages/Landing.razor:606-660
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:585-638
2. **21 lines x 2 copies** — first line: `ArgumentNullException.ThrowIfNull(procedures);`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:112-140
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:67-95
3. **15 lines x 2 copies** — first line: `<span class="perf-unit">hPa</span>`
   - src/ProsimCompanion.Web/Pages/Landing.razor:113-146
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:124-157
4. **14 lines x 2 copies** — first line: `private string? ResolveApiKey(SayIntentionsOptions options)`
   - src/ProsimCompanion.Speech/SayIntentions/SayIntentionsGateAssignService.cs:127-151
   - src/ProsimCompanion.Speech/SayIntentions/SayIntentionsWeatherService.cs:356-380
5. **12 lines x 2 copies** — first line: `SpokenAdvisories? advisories = null)`
   - src/ProsimCompanion.Speech/Crew/AircraftStateAdvisoryService.cs:39-55
   - src/ProsimCompanion.Speech/Crew/DroppedCallAdvisoryService.cs:30-46
6. **12 lines x 2 copies** — first line: `private WatchSet? _watches;`
   - src/ProsimCompanion.Web/Components/StoreObserverComponent.cs:15-41
   - src/ProsimCompanion.Web/Components/StoreObserverLayout.cs:13-34
7. **11 lines x 2 copies** — first line: `ArgumentNullException.ThrowIfNull(failures);`
   - src/ProsimCompanion.Speech/Abnormals/EfatoService.cs:46-56
   - src/ProsimCompanion.Speech/Abnormals/RejectedTakeoffService.cs:64-74
8. **11 lines x 2 copies** — first line: `private TimeSpan VerifyPause()`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:547-564
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:355-372
9. **10 lines x 2 copies** — first line: `ArgumentNullException.ThrowIfNull(options);`
   - src/ProsimCompanion.Prosim/ProsimConnectionService.cs:38-48
   - src/ProsimCompanion.Prosim/Sdk/SdkConnection.cs:64-74
10. **9 lines x 3 copies** — first line: `IOptionsMonitor<TelemetryApiOptions> apiOptions,`
   - src/ProsimCompanion.App/Hosting/DiagnosticsEndpoints.cs:111-125
   - src/ProsimCompanion.App/Hosting/SimDamageEndpoints.cs:151-166
   - src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:122-136
11. **9 lines x 2 copies** — first line: `private WindLimitVerdict? WindVerdict()`
   - src/ProsimCompanion.Web/Pages/Landing.razor:377-390
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:644-657
12. **9 lines x 2 copies** — first line: `_logger = logger;`
   - src/ProsimCompanion.Prosim/Loadsheet/EfbResetService.cs:81-95
   - src/ProsimCompanion.Prosim/Passengers/PassengerSimulationService.cs:38-52
13. **9 lines x 2 copies** — first line: `private async Task LoadAirportAsync()`
   - src/ProsimCompanion.Web/Pages/Landing.razor:547-565
   - src/ProsimCompanion.Web/Pages/Takeoff.razor:527-545
14. **9 lines x 2 copies** — first line: `private static IResult? Gate(`
   - src/ProsimCompanion.App/Hosting/DiagnosticsEndpoints.cs:109-122
   - src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:120-133
15. **9 lines x 2 copies** — first line: `public static string ChannelLabel(AudioChannel channel) => channel switch`
   - src/ProsimCompanion.Audio/Diagnostics/RemoteAudioDiagnosticsCollector.cs:153-162
   - src/ProsimCompanion.Web/Components/AudioPresentation.cs:13-22
16. **8 lines x 2 copies** — first line: `<option value="@value" selected="@(value == Value)">@label</option>`
   - src/ProsimCompanion.Web/Components/GroupedSelectField.razor:20-45
   - src/ProsimCompanion.Web/Components/SelectField.razor:12-35
17. **8 lines x 2 copies** — first line: `var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:245-256
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:198-209
18. **8 lines x 2 copies** — first line: `ProcedureSource procedures,`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:102-109
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:57-64
19. **8 lines x 2 copies** — first line: `private readonly ProcedureSource _procedures;`
   - src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:86-93
   - src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:41-48
20. **8 lines x 2 copies** — first line: `ArgumentNullException.ThrowIfNull(dataRefs);`
   - src/ProsimCompanion.Speech/Callouts/GearCallFeature.cs:46-54
   - src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:94-102
21. **8 lines x 2 copies** — first line: `_speech = speech;`
   - src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:170-180
   - src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs:71-81
22. **8 lines x 2 copies** — first line: `_eventLog = eventLog;`
   - src/ProsimCompanion.Speech/Briefings/MissedApproachRebrief.cs:67-80
   - src/ProsimCompanion.Speech/Monitoring/StabilizedApproachMonitor.cs:64-77
23. **8 lines x 2 copies** — first line: `private void Save()`
   - src/ProsimCompanion.Core/Logbook/LogbookService.cs:559-570
   - src/ProsimCompanion.Core/TechLog/TechLogService.cs:570-581
24. **8 lines x 2 copies** — first line: `if (!apiOptions.CurrentValue.Enabled)`
   - src/ProsimCompanion.App/Hosting/DiagnosticsEndpoints.cs:114-128
   - src/ProsimCompanion.App/Hosting/TelemetryApiEndpoints.cs:125-139
25. **8 lines x 2 copies** — first line: `await ReceiveLoopAsync(socket, stoppingToken).ConfigureAwait(false);`
   - src/ProsimCompanion.Gsx/GsxRemoteApiClient.cs:252-267
   - src/ProsimCompanion.Audio/Mixer/MixerClient.cs:277-292


## 4. Session event name inventory (JsonlEventLog.Record call sites)

NOTE: 4a/4a-plain below are the literal call sites only (277 incl. the `speech.` prefix artefact); 4c adds the wrapper-resolved names and 4d is the merged list the probe auditor should use.

Emitter: `src/ProsimCompanion.Core/EventLog/JsonlEventLog.cs` `Record(string type, object? payload)`. Distinct literal event names: 277. Call sites with a non-literal type argument (listed in 4b; these produce names this scan cannot enumerate): 9.

### 4a. Sorted list (name <- emitting file:line)

- `abnormal.branch` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:344; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:355
- `abnormal.cancelled` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:163
- `abnormal.completed` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:191
- `abnormal.ended` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:560
- `abnormal.handoff` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:847
- `abnormal.line` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:216; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:243; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:250; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:257; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:283; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:291; src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:321
- `abnormal.paused` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:368
- `abnormal.resumed` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:397
- `abnormal.skipped` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:530
- `abnormal.started` <- src/ProsimCompanion.Speech/Abnormals/EcamDialogueCore.cs:150
- `acars.mvt` <- src/ProsimCompanion.Speech/Company/MovementMessageService.cs:282
- `aircraft-state-check` <- src/ProsimCompanion.Gsx/Sync/AircraftStateCheckService.cs:380
- `airport-coordinates` <- src/ProsimCompanion.Core/Flight/FlightProgressService.cs:288
- `approach.gate` <- src/ProsimCompanion.Speech/Monitoring/StabilizedApproachMonitor.cs:192
- `arrival-gate-confirmed` <- src/ProsimCompanion.Core/Gate/ArrivalGateCoordinator.cs:307; src/ProsimCompanion.Core/Gate/ArrivalGateCoordinator.cs:353
- `arrival-gate-dispatched` <- src/ProsimCompanion.Core/Gate/ArrivalGateCoordinator.cs:506
- `arrival-gate-restore-failed` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:95
- `arrival-gate-restored` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:85
- `asr.self-echo` <- src/ProsimCompanion.Speech/Recognition/LanAsrRecognizer.cs:452
- `asr.utterance` <- src/ProsimCompanion.Speech/Recognition/LanAsrRecognizer.cs:274
- `briefing.emergency` <- src/ProsimCompanion.Speech/Briefings/BriefingService.cs:289
- `briefing.spoken` <- src/ProsimCompanion.Speech/Briefings/BriefingService.cs:257; src/ProsimCompanion.Speech/Briefings/BriefingService.cs:265
- `cabin.auto-answer` <- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:670
- `cabin.calling` <- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:462; src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:513
- `cabin.cruise-query` <- src/ProsimCompanion.Speech/Cabin/CabinCruiseQueryDialogue.cs:118 (via QueryEvent)
- `cabin.cruise-reply` <- src/ProsimCompanion.Speech/Cabin/CabinCruiseQueryDialogue.cs:140 (via ReplyEvent)
- `cabin.empty` <- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:239
- `cabin.pa` <- src/ProsimCompanion.Speech/Cabin/PurserPaService.cs:149
- `cabin.report` <- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:479
- `cabin.secure-armed` <- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:395
- `cabin.turbulence` <- src/ProsimCompanion.Speech/Cabin/TurbulenceCabinService.cs:120; src/ProsimCompanion.Speech/Cabin/TurbulenceCabinService.cs:131; src/ProsimCompanion.Speech/Cabin/TurbulenceCabinService.cs:265
- `callout.degraded` <- src/ProsimCompanion.Speech/Callouts/CalloutsEngine.cs:556
- `callout.fired` <- src/ProsimCompanion.Speech/Callouts/CalloutsEngine.cs:534; src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:220
- `callout.suppressed` <- src/ProsimCompanion.Speech/Callouts/CalloutsEngine.cs:543; src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:201; src/ProsimCompanion.Speech/Callouts/EngineStartMonitor.cs:211
- `checklist.voice` <- src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:275; src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:343; src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:347; src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:712; src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:753
- `checklist.voice.item` <- src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:333
- `checklist.voice.reminder` <- src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:637
- `cockpit.press` <- src/ProsimCompanion.Prosim/Switches/CockpitSwitchService.cs:254 (via PressEvent)
- `company.diversion` <- src/ProsimCompanion.Speech/Flight/DiversionService.cs:333
- `company.loadsheet` <- src/ProsimCompanion.Speech/Company/CompanyChannelService.cs:380
- `company.message` <- src/ProsimCompanion.Speech/Company/CompanyChannelService.cs:235; src/ProsimCompanion.Speech/Company/CompanyChannelService.cs:419
- `crew.hail` <- src/ProsimCompanion.Speech/Crew/CrewHailService.cs:262
- `crew.hail.hangup` <- src/ProsimCompanion.Speech/Crew/CrewHailService.cs:311; src/ProsimCompanion.Speech/Crew/CrewHailService.cs:334
- `crew.hail.notkeyed` <- src/ProsimCompanion.Speech/Crew/CrewHailService.cs:186
- `crew.hail.request` <- src/ProsimCompanion.Speech/Crew/CrewHailService.cs:346
- `crew.hail.unmapped` <- src/ProsimCompanion.Speech/Crew/CrewHailService.cs:361
- `crewcall.answered` <- src/ProsimCompanion.Speech/Callouts/AntiIceCallFeature.cs:185; src/ProsimCompanion.Speech/Callouts/EngineFlapCallFeature.cs:220
- `day.deviation` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:608
- `day.end-offer.raised` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:904
- `day.ended` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:712
- `day.fdp` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:636
- `day.idle.started` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:312
- `day.leg.completed` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:517
- `day.leg.dropped` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:247
- `day.leg.route` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:452
- `day.leg.started` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:363
- `day.resumed` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:252
- `day.started` <- src/ProsimCompanion.Speech/Day/CompanyDayService.cs:675
- `debrief.spoken` <- src/ProsimCompanion.Speech/Debrief/DebriefService.cs:276; src/ProsimCompanion.Speech/Debrief/DebriefService.cs:341
- `debrief.styled` <- src/ProsimCompanion.Speech/Debrief/DebriefService.cs:225; src/ProsimCompanion.Speech/Debrief/DebriefService.cs:238
- `deice-procedure` <- src/ProsimCompanion.Speech/Gsx/DeicePacksProcedure.cs:208; src/ProsimCompanion.Speech/Gsx/DeicePacksProcedure.cs:217; src/ProsimCompanion.Speech/Gsx/DeicePacksProcedure.cs:223
- `drill.completed` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:724
- `drill.invoked` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:226
- `drill.started` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:482; src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:706
- `efato.armed` <- src/ProsimCompanion.Speech/Abnormals/EfatoService.cs:137
- `efato.handoff` <- src/ProsimCompanion.Speech/Abnormals/EfatoService.cs:181
- `efato.item` <- src/ProsimCompanion.Speech/Abnormals/EfatoService.cs:167; src/ProsimCompanion.Speech/Abnormals/EfatoService.cs:173
- `efb-reset` <- src/ProsimCompanion.Prosim/Loadsheet/EfbResetService.cs:191
- `failure.burst-held` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:413
- `failure.cleared` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:374
- `failure.detected` <- src/ProsimCompanion.Speech/Abnormals/FailureMonitor.cs:467
- `fcu.aborted` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:223
- `fcu.announce` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:202
- `fcu.backoff` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:214
- `fcu.cancelled` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:128
- `fcu.inhibited` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:168
- `fcu.set` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:233
- `fcu.unable` <- src/ProsimCompanion.Speech/Fcu/FcuExecutor.cs:241
- `flight-progress` <- src/ProsimCompanion.Core/Flight/FlightProgressService.cs:169
- `flight-sample` <- src/ProsimCompanion.Core/Flight/FlightSampleRecorder.cs:123
- `flight-state-discarded` <- src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs:298
- `flight-state-restored` <- src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs:341 (via RestoredEvent)
- `flight-times` <- src/ProsimCompanion.Core/Flight/FlightTimesTracker.cs:367 (via EventType)
- `flight.delayCode` <- src/ProsimCompanion.Speech/Company/MovementMessageService.cs:238
- `flight.diversionCancelled` <- src/ProsimCompanion.Speech/Flight/DiversionService.cs:293
- `flight.diverted` <- src/ProsimCompanion.Speech/Flight/DiversionService.cs:244
- `flight.punctuality` <- src/ProsimCompanion.Speech/Company/MovementMessageService.cs:174
- `flight.route` <- src/ProsimCompanion.Speech/Briefings/BriefingService.cs:377
- `flow.advisory` <- src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs:194; src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs:245; src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs:257
- `flow.resolved` <- src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs:263
- `fms.cleared` <- src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:218
- `fo.ack` <- src/ProsimCompanion.Speech/Questions/FoQuestionService.cs:149 (via AckEvent)
- `fo.aircraft-state-advisory` <- src/ProsimCompanion.Speech/Crew/AircraftStateAdvisoryService.cs:92
- `fo.answer` <- src/ProsimCompanion.Speech/Questions/FoQuestionService.cs:618 (via AnswerEvent)
- `fo.baroAnnounce` <- src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:283
- `fo.baroSet` <- src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:259; src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:266; src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:290; src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:301
- `fo.cancelled` <- src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:159
- `fo.crossCheck` <- src/ProsimCompanion.Speech/FoSide/FmgcCrossCheckService.cs:249; src/ProsimCompanion.Speech/FoSide/FmgcCrossCheckService.cs:266
- `fo.destination-weather` <- src/ProsimCompanion.Speech/Monitoring/DestinationWeatherWatch.cs:133
- `fo.dropped-call-advisory` <- src/ProsimCompanion.Speech/Crew/DroppedCallAdvisoryService.cs:103
- `fo.efisSet` <- src/ProsimCompanion.Speech/FoSide/FoSideDutiesService.cs:377
- `fo.fms-reminder` <- src/ProsimCompanion.Speech/FoSide/FmsPlanReminderService.cs:99 (via EventName)
- `fo.gross-error-check` <- src/ProsimCompanion.Speech/Monitoring/GrossErrorCheckMonitor.cs:213
- `fo.parking-conflict-advisory` <- src/ProsimCompanion.Speech/Crew/ParkingConflictAdvisoryService.cs:180
- `fo.parking-conflict-held` <- src/ProsimCompanion.Speech/Crew/ParkingConflictAdvisoryService.cs:95
- `fo.query` <- src/ProsimCompanion.Speech/Questions/FoQuestionService.cs:166 (via QueryEvent)
- `fo.readback` <- src/ProsimCompanion.Speech/Monitoring/StandaloneReadbacks.cs:114
- `fo.region-fact` <- src/ProsimCompanion.Speech/Immersion/RegionFactService.cs:423 (via EventType)
- `fo.wrong-frequency-advisory` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs:531
- `fuel.check` <- src/ProsimCompanion.Speech/Monitoring/FuelCheckMonitor.cs:153; src/ProsimCompanion.Speech/Monitoring/FuelCheckMonitor.cs:172; src/ProsimCompanion.Speech/Monitoring/FuelCheckMonitor.cs:229; src/ProsimCompanion.Speech/Monitoring/FuelCheckMonitor.cs:265
- `fuel.decision` <- src/ProsimCompanion.Speech/Monitoring/FuelDecisionService.cs:277
- `fuel.extraApplied` <- src/ProsimCompanion.Speech/Monitoring/FuelDecisionService.cs:343
- `fuel.holdingAvailableMinutes` <- src/ProsimCompanion.Speech/Monitoring/FuelCheckMonitor.cs:309; src/ProsimCompanion.Speech/Monitoring/FuelDecisionService.cs:374
- `fuel.log.fix` <- src/ProsimCompanion.Core/Flight/FuelLogService.cs:143 (via FixEvent)
- `fuel.log.skipped` <- src/ProsimCompanion.Core/Flight/FuelLogService.cs:162 (via SkippedEvent)
- `gate-status-changed` <- src/ProsimCompanion.Core/Boarding/GateMonitorService.cs:133
- `gate.std-aligned` <- src/ProsimCompanion.Core/Boarding/GateMonitorService.cs:208
- `goaround.detected` <- src/ProsimCompanion.Speech/Briefings/MissedApproachRebrief.cs:146
- `goaround.rebrief` <- src/ProsimCompanion.Speech/Briefings/MissedApproachRebrief.cs:171
- `ground.calling` <- src/ProsimCompanion.Speech/Crew/GroundCrewUpcallService.cs:175
- `ground.upcall` <- src/ProsimCompanion.Speech/Crew/GroundCrewUpcallService.cs:213
- `gsx-call-dropped` <- src/ProsimCompanion.Gsx/Automation/GsxTriggerSlot.cs:301
- `gsx-decision` <- src/ProsimCompanion.Gsx/Automation/GsxDeiceRequestService.cs:330; src/ProsimCompanion.Gsx/Automation/GsxDepartureDecisionLog.cs:61; src/ProsimCompanion.Gsx/Automation/GsxTriggerSlot.cs:330; src/ProsimCompanion.Gsx/Sync/GsxSimbriefReloadService.cs:465
- `gsx-deice-fluid` <- src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:620; src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:635; src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:658; src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:745
- `gsx-deice-policy` <- src/ProsimCompanion.Gsx/Automation/GsxDeiceRequestService.cs:180; src/ProsimCompanion.Gsx/Automation/GsxDeiceRequestService.cs:278
- `gsx-deice-state` <- src/ProsimCompanion.Gsx/Sync/GsxDeiceStateMonitor.cs:102
- `gsx-engine-restarted` <- src/ProsimCompanion.Gsx/GsxBootstrapService.cs:243
- `gsx-gate-adopted` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:862
- `gsx-gate-airport-pick` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:523
- `gsx-gate-assigned` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:976
- `gsx-gate-confirmed` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:1110
- `gsx-gate-failed` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:1122
- `gsx-gate-not-found` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:1015; src/ProsimCompanion.Gsx/Sync/GsxGateAnchorService.cs:136
- `gsx-gate-number-fallback` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:614
- `gsx-gate-position-menu` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:1215; src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:1237
- `gsx-gate-resolved` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:906
- `gsx-gate-suffix-choice` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:763
- `gsx-gate-too-late` <- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:310
- `gsx-ground-prep-stage` <- src/ProsimCompanion.Gsx/Sync/GsxGroundPrepCoordinator.cs:182; src/ProsimCompanion.Gsx/Sync/GsxGroundPrepCoordinator.cs:507
- `gsx-handler` <- src/ProsimCompanion.App/Hosting/GsxHandlerEndpoints.cs:50
- `gsx-menu-card` <- src/ProsimCompanion.Gsx/Menu/GsxMenuControl.cs:244
- `gsx-parking-wake` <- src/ProsimCompanion.Gsx/Sync/GsxParkingWakeService.cs:289
- `gsx-question` <- src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:790
- `gsx-readiness` <- src/ProsimCompanion.Gsx/GsxBootstrapService.cs:138
- `gsx-service` <- src/ProsimCompanion.Gsx/GsxBootstrapService.cs:248
- `gsx-simbrief-reload` <- src/ProsimCompanion.Gsx/Sync/GsxSimbriefReloadService.cs:466
- `hero-weather-refreshed` <- src/ProsimCompanion.Core/Weather/HeroWeatherService.cs:151
- `input.vjoy` <- src/ProsimCompanion.Sim/Input/VJoyInputRelay.cs:150 (via PressEvent)
- `leg.boundary` <- src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:284; src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:326
- `llm.stream` <- src/ProsimCompanion.Speech/Llm/StreamingNarrator.cs:379 (via EventType)
- `loadsheet.lmc` <- src/ProsimCompanion.Prosim/Loadsheet/LoadsheetService.Lmc.cs:122; src/ProsimCompanion.Prosim/Loadsheet/LoadsheetService.Lmc.cs:191; src/ProsimCompanion.Prosim/Loadsheet/LoadsheetService.Lmc.cs:51
- `loadsheet.lmc.spoken` <- src/ProsimCompanion.Speech/Company/LmcAnnouncer.cs:86
- `logbook.day-recorded` <- src/ProsimCompanion.Core/Logbook/LogbookService.cs:181
- `logbook.folded` <- src/ProsimCompanion.Core/Logbook/LogbookService.cs:233
- `logbook.removed` <- src/ProsimCompanion.Core/Logbook/LogbookService.cs:279
- `mcdu.arrival.aborted` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:269
- `mcdu.arrival.announce` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:262
- `mcdu.arrival.cancelled` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:278
- `mcdu.arrival.erased` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:529
- `mcdu.arrival.refused` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:206; src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:213
- `mcdu.arrival.set` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:480
- `mcdu.arrival.starNotFound` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:446
- `mcdu.arrival.unable` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:241; src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:544
- `mcdu.arrival.unconfirmed` <- src/ProsimCompanion.Speech/Mcdu/McduArrivalChanger.cs:475
- `mcdu.goto` <- src/ProsimCompanion.Speech/Mcdu/McduActuator.cs:189
- `mcdu.radnav.aborted` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:220
- `mcdu.radnav.announce` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:214
- `mcdu.radnav.cancelled` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:302
- `mcdu.radnav.ident-refused` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:249
- `mcdu.radnav.refused` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:149; src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:156
- `mcdu.radnav.tuned` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:290
- `mcdu.radnav.unable` <- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:184; src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:191; src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:228; src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:266; src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs:295
- `mcdu.read` <- src/ProsimCompanion.Speech/Mcdu/McduReader.cs:162; src/ProsimCompanion.Speech/Mcdu/McduReader.cs:167
- `mcdu.read.unavailable` <- src/ProsimCompanion.Speech/Mcdu/McduReader.cs:153
- `mcdu.type` <- src/ProsimCompanion.Speech/Mcdu/McduActuator.cs:169
- `minima.captured` <- src/ProsimCompanion.Speech/Briefings/MinimaCapture.cs:197
- `minima.notBriefed` <- src/ProsimCompanion.Speech/Briefings/MinimaCapture.cs:156; src/ProsimCompanion.Speech/Briefings/MinimaCapture.cs:189
- `notify.failed` <- src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs:111 (via FailedEvent); src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs:146 (via FailedEvent); src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs:240 (via FailedEvent)
- `notify.sent` <- src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs:210 (via SentEvent)
- `ofp.fetched-in-flight` <- src/ProsimCompanion.Core/Flight/FlightStateSnapshotService.cs:667 (via PlanFetchedEvent)
- `ofp.loaded` <- src/ProsimCompanion.Core/Aircraft/Ofp/OfpEventRecorder.cs:82
- `pa.prompt` <- src/ProsimCompanion.Speech/Cabin/PaPromptService.cs:186
- `persona.foOnDuty` <- src/ProsimCompanion.Speech/Persona/FoRosterVoiceFeature.cs:201; src/ProsimCompanion.Speech/Persona/FoRosterVoiceFeature.cs:404
- `persona.gateIntro` <- src/ProsimCompanion.Speech/Persona/FoRosterVoiceFeature.cs:278
- `persona.rosterDraw` <- src/ProsimCompanion.Core/Persona/FoRosterService.cs:143
- `persona.rosterPin` <- src/ProsimCompanion.Core/Persona/FoRosterService.cs:206
- `persona.rosterRelease` <- src/ProsimCompanion.Core/Persona/FoRosterService.cs:223
- `phase-changed` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:136
- `pilot.announcement` <- src/ProsimCompanion.Speech/Callouts/PilotAnnouncementFeature.cs:189
- `profile-changed` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:197
- `prosim-setup-apply` <- src/ProsimCompanion.Prosim/Setup/ProsimSetupCheckService.cs:192
- `prosim-setup-check` <- src/ProsimCompanion.Prosim/Setup/ProsimSetupCheckService.cs:265
- `pushback-choice` <- src/ProsimCompanion.Speech/Gsx/PushbackDirectionVoice.cs:253
- `pushback-direction-decision` <- src/ProsimCompanion.Gsx/Menu/GsxQuestionCatalog.cs:463
- `pushback-suggestion` <- src/ProsimCompanion.Core/Airports/Parking/PushbackSuggestionService.cs:225
- `radio.announce` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:240
- `radio.backoff` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:251
- `radio.cancelled` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:152
- `radio.set` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:261
- `radio.swapped` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:316
- `radio.swapUnverified` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:324
- `radio.tile-tune` <- src/ProsimCompanion.Core/Radios/Com1PanelService.cs:133
- `radio.unable` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:267
- `radio.unreadable` <- src/ProsimCompanion.Speech/Radios/RadioExecutor.cs:196
- `roles.confirm-requested` <- src/ProsimCompanion.Speech/Roles/RoleManager.cs:132
- `roles.handover` <- src/ProsimCompanion.Speech/Roles/RoleManager.cs:189
- `roles.pf-reply` <- src/ProsimCompanion.Speech/Roles/RoleManager.cs:166
- `sayintentions.arrival-gate` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsArrivalGateSource.cs:100
- `sayintentions.atc-line` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs:453
- `sayintentions.atc-readout` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:686
- `sayintentions.checkin` <- src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs:508
- `sayintentions.comms` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:715
- `sayintentions.departureGate` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:868
- `sayintentions.fo-radios` <- src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs:143
- `sayintentions.frequency-assigned` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs:510; src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs:517
- `sayintentions.ptt` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsPttRelay.cs:211
- `sayintentions.readback` <- src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs:327; src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs:353
- `sayintentions.request` <- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:396
- `scenario.armCancelled` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:455
- `scenario.armed` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:500 (via ArmedEvent)
- `scenario.armFailed` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:466; src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:486
- `scenario.arming` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:448 (via ArmingEvent)
- `scenario.clearAllRequested` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:828
- `scenario.decided` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:624
- `scenario.decision` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:608
- `scenario.failuresListed` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:888
- `scenario.fired` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:545 (via FiredEvent)
- `scenario.planned` <- src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:310
- `session-ended` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:106
- `session-rotated` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:124 (via SessionHeader.RotatedEvent)
- `session-started` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:65 (via SessionHeader.StartedEvent)
- `session.finalized` <- src/ProsimCompanion.Core/Sessions/SessionFinalizer.cs:149
- `smalltalk.reply` <- src/ProsimCompanion.Speech/Persona/CruiseSmallTalkService.cs:500 (via ReplyEvent); src/ProsimCompanion.Speech/Persona/CruiseSmallTalkService.cs:547 (via ReplyEvent); src/ProsimCompanion.Speech/Persona/CruiseSmallTalkService.cs:560 (via ReplyEvent)
- `smalltalk.silenced` <- src/ProsimCompanion.Speech/Persona/SmallTalkService.cs:124
- `smalltalk.spoken` <- src/ProsimCompanion.Speech/Persona/CruiseSmallTalkService.cs:683 (via SpokenEvent)
- `speech.` <- src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613
- `step-climb.advice` <- src/ProsimCompanion.Speech/Monitoring/StepClimbAdvisor.cs:209
- `step-climb.query` <- src/ProsimCompanion.Speech/Monitoring/StepClimbAdvisor.cs:138
- `styling.decision` <- src/ProsimCompanion.Speech/Persona/StyledSpeechService.cs:173
- `subsystem-status` <- src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:204
- `takeoff.rejected` <- src/ProsimCompanion.Speech/Abnormals/RejectedTakeoffService.cs:238
- `takeoff.rto-callout` <- src/ProsimCompanion.Speech/Abnormals/RejectedTakeoffService.cs:250
- `takeoff.rto-complete` <- src/ProsimCompanion.Speech/Abnormals/RejectedTakeoffService.cs:220
- `takeoff.stop-refused` <- src/ProsimCompanion.Speech/Abnormals/RejectedTakeoffService.cs:163
- `techlog.autorectified` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:501
- `techlog.briefed` <- src/ProsimCompanion.Speech/TechLog/TechLogVoiceService.cs:247
- `techlog.carried` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:421
- `techlog.consulted` <- src/ProsimCompanion.Speech/Briefings/BriefingService.cs:590; src/ProsimCompanion.Speech/Checklists/SpokenChecklistEngine.cs:935
- `techlog.offer` <- src/ProsimCompanion.Speech/TechLog/TechLogDialogueCore.cs:185
- `techlog.prefilledRaise` <- src/ProsimCompanion.Speech/TechLog/TechLogDialogueCore.cs:113
- `techlog.raised` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:206
- `techlog.rectified` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:257
- `techlog.removed` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:278
- `techlog.wear` <- src/ProsimCompanion.Core/TechLog/TechLogService.cs:470
- `tod-approaching` <- src/ProsimCompanion.Core/Flight/FlightProgressService.cs:200
- `touchdown` <- src/ProsimCompanion.Core/Flight/TouchdownRecorder.cs:121 (via EventType)
- `transition.reminder` <- src/ProsimCompanion.Speech/Callouts/TransitionReminderService.cs:162 (via ReminderEvent)
- `turbulence.calibrated` <- src/ProsimCompanion.Core/Flight/TurbulenceMonitor.cs:137
- `turbulence.episode-ended` <- src/ProsimCompanion.Core/Flight/TurbulenceMonitor.cs:162
- `turbulence.level` <- src/ProsimCompanion.Core/Flight/TurbulenceMonitor.cs:147
- `voice.unmatched` <- src/ProsimCompanion.Speech/Recognition/UnmatchedUtteranceTracker.cs:92
- `voicecommand.chrono` <- src/ProsimCompanion.Speech/Callouts/ChronoCallFeature.cs:156
- `voicecommand.disregard` <- src/ProsimCompanion.Speech/Crew/DisregardVoiceFeature.cs:81
- `voicecommand.executed` <- src/ProsimCompanion.Speech/Commands/ConfiguredVoiceCommands.cs:350
- `voicecommand.gear` <- src/ProsimCompanion.Speech/Callouts/GearCallFeature.cs:124
- `voicecommand.query` <- src/ProsimCompanion.Speech/Commands/ConfiguredVoiceCommands.cs:292
- `voicecommand.verified` <- src/ProsimCompanion.Speech/Commands/ConfiguredVoiceCommands.cs:373
- `walkaround.abandoned` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:379
- `walkaround.finding` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:486
- `walkaround.noted` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:524
- `walkaround.reported` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:417
- `walkaround.restored` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:313
- `walkaround.started` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:358
- `walkaround.techLogRaised` <- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:479; src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:539
- `wind.check` <- src/ProsimCompanion.Speech/Monitoring/WindLimitsVoiceFeature.cs:152
- `wind.limit-advisory` <- src/ProsimCompanion.Speech/Monitoring/WindLimitAdvisory.cs:160
- `wind.runway-condition` <- src/ProsimCompanion.Speech/Monitoring/WindLimitsVoiceFeature.cs:187

### 4a-plain. Plain sorted names (for the probe auditor)

```
abnormal.branch
abnormal.cancelled
abnormal.completed
abnormal.ended
abnormal.handoff
abnormal.line
abnormal.paused
abnormal.resumed
abnormal.skipped
abnormal.started
acars.mvt
aircraft-state-check
airport-coordinates
approach.gate
arrival-gate-confirmed
arrival-gate-dispatched
arrival-gate-restore-failed
arrival-gate-restored
asr.self-echo
asr.utterance
briefing.emergency
briefing.spoken
cabin.auto-answer
cabin.calling
cabin.cruise-query
cabin.cruise-reply
cabin.empty
cabin.pa
cabin.report
cabin.secure-armed
cabin.turbulence
callout.degraded
callout.fired
callout.suppressed
checklist.voice
checklist.voice.item
checklist.voice.reminder
cockpit.press
company.diversion
company.loadsheet
company.message
crew.hail
crew.hail.hangup
crew.hail.notkeyed
crew.hail.request
crew.hail.unmapped
crewcall.answered
day.deviation
day.end-offer.raised
day.ended
day.fdp
day.idle.started
day.leg.completed
day.leg.dropped
day.leg.route
day.leg.started
day.resumed
day.started
debrief.spoken
debrief.styled
deice-procedure
drill.completed
drill.invoked
drill.started
efato.armed
efato.handoff
efato.item
efb-reset
failure.burst-held
failure.cleared
failure.detected
fcu.aborted
fcu.announce
fcu.backoff
fcu.cancelled
fcu.inhibited
fcu.set
fcu.unable
flight-progress
flight-sample
flight-state-discarded
flight-state-restored
flight-times
flight.delayCode
flight.diversionCancelled
flight.diverted
flight.punctuality
flight.route
flow.advisory
flow.resolved
fms.cleared
fo.ack
fo.aircraft-state-advisory
fo.answer
fo.baroAnnounce
fo.baroSet
fo.cancelled
fo.crossCheck
fo.destination-weather
fo.dropped-call-advisory
fo.efisSet
fo.fms-reminder
fo.gross-error-check
fo.parking-conflict-advisory
fo.parking-conflict-held
fo.query
fo.readback
fo.region-fact
fo.wrong-frequency-advisory
fuel.check
fuel.decision
fuel.extraApplied
fuel.holdingAvailableMinutes
fuel.log.fix
fuel.log.skipped
gate-status-changed
gate.std-aligned
goaround.detected
goaround.rebrief
ground.calling
ground.upcall
gsx-call-dropped
gsx-decision
gsx-deice-fluid
gsx-deice-policy
gsx-deice-state
gsx-engine-restarted
gsx-gate-adopted
gsx-gate-airport-pick
gsx-gate-assigned
gsx-gate-confirmed
gsx-gate-failed
gsx-gate-not-found
gsx-gate-number-fallback
gsx-gate-position-menu
gsx-gate-resolved
gsx-gate-suffix-choice
gsx-gate-too-late
gsx-ground-prep-stage
gsx-handler
gsx-menu-card
gsx-parking-wake
gsx-question
gsx-readiness
gsx-service
gsx-simbrief-reload
hero-weather-refreshed
input.vjoy
leg.boundary
llm.stream
loadsheet.lmc
loadsheet.lmc.spoken
logbook.day-recorded
logbook.folded
logbook.removed
mcdu.arrival.aborted
mcdu.arrival.announce
mcdu.arrival.cancelled
mcdu.arrival.erased
mcdu.arrival.refused
mcdu.arrival.set
mcdu.arrival.starNotFound
mcdu.arrival.unable
mcdu.arrival.unconfirmed
mcdu.goto
mcdu.radnav.aborted
mcdu.radnav.announce
mcdu.radnav.cancelled
mcdu.radnav.ident-refused
mcdu.radnav.refused
mcdu.radnav.tuned
mcdu.radnav.unable
mcdu.read
mcdu.read.unavailable
mcdu.type
minima.captured
minima.notBriefed
notify.failed
notify.sent
ofp.fetched-in-flight
ofp.loaded
pa.prompt
persona.foOnDuty
persona.gateIntro
persona.rosterDraw
persona.rosterPin
persona.rosterRelease
phase-changed
pilot.announcement
profile-changed
prosim-setup-apply
prosim-setup-check
pushback-choice
pushback-direction-decision
pushback-suggestion
radio.announce
radio.backoff
radio.cancelled
radio.set
radio.swapped
radio.swapUnverified
radio.tile-tune
radio.unable
radio.unreadable
roles.confirm-requested
roles.handover
roles.pf-reply
sayintentions.arrival-gate
sayintentions.atc-line
sayintentions.atc-readout
sayintentions.checkin
sayintentions.comms
sayintentions.departureGate
sayintentions.fo-radios
sayintentions.frequency-assigned
sayintentions.ptt
sayintentions.readback
sayintentions.request
scenario.armCancelled
scenario.armed
scenario.armFailed
scenario.arming
scenario.clearAllRequested
scenario.decided
scenario.decision
scenario.failuresListed
scenario.fired
scenario.planned
session-ended
session-rotated
session-started
session.finalized
smalltalk.reply
smalltalk.silenced
smalltalk.spoken
speech.
step-climb.advice
step-climb.query
styling.decision
subsystem-status
takeoff.rejected
takeoff.rto-callout
takeoff.rto-complete
takeoff.stop-refused
techlog.autorectified
techlog.briefed
techlog.carried
techlog.consulted
techlog.offer
techlog.prefilledRaise
techlog.raised
techlog.rectified
techlog.removed
techlog.wear
tod-approaching
touchdown
transition.reminder
turbulence.calibrated
turbulence.episode-ended
turbulence.level
voice.unmatched
voicecommand.chrono
voicecommand.disregard
voicecommand.executed
voicecommand.gear
voicecommand.query
voicecommand.verified
walkaround.abandoned
walkaround.finding
walkaround.noted
walkaround.reported
walkaround.restored
walkaround.started
walkaround.techLogRaised
wind.check
wind.limit-advisory
wind.runway-condition
```

### 4b. Non-literal type arguments (need manual follow-up)

- expression: `eventName` — src/ProsimCompanion.App/Logging/SessionEventLogSink.cs:100
- expression: `change.Kind switch
            {
                FlightPauseEvent.Paused => "sim-paused"` — src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:181
- expression: `type` — src/ProsimCompanion.Gsx/Automation/GsxDepartureDecisionLog.cs:81
- expression: `table.EventName` — src/ProsimCompanion.Speech/Gsx/CaptainsQuestionDialogue.cs:150
- expression: `table.EventName` — src/ProsimCompanion.Speech/Gsx/CaptainsQuestionDialogue.cs:220
- expression: `table.EventName` — src/ProsimCompanion.Speech/Gsx/CaptainsQuestionDialogue.cs:242
- expression: `eventType` — src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs:532
- expression: `verified ? ClearedEvent : "scenario.clearFailed"` — src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:704
- expression: `count >= 0 ? ClearedEvent : "scenario.clearFailed"` — src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:853



### 4c. Names resolved by hand through wrapper paths (39 new + 2 already literal)

| Event name | Path |
|---|---|
| `log.warning` | src/ProsimCompanion.App/Logging/SessionEventLogSink.cs:100 via SessionLogEventFormatter.EventName (ILogger Warning+ mirrored into the session log) |
| `log.error` | src/ProsimCompanion.App/Logging/SessionEventLogSink.cs:100 via SessionLogEventFormatter.EventName |
| `log.fatal` | src/ProsimCompanion.App/Logging/SessionEventLogSink.cs:100 via SessionLogEventFormatter.EventName |
| `sim-paused` | src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:181 (switch on FlightPauseEvent) |
| `sim-resumed` | src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:181 (switch on FlightPauseEvent) |
| `sim-pause-ended` | src/ProsimCompanion.Core/Hosting/CoreBootstrapService.cs:181 (switch on FlightPauseEvent) |
| `gsx-automation-phase` | via IGsxDepartureDecisionLog.RecordEvent(type) -> GsxDepartureDecisionLog.cs:81 (literal at a RecordEvent call site in src/ProsimCompanion.Gsx) |
| `gsx-cycle-reset` | via IGsxDepartureDecisionLog.RecordEvent(type) -> GsxDepartureDecisionLog.cs:81 (2 call sites) |
| `gsx-departure-started` | via IGsxDepartureDecisionLog.RecordEvent(type) -> GsxDepartureDecisionLog.cs:81 |
| `gsx-departure-complete` | via IGsxDepartureDecisionLog.RecordEvent and RecordEventEffect in src/ProsimCompanion.Gsx/Automation/DeparturePumpEffectPlanner.cs:195 |
| `gsx-turnaround-question` | RecordEventEffect in src/ProsimCompanion.Gsx/Automation/DeparturePumpEffectPlanner.cs:81 -> DeparturePumpEffectApplier.cs:110 -> RecordEvent |
| `scenario.cleared` | src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:704,853 (ClearedEvent const, ternary) |
| `scenario.clearFailed` | src/ProsimCompanion.Speech/Scenarios/ScenarioService.cs:704,853 (ternary) |
| `speech.enqueued` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 ("speech." + SpeechEventKind lower-cased) |
| `speech.dequeued` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.spoken` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.preempted` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.requeued` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.segment` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.deferred` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.dropped` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.expired` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.suppressed` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `speech.failed` | src/ProsimCompanion.Speech/Arbiter/SpeechArbiterService.cs:613 |
| `briefing.questions` | table.EventName via CaptainsQuestionDialogue.cs:150/220/242 <- src/ProsimCompanion.Speech/Briefings/BriefingQuestionsPhrases.cs:43 |
| `briefing.threats` | table.EventName <- src/ProsimCompanion.Speech/Briefings/ThreatsQuestionPhrases.cs:38 |
| `cabin-secure-question` | table.EventName <- src/ProsimCompanion.Speech/Cabin/CabinSecureQuestionPhrases.cs:28 |
| `delay-code-question` | table.EventName <- src/ProsimCompanion.Speech/Company/DelayCodePhrases.cs:26 |
| `loadsheet-revision-question` | table.EventName <- src/ProsimCompanion.Speech/Company/LmcRevisionPhrases.cs:27 |
| `walkaround.raiseQuestion` | table.EventName <- src/ProsimCompanion.Speech/Crew/WalkaroundRaisePhrases.cs:25 |
| `day.end-offer` | table.EventName <- src/ProsimCompanion.Speech/Day/EndOfDutyPhrases.cs:19 |
| `deice-question` | table.EventName <- src/ProsimCompanion.Speech/Gsx/DeiceQuestionPhrases.cs:32 |
| `gate-choice-question` | table.EventName <- src/ProsimCompanion.Speech/Gsx/GateChoicePhrases.cs:36 |
| `pushback-question` | table.EventName <- src/ProsimCompanion.Speech/Gsx/PushbackQuestionPhrases.cs:33 |
| `turnaround-start-question` | table.EventName <- src/ProsimCompanion.Speech/Gsx/TurnaroundStartPhrases.cs:29 |
| `fuel-decision-question` | table.EventName <- src/ProsimCompanion.Speech/Monitoring/FuelDecisionPhrases.cs:25 |
| `persona.rosterChangeQuestion` | table.EventName <- src/ProsimCompanion.Speech/Persona/FoRosterChangePhrases.cs:25 |
| `smalltalk.offerQuestion` | table.EventName <- src/ProsimCompanion.Speech/Persona/SmallTalkOfferPhrases.cs:25 |
| `scenario.decisionQuestion` | table.EventName <- src/ProsimCompanion.Speech/Scenarios/ScenarioDecisionPhrases.cs:27 |
| `sayintentions.readback` | FoRadiosService.TransmitAsync(eventType) callers :384 (also a direct literal elsewhere) |
| `sayintentions.checkin` | FoRadiosService.TransmitAsync(eventType) callers :462 (also a direct literal elsewhere) |

### 4d. Full deduplicated list (315)

```
abnormal.branch
abnormal.cancelled
abnormal.completed
abnormal.ended
abnormal.handoff
abnormal.line
abnormal.paused
abnormal.resumed
abnormal.skipped
abnormal.started
acars.mvt
aircraft-state-check
airport-coordinates
approach.gate
arrival-gate-confirmed
arrival-gate-dispatched
arrival-gate-restore-failed
arrival-gate-restored
asr.self-echo
asr.utterance
briefing.emergency
briefing.questions
briefing.spoken
briefing.threats
cabin-secure-question
cabin.auto-answer
cabin.calling
cabin.cruise-query
cabin.cruise-reply
cabin.empty
cabin.pa
cabin.report
cabin.secure-armed
cabin.turbulence
callout.degraded
callout.fired
callout.suppressed
checklist.voice
checklist.voice.item
checklist.voice.reminder
cockpit.press
company.diversion
company.loadsheet
company.message
crew.hail
crew.hail.hangup
crew.hail.notkeyed
crew.hail.request
crew.hail.unmapped
crewcall.answered
day.deviation
day.end-offer
day.end-offer.raised
day.ended
day.fdp
day.idle.started
day.leg.completed
day.leg.dropped
day.leg.route
day.leg.started
day.resumed
day.started
debrief.spoken
debrief.styled
deice-procedure
deice-question
delay-code-question
drill.completed
drill.invoked
drill.started
efato.armed
efato.handoff
efato.item
efb-reset
failure.burst-held
failure.cleared
failure.detected
fcu.aborted
fcu.announce
fcu.backoff
fcu.cancelled
fcu.inhibited
fcu.set
fcu.unable
flight-progress
flight-sample
flight-state-discarded
flight-state-restored
flight-times
flight.delayCode
flight.diversionCancelled
flight.diverted
flight.punctuality
flight.route
flow.advisory
flow.resolved
fms.cleared
fo.ack
fo.aircraft-state-advisory
fo.answer
fo.baroAnnounce
fo.baroSet
fo.cancelled
fo.crossCheck
fo.destination-weather
fo.dropped-call-advisory
fo.efisSet
fo.fms-reminder
fo.gross-error-check
fo.parking-conflict-advisory
fo.parking-conflict-held
fo.query
fo.readback
fo.region-fact
fo.wrong-frequency-advisory
fuel-decision-question
fuel.check
fuel.decision
fuel.extraApplied
fuel.holdingAvailableMinutes
fuel.log.fix
fuel.log.skipped
gate-choice-question
gate-status-changed
gate.std-aligned
goaround.detected
goaround.rebrief
ground.calling
ground.upcall
gsx-automation-phase
gsx-call-dropped
gsx-cycle-reset
gsx-decision
gsx-deice-fluid
gsx-deice-policy
gsx-deice-state
gsx-departure-complete
gsx-departure-started
gsx-engine-restarted
gsx-gate-adopted
gsx-gate-airport-pick
gsx-gate-assigned
gsx-gate-confirmed
gsx-gate-failed
gsx-gate-not-found
gsx-gate-number-fallback
gsx-gate-position-menu
gsx-gate-resolved
gsx-gate-suffix-choice
gsx-gate-too-late
gsx-ground-prep-stage
gsx-handler
gsx-menu-card
gsx-parking-wake
gsx-question
gsx-readiness
gsx-service
gsx-simbrief-reload
gsx-turnaround-question
hero-weather-refreshed
input.vjoy
leg.boundary
llm.stream
loadsheet-revision-question
loadsheet.lmc
loadsheet.lmc.spoken
log.error
log.fatal
log.warning
logbook.day-recorded
logbook.folded
logbook.removed
mcdu.arrival.aborted
mcdu.arrival.announce
mcdu.arrival.cancelled
mcdu.arrival.erased
mcdu.arrival.refused
mcdu.arrival.set
mcdu.arrival.starNotFound
mcdu.arrival.unable
mcdu.arrival.unconfirmed
mcdu.goto
mcdu.radnav.aborted
mcdu.radnav.announce
mcdu.radnav.cancelled
mcdu.radnav.ident-refused
mcdu.radnav.refused
mcdu.radnav.tuned
mcdu.radnav.unable
mcdu.read
mcdu.read.unavailable
mcdu.type
minima.captured
minima.notBriefed
notify.failed
notify.sent
ofp.fetched-in-flight
ofp.loaded
pa.prompt
persona.foOnDuty
persona.gateIntro
persona.rosterChangeQuestion
persona.rosterDraw
persona.rosterPin
persona.rosterRelease
phase-changed
pilot.announcement
profile-changed
prosim-setup-apply
prosim-setup-check
pushback-choice
pushback-direction-decision
pushback-question
pushback-suggestion
radio.announce
radio.backoff
radio.cancelled
radio.set
radio.swapped
radio.swapUnverified
radio.tile-tune
radio.unable
radio.unreadable
roles.confirm-requested
roles.handover
roles.pf-reply
sayintentions.arrival-gate
sayintentions.atc-line
sayintentions.atc-readout
sayintentions.checkin
sayintentions.comms
sayintentions.departureGate
sayintentions.fo-radios
sayintentions.frequency-assigned
sayintentions.ptt
sayintentions.readback
sayintentions.request
scenario.armCancelled
scenario.armed
scenario.armFailed
scenario.arming
scenario.clearAllRequested
scenario.cleared
scenario.clearFailed
scenario.decided
scenario.decision
scenario.decisionQuestion
scenario.failuresListed
scenario.fired
scenario.planned
session-ended
session-rotated
session-started
session.finalized
sim-pause-ended
sim-paused
sim-resumed
smalltalk.offerQuestion
smalltalk.reply
smalltalk.silenced
smalltalk.spoken
speech.deferred
speech.dequeued
speech.dropped
speech.enqueued
speech.expired
speech.failed
speech.preempted
speech.requeued
speech.segment
speech.spoken
speech.suppressed
step-climb.advice
step-climb.query
styling.decision
subsystem-status
takeoff.rejected
takeoff.rto-callout
takeoff.rto-complete
takeoff.stop-refused
techlog.autorectified
techlog.briefed
techlog.carried
techlog.consulted
techlog.offer
techlog.prefilledRaise
techlog.raised
techlog.rectified
techlog.removed
techlog.wear
tod-approaching
touchdown
transition.reminder
turbulence.calibrated
turbulence.episode-ended
turbulence.level
turnaround-start-question
voice.unmatched
voicecommand.chrono
voicecommand.disregard
voicecommand.executed
voicecommand.gear
voicecommand.query
voicecommand.verified
walkaround.abandoned
walkaround.finding
walkaround.noted
walkaround.raiseQuestion
walkaround.reported
walkaround.restored
walkaround.started
walkaround.techLogRaised
wind.check
wind.limit-advisory
wind.runway-condition
```

## 5. Options property usage (src/ProsimCompanion.Core/Configuration/)

Classes/records scanned: 102 (86 option-knob classes = *Options.cs + AudioMapping/DepartureServiceStep; the rest is settings infrastructure: Fields/, SettingsJson, SecretProtector, OptionsDraft, paths, importer); public properties: 940 (865 on knob classes). UNREAD (0 word hits in src outside the Configuration folder): 16 (3 knobs). WEB-ONLY (hits only in ProsimCompanion.Web): 37 (16 knobs). Hits are by bare property name, so a name shared by several options classes (column 'same-name classes') or by any unrelated identifier counts as read — under-reporting is the dominant error; over-reporting only where a property is read by reflection/JSON binding alone (the attribute-driven settings renderer lives in Web and reads knob *metadata* by reflection, so a knob can be on a page without a Web hit).

### 5a. Unread properties (0 hits outside Configuration/) — knobs first, then infrastructure

| Knob? | Class | Property | Location | tests | same-name classes |
|---|---|---|---|---|---|
| KNOB | `AudioOptions` | `DefaultAppMappings` | src/ProsimCompanion.Core/Configuration/AudioOptions.cs:38 | 3 | 1 |
| KNOB | `SopOptions` | `DefaultAltitudeCallouts` | src/ProsimCompanion.Core/Configuration/SopOptions.cs:275 | 2 | 1 |
| KNOB | `SpeechOptions` | `ListeningTone` | src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:479 | 1 | 1 |
| infra | `FieldCondition` | `Ups` | src/ProsimCompanion.Core/Configuration/Fields/FieldDependency.cs:17 | 1 | 1 |
| infra | `FieldCondition` | `Negated` | src/ProsimCompanion.Core/Configuration/Fields/FieldDependency.cs:17 | 1 | 1 |
| infra | `FieldCondition` | `EqualTo` | src/ProsimCompanion.Core/Configuration/Fields/FieldDependency.cs:17 | 1 | 1 |
| infra | `OptionFieldAttribute` | `DependsOn` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:83 | 2 | 1 |
| infra | `OptionFieldAttribute` | `RangeEnd` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:107 | 0 | 1 |
| infra | `OptionFieldBinding` | `RootType` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldBinding.cs:30 | 0 | 1 |
| infra | `OptionFieldDescription` | `Dependencies` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldDescription.cs:35 | 1 | 1 |
| infra | `OptionFieldDescription` | `IsEnum` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldDescription.cs:44 | 1 | 1 |
| infra | `OptionSectionDescriptor` | `OptionsType` | src/ProsimCompanion.Core/Configuration/OptionSectionRegistry.cs:5 | 1 | 1 |
| infra | `OptionSectionDescriptor` | `CreateDefaults` | src/ProsimCompanion.Core/Configuration/OptionSectionRegistry.cs:5 | 2 | 1 |
| infra | `PredecessorConfigImporter` | `DefaultProsim2GsxConfigPath` | src/ProsimCompanion.Core/Configuration/PredecessorConfigImporter.cs:20 | 0 | 1 |
| infra | `PredecessorConfigImporter` | `DefaultProsim2FoSettingsPaths` | src/ProsimCompanion.Core/Configuration/PredecessorConfigImporter.cs:27 | 0 | 1 |
| infra | `SettingsJson` | `DraftOptions` | src/ProsimCompanion.Core/Configuration/SettingsJson.cs:25 | 0 | 1 |

### 5b. Web-only properties (only ProsimCompanion.Web touches the name; no feature project reads it) — knobs first

| Knob? | Class | Property | Location | web hits | tests | same-name classes |
|---|---|---|---|---|---|---|
| KNOB | `FlightStatusOptions` | `TileOrder` | src/ProsimCompanion.Core/Configuration/FlightStatusOptions.cs:39 | 7 | 2 | 1 |
| KNOB | `GsxDeiceOptions` | `QuestionTimeoutMinutes` | src/ProsimCompanion.Core/Configuration/GsxDeiceOptions.cs:48 | 1 | 2 | 1 |
| KNOB | `GsxOptions` | `DefaultDepartureServices` | src/ProsimCompanion.Core/Configuration/GsxOptions.cs:119 | 1 | 3 | 1 |
| KNOB | `MixerOptions` | `PanelStrips` | src/ProsimCompanion.Core/Configuration/MixerOptions.cs:55 | 13 | 12 | 2 |
| KNOB | `MixerOptions` | `PanelBuses` | src/ProsimCompanion.Core/Configuration/MixerOptions.cs:58 | 13 | 10 | 2 |
| KNOB | `MixerOptions` | `HasLegacyAgent` | src/ProsimCompanion.Core/Configuration/MixerOptions.cs:74 | 1 | 4 | 1 |
| KNOB | `MixerAgentOptions` | `PanelStrips` | src/ProsimCompanion.Core/Configuration/MixerOptions.cs:158 | 13 | 12 | 2 |
| KNOB | `MixerAgentOptions` | `PanelBuses` | src/ProsimCompanion.Core/Configuration/MixerOptions.cs:161 | 13 | 10 | 2 |
| KNOB | `SkalarkiOptions` | `DeviceLabels` | src/ProsimCompanion.Core/Configuration/SkalarkiOptions.cs:64 | 3 | 5 | 1 |
| KNOB | `SkalarkiOptions` | `ReconnectBackoffMs` | src/ProsimCompanion.Core/Configuration/SkalarkiOptions.cs:72 | 1 | 3 | 1 |
| KNOB | `SopOptions` | `DefaultFlapPlacards` | src/ProsimCompanion.Core/Configuration/SopOptions.cs:306 | 1 | 3 | 1 |
| KNOB | `SopOptions` | `DefaultApproachGates` | src/ProsimCompanion.Core/Configuration/SopOptions.cs:357 | 1 | 2 | 1 |
| KNOB | `WebUiOptions` | `SolariAnimation` | src/ProsimCompanion.Core/Configuration/WebUiOptions.cs:28 | 3 | 0 | 1 |
| KNOB | `WebUiOptions` | `ShowAdvancedSettings` | src/ProsimCompanion.Core/Configuration/WebUiOptions.cs:33 | 2 | 2 | 1 |
| KNOB | `WebUiOptions` | `ShowNextServiceInHeader` | src/ProsimCompanion.Core/Configuration/WebUiOptions.cs:40 | 3 | 1 | 1 |
| KNOB | `WebUiOptions` | `Theme` | src/ProsimCompanion.Core/Configuration/WebUiOptions.cs:67 | 11 | 2 | 1 |
| infra | `OptionFieldAttribute` | `Hint` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:61 | 230 | 5 | 1 |
| infra | `OptionFieldAttribute` | `Placeholder` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:68 | 13 | 2 | 1 |
| infra | `OptionFieldAttribute` | `BlankLabel` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:72 | 8 | 2 | 1 |
| infra | `OptionFieldAttribute` | `EmptyListHint` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:76 | 8 | 2 | 1 |
| infra | `OptionFieldAttribute` | `Advanced` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:88 | 19 | 0 | 1 |
| infra | `OptionFieldAttribute` | `NoDefault` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:104 | 2 | 0 | 1 |
| infra | `OptionFieldAttribute` | `RangeStart` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:110 | 1 | 1 | 1 |
| infra | `OptionFieldAttribute` | `TextKind` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:118 | 1 | 2 | 1 |
| infra | `OptionFieldAttribute` | `Password` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:121 | 4 | 1 | 1 |
| infra | `OptionFieldAttribute` | `EmptyAsNull` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:125 | 1 | 0 | 1 |
| infra | `OptionFieldAttribute` | `BrowseFilter` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:128 | 4 | 0 | 1 |
| infra | `OptionFieldAttribute` | `BrowseFileName` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:131 | 4 | 0 | 1 |
| infra | `OptionFieldAttribute` | `BrowseStartIn` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:136 | 7 | 4 | 1 |
| infra | `OptionFieldAttribute` | `PlainIds` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldAttribute.cs:140 | 9 | 0 | 1 |
| infra | `OptionFieldBinding` | `ValueType` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldBinding.cs:36 | 4 | 1 | 2 |
| infra | `OptionFieldDescription` | `ValueType` | src/ProsimCompanion.Core/Configuration/Fields/OptionFieldDescription.cs:32 | 4 | 1 | 2 |
| infra | `OptionsDraft` | `IsDirty` | src/ProsimCompanion.Core/Configuration/OptionsDraft.cs:13 | 11 | 5 | 1 |
| infra | `OptionsDraft` | `IsDirty` | src/ProsimCompanion.Core/Configuration/OptionsDraft.cs:68 | 11 | 5 | 1 |
| infra | `SecretProtector` | `SecretPaths` | src/ProsimCompanion.Core/Configuration/SecretProtector.cs:46 | 1 | 7 | 1 |
| infra | `UserConfigPaths` | `ThemeLogos` | src/ProsimCompanion.Core/Configuration/UserConfigPaths.cs:44 | 1 | 0 | 1 |
| infra | `UserConfigPaths` | `AirlineLogos` | src/ProsimCompanion.Core/Configuration/UserConfigPaths.cs:48 | 6 | 0 | 1 |


## 6. Log-string interpolation

Strict (`Log<Level>(\$"` / `Log<Level>(ex, \$"` / `.Log(LogLevel.X, ..\$"`): 0. Loose (any `.Log*(` call with an interpolated string somewhere in its argument list, strict hits excluded): 8. Loose hits include `LogBufferStore`/`Log.Logger`-style calls and interpolations used as a template *argument*, which are not violations.

Strict by project: 

### 6a. Strict hits

### 6b. Loose hits
- src/ProsimCompanion.Audio/Mixer/MixerClient.cs:346 — `_logger.LogInformation(`
- src/ProsimCompanion.Audio/Mixer/MixerClient.cs:406 — `_logger.LogInformation(`
- src/ProsimCompanion.Core/Airports/GatewayAirportCoordinates.cs:62 — `_logger.LogInformation(`
- src/ProsimCompanion.Core/Diagnostics/Ecam/DiagnosticsSnapshotService.cs:50 — `_logger.LogInformation(`
- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs:760 — `_logger.LogInformation(`
- src/ProsimCompanion.Speech/Company/MovementMessageService.cs:239 — `_logger.LogInformation("Delay code {Code} ({Title}) on record, {Source}{Note}", code, title, source, note is null ? "" : $" — {note}");`
- src/ProsimCompanion.Speech/Crew/DisregardVoiceFeature.cs:78 — `_logger.LogInformation(`
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:816 — `_logger.LogInformation(`


## 7. Identifiers containing "key" (standing debt against the Snyk naming rule)

Declaration sites matched (locals, fields, parameters, properties, methods, types): 310; excluding false friends (keyboard/keypad/keyword/keyframe/...): 306; distinct identifiers: 167. Not matched: lambda parameters without a type, deconstruction targets, enum members, Razor `@bind` targets, string literals.

By project: ProsimCompanion.Core=142, ProsimCompanion.Speech=87, ProsimCompanion.Gsx=37, ProsimCompanion.Web=27, ProsimCompanion.Prosim=8, ProsimCompanion.Audio=3, ProsimCompanion.App=2

### 7a. Count by file (top 40)

| File | sites | identifiers |
|---|---|---|
| src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs | 72 | Cdu2Key0, Cdu2Key1, Cdu2Key2, Cdu2Key3, Cdu2Key4, Cdu2Key5, Cdu2Key6, Cdu2Key7, Cdu2Key8, Cdu2Key9, Cdu2KeyA, Cdu2KeyAirport, Cdu2KeyArrowDown, Cdu2KeyArrowLeft, Cdu2KeyArrowRight, Cdu2KeyArrowUp, Cdu2KeyAtcCom, Cdu2KeyB, Cdu2KeyC, Cdu2KeyClb, Cdu2KeyClear, Cdu2KeyClearLine, Cdu2KeyD, Cdu2KeyData, Cdu2KeyDir, Cdu2KeyDot, Cdu2KeyE, Cdu2KeyF, Cdu2KeyFpln, Cdu2KeyFuelPred, Cdu2KeyG, Cdu2KeyH, Cdu2KeyI, Cdu2KeyInit, Cdu2KeyJ, Cdu2KeyK, Cdu2KeyL, Cdu2KeyLsk1L, Cdu2KeyLsk1R, Cdu2KeyLsk2L, Cdu2KeyLsk2R, Cdu2KeyLsk3L, Cdu2KeyLsk3R, Cdu2KeyLsk4L, Cdu2KeyLsk4R, Cdu2KeyLsk5L, Cdu2KeyLsk5R, Cdu2KeyLsk6L, Cdu2KeyLsk6R, Cdu2KeyM, Cdu2KeyMenu, Cdu2KeyMinus, Cdu2KeyN, Cdu2KeyO, Cdu2KeyOvfly, Cdu2KeyP, Cdu2KeyPerf, Cdu2KeyProg, Cdu2KeyQ, Cdu2KeyR, Cdu2KeyRadNav, Cdu2KeyS, Cdu2KeySecFpln, Cdu2KeySlash, Cdu2KeySpace, Cdu2KeyT, Cdu2KeyU, Cdu2KeyV, Cdu2KeyW, Cdu2KeyX, Cdu2KeyY, Cdu2KeyZ |
| src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs | 16 | _keysDown, baselineKeys, CanonicalKeyNames, FormatKey, key, KeyDown, KeyNames, keys, legacyKey, ParseKey, RegKey, WmKeydown, WmKeyup, WmSyskeydown, WmSyskeyup |
| src/ProsimCompanion.Core/Configuration/PredecessorConfigImporter.cs | 9 | fromKey, keys, toKey |
| src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs | 8 | ApiKey, apiKey, ResolveApiKey, WithApiKey |
| src/ProsimCompanion.Core/Configuration/SpeechOptions.cs | 8 | AcpIntKeyRequired, AtcMuteKey, ElevenLabsApiKey, GoogleKeyFilePath, Key, key, keys, PttKey |
| src/ProsimCompanion.Web/Pages/AudioSettings.razor | 7 | Key, key, TargetKey |
| src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs | 6 | key |
| src/ProsimCompanion.Gsx/GsxBootstrapService.cs | 5 | _reportedUnknownKeys, key, KnownIgnoredKeys, OnUnknownKey |
| src/ProsimCompanion.Speech/SayIntentions/SayIntentionsWeatherService.cs | 5 | apiKey, key, ResolveApiKey |
| src/ProsimCompanion.Gsx/Sync/GsxJetwayStairsService.cs | 5 | _currentGateKey, _handledGateKey, gateKey, key |
| src/ProsimCompanion.Speech/Mcdu/McduActuator.cs | 5 | funcKeySuffix, InterKeyPauseMs, key, PressKeyAsync |
| src/ProsimCompanion.Core/Configuration/SecretProtector.cs | 5 | ExpandSecretKeys, Key, key, loadedKeys |
| src/ProsimCompanion.Core/Airports/RunwayLookupCache.cs | 4 | _key, key, Key |
| src/ProsimCompanion.Core/Airports/Parking/ParkingIdentity.cs | 4 | key, ParsePyGroupKey, PyGroupKey |
| src/ProsimCompanion.Speech/Immersion/RegionFactService.cs | 4 | key |
| src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs | 4 | apiKey, key |
| src/ProsimCompanion.Gsx/Mirror/GsxStateMirror.cs | 4 | _handlerGateKey, GateContextKey, key, UnknownKeySeen |
| src/ProsimCompanion.Prosim/Simbrief/SimbriefRandomizationLatch.cs | 4 | key, OfpKey, ofpKey |
| src/ProsimCompanion.Core/Configuration/SayIntentionsOptions.cs | 3 | ApiKeySource, key, ManualApiKey |
| src/ProsimCompanion.Web/Layout/SettingsSearch.cs | 3 | GroupKey, ItemKey, key |
| src/ProsimCompanion.Core/Flight/FlightStateParts.cs | 3 | Key |
| src/ProsimCompanion.Speech/Crew/AcpHailGateCore.cs | 3 | intKeyPushed, intKeyRequired, key |
| src/ProsimCompanion.Speech/Commands/HumanTiming.cs | 3 | afterPageKey, IsPageKey, key |
| src/ProsimCompanion.Prosim/Sdk/SdkConstructorSelector.cs | 3 | apiKey |
| src/ProsimCompanion.Web/Layout/SettingsNav.cs | 3 | Key |
| src/ProsimCompanion.Speech/Immersion/RegionFactCore.cs | 3 | Key, regionKey |
| src/ProsimCompanion.Speech/Mcdu/McduControls.cs | 3 | Key, KeyForChar, KeyPrefix |
| src/ProsimCompanion.Speech/Crew/AcpChannel.cs | 3 | IntKeyPushed, key |
| src/ProsimCompanion.Gsx/Gate/GsxGateResolver.cs | 2 | gateContextKey |
| src/ProsimCompanion.Speech/Immersion/RegionFactBank.cs | 2 | key, regionKey |
| src/ProsimCompanion.Speech/Persona/StyledSpeechService.cs | 2 | cacheKey |
| src/ProsimCompanion.Gsx/Profiles/PyLiteral.cs | 2 | key, KeyText |
| src/ProsimCompanion.Gsx/Sync/GsxGateAnchorService.cs | 2 | _handledGateKey |
| src/ProsimCompanion.Gsx/Sync/GsxGroundPrepCoordinator.cs | 2 | _sessionGateKey, gateKey |
| src/ProsimCompanion.Gsx/Sync/PrepStageMachine.cs | 2 | GateKey, SessionGateKey |
| src/ProsimCompanion.Speech/Recognition/ProsimPttSwitches.cs | 2 | Key, key |
| src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs | 2 | key, ParseStandKey |
| src/ProsimCompanion.Speech/Briefings/DfdNavDataProvider.cs | 2 | Key, RunwayKey |
| src/ProsimCompanion.Gsx/Sync/GsxRepositionService.cs | 2 | _handledGateKey |
| src/ProsimCompanion.Speech/SayIntentions/FlightJsonFile.cs | 2 | _lastGoodApiKey, TryReadApiKey |

### 7b. Distinct identifier names (count of sites)

`key`(85), `ApiKey`(20), `keys`(12), `gateContextKey`(5), `_handledGateKey`(5), `fromKey`(4), `toKey`(4), `gateKey`(4), `TargetKey`(3), `ResolveApiKey`(3), `regionKey`(3), `OfpKey`(3), `KeyOf`(2), `_lastKey`(2), `IntKeyPushed`(2), `funcKeySuffix`(2), `cacheKey`(2), `_keyRejected`(2), `ItemKey`(1), `intKeyRequired`(1), `IsPageKey`(1), `KeyDown`(1), `KeyForChar`(1), `KeyName`(1), `KeyNames`(1), `KeyPrefix`(1), `keystroke`(1), `InterKeyPauseMs`(1), `_currentGateKey`(1), `gsxGateContextKey`(1), `GroupKey`(1), `GoogleKeyFilePath`(1), `KeyText`(1), `FormatKey`(1), `ExpandSecretKeys`(1), `ElevenLabsApiKey`(1), `CustomKey`(1), `Cdu2KeyZ`(1), `Cdu2KeyY`(1), `Cdu2KeyX`(1), `Cdu2KeyW`(1), `Cdu2KeyV`(1), `Cdu2KeyU`(1), `HexKeyPattern`(1), `KeyValue`(1), `MonthKey`(1), `legacyKey`(1), `RadNavKey`(1), `ReasonKey`(1), `RecallKeys`(1), `RegKey`(1), `RunwayKey`(1), `SessionGateKey`(1), `PyGroupKey`(1), `TallyKey`(1), `UninstallKeyPath`(1), `UnknownKeySeen`(1), `VersionKey`(1), `WithApiKey`(1), `WmKeydown`(1), `WmKeyup`(1), `TryReadApiKey`(1), `PttKey`(1), `PressKeyAsync`(1), `PlanKeyOf`(1), `LlmApiKey`(1), `loadedKeys`(1), `ManualApiKey`(1), `Cdu2KeyT`(1), `OnClearKeyAsync`(1), `OnEditKeyDown`(1), `OnGpuKeyAsync`(1), `OnRowKey`(1), `OnSearchKey`(1), `OnUnknownKey`(1), `ParseKey`(1), `ParsePyGroupKey`(1), `ParseStandKey`(1), `PhoneticKeys`(1), `PlanKey`(1), `KnownIgnoredKeys`(1), `Cdu2KeySpace`(1), `Cdu2KeyS`(1), `Cdu2KeySecFpln`(1), `Cdu2Key7`(1), `Cdu2Key8`(1), `Cdu2Key9`(1), `Cdu2KeyA`(1), `Cdu2KeyAirport`(1), `Cdu2KeyArrowDown`(1), `Cdu2KeyArrowLeft`(1), `Cdu2KeyArrowRight`(1), `Cdu2KeyArrowUp`(1), `Cdu2KeyAtcCom`(1), `Cdu2KeyB`(1), `Cdu2KeyC`(1), `Cdu2KeyClb`(1), `Cdu2KeyClear`(1), `Cdu2KeyClearLine`(1), `Cdu2Key6`(1), `Cdu2Key5`(1), `Cdu2Key4`(1), `Cdu2Key3`(1), `_handlerGateKey`(1), `_key`(1), `_keysDown`(1), `_lastGoodApiKey`(1), `_reportedUnknownKeys`(1), `_sessionGateKey`(1), `_tickKey`(1), `Cdu2KeyD`(1), `AcpIntKeyRequired`(1), `ApiKeySource`(1), `AtcMuteKey`(1), `baselineKeys`(1), `CanonicalKeyNames`(1), `Cdu2Key0`(1), `Cdu2Key1`(1), `Cdu2Key2`(1), `afterPageKey`(1), `Cdu2KeyData`(1), `Cdu2KeyDir`(1), `Cdu2KeyDot`(1), `Cdu2KeyLsk5R`(1), `Cdu2KeyLsk6L`(1), `Cdu2KeyLsk6R`(1), `Cdu2KeyM`(1), `Cdu2KeyMenu`(1), `Cdu2KeyMinus`(1), `Cdu2KeyN`(1), `Cdu2KeyLsk5L`(1), `Cdu2KeyO`(1), `Cdu2KeyP`(1), `Cdu2KeyPerf`(1), `Cdu2KeyProg`(1), `Cdu2KeyQ`(1), `Cdu2KeyR`(1), `Cdu2KeyRadNav`(1), `WmSyskeydown`(1), `Cdu2KeyOvfly`(1), `Cdu2KeySlash`(1), `Cdu2KeyLsk4R`(1), `Cdu2KeyLsk3R`(1), `Cdu2KeyE`(1), `Cdu2KeyF`(1), `Cdu2KeyFpln`(1), `Cdu2KeyFuelPred`(1), `Cdu2KeyG`(1), `Cdu2KeyH`(1), `Cdu2KeyI`(1), `Cdu2KeyLsk4L`(1), `Cdu2KeyInit`(1), `Cdu2KeyK`(1), `Cdu2KeyL`(1), `Cdu2KeyLsk1L`(1), `Cdu2KeyLsk1R`(1), `Cdu2KeyLsk2L`(1), `Cdu2KeyLsk2R`(1), `Cdu2KeyLsk3L`(1), `Cdu2KeyJ`(1), `WmSyskeyup`(1)

### 7c. Public API surface with 'key' (types, methods, public properties) — the costly ones to rename
- src/ProsimCompanion.Audio/Acp/AcpChannelFeed.cs:103 `keys` (var/field/param/prop) — `public void Bind(IReadOnlyCollection<(AcpSide Acp, AudioChannel Channel)> keys, IAcpVolumeSink sink)`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:943 `Cdu2Key0` (var/field/param/prop) — `public const string Cdu2Key0 = "system.switches.S_CDU2_KEY_0";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:944 `Cdu2Key1` (var/field/param/prop) — `public const string Cdu2Key1 = "system.switches.S_CDU2_KEY_1";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:945 `Cdu2Key2` (var/field/param/prop) — `public const string Cdu2Key2 = "system.switches.S_CDU2_KEY_2";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:946 `Cdu2Key3` (var/field/param/prop) — `public const string Cdu2Key3 = "system.switches.S_CDU2_KEY_3";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:947 `Cdu2Key4` (var/field/param/prop) — `public const string Cdu2Key4 = "system.switches.S_CDU2_KEY_4";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:948 `Cdu2Key5` (var/field/param/prop) — `public const string Cdu2Key5 = "system.switches.S_CDU2_KEY_5";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:949 `Cdu2Key6` (var/field/param/prop) — `public const string Cdu2Key6 = "system.switches.S_CDU2_KEY_6";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:950 `Cdu2Key7` (var/field/param/prop) — `public const string Cdu2Key7 = "system.switches.S_CDU2_KEY_7";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:951 `Cdu2Key8` (var/field/param/prop) — `public const string Cdu2Key8 = "system.switches.S_CDU2_KEY_8";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:952 `Cdu2Key9` (var/field/param/prop) — `public const string Cdu2Key9 = "system.switches.S_CDU2_KEY_9";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:953 `Cdu2KeyA` (var/field/param/prop) — `public const string Cdu2KeyA = "system.switches.S_CDU2_KEY_A";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:954 `Cdu2KeyB` (var/field/param/prop) — `public const string Cdu2KeyB = "system.switches.S_CDU2_KEY_B";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:955 `Cdu2KeyC` (var/field/param/prop) — `public const string Cdu2KeyC = "system.switches.S_CDU2_KEY_C";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:956 `Cdu2KeyD` (var/field/param/prop) — `public const string Cdu2KeyD = "system.switches.S_CDU2_KEY_D";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:957 `Cdu2KeyE` (var/field/param/prop) — `public const string Cdu2KeyE = "system.switches.S_CDU2_KEY_E";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:958 `Cdu2KeyF` (var/field/param/prop) — `public const string Cdu2KeyF = "system.switches.S_CDU2_KEY_F";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:959 `Cdu2KeyG` (var/field/param/prop) — `public const string Cdu2KeyG = "system.switches.S_CDU2_KEY_G";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:960 `Cdu2KeyH` (var/field/param/prop) — `public const string Cdu2KeyH = "system.switches.S_CDU2_KEY_H";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:961 `Cdu2KeyI` (var/field/param/prop) — `public const string Cdu2KeyI = "system.switches.S_CDU2_KEY_I";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:962 `Cdu2KeyJ` (var/field/param/prop) — `public const string Cdu2KeyJ = "system.switches.S_CDU2_KEY_J";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:963 `Cdu2KeyK` (var/field/param/prop) — `public const string Cdu2KeyK = "system.switches.S_CDU2_KEY_K";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:964 `Cdu2KeyL` (var/field/param/prop) — `public const string Cdu2KeyL = "system.switches.S_CDU2_KEY_L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:965 `Cdu2KeyM` (var/field/param/prop) — `public const string Cdu2KeyM = "system.switches.S_CDU2_KEY_M";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:966 `Cdu2KeyN` (var/field/param/prop) — `public const string Cdu2KeyN = "system.switches.S_CDU2_KEY_N";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:967 `Cdu2KeyO` (var/field/param/prop) — `public const string Cdu2KeyO = "system.switches.S_CDU2_KEY_O";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:968 `Cdu2KeyP` (var/field/param/prop) — `public const string Cdu2KeyP = "system.switches.S_CDU2_KEY_P";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:969 `Cdu2KeyQ` (var/field/param/prop) — `public const string Cdu2KeyQ = "system.switches.S_CDU2_KEY_Q";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:970 `Cdu2KeyR` (var/field/param/prop) — `public const string Cdu2KeyR = "system.switches.S_CDU2_KEY_R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:971 `Cdu2KeyS` (var/field/param/prop) — `public const string Cdu2KeyS = "system.switches.S_CDU2_KEY_S";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:972 `Cdu2KeyT` (var/field/param/prop) — `public const string Cdu2KeyT = "system.switches.S_CDU2_KEY_T";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:973 `Cdu2KeyU` (var/field/param/prop) — `public const string Cdu2KeyU = "system.switches.S_CDU2_KEY_U";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:974 `Cdu2KeyV` (var/field/param/prop) — `public const string Cdu2KeyV = "system.switches.S_CDU2_KEY_V";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:975 `Cdu2KeyW` (var/field/param/prop) — `public const string Cdu2KeyW = "system.switches.S_CDU2_KEY_W";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:976 `Cdu2KeyX` (var/field/param/prop) — `public const string Cdu2KeyX = "system.switches.S_CDU2_KEY_X";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:977 `Cdu2KeyY` (var/field/param/prop) — `public const string Cdu2KeyY = "system.switches.S_CDU2_KEY_Y";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:978 `Cdu2KeyZ` (var/field/param/prop) — `public const string Cdu2KeyZ = "system.switches.S_CDU2_KEY_Z";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:979 `Cdu2KeyAirport` (var/field/param/prop) — `public const string Cdu2KeyAirport = "system.switches.S_CDU2_KEY_AIRPORT";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:980 `Cdu2KeyArrowDown` (var/field/param/prop) — `public const string Cdu2KeyArrowDown = "system.switches.S_CDU2_KEY_ARROW_DOWN";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:981 `Cdu2KeyArrowLeft` (var/field/param/prop) — `public const string Cdu2KeyArrowLeft = "system.switches.S_CDU2_KEY_ARROW_LEFT";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:982 `Cdu2KeyArrowRight` (var/field/param/prop) — `public const string Cdu2KeyArrowRight = "system.switches.S_CDU2_KEY_ARROW_RIGHT";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:983 `Cdu2KeyArrowUp` (var/field/param/prop) — `public const string Cdu2KeyArrowUp = "system.switches.S_CDU2_KEY_ARROW_UP";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:984 `Cdu2KeyAtcCom` (var/field/param/prop) — `public const string Cdu2KeyAtcCom = "system.switches.S_CDU2_KEY_ATC_COM";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:985 `Cdu2KeyClb` (var/field/param/prop) — `public const string Cdu2KeyClb = "system.switches.S_CDU2_KEY_CLB";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:986 `Cdu2KeyClear` (var/field/param/prop) — `public const string Cdu2KeyClear = "system.switches.S_CDU2_KEY_CLEAR";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:987 `Cdu2KeyClearLine` (var/field/param/prop) — `public const string Cdu2KeyClearLine = "system.switches.S_CDU2_KEY_CLEAR_LINE";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:988 `Cdu2KeyData` (var/field/param/prop) — `public const string Cdu2KeyData = "system.switches.S_CDU2_KEY_DATA";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:989 `Cdu2KeyDir` (var/field/param/prop) — `public const string Cdu2KeyDir = "system.switches.S_CDU2_KEY_DIR";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:990 `Cdu2KeyDot` (var/field/param/prop) — `public const string Cdu2KeyDot = "system.switches.S_CDU2_KEY_DOT";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:991 `Cdu2KeyFpln` (var/field/param/prop) — `public const string Cdu2KeyFpln = "system.switches.S_CDU2_KEY_FPLN";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:992 `Cdu2KeyFuelPred` (var/field/param/prop) — `public const string Cdu2KeyFuelPred = "system.switches.S_CDU2_KEY_FUEL_PRED";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:993 `Cdu2KeyInit` (var/field/param/prop) — `public const string Cdu2KeyInit = "system.switches.S_CDU2_KEY_INIT";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:994 `Cdu2KeyLsk1L` (var/field/param/prop) — `public const string Cdu2KeyLsk1L = "system.switches.S_CDU2_KEY_LSK1L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:995 `Cdu2KeyLsk1R` (var/field/param/prop) — `public const string Cdu2KeyLsk1R = "system.switches.S_CDU2_KEY_LSK1R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:996 `Cdu2KeyLsk2L` (var/field/param/prop) — `public const string Cdu2KeyLsk2L = "system.switches.S_CDU2_KEY_LSK2L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:997 `Cdu2KeyLsk2R` (var/field/param/prop) — `public const string Cdu2KeyLsk2R = "system.switches.S_CDU2_KEY_LSK2R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:998 `Cdu2KeyLsk3L` (var/field/param/prop) — `public const string Cdu2KeyLsk3L = "system.switches.S_CDU2_KEY_LSK3L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:999 `Cdu2KeyLsk3R` (var/field/param/prop) — `public const string Cdu2KeyLsk3R = "system.switches.S_CDU2_KEY_LSK3R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1000 `Cdu2KeyLsk4L` (var/field/param/prop) — `public const string Cdu2KeyLsk4L = "system.switches.S_CDU2_KEY_LSK4L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1001 `Cdu2KeyLsk4R` (var/field/param/prop) — `public const string Cdu2KeyLsk4R = "system.switches.S_CDU2_KEY_LSK4R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1002 `Cdu2KeyLsk5L` (var/field/param/prop) — `public const string Cdu2KeyLsk5L = "system.switches.S_CDU2_KEY_LSK5L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1003 `Cdu2KeyLsk5R` (var/field/param/prop) — `public const string Cdu2KeyLsk5R = "system.switches.S_CDU2_KEY_LSK5R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1004 `Cdu2KeyLsk6L` (var/field/param/prop) — `public const string Cdu2KeyLsk6L = "system.switches.S_CDU2_KEY_LSK6L";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1005 `Cdu2KeyLsk6R` (var/field/param/prop) — `public const string Cdu2KeyLsk6R = "system.switches.S_CDU2_KEY_LSK6R";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1006 `Cdu2KeyMenu` (var/field/param/prop) — `public const string Cdu2KeyMenu = "system.switches.S_CDU2_KEY_MENU";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1007 `Cdu2KeyMinus` (var/field/param/prop) — `public const string Cdu2KeyMinus = "system.switches.S_CDU2_KEY_MINUS";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1008 `Cdu2KeyOvfly` (var/field/param/prop) — `public const string Cdu2KeyOvfly = "system.switches.S_CDU2_KEY_OVFLY";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1009 `Cdu2KeyPerf` (var/field/param/prop) — `public const string Cdu2KeyPerf = "system.switches.S_CDU2_KEY_PERF";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1010 `Cdu2KeyProg` (var/field/param/prop) — `public const string Cdu2KeyProg = "system.switches.S_CDU2_KEY_PROG";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1011 `Cdu2KeyRadNav` (var/field/param/prop) — `public const string Cdu2KeyRadNav = "system.switches.S_CDU2_KEY_RAD_NAV";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1012 `Cdu2KeySecFpln` (var/field/param/prop) — `public const string Cdu2KeySecFpln = "system.switches.S_CDU2_KEY_SEC_FPLN";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1013 `Cdu2KeySlash` (var/field/param/prop) — `public const string Cdu2KeySlash = "system.switches.S_CDU2_KEY_SLASH";`
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs:1014 `Cdu2KeySpace` (var/field/param/prop) — `public const string Cdu2KeySpace = "system.switches.S_CDU2_KEY_SPACE";`
- src/ProsimCompanion.Core/Airports/Parking/ParkingIdentity.cs:62 `PyGroupKey` (var/field/param/prop) — `public string PyGroupKey => Name switch`
- src/ProsimCompanion.Core/Airports/Parking/ParkingIdentity.cs:182 `ParsePyGroupKey` (method) — `public static ParkingName? ParsePyGroupKey(string key)`
- src/ProsimCompanion.Core/Airports/Parking/ParkingIdentity.cs:182 `key` (var/field/param/prop) — `public static ParkingName? ParsePyGroupKey(string key)`
- src/ProsimCompanion.Core/Airports/Parking/PushbackSuggestionService.cs:268 `gsxGateContextKey` (var/field/param/prop) — `public static AirportParking? FindStand(AirportParkings catalogue, string? gsxGateContextKey, GeoPoint? aircraft)`
- src/ProsimCompanion.Core/Airports/RunwayLookupCache.cs:23 `Key` (method) — `public static string Key(string icao, string runway, string? planId)`
- src/ProsimCompanion.Core/Airports/RunwayLookupCache.cs:29 `key` (var/field/param/prop) — `public bool TryGet(string key, DateTimeOffset nowUtc, out RunwayGeometry? geometry)`
- src/ProsimCompanion.Core/Airports/RunwayLookupCache.cs:48 `key` (var/field/param/prop) — `public void Store(string key, RunwayGeometry? geometry, DateTimeOffset nowUtc)`
- src/ProsimCompanion.Core/Boarding/GateMonitorService.cs:234 `gateContextKey` (var/field/param/prop) — `public static string? StandLabel(string? gateContextKey)`
- src/ProsimCompanion.Core/Configuration/BriefingOptions.cs:201 `LlmApiKey` (var/field/param/prop) — `public string LlmApiKey { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/ProsimOptions.cs:25 `ApiKey` (var/field/param/prop) — `public string? ApiKey { get; set; }`
- src/ProsimCompanion.Core/Configuration/SayIntentionsOptions.cs:18 `ApiKeySource` (var/field/param/prop) — `public string ApiKeySource { get; set; } = "auto";`
- src/ProsimCompanion.Core/Configuration/SayIntentionsOptions.cs:21 `ManualApiKey` (var/field/param/prop) — `public string ManualApiKey { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SecretProtector.cs:271 `loadedKeys` (var/field/param/prop) — `public static IEnumerable<string> ExpandSecretKeys(IEnumerable<string> loadedKeys)`
- src/ProsimCompanion.Core/Configuration/SecretProtector.cs:271 `ExpandSecretKeys` (method) — `public static IEnumerable<string> ExpandSecretKeys(IEnumerable<string> loadedKeys)`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:28 `Key` (var/field/param/prop) — `public string Key { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:208 `GoogleKeyFilePath` (var/field/param/prop) — `public string GoogleKeyFilePath { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:227 `ElevenLabsApiKey` (var/field/param/prop) — `public string ElevenLabsApiKey { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:396 `PttKey` (var/field/param/prop) — `public string PttKey { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:410 `AtcMuteKey` (var/field/param/prop) — `public string AtcMuteKey { get; set; } = "";`
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs:438 `AcpIntKeyRequired` (var/field/param/prop) — `public bool AcpIntKeyRequired { get; set; }`
- src/ProsimCompanion.Core/Diagnostics/SettingsRedactor.cs:133 `HexKeyPattern` (method) — `private static partial Regex HexKeyPattern();`
- src/ProsimCompanion.Core/Display/CamerasCfgEditor.cs:478 `KeyValue` (method) — `private static partial Regex KeyValue();`
- src/ProsimCompanion.Core/Flight/FlightProgressService.cs:350 `KeyOf` (method) — ``
- src/ProsimCompanion.Core/Flight/FlightStateParts.cs:29 `Key` (var/field/param/prop) — `public string Key => "ofp";`
- src/ProsimCompanion.Core/Flight/FlightStateParts.cs:76 `Key` (var/field/param/prop) — `public string Key => "minima";`
- src/ProsimCompanion.Core/Flight/FlightStateParts.cs:127 `Key` (var/field/param/prop) — `public string Key => "departureCycle";`
- src/ProsimCompanion.Core/Flight/FlightTimesTracker.cs:304 `Key` (var/field/param/prop) — `public string Key => "flightTimes";`
- src/ProsimCompanion.Core/Flight/FuelLog.cs:98 `PlanKeyOf` (method) — `public static string PlanKeyOf(OfpData? ofp)`
- src/ProsimCompanion.Core/Flight/FuelLogService.cs:78 `Key` (var/field/param/prop) — `public string Key => "fuelLog";`
- src/ProsimCompanion.Core/Flight/LegBoundaryService.cs:105 `Key` (var/field/param/prop) — `public string Key => SnapshotPartName;`
- src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs:267 `Key` (method) — `private static string Key(NotificationTarget target) => target.Name.Trim().Length > 0 ? target.Name.Trim() : target.Kind.ToString();`
- src/ProsimCompanion.Core/Notifications/NotificationSignalSource.cs:312 `Key` (var/field/param/prop) — `public string Key => "notifications";`
- src/ProsimCompanion.Core/Speech/NarrationSection.cs:15 `Key` (var/field/param/prop) — `public sealed record NarrationSection(string Key, string Text)`
- src/ProsimCompanion.Core/State/IPttInputCapture.cs:9 `KeyName` (var/field/param/prop) — `public sealed record PttInputCaptureResult(string? KeyName, int? JoystickId, int? JoystickButton)`
- src/ProsimCompanion.Gsx/Gate/GsxGateResolver.cs:365 `gateContextKey` (var/field/param/prop) — `public static string? ResolveAnchorToken(IReadOnlyList<GsxParking> parkings, string gateContextKey)`
- src/ProsimCompanion.Gsx/Gate/GsxGateResolver.cs:376 `gateContextKey` (var/field/param/prop) — `public static IReadOnlyList<string> AnchorTokenLadder(IReadOnlyList<GsxParking> parkings, string gateContextKey)`
- src/ProsimCompanion.Gsx/GsxBootstrapService.cs:157 `OnUnknownKey` (method) — `private void OnUnknownKey(string key)`
- src/ProsimCompanion.Gsx/Mirror/GsxStateMirror.cs:21 `UnknownKeySeen` (var/field/param/prop) — `public event Action<string>? UnknownKeySeen;`
- src/ProsimCompanion.Gsx/Mirror/GsxStateMirror.cs:60 `GateContextKey` (var/field/param/prop) — `public string? GateContextKey => _parkingName ?? _handlerGateKey;`
- src/ProsimCompanion.Gsx/Mirror/GsxStateMirror.cs:66 `key` (var/field/param/prop) — `public void ApplyState(string key, JsonNode? value)`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:12 `key` (var/field/param/prop) — `public string? Get(string key) => Values.TryGetValue(key, out var value) ? value : null;`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:14 `key` (var/field/param/prop) — `public double? GetDouble(string key)`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:17 `key` (var/field/param/prop) — `public int? GetInt(string key)`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:20 `key` (var/field/param/prop) — `public bool? GetBool(string key) => GetInt(key) switch { null => null, 0 => false, _ => true };`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:24 `key` (var/field/param/prop) — `public GeoPose? GetPose(string key)`
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs:39 `key` (var/field/param/prop) — `public IReadOnlyList<string> GetList(string key)`
- src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs:147 `key` (var/field/param/prop) — `public static (int Number, string Suffix)? ParseStandKey(string key)`
- src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs:147 `ParseStandKey` (method) — `public static (int Number, string Suffix)? ParseStandKey(string key)`
- src/ProsimCompanion.Gsx/Profiles/PyLiteral.cs:281 `KeyText` (method) — ``
- src/ProsimCompanion.Gsx/Protocol/GsxFrame.cs:190 `Key` (var/field/param/prop) — `public sealed record GsxPatchFrame(string Key, JsonNode? Value) : GsxFrame;`
- src/ProsimCompanion.Prosim/Sdk/SdkConstructorSelector.cs:89 `apiKey` (var/field/param/prop) — `public static object Create(Type connectType, string? apiKey)`
- src/ProsimCompanion.Prosim/Simbrief/SimbriefRandomizationLatch.cs:10 `OfpKey` (var/field/param/prop) — `public sealed record SimbriefRandomizationLatch(string OfpKey, bool[] BookedMap, int PaxCount, double CargoKg);`
- src/ProsimCompanion.Prosim/Simbrief/SimbriefRandomizationLatch.cs:24 `OfpKey` (method) — `public static string OfpKey(string? requestId, string? flightNumber, DateTimeOffset? scheduledOutUtc)`
- src/ProsimCompanion.Prosim/Simbrief/SimbriefRandomizationLatch.cs:47 `ofpKey` (var/field/param/prop) — `public static bool ShouldReuse(SimbriefRandomizationLatch? latched, string ofpKey, bool force)`
- src/ProsimCompanion.Speech/Briefings/DfdNavDataProvider.cs:892 `RunwayKey` (method) — `private static (string? Num, string Side) RunwayKey(string? runway)`
- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs:273 `Key` (var/field/param/prop) — `public string Key => "cabin";`
- src/ProsimCompanion.Speech/Callouts/ChronoCallFeature.cs:64 `Key` (var/field/param/prop) — `public string Key => "chrono";`
- src/ProsimCompanion.Speech/Commands/HumanTiming.cs:17 `IsPageKey` (method) — `public static bool IsPageKey(string datarefOrSuffix)`
- src/ProsimCompanion.Speech/Commands/HumanTiming.cs:46 `afterPageKey` (var/field/param/prop) — `public static int Gap(HumanizeOptions options, int baseDelayMs, bool afterPageKey, Random rng)`
- src/ProsimCompanion.Speech/Crew/AcpChannel.cs:39 `IntKeyPushed` (var/field/param/prop) — `public readonly record struct AcpTransmitState(AcpTransmitTarget Target, bool IntKeyPushed);`
- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs:262 `Key` (var/field/param/prop) — `public string Key => "walkaround";`
- src/ProsimCompanion.Speech/Immersion/RegionFactBank.cs:36 `regionKey` (var/field/param/prop) — `public IReadOnlyList<string> FactsFor(string regionKey)`
- src/ProsimCompanion.Speech/Immersion/RegionFactCore.cs:92 `Key` (var/field/param/prop) — `public static (string Key, string Name)? RegionOf(PlaceFix? fix)`
- src/ProsimCompanion.Speech/Immersion/RegionFactCore.cs:158 `regionKey` (var/field/param/prop) — `public bool TryClaim(string regionKey, RegionFactsOptions options, DateTimeOffset nowUtc, Func<double> roll)`
- src/ProsimCompanion.Speech/Immersion/RegionFactCore.cs:181 `regionKey` (var/field/param/prop) — `public void Release(string regionKey)`
- src/ProsimCompanion.Speech/Mcdu/McduActuator.cs:100 `PressKeyAsync` (method) — ``
- src/ProsimCompanion.Speech/Mcdu/McduControls.cs:21 `KeyPrefix` (var/field/param/prop) — `public const string KeyPrefix = "system.switches.S_CDU2_KEY_";`
- src/ProsimCompanion.Speech/Mcdu/McduControls.cs:26 `Key` (method) — `public static string Key(string suffix)`
- src/ProsimCompanion.Speech/Mcdu/McduControls.cs:44 `KeyForChar` (method) — `public static string? KeyForChar(char c)`
- src/ProsimCompanion.Speech/Recognition/CommandMatcher.cs:171 `PhoneticKeys` (method) — `private static (string Primary, string Alternate) PhoneticKeys(string normalized)`
- src/ProsimCompanion.Speech/Recognition/ProsimPttSwitches.cs:16 `Key` (var/field/param/prop) — `public static IReadOnlyList<(string Key, string Label)> Sources { get; } =`
- src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs:296 `key` (var/field/param/prop) — `public static int ParseKey(string? key)`
- src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs:296 `ParseKey` (method) — `public static int ParseKey(string? key)`
- src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs:325 `FormatKey` (method) — `public static string FormatKey(int vk) =>`
- src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs:724 `KeyDown` (method) — ``
- src/ProsimCompanion.Speech/Recognition/UnmatchedUtteranceTracker.cs:111 `ReasonKey` (method) — `public static string ReasonKey(UnmatchedReason reason) => reason switch`
- src/ProsimCompanion.Speech/SayIntentions/FlightJsonFile.cs:27 `TryReadApiKey` (method) — `public static string? TryReadApiKey()`
- src/ProsimCompanion.Speech/SayIntentions/SapiClient.cs:14 `apiKey` (var/field/param/prop) — `public static async Task<bool> SayAsAsync(string apiKey, string message, ILogger logger, CancellationToken cancellationToken = default)`
- src/ProsimCompanion.Speech/SayIntentions/SapiClient.cs:45 `apiKey` (var/field/param/prop) — `public static async Task<bool> SetVarAsync(string apiKey, string name, string value, ILogger logger, CancellationToken cancellationToken = default)`
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsGateAssignService.cs:127 `ResolveApiKey` (method) — `private string? ResolveApiKey(SayIntentionsOptions options)`
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:981 `WithApiKey` (method) — ``
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs:991 `ResolveApiKey` (method) — ``
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsWeatherService.cs:356 `ResolveApiKey` (method) — `private string? ResolveApiKey(SayIntentionsOptions options)`
- src/ProsimCompanion.Speech/Tts/TtsUsageTracker.cs:96 `MonthKey` (method) — ``
- src/ProsimCompanion.Web/Components/Diagnostics/EcamGsxPage.razor:213 `OnGpuKeyAsync` (method) — ``
- src/ProsimCompanion.Web/Components/Diagnostics/EcamStatusPage.razor:113 `OnClearKeyAsync` (method) — ``
- src/ProsimCompanion.Web/Components/TextField.razor:76 `TallyKey` (var/field/param/prop) — `public string? TallyKey { get; set; }`
- src/ProsimCompanion.Web/Components/VoicePickerField.razor:101 `KeyOf` (method) — ``
- src/ProsimCompanion.Web/Layout/SettingsLayout.razor:210 `OnSearchKey` (method) — ``
- src/ProsimCompanion.Web/Layout/SettingsNav.cs:6 `Key` (var/field/param/prop) — `public sealed record SettingsNavItem(string Key, string Label, string Href)`
- src/ProsimCompanion.Web/Layout/SettingsNav.cs:16 `Key` (var/field/param/prop) — `public sealed record SettingsNavGroup(string Key, string Label, string Href, IReadOnlyList<SettingsNavItem> Items)`
- src/ProsimCompanion.Web/Layout/SettingsSearch.cs:34 `ItemKey` (var/field/param/prop) — `public static readonly IReadOnlyList<(string Root, string RouteBase, string? GroupKey, string? ItemKey)> Roots =`
- src/ProsimCompanion.Web/Layout/SettingsSearch.cs:34 `GroupKey` (var/field/param/prop) — `public static readonly IReadOnlyList<(string Root, string RouteBase, string? GroupKey, string? ItemKey)> Roots =`
- src/ProsimCompanion.Web/Pages/AudioSettings.razor:619 `TargetKey` (method) — `private static string TargetKey(string agent, bool isBus, int index) => $"{agent}|{(isBus ? "bus" : "strip")}:{index}";`
- src/ProsimCompanion.Web/Pages/AudioSettings.razor:620 `TargetKey` (method) — ``
- src/ProsimCompanion.Web/Pages/AudioSettings.razor:909 `TargetKey` (method) — ``
- src/ProsimCompanion.Web/Pages/EfbInit.razor:349 `OnEditKeyDown` (method) — ``
- src/ProsimCompanion.Web/Pages/Logbook.razor:304 `OnRowKey` (method) — ``

### 7d. All files with counts
- src/ProsimCompanion.App/Hosting/SimDamageEndpoints.cs: 1
- src/ProsimCompanion.App/Program.cs: 1
- src/ProsimCompanion.Audio/Acp/AcpChannelFeed.cs: 1
- src/ProsimCompanion.Audio/Backends/VoiceMeeter/VoiceMeeterLocator.cs: 2
- src/ProsimCompanion.Core/Aircraft/Ofp/OfpEventRecorder.cs: 1
- src/ProsimCompanion.Core/Aircraft/ProsimDataRefNames.cs: 72
- src/ProsimCompanion.Core/Airports/Parking/ParkingIdentity.cs: 4
- src/ProsimCompanion.Core/Airports/Parking/PushbackSuggestionService.cs: 2
- src/ProsimCompanion.Core/Airports/RunwayLookupCache.cs: 4
- src/ProsimCompanion.Core/Boarding/GateMonitorService.cs: 1
- src/ProsimCompanion.Core/Commands/CommandResult.cs: 1
- src/ProsimCompanion.Core/Configuration/BriefingOptions.cs: 1
- src/ProsimCompanion.Core/Configuration/Fields/OptionFieldNormalizer.cs: 1
- src/ProsimCompanion.Core/Configuration/Fields/TextFieldKind.cs: 1
- src/ProsimCompanion.Core/Configuration/IOptionNormalize.cs: 1
- src/ProsimCompanion.Core/Configuration/PredecessorConfigImporter.cs: 9
- src/ProsimCompanion.Core/Configuration/ProsimOptions.cs: 1
- src/ProsimCompanion.Core/Configuration/SayIntentionsOptions.cs: 3
- src/ProsimCompanion.Core/Configuration/SecretProtector.cs: 5
- src/ProsimCompanion.Core/Configuration/SettingsDefaultsWriter.cs: 1
- src/ProsimCompanion.Core/Configuration/SettingsJson.cs: 2
- src/ProsimCompanion.Core/Configuration/SettingsMigrator.cs: 1
- src/ProsimCompanion.Core/Configuration/SettingsWriter.cs: 1
- src/ProsimCompanion.Core/Configuration/SpeechOptions.cs: 8
- src/ProsimCompanion.Core/Configuration/UserConfigPaths.cs: 1
- src/ProsimCompanion.Core/Diagnostics/SettingsRedactor.cs: 2
- src/ProsimCompanion.Core/Display/CamerasCfgEditor.cs: 1
- src/ProsimCompanion.Core/Flight/FlightProgressService.cs: 2
- src/ProsimCompanion.Core/Flight/FlightStateParts.cs: 3
- src/ProsimCompanion.Core/Flight/FlightStateSnapshot.cs: 1
- src/ProsimCompanion.Core/Flight/FlightTimesTracker.cs: 1
- src/ProsimCompanion.Core/Flight/FuelLog.cs: 2
- src/ProsimCompanion.Core/Flight/FuelLogService.cs: 1
- src/ProsimCompanion.Core/Flight/LegBoundaryService.cs: 1
- src/ProsimCompanion.Core/Gate/ISayIntentionsGateAssign.cs: 1
- src/ProsimCompanion.Core/Notifications/NotificationDispatcher.cs: 1
- src/ProsimCompanion.Core/Notifications/NotificationSignalSource.cs: 1
- src/ProsimCompanion.Core/Speech/NarrationSection.cs: 1
- src/ProsimCompanion.Core/State/GsxDiagnosticsStore.cs: 1
- src/ProsimCompanion.Core/State/IElevenLabsVoiceCatalog.cs: 1
- src/ProsimCompanion.Core/State/IPttInputCapture.cs: 1
- src/ProsimCompanion.Gsx/Gate/GsxGateResolver.cs: 2
- src/ProsimCompanion.Gsx/Gate/GsxGateSelectionService.cs: 1
- src/ProsimCompanion.Gsx/GsxBootstrapService.cs: 5
- src/ProsimCompanion.Gsx/Mirror/GsxStateMirror.cs: 4
- src/ProsimCompanion.Gsx/Profiles/GsxIniProfile.cs: 6
- src/ProsimCompanion.Gsx/Profiles/GsxPyProfile.cs: 2
- src/ProsimCompanion.Gsx/Profiles/PyLiteral.cs: 2
- src/ProsimCompanion.Gsx/Protocol/GsxFrame.cs: 1
- src/ProsimCompanion.Gsx/Sync/GsxGateAnchorService.cs: 2
- src/ProsimCompanion.Gsx/Sync/GsxGroundPrepCoordinator.cs: 2
- src/ProsimCompanion.Gsx/Sync/GsxJetwayStairsService.cs: 5
- src/ProsimCompanion.Gsx/Sync/GsxRepositionService.cs: 2
- src/ProsimCompanion.Gsx/Sync/GsxSimbriefReloadService.cs: 1
- src/ProsimCompanion.Gsx/Sync/PrepStageMachine.cs: 2
- src/ProsimCompanion.Prosim/Loadsheet/EfbResetService.cs: 1
- src/ProsimCompanion.Prosim/Sdk/SdkConstructorSelector.cs: 3
- src/ProsimCompanion.Prosim/Simbrief/SimbriefRandomizationLatch.cs: 4
- src/ProsimCompanion.Speech/Briefings/DfdNavDataProvider.cs: 2
- src/ProsimCompanion.Speech/Cabin/CabinAutoAnswer.cs: 1
- src/ProsimCompanion.Speech/Cabin/CabinCrewService.cs: 1
- src/ProsimCompanion.Speech/Callouts/ChronoCallFeature.cs: 1
- src/ProsimCompanion.Speech/Commands/ConfiguredVoiceCommands.cs: 1
- src/ProsimCompanion.Speech/Commands/HumanTiming.cs: 3
- src/ProsimCompanion.Speech/Commands/VoiceCommandModels.cs: 1
- src/ProsimCompanion.Speech/Commands/VoiceCommandWriteGate.cs: 1
- src/ProsimCompanion.Speech/Crew/AcpChannel.cs: 3
- src/ProsimCompanion.Speech/Crew/AcpHailGateCore.cs: 3
- src/ProsimCompanion.Speech/Crew/WalkaroundService.cs: 1
- src/ProsimCompanion.Speech/Immersion/RegionFactBank.cs: 2
- src/ProsimCompanion.Speech/Immersion/RegionFactCore.cs: 3
- src/ProsimCompanion.Speech/Immersion/RegionFactService.cs: 4
- src/ProsimCompanion.Speech/Mcdu/McduActuator.cs: 5
- src/ProsimCompanion.Speech/Mcdu/McduControls.cs: 3
- src/ProsimCompanion.Speech/Mcdu/McduRadNavTuner.cs: 1
- src/ProsimCompanion.Speech/Monitoring/FlowMonitor.cs: 1
- src/ProsimCompanion.Speech/Persona/StyledSpeechService.cs: 2
- src/ProsimCompanion.Speech/Recognition/CommandMatcher.cs: 1
- src/ProsimCompanion.Speech/Recognition/ProsimPttSwitches.cs: 2
- src/ProsimCompanion.Speech/Recognition/PushToTalkService.cs: 16
- src/ProsimCompanion.Speech/Recognition/UnmatchedUtteranceTracker.cs: 1
- src/ProsimCompanion.Speech/SayIntentions/FlightJsonFile.cs: 2
- src/ProsimCompanion.Speech/SayIntentions/FoRadiosService.cs: 1
- src/ProsimCompanion.Speech/SayIntentions/SapiClient.cs: 2
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsFrequencyMonitor.cs: 4
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsGateAssignService.cs: 1
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsService.cs: 8
- src/ProsimCompanion.Speech/SayIntentions/SayIntentionsWeatherService.cs: 5
- src/ProsimCompanion.Speech/Tts/ElevenLabsTtsProvider.cs: 2
- src/ProsimCompanion.Speech/Tts/ElevenLabsVoiceCatalog.cs: 1
- src/ProsimCompanion.Speech/Tts/TtsRouter.cs: 1
- src/ProsimCompanion.Speech/Tts/TtsUsageTracker.cs: 1
- src/ProsimCompanion.Web/Components/Diagnostics/EcamGsxPage.razor: 1
- src/ProsimCompanion.Web/Components/Diagnostics/EcamStatusPage.razor: 1
- src/ProsimCompanion.Web/Components/LogbookPresentation.cs: 1
- src/ProsimCompanion.Web/Components/SpeechStatusPanel.razor: 1
- src/ProsimCompanion.Web/Components/TextField.razor: 2
- src/ProsimCompanion.Web/Components/VoicePickerField.razor: 2
- src/ProsimCompanion.Web/Layout/SettingsLayout.razor: 1
- src/ProsimCompanion.Web/Layout/SettingsNav.cs: 3
- src/ProsimCompanion.Web/Layout/SettingsSearch.cs: 3
- src/ProsimCompanion.Web/Pages/AudioSettings.razor: 7
- src/ProsimCompanion.Web/Pages/DisplaySettings.razor: 1
- src/ProsimCompanion.Web/Pages/EfbInit.razor: 2
- src/ProsimCompanion.Web/Pages/Logbook.razor: 1
- src/ProsimCompanion.Web/Pages/Monitor.razor: 1


## 8. TODO / FIXME / HACK / XXX

Total in src+tests: 0 (src 0, tests 0). By tag: .

| Project | count |
|---|---|

### 8a. 20 oldest (git blame author date)


### 8b. All



