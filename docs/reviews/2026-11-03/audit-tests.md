# Test project audit — tests/ProsimCompanion.Core.Tests (2026-11-03)

Branch `feature/settings-hub-usability` @ e5d86287. Read-only audit; one Release run.
Raw artefacts: `scratchpad/testrun.log`, `scratchpad/trx/audit.trx`, `scratchpad/analyze.out`, `scratchpad/untested.out`.

## A. RUN

`dotnet test tests/ProsimCompanion.Core.Tests -c Release --nologo -v q` (+ trx logger)

| Metric | Value |
|---|---|
| Total / Passed / Failed / Skipped | **6187 / 6187 / 0 / 0** |
| Test-phase duration (runner) | **32 s** |
| Wall time incl. restore + build | 1 m 33 s |
| Sum of per-test durations | 199.2 s (≈6× class-level parallelism) |
| Tests > 1 s / > 0.2 s | 31 / 174 |
| Test classes / methods / [Fact] / [Theory] | 433 classes, 3463 methods (3464 Fact + 497 Theory attrs; 6187 rows after theory expansion) |
| Skipped | none; no `Skip =` anywhere, no `[Trait]` |
| Build warnings in tests | CA1305 ×3 (culture-dependent ToString in expectations), CA1001 ×3 (test class owns IDisposable field), xUnit1031 ×1 (blocking `GetAwaiter().GetResult()` in PersonaConfidenceTests:143), xUnit2013 ×1, CA1861 ×4, CA1310 ×1 |

### Slowest 15 tests (from the TRX; no rerun needed)

| s | Test |
|---|---|
| 10.95 | ReduceTests.Report_OverTheRecordingsFolder_HasTheDocumentedShape |
| 10.10 | GsxSimbriefReloadServiceTests.Manual_PickAcknowledgedWithoutAnySignal_IsSentUnconfirmed |
| 6.81 | ReduceTests.Trim_DropsHistogramsFirst_ThenThinsPoints_AndNotesTheStaticRemainder |
| 5.90 | FlightStateSnapshotTests.BoundaryPart_IsKeptThroughColdAndDark_AndWrittenAloneWhileSuspended |
| 5.74 | FlightStateSnapshotTests.ColdAndDark_DeletesTheFile_AndHoldsWritesUntilTheNextLegStarts |
| 5.52 | FlightReplayTests.CheckedInRecordings_ReplayToTheirExpectedTimelines |
| 5.12 | FoQuestionServiceTests.AnAnswerIsSpeechOnly_EvenWhenItContainsCommandPhrases |
| 5.05 | RegionFactServiceTests.ModelSilent_FallsBackToTheCuratedFact |
| 5.03 | FoQuestionServiceTests.SmallTalk_AFlightQuestion_StillTakesTheStrictPath |
| 5.03 | ReduceTests.Report_NeverEmitsRawSamples |
| 4.80 | GroundFactsReplayTests.CheckedInRecordings_ParkAtTheGateAndReachStableParkedAfterTheHold |
| 3.85 | FlightStateSnapshotTests.ANewOfpRequestId_InvalidatesTheFile_ThenTheNewStateIsWritten |
| 3.83 | FlightStateSnapshotTests.Writes_AreDebounced_CarryTheIdentity_AndStopUntilDecided |
| 2.10 | GsxTriggerSlotTests.NoConfirmSend_HoldsTheSlotForSpacing_ThenFrees |
| 1.90 | FailureMonitorTests.Drill_SpeaksAnnounceItemsAndStatus_Critical |

Slowest classes (sum): ReduceTests 23.3 s (15 tests), FlightStateSnapshotTests 20.4 s (25), SettingsPagesRenderTests 16.2 s (83), FoQuestionServiceTests 15.4 s (83), GsxSimbriefReloadServiceTests 11.3 s (13), GsxTriggerSlotTests 10.0 s (14), GroundFactsReplayTests 6.8 s, FlightReplayTests 6.2 s, RegionFactServiceTests 5.2 s, DfdNavDataProviderTests 5.1 s.

Why they are slow (read, not guessed):
- **ReduceTests** opens the whole `Flight/Recordings` folder (22 MB of JSONL) as a bundle and runs `Reducer.Run` over it in three separate tests (lines 39, 85, 97).
- **FlightStateSnapshotTests** awaits the real `FlightStateSnapshotService.WriteDelay` (1.5 s) + 400 ms, ten times (lines 323–426) — the debounce is not clock-injected.
- **GsxSimbriefReloadServiceTests** / **GsxTriggerSlotTests** poll `DateTimeOffset.UtcNow` deadlines with `Task.Delay(25/200)` against real `MenuOpenTimeoutMs = 300` timeouts.
- **FoQuestionServiceTests / RegionFactServiceTests** use `LlmStreamTestSupport` with real `Task.Delay(FirstAudioDelay/SegmentDuration)` (700–1800 ms per segment) and a 30 s hung-stream handler cut by a budget.
- **SettingsPagesRenderTests** renders each Razor page with a fresh `HtmlRenderer` per fact (83 rows).

