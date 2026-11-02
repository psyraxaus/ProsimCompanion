# Audit: src/ProsimCompanion.Speech (read-only, 2026-11-03)

Scope: 256 .cs files (obj/ excluded), 54,705 lines, branch `feature/settings-hub-usability` at e5d86287.
Method: file reads of the pipeline spine (DI, router, controller, arbiter, TTS router, playback,
recognizer heads), perl/ripgrep sweeps for mechanics (ctor arity, timers, catch blocks, ThrowIfNull,
"key" identifiers), three delegated sweeps (dead code, hard-coded English, redundancy) whose
file:line claims were spot-checked. Nothing was edited or built.

Paths below are relative to `C:\Users\johncarlo\git\ProsimCompanion\src\ProsimCompanion.Speech\`
unless prefixed `Core\` (= `src\ProsimCompanion.Core\`).

---

## A. Pathway map

### A.1 The voice turn (pilot speaks → FO answers)

| # | Stage | Concrete class | Hand-off |
|---|---|---|---|
| 1 | Push-to-talk / ATC-mute input | `Recognition\PushToTalkService` (WH_KEYBOARD_LL hook thread + winmm joystick poll thread; ProSim PTT switch via `ProsimPttSwitches`) | `event Action<bool> OwnPttChanged / AtcPttChanged` |
| 2 | Listen decision | `Recognition\RecognitionController.Evaluate()` — `shouldListen = windowOpen && !paused && !atcPtt && (continuous \|\| ownPtt)`; engine chain LAN → offline with readiness probe / retry ladder / 30 s re-probe | `IVoiceRecognizer.StartListening/StopListening`; surfaces `IRecognitionWindow` (+ `IVoiceListeningControl` for the ear-off latch) |
| 3 | Audio capture | `Recognition\LanAsrRecognizer` (NAudio `WaveInEvent`, 16 kHz mono) — or `Recognition\SystemSpeechRecognizer` (closed grammar, offline) | PCM buffers → `OnData` |
| 4 | Mic gate (while FO speaks) | `Recognition\SelfEchoGuard.IsSuppressed` inside `LanAsrRecognizer.OnData` (:322) — frames dropped while the arbiter plays + `MicTailAfterSpeakingMs` | silence to the segmenter |
| 5 | VAD / segmentation | `Recognition\Vad\UtteranceSegmenter` over `ISpeechFrameClassifier` (`SileroVadClassifier` default, `RmsClassifier` fallback) | `UtteranceReady(pcm, stats)` → `asr.utterance` JSONL event |
| 6 | ASR | `LanAsrRecognizer.TranscribeAsync` → `AsrServerApi` (fasterWhisper / whisperCpp flavours; grammar phrases as prompt/hotwords) | `AsrTranscript` |
| 7 | Self-echo text guard | `SelfEchoGuard.LooksLikeOwnSpeech` (token overlap ≥ 0.6 against arbiter's recent lines, minus `IExpectedReplyProvider` exemptions from `ExpectedReplyStore`) | `Rejected` or `Accepted(RecognizedEventArgs)` |
| 8 | Controller fan-out | `RecognitionController.OnAccepted/OnRejected` (latency + `VoiceTurnTracker` stamps) | `IRecognitionWindow.Accepted/Rejected` |
| 9 | Borrowed-mic check | `Recognition\MicOwnership.IsBorrowed` — if a dialogue owns the mic the router ignores the event; `MicOwnership.ListenAsync` resolves it instead | — |
| 10 | Router entry | `Recognition\UtteranceRouter.OnRecognized → RouteUtterance` (host = `SpokenChecklistEngine` via `IChecklistRoutingHost`) | — |
| 11 | Tier 0: raw value parsers | `VoiceFeatureRoster.EnabledValueParsers()` get the RAW text first unless `IsExactKnownPhrase` (FCU `FcuExecutor`, `RadioExecutor`, `StandaloneReadbacks`, `FoSideDutiesService`, `McduArrivalChanger`…) | `IVoiceFeature.TryHandle(raw)` |
| 12 | Tier 1: checklist control-word at edge | `ChecklistControlWords.AtEdge` | `RouteText` |
| 13 | Tier 2: interpreter | `Recognition\UtteranceInterpreter.Interpret` — acoustic gates (`AcousticConfidence`/`NoSpeechProb`), hallucination list, exact normalized match (`CommandMatcher.Normalize`), phonetic snap (`CommandMatcher.Snap` / `DoubleMetaphone`) with gray band 0.70–0.85 | `InterpretResult(Kind, Text, Score)` |
| 14 | Courtesy absorb | `CourtesyReplyCore.Decide` (reply window after FO activity) | spoken `courtesy` or absorbed |
| 15 | Gray band | `UtteranceRouter.ConfirmAndRouteAsync` — borrows mic, "did you mean …?", listens 8 s on `ConfirmVocabulary`; sterile shortcut for acknowledge-only features (`IVoiceFeature.AcknowledgeOnly`) | `RouteText(candidate)` |
| 16 | Tier 3: routed text | `UtteranceRouter.RouteText`: accepted checklist answer → global `VoiceCommands` switch → `VoiceFeatureRoster.Candidates(text)` (exact-phrase owners first, then `VoiceFeatureDeclaration.Precedence`) → pending item answer → `FailureMonitor.TryRunDrillByPhrase` → checklist start phrase | `IVoiceFeature.TryHandle(text)` / `IChecklistRoutingHost.Complete` |
| 17 | Tier 4: free-form question (LLM) | `UtteranceRouter.HandleIdleMiss` → `IFreeFormQuestionHandler` = `Questions\FoQuestionService.TryAsk` (only when `IRecognitionWindow.FreeFormCapable`) → `FoQuestionCore` → `Llm\StreamingNarrator` / `OpenAiChatClient` | arbiter stream |
| 18 | Idle miss | `IdleMissPolicy.Decide` (+ `PersonaService.Acknowledge`, `PhraseBank.NextDidNotCatch`), sterile-phase absorb, `UnmatchedUtteranceTracker.Record` | `SpeechRequest(tag "reject"/"advisory")` |
| 19 | Arbiter | `Arbiter\SpeechArbiterService.EnqueueAsync` → `SpeechArbiterCore` (4-band priority queue, TTL, `IsStillValid`, Critical pre-emption) with rule chain `SterileCockpitRule`, `QuietCockpitRule`, `DialogueHoldRule` (`ISpeechSuppressionRule`) | `ArbiterItem` → `RenderOneAsync` |
| 20 | Voice resolution | `RoleVoiceResolver.Resolve(role, VoicesOptions, firstOfficerVoice)` (roster FO voice from `IFoRosterService`), `Crew\AccentVoiceResolver` for `GroundCrew` | `voiceOverride`, `IntercomOverride` |
| 21 | TTS | `Tts\TtsRouter.SynthesizeAsync` walks `ITtsProvider` chain in DI order (Kokoro → ElevenLabs → Google → WinRT → SAPI5), 60 s cooldown per failing provider, `TtsDiskCache` (bypassed for LLM sentences), `TtsUsageTracker` | `TtsAudio(WavBytes)` |
| 22 | Playback | `Playback\SpeechPlayback.PlayAsync` (WASAPI per clip, `IntercomFilterProvider` band-pass, `ChimeSynth` cue chimes) | `SpeechArbiterEvent` (`Observed`) → `SelfEchoGuard.Update` (step 4) and `UtteranceRouter.OnArbiterEvent` (courtesy clock) |

The streamed path (briefings, debrief, questions) is the same from 19 on: `StreamingNarrator`
→ `SentenceChunker` → `NumberVerifier`/`NarrationCore` → one `StreamedUtterance` queue item;
the arbiter speaks High interjections between sentences and lets a Critical cut the stream.

### A.2 Side entrances into the arbiter (79 files call `EnqueueAsync`/`SpeakAsync`)

| Entrance | Trigger source | Concrete classes |
|---|---|---|
| Timed / sampled callouts | `IFlightPhaseSource` samples + `Timer` | `Callouts\CalloutsEngine` (V1/rotate/positive climb/altitude/minimums/rollout/placards), `TransitionReminderService`, `EngineStartMonitor`, `Monitoring\StabilizedApproachMonitor`, `FlowMonitor`, `FuelCheckMonitor`, `GrossErrorCheckMonitor`, `StepClimbAdvisor`, `DestinationWeatherWatch`, `WindLimitAdvisory` |
| Flight-phase edges | `IFlightPhaseSource.PhaseChanged` (17 subscribers) | `Briefings\MissedApproachRebrief`, `Cabin\*`, `Company\MovementMessageService`, `Day\CompanyDayService`, `Persona\FoRosterVoiceFeature`, `Scenarios\ScenarioService`, `Debrief\DebriefService`, `TechLog\*` |
| GSX / ground-ops events | `Core.State.GroundOpsSignals` (16 files; `FlightCycleReset` ×14, `ArrivalCompleted` ×3, `TodApproaching`, `RefuelSkippedForTankering`), Core captain's-question stores, `DeiceRequestStore`, GSX diagnostics mirror | `Gsx\CaptainsQuestionDialogue` (one dialogue over all `ICaptainsQuestionStore`s, 15 `*Phrases` tables), `Gsx\DeicePacksProcedure`, `Gsx\GsxVoiceService`, `Crew\GroundCrewUpcallService`, `Crew\WalkaroundService`, `Crew\ParkingConflictAdvisoryService`, `Crew\DroppedCallAdvisoryService`, `Crew\AircraftStateAdvisoryService` |
| Failures / abnormals | datarefs + EWD text at 2 Hz | `Abnormals\FailureMonitor` (`AbnormalBurstGuard`, `EcamDialogueCore` with borrowed mic, drills), `RejectedTakeoffService`, `EfatoService`, `Scenarios\ScenarioService` (injects, then listens for its own detection) |
| SayIntentions | flight.json 1 Hz poll, SAPI history poll, SI LVARs | `SayIntentions\SayIntentionsService` (requests, comms hand-over, legacy departure gate), `SayIntentionsFrequencyMonitor` (wrong-frequency advisory), `FoRadiosService` (read-backs, hand-off tune, check-in via `FoRadiosComposer`/`CommsHandoverCore`), `SayIntentionsPttRelay`, `SayIntentionsArrivalGateSource` |
| Cabin / company | ACP latches, loadsheet store, turbulence store | `Cabin\CabinCrewService` (+ `CabinCruiseQueryDialogue`), `PurserPaService`, `PaPromptService`, `TurbulenceCabinService`, `Company\CompanyChannelService`, `LmcAnnouncer` |
| Ambient persona | 30 s tick + arbiter `Observed` quiet clock | `Persona\CruiseSmallTalkService`, `Immersion\RegionFactService` |
| Web / API / Stream Deck | Core command registry / seams | `IAbnormalDialogueControl`, `IDayControl`, `IScenarioControl`, `IDiversionControl`, `IWeatherControl`, `ISpeechControl.SpeakTest` |
| Post-flight | `ISessionFinalizationStep` order 10/20/30/40 | `Debrief\DebriefService`, `Logbook\LogbookVoiceService`, `TechLog\TechLogDialogueService`, `Day\CompanyDayService` |

### A.3 Hold / gate inventory

| # | Gate | Lives in | Decides | Overlaps with |
|---|---|---|---|---|
| G1 | PTT / continuous + ATC-mute | `RecognitionController.Evaluate` (:319) with `PushToTalkService` | whether the engine captures at all | G2 (same predicate) |
| G2 | Voice pause ("ear off") | `RecognitionController.SetPaused` (:261), `IVoiceListeningControl` | same predicate, pilot latch, silent | G1 — one more term of the same boolean; fine |
| G3 | Mic gate while FO speaks | `SelfEchoGuard.IsSuppressed` in `LanAsrRecognizer.OnData` (:322), `speech.muteMicWhileSpeaking` + tail ms | drop frames during playback | G4 (same guard, second check), G6 (MicOwnership's `MaxSpeechFreeze` also watches NowPlaying) |
| G4 | Self-echo text guard | `SelfEchoGuard.LooksLikeOwnSpeech` in `LanAsrRecognizer.TranscribeAsync` (:446) + `ExpectedReplyStore` exemptions | drop transcripts that are the FO's own words | G3 (belt and braces, by design — #172) |
| G5 | Mic borrow | `MicOwnership.Borrow/ListenAsync`; routing stands down on `IsBorrowed` (`UtteranceRouter.OnRecognized` :201); 8 borrowers (confirmCommand, techlog, small-talk, crew-hail, cabin-cruise-query, minimaCapture, ECAM dialogue, captain's question) | exclusive recognition for a dialogue | G7 (DialogueHold reads `IsBorrowed && IsListening`) |
| G6 | Listen-deadline freeze | `MicOwnership.ListenAsync` slice loop (250 ms) extends the deadline while NowPlaying, cap `MaxSpeechFreeze` 60 s | a dialogue's listen is not eaten by the FO's own question | G3 |
| G7 | Dialogue hold | `Arbiter\DialogueHoldRule` (Defer Normal/Low non-exempt tags while a dialogue listens; 11 exempt prefixes) | other features' prompts wait | G5; the exempt-prefix list is a second, string-typed taxonomy of "what is a dialogue line" |
| G8 | Sterile cockpit (arbiter) | `Arbiter\SpeechSuppression.SterileCockpitRule.IsSterile` (MSL < ceiling, 4 airborne phases) | drop Low, defer/allow Normal | G9, G10 |
| G9 | Sterile (router idle miss) | `UtteranceRouter.HandleIdleMiss` (:514 TakeoffRoll/InitialClimb/LandingRollout + :526 Approach < 1000 ft RA hard-coded) | absorb unmatched speech silently | G10 (same phases, different threshold source), G8 |
| G10 | Sterile (router clarifier) | `UtteranceRouter.IsSterileForClarifier` (:771) — same 3 phases + `ClarifierMinAglFt` option | no "did you mean" | G9 — **two copies of one rule with two thresholds (1000 hard-coded vs option)** |
| G11 | Sterile copies in features | `FoQuestionService.IsSterile` (:606), `FlowMonitor` :104, `SmallTalkCore` :168, plus 6 hand-built `SpeechContext`s (RegionFact, FuelCheck, CruiseSmallTalk, Scenario, FoQuestion, arbiter) | each feature decides sterile itself before enqueueing | G8 — the arbiter already judges; duplicates exist because features want to *not record an event* rather than be suppressed |
| G12 | Quiet latch | `Persona\QuietState` + `QuietCockpitRule` (drops Low) — read directly by `RegionFactService`, `PersonaService` | "quiet please" for the session | G8 (same band logic) |
| G13 | Flight-live gate (#114) | `IFlightPhaseSource.IsLive` — 36 sites in ~24 files, the comment pasted 14 times | nothing fires before a live sim session | none, but every service re-implements the early-return |
| G14 | FO away (walkaround) | `Core.State.FoAvailabilityStore.IsAway` + `VoiceFeatureDeclaration.AvailableWhileFoAway`, applied in `UtteranceRouter.SkipWhileFoAway` | features skipped, one away line | — |
| G15 | ACP transmit gate (#72) | `Crew\AcpHailGateCore` + `Crew\AcpChannel.TransmitSnapshot` | hails accepted only on INT/CAB | — |
| G16 | ACP receive gate | `IAcpChannel.AwaitReceiveAsync` (latch-or-grace) — re-implemented in `CabinCrewService` :156-160/:573 and the MECH-press ring block in `GroundCrewUpcallService` :176, `WalkaroundService` :568, `CrewHailService` :293 | crew speech waits for the receive latch | three copies of one 30-line block |
| G17 | Radio-clear gate | `SayIntentions\RadioClearGate` (COM1 receiving/PTT LVARs, 20 s cap) | FO transmits into a quiet COM1 | — |
| G18 | Abnormal burst guard | `Abnormals\AbnormalBurstGuard` | hold a multi-system burst on the ground | — |
| G19 | Write gates | `Commands\VoiceCommandWriteGate`, `FoSide\FoSideDutiesGate`, `ProsimWriteGate` (Prosim pillar) | allow-listed writes | by design, layered |
| G20 | Feature `Enabled` | `IVoiceFeature.Enabled` filtered by `VoiceFeatureRoster.Enabled()`; **re-checked inside `TryHandle` in 8 features** (CrewHail :146, GsxVoice :75, PushbackDirectionVoice :219, McduArrivalChanger :164, McduRadNavTuner :117, McduReader :137, SayIntentionsService :243, TechLogDialogueService :99) | — | redundant with the roster |

Overlap verdicts: G9/G10 should be one `IsSterileMoment()` on the router; G11 should become
one `ISterileCockpit` (or a static `SterileCockpitRule.IsSterile(options, IFlightPhaseSource)`)
so no feature builds `SpeechContext` by hand; G3/G4/G6 are intentional layering (document,
do not merge); G16 should be one `IAcpChannel.RingAndAwaitAsync`.

---

## B. Redundancy (evidence, keeper)

B1. **Tick shell copy-pasted 21×** (+7 variants). Same 20 lines: `Interlocked.Exchange(ref _ticking,1)` guard,
try/`ProcessTick`/catch log/finally. Examples: `Cabin\PaPromptService.cs:121-140`, `Cabin\PurserPaService.cs:87-106`,
`Monitoring\GrossErrorCheckMonitor.cs:225-244`, `Monitoring\StepClimbAdvisor.cs:233-252`,
`Persona\CruiseSmallTalkService.cs:220-239`; also EfatoService 187-219, FailureMonitor 934-953,
RejectedTakeoffService 268-292, TurbulenceCabinService 136-155, CalloutsEngine 560-579,
EngineStartMonitor 227-245, TransitionReminderService 252-271, WalkaroundService 185-204,
FmgcCrossCheckService 202-239, FoSideDutiesService 207-235, RegionFactService 173-192,
FlowMonitor 333-352, FuelCheckMonitor 324-343, StabilizedApproachMonitor 229-248,
SayIntentionsFrequencyMonitor 156-252 (async), ScenarioService 345-364. Variants with a different
guard: CompanyChannelService 287-301 (`_busy` under lock), GroundCrewUpcallService 108-120,
MissedApproachRebrief 120-130 (**no guard**), FoRadiosService 163, CabinCrewService. Catch level
is Debug in 12 files, Error in 9, Warning in 5 with no stated reason (see F). Keeper: a
`GuardedTimer`/`PolledService` in Core.Hosting owning timer, guard, one catch level and disposal.
~350–420 lines.

B2. **Spoken-number parsing ×4**: `Fcu\SpokenNumber.cs:11-33` (keeper), `Llm\SpokenNumberText.cs:25-40,180`,
`Recognition\NumberExtractor.cs:16-39`, `Callouts\Aviation.cs:22-53` (`TryParseSpoken`, zero callers — also dead).
`Aviation.NumberGrammarWords` :17 = `NumberExtractor.GrammarWords` :35. ~120–150 lines.

B3. **Units/digits formatting**: `ToDigits(x.ToString(InvariantCulture))` hand-written at 12 sites
(SpokenValueFormatting :154/:175, AtcInstructionParser :196, CrossCheckComposer :56/:94/:98,
FoSideDutiesService :278/:306, StepClimbAdvisor :161/:201) + private `Digits(int)` wrappers
(CrossCheckComposer :158, ReadbackCore :258). Frequency-from-kHz ×3: RadioExecutor :351-353,
SayIntentionsFrequencyMonitor :137-138, SayIntentionsService :663-664. Tonnes `(kg/1000.0).ToString("0.0")`:
CompanyChannelService :271, FuelCheckCore :151, inline GroundCrewUpcallService :248, FuelDecisionService :351,
and 4 copies in Core with **two different roundings** (`Core\Notifications\NotificationSignalSource.cs:426`
AwayFromZero vs the rest). Flight level inline ×5 vs `SpokenValueFormatting.FlightLevel` :166.
NATO table re-declared in `Gsx\GateChoicePhrases.cs:20-27` (= `Core\Speech\NatoPhonetics.Word`). Compass
eight-point list ×3 (PushbackDirectionVoice :22, Core PushbackAdvisor :99/:146, Core PlaceFixText :189).
Keeper: Core `SpokenUnits.Tonnes/Kilos`, `Aviation.ToDigits(int)`, `SpokenText.FrequencyKhz`. ~80–100 lines.

B4. **Captain's-question yes/no tables ×9** structurally identical (grammar spread, `Interpret → switch
{true=>Yes,false=>No,null=>null}`, the same `ConfirmVocabulary.Negative.Any(n => text == n || text.StartsWith(n + " "))`
tail ×9 / Affirm ×14, `RaisedPayload` ×8): CabinSecureQuestionPhrases :64, LmcRevisionPhrases :72,
DeiceQuestionPhrases :83, FuelDecisionPhrases :67, WalkaroundRaise, EndOfDuty, TurnaroundStart,
FoRosterChange, SmallTalkOffer. `BriefingQuestionsPhrases.cs:24` and `ThreatsQuestionPhrases.cs:23`
re-list "no/nope/negative" by hand instead of `ConfirmVocabulary`. Keeper: `ConfirmVocabulary.Classify(text)→bool?`
+ `CaptainsQuestionPhrases.YesNo(...)` factory. ~180 lines.

B5. **Cancel-window executor ×5**: own `CancelWords`, `CancellationTokenSource? _pending`, `CancelPending()`,
`SemaphoreSlim`, `VerifyPause()`, private `Speak()` in `Fcu\FcuExecutor.cs:25,369`, `Radios\RadioExecutor.cs:75,86,138-148`,
`Mcdu\McduArrivalChanger.cs:81,547-565`, `Mcdu\McduRadNavTuner.cs:32,355-372`, `FoSide\FoSideDutiesService.cs:30,424`
(+ `Crew\DisregardVoiceFeature.cs:23`). The MCDU "unable ladder" (not connected / not armed / you're PF /
refused event) is verbatim in McduArrivalChanger :202-220, McduRadNavTuner :145-164, McduReader :150-154.
Keeper: `ConfirmVocabulary.Cancel`, a `CancelWindow` helper, `McduPreconditions.Check`, and a base for
the two MCDU changers (25–30 % shared). ~150–200 lines.

B6. **Phase predicates hand-rolled at 47 sites / ~30 files** although `Core\Flight\FlightPhase.cs:63-81`
has `IsAtGate/IsBeforeTaxiOut/IsAirborne`. Exact `IsAirborne` copies: CalloutsEngine :226-228 (`InFlight`),
RoleManager :176-178, FlowMonitor :110-111. "Descending" ×10 (CabinCrewCore :229, PurserPaCore :63,
CalloutsEngine :147, PilotAnnouncementFeature :127/:157, TransitionReminderCore :81/:108, FlowMonitor :109,
FoRadiosComposer :526, SayIntentionsFrequencyMonitor :265). Go-around edge ×3 (MissedApproachRebrief :136,
StabilizedApproachMonitor :136, CalloutsEngine :180). Ground-sterile ×3 (UtteranceRouter :514/:771,
FoQuestionService :606). Keeper: add `IsClimbing/IsDescending/IsGroundBeforeTakeoff/IsGroundSterile/IsGoAround(from,to)`
to `FlightPhaseExtensions`. ~70–90 lines, mainly drift insurance (the Departure-phase insertion already bit once).

B7. **Per-flight reset has 4 competing mechanisms**: `GroundOpsSignals.FlightCycleReset` (14 subscribers),
`IFlightPhaseSource.SessionReset` (7, five of them double-subscribed with the same handler: PurserPa,
TransitionReminder, FmgcCrossCheck, FoSideDuties, StepClimb), a hand-rolled turnaround edge ×5
(CabinCrewCore :142, CompanyChannelService :273, RegionFactCore :76, SmallTalkCore :111, GrossErrorCheckMonitor :143)
and 3 divergent "new leg" definitions (DebriefService :182, TechLogVoiceService :172/:180,
SayIntentionsArrivalGateSource :71). 31 bool "said-once" fields + 7 `HashSet` latches. Keeper: one Core
`NewLeg` signal (ADR-0019 already says a leg begins on its OFP) and an `OncePerCycle<T>`. ~60–80 lines.

B8. **Edge detectors**: 32 `_last*/_prev*/_was*` fields; nullable-bool rising edges in PaPromptCore :30,
PurserPaCore :37, WalkaroundCore :96, BaroFollowCore :57; NaN-primed crossings in CalloutsEngine :61/:312/:429,
EfatoCore :42, EngineStartMonitor :98, PurserPaCore :48. Only `SayIntentionsPttRelay.PttEdgeTracker` exists.
Keeper: tiny `Edge`/`Crossing` structs. ~40 lines, readability.

B9. **ACP ring-and-await block ×3** (G16 above) and CabinCrewService's own CAB latch subscriptions
(:84, :156-160, :573) duplicating `AcpChannel.cs:78-81,102-103`. ~50–60 lines.

B10. **Time-of-day greeting**: `Core\Aircraft\SimLocalTime.cs:135 TimeOfDayGreeting` is the keeper (used by
BriefingService :490, FoRosterVoiceFeature :276, FoRadiosService :555); `BriefingComposer.cs:122,603` and
`FoRosterVoiceFeature.cs:299` compose "Good {word}" themselves; **`CruiseSmallTalkService.cs:624` hand-rolls
`morning = TotalHours < 12`, which calls 00:00–03:59 "morning" while Core says "evening"** — a behaviour bug.

B11. **Phrase pick**: no shared helper. `SmallTalkBank.Pick` :186 (keeper), same formula inline in
`SmallTalkCore` :134 and `RegionFactService` :379; `SayIntentionsService.PickAcknowledgement` :345-354 and
`CompanyChannelService` :324 use `Random.Shared` directly (untestable); `PersonaService` :58 has its own
`new Random()` under a lock; `Func<double> Roll` seam re-declared in 4 ctors. ~15 lines.

B12. **Grammar arrays retyped inside `VoiceReferenceDescription`** instead of referencing the field — 25 sites
(RoleManager :24/:83, SmallTalkService :68/:101, FuelDecisionService :31/:112, CompanyChannelService :39/:118,
WindLimitsVoiceFeature :26/:83, FcuExecutor :25/:91, McduRadNavTuner :32/:103, GrossErrorCheckMonitor :25/:100,
FuelCheckMonitor :25/:112, CompanyDayService :56/:170, TechLogDialogueService :27/:78, ScenarioService :45/:161,
PushbackDirectionVoice :30/:204, McduReader :42/:94, MinimaQueryVoiceFeature :18/:46, FoRosterVoiceFeature :33/:106,
DisregardVoiceFeature :23/:58). A renamed phrase silently drops its drawer row — the exact drift
`VoiceFeatureDeclaration` was introduced to kill. ~60 lines.

B13. **Lazy dataref subscribe-on-read cache ×7**: FailureMonitor :923, BriefingService :727, ProcedureSource :223,
ControlSweepService :173, SpokenTokenSource :170, FcuExecutor :360, RadioExecutor :342. ~55 lines.

B14. **Speak-then-Record pairs**: 254 `_eventLog.Record(` calls, 183 with anonymous payloads, 48 within 4 lines of a
speak; 16 private `Record*` wrappers. McduRadNavTuner (12) / McduArrivalChanger (11) worst. ~60–100 lines.

B15. **Service pairs**: `PaPromptService` vs `PurserPaService` shells ~70 % identical (cores distinct) — fold into
one host or let B1 absorb it. `McduArrivalChanger` vs `McduRadNavTuner` 25–30 % — shared base. `SmallTalkService`
is actually the "quiet please" feature (stale doc "no persona pillar here yet") — rename `QuietCockpitVoiceFeature`.
`RadioExecutor.WriteVerify` :333-338 duplicates `Core\Radios\Com1PanelService.cs:123-128`. The other pairs asked
about (FuelCheck/FuelDecision, TransitionReminder/Callouts, FoRadios/RadioExecutor, AircraftStateAdvisory/Voice,
MissedApproachRebrief/VoiceFeature, LlmHealthProbe/LlmHostProbe) are deliberate splits (<15 % overlap) — leave.

---

## C. Dead code (grep-confirmed, zero references in src/, tests/, tools/, .razor)

Types: **none** dead (every DI-only type is resolved through an interface/hosted/startup registration).

Zero-reference members (~45 lines):
- `Callouts\Aviation.cs:20-53` `TryParseSpoken` (34 lines; also the 4th number parser).
- `Company\CompanyChannelService.cs:216` `RequestLoadsheet()` — doc says the web "request loadsheet" button uses it; no .cs/.razor/.js caller.
- `FoSide\FoSidePhrases.cs:16` `RangesNm`.
- `Recognition\VoiceFeatureDeclaration.cs:63` `VoiceFeatureGate.IsAlwaysOn`.
- `Persona\CruiseSmallTalkService.cs:167` `CoffeeInFlight` (set at :567, "for tests", no test).
- `Abnormals\AbnormalModels.cs:38` `AbnormalDefinition.ContextFlags` — deserialised from ~20 `config/abnormals/*.json` files, read by nothing; `docs/integrations/abnormals.md:24` claims "flags other features read".

Test-only members (~60–75 lines + ~35 test lines): `BriefingService.ParseMetarBasics` :678 (doc says superseded by
`MetarParser`), `BriefingService.ParseFmsPlan` :716, `IAcpChannel.IsReceiving` :55/:132, `TurbulenceCabinCore.SecuredDueAtUtc` :64,
`BaroFollowCore.Inhibited` :68, `RegionFactCore.SpokenRegions` :61, `SayIntentionsLvarNames.ControlPttLvars` :24 (allow-list
check — keep).

"For tests" doc comments that are stale (members are live in production; narrow visibility or fix the comment):
RejectedTakeoffService :148 `HandleStopWord`, SpokenChecklistEngine :74 `ReminderAfter` (no test sets it),
WalkaroundService :317, CompanyDayService :811, LlmHostProbe :126, NarrationCore :59, WindLimitAdvisory :102,
CruiseSmallTalkService :277, FoRosterVoiceFeature :168/:315, FoQuestionCore :102, LanAsrRecognizer :364,
UtteranceRouter :420, ScenarioService :397, `ElevenLabsVoiceCatalog` :28 internal ctor.

Stale intents/phrases: **none** — all 10 `VoiceCommands` constants hit the `RouteText` switch (:564-598),
`ConfirmVocabulary`/`ChecklistControlWords` fully consumed, every feature `Phrases` array feeds its `TryHandle`.

---

## D. Bloat

### D.1 Files > 500 lines (24)

| File | Lines | Verdict | Natural seam |
|---|---|---|---|
| SayIntentions\SayIntentionsService.cs | 1080 | split | three jobs: (a) ATC request transmit (`TryHandle`, `TransmitRequestAsync`, `PickTemplate`, `LoadRequests`, `AtcRequestDefinition` :19), (b) flight.json poll + `FlightContext` (:100, :485, :1003), (c) comms hand-over / legacy departure gate (`OnPhaseChanged` :576, `ApplyHandoverAsync` :694, `TuneTowerAsync`, `PrimeDepartureRunwayAsync` :767, `TakeCommsForDepartureAsync` :854, `TryAutoTuneAsync`). (b) is already half-duplicated by `FlightJsonFile`; (c) duplicates `FoRadiosService.PrimeDepartureRunway` :211 / `DistanceToDepartureRunwayNm` :277 (same name, same maths, SayIntentionsService :834) |
| Briefings\DfdNavDataProvider.cs | 1047 | split | 4 records + schema discovery (:57-:246) / fix-name tables (:297-:453) / procedure queries (`ApproachesForRunway`, `MissedApproach`, `AirportMsaFt`, `EngineOutProcedure` :541-:846) / SQLite plumbing (:953-:1022). Seam: `DfdConnection` (open/schema/scalar) + `DfdProcedureQueries` |
| Day\CompanyDayService.cs | 997 | split | the pure engine is already `CompanyDayEngine` (Core); the shell still carries OFP seeding (:412-:465), leg-fact filling as finalizer (:578), the end-of-duty offer/debrief (:697-:822), the 60 s idle tick (:840-:914), the voice feature (:914). Seam: `DayLegFactsFinalizer` (finalizer step) + `EndOfDutyDialogue` |
| Abnormals\FailureMonitor.cs | 977 | split | detection (`Evaluate` :291, `EvaluateTrigger` :732, `BurstHoldUntil`, `ReadEwdText`, dataref cache :919) vs dialogue running (`RunEcamDialogueAsync` :517, `BorrowMicAsync` :625, `SpeakDrillAsync` :702, `TryRunProcedure` :831). Seam: `FailureDetector` (pure-ish, timer-driven) + `EcamDialogueRunner` (already has `EcamDialogueCore`) |
| Scenarios\ScenarioService.cs | 964 | split | scheduler shell (:194-:430) vs injector/clear path (`ArmPlanAsync` :431, `ArmAsync` :721, `ClearAsync`/`ClearAllAsync` :802-:867, `ListFailuresAsync`) vs decision dialogue (:574-:655) vs voice feature (:901). Seam: `ScenarioInjector` (the serialised write path = `IScenarioControl`) |
| Checklists\SpokenChecklistEngine.cs | 961 | justified-ish | one state machine; but `RunVerifyAsync/RunActionAsync/RunMonitorControlsAsync/MonitorPhaseAsync` (:427-:589) are the control-monitor branch and could move beside `ControlMonitor`/`ControlSweepService`; `AwaitAcceptedAsync` :589-:709 (120 lines) is the item window |
| Recognition\UtteranceRouter.cs | 854 | justified | the router is the precedence spec; only `ConfirmAndRouteAsync` + courtesy (~120 lines) could become `ClarifierDialogue` |
| Recognition\PushToTalkService.cs | 766 | split | Win32 hook (:56-:74, :530-:584) / joystick poll (:350, :467, :584-:656, :758) / binding+capture (:187-:258, :296-:334, :364-:440). Seam: `KeyboardHookSource`, `JoystickPollSource`, `PttBindings` — the winmm part is also the crash-prone code (#165 memory) and deserves isolation |
| Briefings\BriefingService.cs | 737 | split | fact building (`BuildFactsAsync` :438-:605, `BuildEmergencyFacts`, `BuildGoAroundFacts`, `WindLimitsFor`, `ConsultTechLog`) vs the run/ask dialogue (:221-:424). Seam: `BriefingFactsBuilder` |
| Briefings\BriefingComposer.cs | 720 | justified (pure) | template sections + allowed-number set + prompt; could drop `EmergencySections`/`GoAroundSections` into siblings but it is already pure and tested |
| Persona\CruiseSmallTalkService.cs | 710 | split | 27-param ctor; delivery (`DeliverAsync` :320, two `Task<…>` LLM paths :379/:440, `ListenOnceAsync` :479) vs context building (`BuildContext` :588-:658). Seam: `SmallTalkContextBuilder` (pure, takes the 10 read-only stores) |
| Cabin\CabinCrewService.cs | 685 | split | interphone report runner (:436-:665 `AnnounceAsync`, `RunReportAsync`, `RingAndWaitAsync`, `TryAnswerAsync`, `ReleaseAnswerAsync`) vs cycle/phase/state (:140-:325). Seam: `InterphoneReportRunner` sharing `IAcpChannel` (B9) |
| Recognition\RecognitionController.cs | 669 | justified-ish | engine swap/probe/retry (:383-:656) could be `EngineChain`; listen decision stays |
| Arbiter\SpeechArbiterService.cs | 660 | justified | shell over `SpeechArbiterCore`; the stream render (:458-:583) is the only candidate |
| SayIntentions\FoRadiosComposer.cs | 651 | justified (pure, 4 static classes) | already seamed into `ReadbackComposer`/`CheckInComposer`/… |
| Questions\FoQuestionService.cs | 644 | split | three answer modes (`AnswerChatAsync` :253, `AnswerPlaceAsync` :324, `AnswerFlightAsync` :407) + strict retry + stand-by. Seam: one `IFoAnswerMode` per mode |
| Crew\WalkaroundService.cs | 597 | split | 20-param ctor; finding → tech-log/de-ice (:455-:548) and `RingInterphoneAsync` :568 (B9) |
| SayIntentions\FoRadiosService.cs | 592 | split | departure-runway priming (:211-:289) duplicates SayIntentionsService; `RespondAsync` :364-:485 (120 lines) |
| Callouts\CalloutsEngine.cs | 580 | justified-ish | one sampler; `Placards` (:463-:527) is a separate concern (A322 flap/gear placards, ADR-0012) |
| Mcdu\McduArrivalChanger.cs | 565 | split | `McduArrivalPhrases` (:23-:78) + the state machine (:384-:541); shared base with RadNavTuner (B5) |
| SayIntentions\SayIntentionsFrequencyMonitor.cs | 546 | split | three polls (stations :272, history :338, correct-frequency :471) in one async tick; `AtcTransmission` record |
| Recognition\LanAsrRecognizer.cs | 541 | justified-ish | capture vs transcription; `ToWav`/`PeakDb`/`ResolveDevice` are utilities |
| Abnormals\EcamDialogueCore.cs | 518 | justified (pure) | |

### D.2 Constructors with > 12 parameters (23)

| Class | Params (optional) | Note |
|---|---|---|
| CruiseSmallTalkService | 27 (8) | 10+ read-only stores → `SmallTalkContextBuilder` |
| BriefingService | 24 (13) | 13 optionals = "degrade" done by null-checks; facts builder seam |
| ScenarioService | 22 (6) | injector seam |
| WalkaroundService | 20 (7) | |
| UtteranceRouter | 19 (8) | optionals exist for bare test construction — a `RouterSeams` record (like `RecognitionEngineSeams`) would cut it to ~11 |
| SpokenChecklistEngine | 18 (2) | |
| CompanyDayService | 18 (0) | |
| CabinCrewService | 17 (5) | |
| RegionFactService | 16 (2) | |
| DiversionService | 15 (5) | |
| FoRadiosService | 15 (10) | |
| SayIntentionsService | 15 (11) | |
| FuelDecisionService | 14 (1) | |
| SpeechArbiterService, PaPromptService, DebriefService, FuelCheckMonitor, FoRosterVoiceFeature, FoQuestionService, RecognitionController | 13 | |
| TransitionReminderService, MovementMessageService, VoiceDiagnosticsCollector, WindLimitAdvisory | 12 | |

Recurring cause: every service takes `IFlightPhaseSource`, `JsonlEventLog`, `ISpeechArbiter`, `ILogger<T>`,
`IOptionsMonitor<X>`, `GroundOpsSignals` separately. A `VoiceFeatureHost` (arbiter + event log + phase +
signals) value object would take 4 params off ~40 constructors.

---

## E. Hard-coded English

**Premise correction.** `docs/agents/localisation.md` (Rules) says: "Logs, probes, telemetry, **the voice First
Officer** and the Stream Deck plugin stay English. Do not localise … **spoken phrases**." The memory note
"Core-built texts still English" refers to Core-built *UI* strings, not FO speech. So nothing in Speech violates
ADR-0014. The real question is **code vs `phrases.json`** (ADR-0007 user-editable content): `Persona\PhraseBank`
holds only three pools (are-you-sure, did-not-catch, acknowledgements) and has four consumers
(SpokenChecklistEngine, PersonaService, UtteranceRouter, DI). Everything else is C#.

Counts (perl scan of capitalised sentence literals passed to speech, log/exception/SQL/tag literals excluded,
±15 %): **~580 spoken literals in 81 non-table files + ~165 in 20 `*Phrases.cs`/`*Bank.cs` tables ≈ 745.**

Top offenders (non-table): BriefingComposer 59 (some are LLM prompt), DiversionService 22, ScenarioService 20,
PushbackDirectionVoice 18, CrossCheckComposer 18, SayIntentionsService 17, McduArrivalChanger 17, FoQuestionCore 16,
ReadbackCore 16, TechLogDialogueCore 15, McduRadNavTuner 15, CompanyChannelService 14, LogbookVoiceService 13,
FcuExecutor 13, EngineFlapCallFeature 13, RadioExecutor 12, SpokenChecklistEngine 12, FoRosterVoiceFeature 11,
FoSideDutiesService 11, WindLimitsVoiceFeature 10, FuelDecisionService 10, CompanyDayService 10, EcamDialogueCore 10.

Examples: `BriefingComposer.cs:122` `$"Good {word}, Captain. {header}"`; `DiversionService.cs:223` "Divert to where,
captain? …"; `McduArrivalChanger.cs:205` / `McduRadNavTuner.cs:148` / `McduReader.cs:152` the identical "Unable — I can't
see the M C D U; ProSim isn't connected."; `ReadbackCore.cs:176` `$"Negative. I read QNH {spoken}, not standard."`;
`SayIntentionsService.cs:349-351` three "Roger — calling {name}." variants; `UtteranceRouter.cs:654`
`$"Say again — did you mean {candidate}?"`; `IdleMissPolicy.cs` `LlmOfflineAdvisory`; `TtsPrewarmService.cs:104,105,169`
re-type checklist/loadsheet lines that must stay byte-identical with the speaking code (drift trap).

The 20 phrase tables (2,093 lines) are standalone English; none references PhraseBank. The 13 captain's-question
tables are the B4 boilerplate. If the owner wants user-editable FO wording (the predecessor's `phrases.json` promise),
the lowest-cost step is to move the ~40 *reusable* lines (unable/PF/not-connected/roger/stand-by/did-not-catch
variants) into `PhraseBank` pools with text ids, not all 745.

---

## F. Convention drift vs CLAUDE.md

| Rule | Result |
|---|---|
| `async void` | **0** |
| Blocking on async (`.Result`/`.Wait()`/`GetAwaiter().GetResult()`) | **0** (the `.Result` hits are record properties) |
| Interpolated log strings | **0** |
| Missing `ThrowIfNull` on required ctor params | only `Arbiter\SpeechArbiterCore.ArbiterItem` (internal) and `Vad\UtteranceReadyEventArgs` (event args) — negligible |
| Catch-all swallow without logging | 15 sites: `Playback\SpeechPlayback.cs:105` `catch { }` (Stop on cancel — acceptable), `Abnormals\FailureMonitor.cs:747` bare `catch { condHit = false; }` (**a broken abnormal condition expression is silently false forever**), `Briefings\FmsPlanParser.cs:57` and `Mcdu\McduDisplayParser.cs:59` bare catch → Empty, `Questions\FoFactSource.cs:114` → null, `SayIntentions\FrequencyAssignment.cs:137,161,178` and `SayIntentionsWeatherService.cs:266,277,289,327,338,347` JSON-shape fallbacks, `Llm\StreamingNarrator.cs:286`, `Recognition\IExpectedReplyProvider.cs:93`, `Briefings\BriefingQuestionsPhrases.cs:108`. The JSON ones should be `catch (JsonException)`; FailureMonitor :747 should log once per definition |
| Tick catch log level | inconsistent: Debug ×12 (Cabin\*, Company\*, Crew\*, Day, SayIntentions, Scenarios, MissedApproachRebrief, EngineStartMonitor), Error ×9 (Monitoring\*, Callouts, FailureMonitor, RegionFact, CruiseSmallTalk), Warning ×5 (Abnormals RTO/EFATO, FoSide\*, StepClimb). A tick that throws every second at Debug is invisible in a Warning-level CMTrace |
| Identifiers containing "key" (Snyk rule, owner 2026-10-04/13 — applies to NEW identifiers) | existing: `cacheKey` (StyledSpeechService ×10), `regionKey` (RegionFactCore/Bank/Service ×15), `MonthKey` (TtsUsageTracker), `reasonKey` (UnmatchedUtteranceTracker), `runwayKey/kindKey/starKey` (McduArrivalChanger), `RadNavKey` (McduRadNavTuner), `KeyPrefix` (McduControls), `legacyKey/ParseKey/FormatKey/RegKey/PttKey` (PushToTalkService — keyboard keys, legitimate). Rename candidates: `cacheKey→cacheId`, `regionKey→regionId`, `MonthKey→monthId`, `reasonKey→reasonId`, `runwayKey/kindKey/starKey→*Id`, `RadNavKey→RadNavId` |
| Timers without a testable core | timer-driven services with **no `*Core`/pure class**: `Abnormals\FailureMonitor` (977), `Scenarios\ScenarioService` (964 — `ScenarioScheduler` in Core covers scheduling only), `Callouts\CalloutsEngine` (580 — `ProcessSample` is testable but not pure), `Company\CompanyChannelService` (482), `Monitoring\StabilizedApproachMonitor` (249, has criteria but no core), `Monitoring\StepClimbAdvisor` (253), `Monitoring\FlowMonitor` (353), `Briefings\MissedApproachRebrief` (190, no reentrancy guard), `SayIntentions\SayIntentionsService` (1080), `SayIntentions\SayIntentionsFrequencyMonitor` (546), `SayIntentions\FoRadiosService` (592 — composers are pure, the tick is not), `FoSide\FmgcCrossCheckService` (286), `Tts\TtsPrewarmService`. `CompanyDayService` has `ProcessTick` :859 and `CompanyDayEngine` (Core) — fine |
| Degrade-not-fail / write allow-lists / dataref-first | no violations seen; write paths all pass `VoiceCommandWriteGate`/`FoSideDutiesGate`/`IScenarioControl` |
| "Never log the token/keys" | `SayIntentionsService.WithApiKey/ResolveApiKey` :982-:992 — not logged (checked) |

---

## G. Test seams

135 test files under tests/ProsimCompanion.Core.Tests reference Speech. Cross-check of class names in tests:

**Large services with zero test mention of the class** (their cores may be tested, the shell is not):
`SayIntentions\FoRadiosService` (592), `Recognition\LanAsrRecognizer` (541, hardware — accept), `Cabin\PaPromptService` (199),
`Cabin\PurserPaService` (154), `Cabin\TurbulenceCabinService` (267), `Callouts\TransitionReminderService` (272),
`Crew\GroundCrewUpcallService` (251), `Briefings\MissedApproachRebrief` (190 — **no core, no test, no reentrancy guard**),
`Mcdu\McduActuator` (240), `SayIntentions\SayIntentionsArrivalGateSource` (122), `SayIntentions\SayIntentionsGateAssignService` (154),
`SayIntentions\FlightJsonFile` (123), `Playback\SpeechPlayback` (194, hardware — accept). Mentioned once only:
`SayIntentionsService` (1080), `FoSideDutiesService` (447).

**Large services with no pure core** (so the tests that exist drive the timer shell with fakes):
FailureMonitor (FailureMonitorTests exist, drive the shell), ScenarioService (ScenarioTests), CalloutsEngine
(CalloutsEngineTests via `ProcessSample`), CompanyChannelService (CabinCompanyTests/CompanyDeliverMessageTests),
StabilizedApproachMonitor, StepClimbAdvisor, FlowMonitor, SayIntentionsFrequencyMonitor (SayIntentionsFrequencyTests
cover `FrequencyAssignment` parsing), FmgcCrossCheckService (one auto-due test).

Good examples to copy: `Monitoring\FuelCheckMonitor` + `FuelCheckCore`, `Crew\WalkaroundService` + `WalkaroundCore`
(edge-baselined tick), `Persona\CruiseSmallTalkService` + `SmallTalkCore` (`BuildTick` → `ProcessTick`), `Cabin\*Core`.

Recommended extractions in order of risk × size: `FailureDetectorCore` (from FailureMonitor.Evaluate/EvaluateTrigger),
`SayIntentionsCommsCore` (SayIntentionsService phase/hand-over decisions — partly `CommsHandoverCore` already),
`StepClimbCore`, `StabilizedApproachCore` (criteria already a record), `MissedApproachRebriefCore`, `FlowMonitorCore`.

---

## Top 15 by payoff

| # | Finding | Effort | Lines removable | Why first |
|---|---|---|---|---|
| 1 | B1 Tick shell ×21 (+7 variants) → `GuardedTimer`/`PolledService` in Core.Hosting; fixes the Debug/Warning/Error log-level lottery and MissedApproachRebrief's missing guard | M | 350–420 | biggest mechanical win, zero behaviour change |
| 2 | A.3 G9/G10/G11 sterile predicate → one `ISterileCockpit` + `FlightPhaseExtensions.IsGroundSterile()`; removes 6 hand-built `SpeechContext`s and the 1000-ft hard-code vs option split | S | 80–90 | a safety gate that currently has two thresholds |
| 3 | D.1 Split `SayIntentionsService` (1080) into request transmit / flight.json poll / comms hand-over, and dedupe `PrimeDepartureRunway`+`DistanceToDepartureRunwayNm` with `FoRadiosService` | L | ~150 net, 3 files of ~300 | largest file, 1 test mention, duplicated runway maths in two SI services |
| 4 | B2+B3 One spoken-number parser (`SpokenNumber`) + `ToDigits(int)`, `FrequencyKhz`, Core `SpokenUnits.Tonnes` (one rounding), delete `Aviation.TryParseSpoken` and the NATO copy | M | 200–250 | 4 parsers and 2 roundings are a verifier-mismatch risk (NumberVerifier compares spoken forms) |
| 5 | B4 Captain's-question yes/no factory + `ConfirmVocabulary.Classify` | S | ~180 | 9 identical tables; new questions become 10 lines |
| 6 | B5 `CancelWindow` + `McduPreconditions` + shared base for McduArrivalChanger/McduRadNavTuner | M | 150–200 | the 5 write-side executors share their riskiest code by copy |
| 7 | B7 One `NewLeg` reset signal (fuse `FlightCycleReset`, `SessionReset`, 5 turnaround edges, 3 "new leg" definitions) | M | 60–80 | ADR-0019 says a leg begins on its OFP; three services still disagree |
| 8 | F FailureMonitor :747 bare catch → log once per definition; JSON-shape catches → `catch (JsonException)` (9 sites) | S | 0 | a malformed abnormal condition is silently "never fires" today |
| 9 | B10 `CruiseSmallTalkService.cs:624` morning/evening bug → `TimeOfDayGreeting`; BriefingComposer/FoRoster compose via Core | S | ~10 | user-visible wrong greeting 00:00–03:59 |
| 10 | B12 Grammar arrays referenced (not retyped) in 25 `VoiceReferenceDescription`s + B11/G20 drop the 8 redundant `Enabled` re-checks | S | ~70 | kills the drawer-row drift class `VoiceFeatureDeclaration` was created for |
| 11 | D.1 `FailureMonitor` → `FailureDetector` (pure, timer) + `EcamDialogueRunner`; G test seam | L | 0 net, testability | 977 lines, no pure core, 2 Hz timer, mic borrow inside |
| 12 | D.2 `RouterSeams`/`VoiceFeatureHost` value objects to cut the 19–27-param ctors (UtteranceRouter, CruiseSmallTalk, BriefingService, Scenario, Walkaround) | M | ~120 (ctor bodies) | readability + test setup |
| 13 | B9/G16 `IAcpChannel.RingAndAwaitAsync`; route CabinCrewService CAB latches through it | S | 50–60 | three copies of an interphone timing block (the crew hail bugs live here) |
| 14 | C Delete the 6 zero-reference members (+ `contextFlags` JSON keys, abnormals.md row) and fix 16 stale "for tests" comments / narrow visibility | S | ~45 (+60 test-only) | cheap hygiene; `ContextFlags` is a documented feature that does not exist |
| 15 | F Rename the 9 non-keyboard `*Key` identifiers (`cacheKey`, `regionKey`, `MonthKey`, `reasonKey`, `runwayKey`, `kindKey`, `starKey`, `RadNavKey`, `KeyPrefix`) per the Snyk rule | S | 0 | rule applies to new identifiers, but these are the ones a future edit will copy |

Rough total removable: 1,300–1,600 lines (~2.5–3 %), plus 3–4 large files split and 5–6 new pure cores.

---

## Appendix — the rest

- B8 Edge/Crossing structs (~40 lines); B13 `DataRefReadCache` ×7 (~55); B14 `SpeakAndRecord` helper (~60–100);
  B15 PaPrompt/PurserPa fold (~80), `RadioExecutor.WriteVerify` vs Core `Com1PanelService` (~6), rename
  `SmallTalkService` → `QuietCockpitVoiceFeature` + fix its stale summary.
- B11 `SayIntentionsService.PickAcknowledgement` and `CompanyChannelService` :324 use `Random.Shared` directly
  (untestable); `PersonaService` has the only `new Random()`.
- D.1 second tier splits: `DfdNavDataProvider` (connection/schema vs queries), `CompanyDayService` (finalizer +
  end-of-duty dialogue), `PushToTalkService` (hook / joystick / bindings — isolates the winmm crash surface),
  `BriefingService` (facts builder), `CruiseSmallTalkService` (context builder), `CabinCrewService`
  (interphone report runner), `FoQuestionService` (answer modes), `SayIntentionsFrequencyMonitor` (3 polls).
- E `TtsPrewarmService.cs:104,105,169` re-types spoken lines for cache warming — a drift trap; expose the line
  builders from the speaking classes instead.
- E If `phrases.json` is to mean anything, promote the ~40 reusable FO lines (unable/not-connected/PF/roger/
  stand-by variants) into `PhraseBank` pools; the per-feature sentences can stay in code under the localisation
  rule as written.
- G Zero-test shells worth one test each: `PaPromptService`, `PurserPaService`, `TurbulenceCabinService`,
  `TransitionReminderService`, `GroundCrewUpcallService` (their cores are tested; the shells are B1 copies — the
  `PolledService` refactor makes one test cover all of them), `MissedApproachRebrief`, `McduActuator`,
  `SayIntentionsArrivalGateSource`, `SayIntentionsGateAssignService`, `FlightJsonFile`.
- F `DialogueHoldRule.ExemptPrefixes` is a string-typed taxonomy of "dialogue-owned tags" that grows per incident
  (`day.debrief` was added after 2026-11-01); a `SpeechRequest.HoldExempt` flag set by the dialogues would be typed.
- A note for the pathway: `UtteranceRouter.Start` is driven by `SpokenChecklistEngine.Start` (:147-148) — the
  router does not exist as a startup module in its own right, so a host without the checklist engine has no
  voice routing. Worth a comment in DI or an `IStartupModule` of its own.