Flaky / wall-clock risk (none failed this run, but these are the ones that will): see D.

## B. SHAPE

### Tests per production project (primary = outermost `using ProsimCompanion.X` in the file)

| Project | Test files | Test lines | Prod lines (.cs+.razor) | Ratio | Prod files never named in any test |
|---|---|---|---|---|---|
| Core | 172 | 23,787 | 53,800 | 0.44 | 75 files / 8,722 lines (16%) |
| Speech | 133 | 26,568 | 54,590 | 0.49 | 54 / 7,929 (15%) |
| Gsx | 56 | 12,560 | 20,016 | 0.63 | 18 / 3,596 (18%) |
| Web | 25 | 5,594 | 25,221 | 0.22 | 51 / 7,133 (28%) |
| Prosim | 13 | 1,757 | 6,234 | 0.28 | 13 / 2,536 (41%) |
| Audio | 15 | 1,739 | 4,532 | 0.38 | 8 / 1,470 (32%) |
| App | 10 | 1,476 | 3,563 | 0.41 | 14 / 1,234 (35%) |
| Skalarki | 4 | 840 | 1,262 | 0.67 | 2 / 144 (11%) |
| Sim | 3 | 330 | 2,035 | 0.16 | 7 / 1,249 (61%) |
| Skalarki.Sdk | 0 | 0 | 251 | 0 | 1 / 251 (100%) |
| Reduce (tools) | 2 | 521 | 1,886 | 0.28 | — |
| Analyzers | 0 (see F) | 0 | 73 | 0 | — |
| **Total** | **433 (+6 helpers)** | **75,172** | **~173,000** | **0.43** | |

Test folder sizes: Speech 121 files/22,992 lines, Gsx 58/12,549, Flight 24/6,069, Web 27/5,159, Configuration 21/2,324, Aircraft 21/2,296, Diagnostics 13/1,909, Prosim 14/1,820 … 40 folders in all.

### Test files > 800 lines (3)
- `tests/ProsimCompanion.Core.Tests/Flight/FlightStateEngineTests.cs` — 964
- `tests/ProsimCompanion.Core.Tests/Gsx/GsxServiceControlTests.cs` — 861
- `tests/ProsimCompanion.Core.Tests/Day/CompanyDayServiceTests.cs` — 817
(`SettingsPagesRenderTests` is one partial class spread over 5 files; its 83 facts would exceed both limits if it were one file.)

### Test classes with > 60 facts
By source attributes: **none** (largest: GsxServiceControlTests 47 Fact/Theory attrs). By executed rows (theory expansion): FoQuestionServiceTests 83, SettingsPagesRenderTests 83, FoRadiosComposerTests 76, FlightStatusPresentationTests 69, RecognitionTests 68, McduVoiceTests 61, FieldValidationTests 61.

### Production areas with NO tests (folder has zero types referenced by any test file)
- `src/ProsimCompanion.Sim/Input/` — 487 lines (`VJoyInputRelay`, `VJoyInterface`, `DeviceStatus`)
- `src/ProsimCompanion.Skalarki.Sdk/` — 251 lines (`SdkSkalarkiClient`; not even referenced by the test csproj)
- `src/ProsimCompanion.Prosim/` root — 287 lines (`ProsimConnectionService`, `ProsimServiceCollectionExtensions`)
- `src/ProsimCompanion.Prosim/Acars/` — 49 lines (`AcarsUplink`)
- `src/ProsimCompanion.Core/DependencyInjection/` — 492 lines (`CoreServiceCollectionExtensions`) — no direct test, but exercised end-to-end by `App/ServiceCompositionTests` (`AddProsimCompanionServices` + `ValidateOnBuild`), so this is the one "zero" that is actually covered.
- `src/ProsimCompanion.Web/DependencyInjection/` — 67 lines (same caveat).

Folders where ≥ 60 % of the lines live in classes no test names (type-level check, not file-name):
- `Prosim/Loadsheet` 82 % (1,483/1,815): `LoadsheetService` (924) + `.Lmc` (221), `EfbInitOverridesService` (249), `FmsPerfUplinkService`, `FlightDataBootstrapService`
- `Audio/` root 88 % (640/731): `AudioControlService` (419), `ProsimNativeAudioGuard` (152)
- `Audio/Backends/CoreAudio` 78 % (712/915): `AppSessionBinding` (425), `CoreAudioBackend` (287)
- `Sim/` root 62 % (678/1,098): `SimConnectService` (514), `SimSessionSignals`, `FovSimNudge`
- `Core/Hosting` 68 % (611/893): `CoreBootstrapService` (207), `NativeFilePicker` (198), `StartupModules` (111), `PostFlightBootstrapService`, `PlaceholderSubsystemService`
- `Prosim/Gateway` 79 % (`ProsimGatewayClient` 366 — only `GraphQlMessages` is tested)
- `Prosim/Failures` 80 % (`SdkFailureInjector` 241)
- `Speech/Playback` 82 % (`SpeechPlayback` 194, `AudioDeviceCatalog`, `IntercomFilterProvider`)
- `Core/Aircraft/Gateway` 84 % (DTO records only — fine)

Largest individual untested classes (confirmed by type name, not file name): `LoadsheetService` 924, `GsxStartupResyncService` 688 (its `GsxStartupResync` core *is* tested), `FoRadiosService` 592 (its composers are tested), `LanAsrRecognizer` 541, `SimConnectService` 514, `GsxBoardingSync` 479, `CoreServiceCollectionExtensions` 436, `AppSessionBinding` 425, `AudioControlService` 419, `FlightTimesTracker` 415 (core tested), `DoubleMetaphone` 402 (pure algorithm, zero tests), `ProsimGatewayClient` 366, `VJoyInputRelay` 364, `GsxJetwayStairsService` 360, `FlightProgressService` 359, `TransitionReminderService` 272, `TurbulenceCabinService` 267, `GsxRefuelSync` 263, `GsxBootstrapService` 252, `GroundCrewUpcallService` 251, `SdkFailureInjector` 241, `ProsimConnectionService` 215, `CoreBootstrapService` 207. Web: `Home.razor` 885, `EfbInit.razor` 659, `WeightBalance.razor` 505, `OfpPage.razor` 481, `SpeechStatusPanel.razor` 387, `GsxStatusPanel.razor` 281 are never rendered by a test (29 of 90 Web components are).

Hosted services: 26 `BackgroundService`/`IHostedService` classes in src; **22 are never constructed in a test** (all bootstrap/connection services: CoreBootstrapService, GsxBootstrapService, ProsimConnectionService, SimConnectService, SimSessionService, CockpitSwitchService, FlightSampleRecorder, DiagnosticsSnapshotService, UpdateCheckService, LlmHealthProbeService, LlmHostProbe, CabinDingService, DayBootstrapService, …).

## C. REDUNDANCY

Mechanical scan (`analyze.ps1`): 132 nested fake/stub classes in the test project, 35 of them single-use; 0 test methods with byte-identical bodies; exactly one 30-line block copy-pasted into ≥ 3 files; 20 thirty-line windows shared by exactly 2 files (2 file pairs).

### Duplicated fake families (file:line → size)

1. **`IGsxRemoteApi` — `FakeGsxApi` ×9 (14–23 lines each, ~175 lines) + `Mock<IGsxRemoteApi>` ×4.**
   Gsx/ParkingChangeMenuAssignedGateTests.cs:185, Gsx/GsxGatePositionMenuAnswerTests.cs:131, Gsx/GsxSimbriefReloadServiceTests.cs:337, Gsx/GsxParkingWakeTests.cs:293, Gsx/DeiceMenuAnswerTests.cs:242, Gsx/GsxGateAirportPickPagingTests.cs:162, Gsx/GsxGateSuffixVariantServiceTests.cs:264, Gsx/GsxMenuControlTests.cs:196, Gsx/GsxMenuIntentExecutorTests.cs:511. All are the same shape: `Readiness`, `Mirror`, `Commands` list, `OnCommand` callback. → one `Gsx/TestSupport/RecordingGsxApi`.

2. **`IOptionsMonitor<T>` — `FakeOptionsMonitor(GsxOptions)` ×8 (6 lines each) + `Mixer/FakeMixerOptionsMonitor.cs` + `new Mock<IOptionsMonitor<…>>` ×62 in 40 files.**
   `src/ProsimCompanion.Core/Configuration/FixedOptionsMonitor.cs` already exists (and 16 test files already use it). Every hand-rolled copy and almost every Moq setup (`SetupGet(m => m.CurrentValue)…`) is redundant with it.

3. **ProSim data-ref fakes — three interfaces, 28 implementations.**
   `IDataRefSubscription` ×14 (10–38 lines; Aircraft/TypedSubscriptionExtensionsTests:73, Radios/Com1PanelServiceTests:133, Gsx/GsxGroundOpsSignalRelayTests:159, Gsx/GsxGateSuffixVariantServiceTests:302, Aircraft/SimLocalTimeTests:75, Aircraft/SimClockTests:97, Gsx/GsxGatePositionMenuAnswerTests:165, Gsx/GsxGateAirportPickPagingTests:200, Speech/ConfiguredVoiceCommandsTests:37, Mixer/MixerMappingServiceTests:234, Speech/FailureMonitorTests:33, Speech/RadioClearGateTests:88, App/SimDamageApiTests:28, Audio/AcpChannelFeedTests:225) + `IProsimDataRefs` ×7 (12–60 lines; Aircraft/TypedSubscriptionExtensionsTests:59, Aircraft/SimClockTests:77, Mixer/MixerMappingServiceTests:200, Diagnostics/Ecam/CollectorTestSupport.cs:8 `TableDataRefs`, Speech/ConfiguredVoiceCommandsTests:17, Speech/FailureMonitorTests:12, Audio/AcpChannelFeedTests:196) + `ISimVars` ×7 (6–31 lines; App/SimDamageApiTests:11, Gsx/GsxDoorServiceCateringDoorTests:140, Gsx/GsxGateAirportPickPagingTests:193, Gsx/GsxGatePositionMenuAnswerTests:158, Gsx/GsxGateSuffixVariantServiceTests:295, Gsx/GsxGroundOpsSignalRelayTests:151, Speech/RadioClearGateTests:68) + `Mock<IProsimDataRefs>` ×7, `Mock<ISimVars>` ×4, `Mock<IDataRefSubscription>` ×5. ≈ 580 lines doing one job: "a table of named values whose `Set(name, value)` raises `ValueChanged`". → one `TestSupport/FakeDataRefs : IProsimDataRefs, ISimVars` with `Set()`/`Writes` list.

4. **`HttpMessageHandler` ×13 (6–25 lines, ~185 lines)**: Diagnostics/Ecam/VoiceDiagnosticsCollectorTests:248 `SseHandler`, Notifications/NotificationTests:22 `RecordingHandler`, Speech/DebriefLlmStylingTests:56, Speech/FoQuestionServiceTests:26 + Speech/RegionFactTests:270 (identical `SequenceHandler`), Speech/OllamaChatClientTests:158, Speech/ElevenLabsTtsProviderTests:49, Speech/KokoroVoicesTests:131, Speech/LlmHealthTests:17/24, Speech/KokoroTtsProviderTests:30/149, Speech/LlmStreamTestSupport.cs:78. → one `ScriptedHttpHandler(params Func<HttpRequestMessage, HttpResponseMessage>[])` with `Requests` list and optional per-response delay/stream.

5. **Clocks — three time abstractions, four fakes**: `TechLog/TechLogTestSupport.cs:7 TestTimeProvider : TimeProvider` (6 lines), `Skalarki/SkalarkiControlTests.cs:121 ManualClock : TimeProvider` + `ManualTimer : ITimer` (84 lines), `Persona/FoRosterVoiceFeatureTests.cs:22 FakeClock : ISimLocalTime`, `Mock<ISimClock>` ×2 (Flight/FuelLogServiceTests:38, Web/MonitorRouteStripRenderTests:54). Production has `TimeProvider`, `ISimClock` and `ISimLocalTime` side by side. → one `ManualClock` implementing all three (keep the Skalarki `ManualTimer`, it is the good one).

6. **`ITtsProvider` ×6 (9–21 lines)**: Diagnostics/Ecam/VoiceDiagnosticsCollectorTests:264, Speech/RoleVoiceTests:84, Speech/SpeechArbiterStreamTests:23, Speech/TtsRouterVoiceRefTests:15, Speech/TtsRouterTests:13, Speech/TtsRouterStatusTests:16 (three are literally `FakeProvider(name, network/fail)`).

7. **`IVoiceFeature` ×8 (~150 lines)**: Speech/UtteranceRouterTests:57/79/164/205, Speech/VoiceFeatureRosterTests:425/450, Speech/MicOwnershipTests:225, Speech/VoiceReferenceBuilderTests:199 — all "a feature that matches one phrase and records the call", differing by one flag each.

8. **`RepoRoot()` walk-up-to-`ProsimCompanion.slnx`** copied into Aircraft/DataRefCatalogGuardTests, Configuration/CommsHandoverOptionsGuardTests, Gsx/TriggerPathGuardTests, State/StoreObserverGuardTests, Web/RazorAttributeEscapeGuardTests, Scenarios/ScenarioTests (`FindRepoRoot`), Localization/TestLocalizer, Localization/ShippedLanguagePackGuardTests, Localization/AppearancePageLanguageRenderTests (~9 copies).

9. **Poll-until helpers** (`var deadline = UtcNow + …; while (!cond && UtcNow < deadline) await Task.Delay(…)`) hand-rolled in Gsx/GsxTriggerSlotTests:263, Gsx/GsxSimbriefReloadServiceTests:227/330, Speech/UtteranceRouterTests:194, Gsx/GsxGroundEquipmentServiceTests:173, Gsx/GsxGateSuffixVariantServiceTests:101, Gsx/PushbackTickShellTests:315, Mixer/MixerMappingServiceTests:141, Speech/SpeechArbiterStreamTests:115 (8 copies).

10. Smaller pairs: `TestNavigationManager` ×2 (Web), `ScriptedQuestions : IFreeFormQuestionHandler` ×2, `IFlightDataSource` ×3, `ISkalarkiClientFactory` ×2, `IChecklistRoutingHost` ×2, `IMicOwnership` ×2, `ISpeechArbiter` ×2 (`SpeechTestFakes.FakeArbiter` 35 lines vs `LlmStreamTestSupport.DrainingArbiter` 103 lines).

11. Session-JSONL writers: `Debrief/SessionLogBuilder.cs`, `FlightReplayTests.Envelope()/Lines()`, and ReduceTests' inline session builders each serialise `{timestamp,type,payload}` envelopes independently.

### Tests asserting the same behaviour under different names
No byte-identical bodies. Overlaps found by reading:
- `Gsx/TriggerPathGuardTests` re-implements the PCGSX001 analyzer as a regex sweep over `src` (documented as belt-and-braces; fine, but it means the rule has two sources of truth and the analyzer itself has no test — see F).
- Three tests each replay **every** checked-in recording end to end: `FlightReplayTests.CheckedInRecordings_ReplayToTheirExpectedTimelines`, `GroundFactsReplayTests.CheckedInRecordings_ParkAtTheGateAndReachStableParkedAfterTheHold`, `ScenarioTests.ReplayRecordings_NeverArmAnything`; plus `ReduceTests` opens the folder three more times. Each 22 MB recording set is parsed ~6× per run.
- `SettingsPagesRenderTests` (83 rows) and `OptionFieldCompletenessTests`/`OptionSectionGuardTests` all assert "every option has a control on a page" from different angles (render vs. regex over `.razor`); the owner rule needs one of them, not three.

### Copy-pasted Arrange blocks > 30 lines in ≥ 3 files
Exactly one: the `FakeGsxApi` + `FakeOptionsMonitor` + `StubSimVars` + `StubSubscription` tail, identical in Gsx/GsxSimbriefReloadServiceTests.cs:337, Gsx/GsxGateAirportPickPagingTests.cs:162 and Gsx/GsxGateSuffixVariantServiceTests.cs:264 (≈ 55 lines each). The other six `FakeGsxApi` copies differ by a line or two, which is why the window scan did not catch them.

## D. QUALITY SMELLS

**Thread.Sleep — 9 occurrences in 5 files**
Persona/FoRosterTests.cs:176 (20 ms, mtime bump), Speech/CabinCrewServiceTests.cs:71 (10 ms loop), Speech/CrewHailServiceTests.cs:320 and :371 (100 ms "give the fire-and-forget path a moment to (not) act" — a negative assertion after a sleep), Gsx/GsxGroundEquipmentServiceTests.cs:176 (10 ms poll), Speech/RecognitionControllerTests.cs:125/263/315/328 (100 ms "several test-backoff periods").

**Task.Delay — 91 occurrences in 25 files.** Heaviest: Flight/FlightStateSnapshotTests 14 (real 1.5 s `WriteDelay`), Notifications/NotificationTests 12, Skalarki/SkalarkiControlTests 8, Gsx/GsxSimbriefReloadServiceTests 7, Skalarki/SkalarkiServiceTests 6 (`StartAsync(); await Task.Delay(100); StopAsync()`), Speech/SpeechArbiterStreamTests 4, Speech/LlmStreamTestSupport 4, Gsx/GsxTriggerSlotTests 4, Speech/MicOwnershipTests 3, Speech/EcamDialogueTests 3, Prosim/ProsimSetupCheckServiceTests 3. About 35 s of the 199 s CPU total is literal waiting.

**Wall-clock / elapsed-time assertions (flake risk on a loaded box)**
- Aircraft/SimClockTests.cs:19 — compares `SimClock.UtcNowOrReal` to real `UtcNow` (5 s tolerance; OK).
- Speech/FoQuestionServiceTests.cs:223 — `Assert.True(DateTime.UtcNow - started < 10 s)`.
- Gsx/GsxTriggerSlotTests.cs:312 — `Assert.True(UtcNow - started < 2 s, "the duplicate must not wait for the slot")`.
- Speech/StreamingNarratorTests.cs:211/259 — `Stopwatch` timing assertions.
- Persona/FoRosterTests.cs:176 and Localization/LanguagePackCatalogTests.cs:129 — depend on file mtime resolution (they push `LastWriteTimeUtc` forward, which is the right workaround).
- 8 hand-rolled deadline polls (C.9) with 2–10 s ceilings.
- Culture: four tests deliberately set `de-DE` and restore (Web/FlightStatusPresentationTests:717, Web/FlightMonitorPresentationTests:232, Logbook/LogbookLandingAnalysisTests:361, Speech/CabinCompanyTests:425) — correct. Culture-sensitive *expectations* flagged by the compiler: Day/CompanyDayServiceTests.cs:543/545 (`DateTimeOffset.ToString(string)`), Configuration/SopOptionsBindingTests.cs:34 (`int.ToString()`), Gsx/RefuelCoreTests.cs:85 (`StartsWith(string)`).

**Tests with no assertion — 4** (all "does not throw"; should say so with `Record.Exception`/`Assert.Null`):
Gsx/DeparturePumpEffectApplierTests.cs:214 `SimbriefImport_Landed_RaisesTheRePump`, Logging/SessionEventLogSinkTests.cs:148 `Emit_BeforeTheHostSetsTheTarget_DropsSilently`, Prosim/DataRefSubscriptionTableTests.cs:124 `UpdateValue_UnknownName_IsIgnored`, Speech/WavRepairTests.cs:78 `TooShortBuffer_DoesNotThrow`.

**Asserting on log text instead of behaviour** — rare and contained: only `Diagnostics/Ecam/EcamTestSupport.cs CountingLogger` is a capturing logger; `Diagnostics/Ecam/DiagnosticsSnapshotCoreTests.cs:42/74/75/150` assert warning counts and the message text "reporting again". No `Mock<ILogger>`; `NullLogger` used in 141 files. Good.

**Moq** — 155 `new Mock<…>` in 70 files; 62 of them are `IOptionsMonitor` (see C.2). `VerifyAll()` ×3 (Commands/OfpCommandHandlerTests, InputCommandHandlersTests, CockpitCommandHandlersTests — one each), `MockBehavior.Strict`/`VerifyNoOtherCalls` ×22. No overuse.

**Real file system outside a temp dir** — writes are all under temp (39 files use `Path.GetTempPath`/`TempDir`). Read-only reaches outside `bin`:
- Guard tests walk up to `ProsimCompanion.slnx` and read `src/**/*.cs|*.razor` or `src/ProsimCompanion.App/config/lang/**` (Aircraft/DataRefCatalogGuardTests, Configuration/OptionSectionGuardTests, Configuration/CommsHandoverOptionsGuardTests, Gsx/TriggerPathGuardTests, State/StoreObserverGuardTests, Web/RazorAttributeEscapeGuardTests, Web/OptionFieldCompletenessTests, Localization/ShippedLanguagePackGuardTests, Localization/AppearancePageLanguageRenderTests, Scenarios/ScenarioTests). They fail when the test dll runs from anywhere but the source tree (CI artefact download, `dotnet test` on a published folder).
- Web/EcamPanelsRenderTests.cs:227 reaches `..\..\..\ProsimCompanion.Web\wwwroot\data\skalarki-layout.json` relative to the language-pack root.
- App/TelemetryApiTests `C:\Windows\win.ini` is a traversal *input* that must be refused — fine.
- Audio/VoiceMeeterLocatorTests and App/WebListenerPlanTests use `C:\…` strings as data through injected delegates — fine.

**BackgroundService tested by starting it** (memory: .NET 10 `BackgroundService` test race — `StartAsync` returns before `ExecuteAsync` has observed anything):
- `SkalarkiConnectionService` — Skalarki/SkalarkiServiceTests.cs:92/111/132/159 and Skalarki/SkalarkiControlTests.cs:23: `StartAsync → Task.Delay(100) → StopAsync → assert`.
- `MixerClientHost` — Mixer/MixerClientHostTests `HostFixture.StartAsync` (7 tests) and Mixer/MixerMappingServiceTests:176/179 (`MixerMappingService` too), with a real `FakeBridgeServer` TCP listener.
- `OfpEventRecorder` — Ofp/OfpEventRecorderTests.cs:35.
13 tests in all. Everything else hosted is not tested at all (B).

**Other**: `GsxGateAirportPickPagingTests.Dispose()` and PersonaConfidenceTests:143 block on `DisposeAsync().GetAwaiter().GetResult()` (xUnit1031); three test classes own disposables without being `IDisposable` (CA1001: GsxServiceControlTests `_fuelConfirmation`, PushbackDirectionVoiceTests `_store`, …); no `xunit.runner.json`, so the default class-level parallelism is what hides the 199 s.

## E. REPLAY / PROBE TESTS

Recordings live in `tests/ProsimCompanion.Core.Tests/Flight/Recordings/` (copied to output by the csproj `None Include="Flight\Recordings\**"`), **8 recordings, 22 MB**, each with a `.expected.txt` sidecar:

| Recording | .jsonl | expected |
|---|---|---|
| efhk-egcc-2026-10-10-0.7.0-rc1 | 4.52 MB | 952 B |
| egcc-efhk-2026-10-11-0.7.0-rc4 | 4.17 MB | 1,575 B |
| egll-lirf-2026-09-13-beta28 | 3.66 MB | 657 B |
| essa-egll-2026-08-29-beta23 | 3.48 MB | 605 B |
| lkpr-egll-2026-10-31-0.8.0-rc24 | 2.89 MB | 1,608 B |
| egll-eham-2026-11-01-1.0.0-rc1 | 2.80 MB | 304 B |
| egll-departure-2026-09-19-0.4.0-beta2 | 0.84 MB | 460 B |
| synthetic-full-flight | 5 KB | 382 B |

Consumers: `Flight/FlightReplayTests.CheckedInRecordings_ReplayToTheirExpectedTimelines` (`FlightReplay.RunFile` over every `*.jsonl`, compares the commit edges to the sidecar; 5.5 s), `Flight/GroundFactsReplayTests` (every recording; 4.8 s), `Scenarios/ScenarioTests.ReplayRecordings_NeverArmAnything` (every recording, via `FindRepoRoot()` not `AppContext.BaseDirectory`), `Reduce/ReduceTests` (whole folder as a bundle, 3 tests, 23 s), plus by name: `egcc-efhk` (FlightReplayTests:266), `egll-eham` (FlightReplayTurnaroundTests:56), `synthetic-full-flight` (FlightReplayTurnaroundTests:66, ReduceTests:114). **No recording is unreferenced** — all eight are swept by the four directory-enumerating tests, five of them only by those sweeps. `FlightReplay` itself is production code (`src/ProsimCompanion.Core/Flight/FlightReplay.cs`), per docs/agents/flight-verification.md §"Replaying a flight".

Probe catalog: `docs/agents/verification-probes.json` (481 KB, 211 probes) has **no test** — nothing validates its schema, that every `issue` field is an int, or that regexes compile. Other fixture: `Prosim/Fixtures/simbrief-ofp-sample.json` (3 KB).

## F. ANALYZERS

`src/ProsimCompanion.Analyzers/` contains one analyzer, `TriggerSlotAnalyzer` (73 lines): **PCGSX001**, severity Error — any invocation named `SendCommandAsync` whose first argument is the string literal `"service.trigger"` is reported unless the enclosing type is named `GsxTriggerSlot` (the trigger-slot invariant: GSX drops rapid-fire triggers, so exactly one serialized sender). It is referenced only by `src/ProsimCompanion.Gsx/ProsimCompanion.Gsx.csproj` (App, which also references Gsx, is not covered by it).

**No analyzer tests exist** (no `Microsoft.CodeAnalysis.*.Testing` package, nothing under tests references the analyzer). `Gsx/TriggerPathGuardTests` is the stand-in: a regex sweep of all `src/**/*.cs` for the literal, with an allow-list of three files. Gaps a Roslyn test would expose: a `const string` / interpolated / concatenated first argument is not seen by the analyzer (literal-only check), a type *nested in or named like* `GsxTriggerSlot` is exempt by name alone, and `SendCommandAsync` on an unrelated type trips it.

## G. RECOMMENDATIONS

### Top 12 by payoff

| # | Change | Effort | Lines removable / effect |
|---|---|---|---|
| 1 | Use the existing `FixedOptionsMonitor<T>` (src/Core/Configuration) everywhere: delete 9 hand-rolled `FakeOptionsMonitor`s and the ~62 `Mock<IOptionsMonitor<…>>` setup blocks | S | ~250 |
| 2 | One `TestSupport/FakeDataRefs` implementing `IProsimDataRefs` + `ISimVars` with `Set(name, value)` raising `ValueChanged` and a `Writes` list; replaces 14 `IDataRefSubscription`, 7 `IProsimDataRefs`, 7 `ISimVars` fakes and 16 Moq mocks | M | ~550 |
| 3 | One `Gsx/TestSupport/RecordingGsxApi` (Readiness, Mirror, Commands, OnCommand) replacing 9 `FakeGsxApi` + 4 mocks, and delete the triplicated 55-line fake tail | S | ~180 |
| 4 | Inject a clock into `FlightStateSnapshotService.WriteDelay`, `SkalarkiConnectionService` backoff, and `GsxTriggerSlot` spacing; one `ManualClock : TimeProvider, ISimClock, ISimLocalTime` (promote the Skalarki `ManualClock`/`ManualTimer`) replacing 4 clock fakes | M | ~60 lines; −35 s test CPU (FlightStateSnapshotTests 20 s → <1 s, GsxTriggerSlot/SimbriefReload 21 s → ~2 s); removes the main flake source |
| 5 | One `ScriptedHttpHandler` (sequence of responders, request log, optional delay/stream) replacing 13 `HttpMessageHandler` fakes | S | ~130 |
| 6 | Shared `Eventually(Func<bool>, TimeSpan)` + `EventuallyAsync` replacing 8 hand-rolled deadline polls and the 9 `Thread.Sleep`s; turn the 3 negative "sleep then assert nothing happened" tests into explicit completion signals | S | ~90; removes timing flakes |
| 7 | Replay fixture: a collection fixture that parses each recording once and hands `IReadOnlyList<FlightSample>` to FlightReplayTests, GroundFactsReplayTests, ScenarioTests; make ReduceTests run on `synthetic-full-flight` + one real recording instead of the 22 MB folder three times | M | −18 s wall (ReduceTests 23 s → ~5 s), −5 s parsing |
| 8 | Hosted-service tests without `StartAsync` + `Task.Delay(100)`: extract `ProcessTick`/`Run(once)` cores from `SkalarkiConnectionService`, `MixerClientHost`, `OfpEventRecorder` and test those (13 racy tests) | M | ~40; removes the .NET 10 BackgroundService race from the suite |
| 9 | One `TestPaths.RepoRoot()` / `TestPaths.Recordings` helper replacing 9 copies; make the guard tests skip with a clear message when `ProsimCompanion.slnx` is not above the dll | S | ~80 |
| 10 | Consolidate `ITtsProvider` ×6 and `IVoiceFeature` ×8 into `FakeTtsProvider(name, network, fail)` and `ScriptedFeature(phrase, enabled, valueParse, greedy, acknowledgeOnly)` in `Speech/SpeechTestFakes.cs` (already the home of `FakeArbiter`) | S | ~200 |
| 11 | Roslyn tests for PCGSX001 (`Microsoft.CodeAnalysis.CSharp.Analyzer.Testing.XUnit`): positive, negative, `const`/interpolated bypass, nested-type exemption; then tighten the analyzer to constant-value analysis and reference it from App too | S | adds ~80; closes the literal-only hole |
| 12 | Guard test for `docs/agents/verification-probes.json` (schema, unique ids, regexes compile, `issue` ints) and a `SettingsPagesRenderTests` shared render cache per page (83 facts render 83 pages) | S / M | adds ~60; −10 s |

Total removable from 1–3, 5–6, 9–10: **≈ 1,500 lines** of fakes/helpers (2 % of the project, but 70 % of the hand-rolled fake surface), plus ≈ 60 s of the 199 s CPU and most of the timing flake exposure from 4, 6, 7, 8.

### Top 8 untested high-risk production areas deserving a pure-core extraction + tests

1. **`Prosim/Loadsheet/LoadsheetService` (924) + `LoadsheetService.Lmc` (221) + `EfbInitOverridesService` (249)** — writes fuel/pax/cargo/LMC to ProSim; only the formatter/envelope helpers are tested. Extract `LoadsheetPlan.Decide(ofp, aircraftState, options) → writes`.
2. **`Gsx/Sync/GsxBoardingSync` (479), `GsxRefuelSync` (263), `GsxJetwayStairsService` (360)** — the GSX→ProSim pax/fuel/jetway sync shells have zero tests (only `RefuelCore` maths is). Extract tick cores with (gsxState, prosimState) → (writes, diagnostics).
3. **`Gsx/Sync/GsxStartupResyncService` (688) + `GsxBootstrapService` (252)** — the resync *core* is tested but the service that orders evidence seeding and LVAR fallbacks (issue #30/#220 history) is not; a replayable `ResyncPlan` over a restart recording would close it.
4. **`Speech/SayIntentions/FoRadiosService` (592)** — composers are tested (76 rows); the service that owns hand-off order, PTT and the radio-clear gate (flight-test 2026-10-31 findings) is not. Extract `RadioTurnPlanner`.
5. **`Core/Flight/FlightProgressService` (359) + `FlightTimesTracker` (415 shell)** — debrief fuel/time stamps (#155) ride on these; the tracker core is tested, the progress service is not.
6. **`Audio/AudioControlService` (419) + `Backends/CoreAudio/AppSessionBinding` (425) + `ProsimNativeAudioGuard` (152)** — volume/mute mapping to Windows sessions; extract `VolumePlan` (ACP knob values → session volume/mute deltas) and test it without CoreAudio.
7. **Timer-driven Speech services: `TransitionReminderService` (272), `TurbulenceCabinService` (267), `GroundCrewUpcallService` (251), `CabinDingService`** — immersion behaviour verified only by flights; each needs a `ProcessTick(state, now)` core.
8. **Pure algorithms with no tests at all: `Speech/Recognition/DoubleMetaphone` (402)** (phonetic matcher behind the exact-phrase router — cheapest win in the list) and the frame/protocol parsers inside **`LanAsrRecognizer` (541)**, **`Sim/SimConnectService` (514)** and **`Sim/Input/VJoyInputRelay` (364)** (winmm/vJoy crash history, memory 2026-10-11) — extract the parse/plan parts from the P/Invoke shells.

Runner-up: `Prosim/Gateway/ProsimGatewayClient` (366), `Prosim/Failures/SdkFailureInjector` (241), `Core/Hosting/CoreBootstrapService` (207) + `StartupModules` (111) — the degraded-mode promise in CLAUDE.md ("a failed subsystem never blocks startup") has no test beyond `ServiceCompositionTests`.
