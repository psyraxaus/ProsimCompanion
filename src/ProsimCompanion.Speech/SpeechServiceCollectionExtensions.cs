using ProsimCompanion.Core.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Checklists;
using ProsimCompanion.Speech.Monitoring;
using ProsimCompanion.Speech.Playback;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech;

public static class SpeechServiceCollectionExtensions
{
    /// <summary>Registers the voice First Officer pillar's speech foundations: arbiter, TTS
    /// router and playback. Provider registration order IS the fallback chain order —
    /// Kokoro → ElevenLabs → Google → WinRT → SAPI5 (docs/integrations/speech.md). A user
    /// configures at most one paid cloud provider; whichever is configured wins.</summary>
    public static IServiceCollection AddSpeechServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TtsDiskCache>();
        services.AddSingleton<TtsUsageTracker>();
        // Core-facing read-outs for the settings pages (Web references only Core).
        services.AddSingleton<Core.State.ITtsUsageReadout, TtsUsageReadout>();
        services.AddSingleton<Core.State.IElevenLabsVoiceCatalog, ElevenLabsVoiceCatalog>();
        services.AddSingleton<Core.State.IKokoroVoiceCatalog, KokoroVoiceCatalog>();
        services.AddSingleton<Core.State.IVoiceCatalog, VoiceCatalog>();
        services.AddSingleton<ITtsProvider, KokoroTtsProvider>();
        services.AddSingleton<ITtsProvider, ElevenLabsTtsProvider>();
        services.AddSingleton<ITtsProvider, GoogleTtsProvider>();
        services.AddSingleton<ITtsProvider, WinRtTtsProvider>();
        services.AddSingleton<ITtsProvider, Sapi5TtsProvider>();
        services.AddSingleton<TtsRouter>();
        services.AddSingleton<ISpeechPlayback, SpeechPlayback>();
        services.AddSingleton<IAudioDeviceCatalog, AudioDeviceCatalog>();
        services.AddSingleton<ISpeechDiagnostics, SpeechDiagnosticsService>();
        services.AddStartupModule<SpeechArbiterService>();
        services.AddSingleton<ISpeechArbiter>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddSingleton<ISpeechControl>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddStartupModule<CalloutsEngine>();
        services.AddStartupModule<StabilizedApproachMonitor>();
        services.AddStartupModule<FlowMonitor>();
        // Rejected takeoff (issue #187): the captain's "stop" must beat every other reading of
        // the word, so it dispatches first; the EFATO sequencer paces the drill's items. Both
        // register after the failure monitor they depend on (declared further up).
        services.AddVoiceFeature<Abnormals.RejectedTakeoffService>();
        services.AddStartupModule<Abnormals.RejectedTakeoffService>();
        services.AddStartupModule<Abnormals.EfatoService>();
        // In-flight FO monitoring (issue #148), each behind its own sop.monitoring switch.
        services.AddStartupModule<Monitoring.FuelCheckMonitor>();
        services.AddVoiceFeature<Monitoring.FuelCheckMonitor>();
        services.AddSingleton<Core.Flight.IFuelCheckRequests>(p => p.GetRequiredService<Monitoring.FuelCheckMonitor>());
        // The captain's fuel decision at dispatch and "how long can we hold" (issue #183).
        services.AddStartupModule<Monitoring.FuelDecisionService>();
        services.AddVoiceFeature<Monitoring.FuelDecisionService>();
        services.AddStartupModule<Monitoring.GrossErrorCheckMonitor>();
        services.AddVoiceFeature<Monitoring.GrossErrorCheckMonitor>();
        services.AddStartupModule<Monitoring.DestinationWeatherWatch>();
        // Wind limits (issue #182): "wind check" / "runway condition" and the once-per-approach
        // advisory when the destination wind puts the landing runway beyond a limit.
        services.AddStartupModule<Monitoring.WindLimitAdvisory>();
        services.AddVoiceFeature<Monitoring.WindLimitsVoiceFeature>();
        // PM engine-start monitoring (issue #131): starting / avail / both stabilized, from
        // datarefs — advisory only, ground only.
        services.AddStartupModule<Callouts.EngineStartMonitor>();
        services.AddStartupModule<TtsPrewarmService>();
        services.AddStartupModule<PushToTalkService>();
        services.AddSingleton<IPttInputCapture>(p => p.GetRequiredService<PushToTalkService>());
        services.AddSingleton<RecognitionController>();
        // The controller's listening-window surface + the exclusive-mic seam over it (guided
        // dialogues borrow the mic; normal routing stands down while borrowed).
        services.AddSingleton<IRecognitionWindow>(p => p.GetRequiredService<RecognitionController>());
        // Pilot "ear off" latch (Stream Deck / web toggle) — the Core seam the command
        // registry and the web panel reach the controller through.
        services.AddSingleton<IVoiceListeningControl>(p => p.GetRequiredService<RecognitionController>());
        services.AddSingleton<IMicOwnership, MicOwnership>();
        // What the FO is waiting to hear (issue #172): the router writes the clarifier
        // candidate and the open checklist item, the LAN recognizer's self-echo guard reads.
        services.AddSingleton<ExpectedReplyStore>();
        services.AddSingleton<IExpectedReplyProvider>(p => p.GetRequiredService<ExpectedReplyStore>());
        services.AddSingleton<UtteranceInterpreter>();
        services.AddSingleton<ControlMonitor>();
        services.AddSingleton<ControlSweepService>();
        services.AddStartupModule<Abnormals.FailureMonitor>();
        // Web-side ECAM escape hatch (issue #56) — the Web project reaches the running
        // dialogue only through this Core seam.
        services.AddSingleton<Core.State.IAbnormalDialogueControl>(
            p => p.GetRequiredService<Abnormals.FailureMonitor>());
        // Voice features. Each declares its own dispatch precedence, gate and drawer rows
        // (VoiceFeatureDeclaration); the roster below sorts them, so nothing here is ordered.
        services.AddVoiceFeature<Roles.RoleManager>();
        services.AddVoiceFeature<Monitoring.StandaloneReadbacks>();
        services.AddVoiceFeature<Radios.RadioExecutor>();
        services.AddVoiceFeature<Fcu.FcuExecutor>();
        // The FO's own-side duties (issue #188): baro follow + EFIS2 commands through the four-name
        // FO-side gate, and the read-only ADIRS/FMGC cross-check. Right after the FCU (same flow),
        // before the file commands (the bare "set standard" / "set QNH" stay there).
        services.AddVoiceFeature<FoSide.FoSideDutiesService>();
        services.AddStartupModule<FoSide.FoSideDutiesService>();
        services.AddVoiceFeature<FoSide.FmgcCrossCheckService>();
        services.AddStartupModule<FoSide.FmgcCrossCheckService>();
        services.AddSingleton<Commands.SpokenTokenSource>();
        services.AddVoiceFeature<Commands.ConfiguredVoiceCommands>();
        services.AddVoiceFeature<Callouts.EngineFlapCallFeature>();
        services.AddVoiceFeature<Callouts.GearCallFeature>();
        // "Takeoff" chrono (issue #126): a startup module so the touchdown edge is wired even
        // before the first utterance.
        services.AddStartupModule<Callouts.ChronoCallFeature>();
        services.AddVoiceFeature<Callouts.ChronoCallFeature>();
        // The chrono's running latch rides in the flight-state snapshot (issue #167).
        services.AddSingleton<Core.Flight.IFlightStatePart>(p => p.GetRequiredService<Callouts.ChronoCallFeature>());
        services.AddVoiceFeature<Callouts.PilotAnnouncementFeature>();
        services.AddVoiceFeature<Mcdu.McduReader>();
        services.AddSingleton<Mcdu.IMcduReader>(p => p.GetRequiredService<Mcdu.McduReader>());
        services.AddSingleton<Mcdu.IMcduActuator, Mcdu.McduActuator>();
        services.AddVoiceFeature<Mcdu.McduRadNavTuner>();
        services.AddVoiceFeature<Mcdu.McduArrivalChanger>();
        services.AddSingleton<Briefings.DfdNavDataProvider>();
        // ICAO → spoken airport name (issue #70): curated short names + the DFD's
        // airport_name column. Optional everywhere it is consumed, so a missing DFD just
        // means spelled ICAOs again.
        services.AddSingleton<Briefings.DfdAirportNames>();
        services.AddSingleton<Core.Airports.IAirportNames>(
            p => p.GetRequiredService<Briefings.DfdAirportNames>());
        // Airport coordinates for the flight-progress store (issue #145): the fallback tier
        // behind the gateway's runway list.
        services.AddSingleton<Core.Airports.IAirportCoordinateSource, Briefings.DfdAirportCoordinates>();
        // Runway threshold + bearing for the pushback advisor (2026-10-04), behind the gateway tier.
        services.AddSingleton<Core.Airports.IRunwayGeometrySource, Briefings.DfdRunwayGeometry>();
        services.AddSingleton<Briefings.ProcedureSource>();
        services.AddSingleton<Core.Airports.IDepartureRunwaySource, Briefings.FmsDepartureRunway>();
        services.AddSingleton<Briefings.MinimaCaptureDialogue>();
        // The diversion workflow (issue #184): declares on the Core store (which overlays the
        // OFP destination), speaks the offers, tells the company; implements the command seam.
        services.AddStartupModule<Flight.DiversionService>();
        services.AddVoiceFeature<Flight.DiversionService>();
        services.AddSingleton<Core.Commands.Handlers.IDiversionControl>(p => p.GetRequiredService<Flight.DiversionService>());
        services.AddStartupModule<Briefings.MissedApproachRebrief>();
        services.AddVoiceFeature<Briefings.BriefingService>();
        services.AddVoiceFeature<Briefings.MissedApproachVoiceFeature>();
        services.AddVoiceFeature<Briefings.MinimaQueryVoiceFeature>();
        services.AddStartupModule<Company.CompanyChannelService>();
        services.AddVoiceFeature<Company.CompanyChannelService>();
        services.AddSingleton<Company.ICompanyChannel>(p => p.GetRequiredService<Company.CompanyChannelService>());
        // Company day mode: voice start/end, leg tracking (finalizer Order 40), turnaround +
        // end-of-day summaries. Off by default (day.enabled).
        services.AddVoiceFeature<Day.CompanyDayService>();
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<Day.CompanyDayService>());
        services.AddSingleton<Core.Day.IDayControl>(p => p.GetRequiredService<Day.CompanyDayService>());
        services.AddHostedService<Day.DayBootstrapService>();
        services.AddStartupModule<SayIntentions.SayIntentionsService>();
        services.AddVoiceFeature<SayIntentions.SayIntentionsService>();
        // Wrong-frequency report: COM1 against the frequency SayIntentions ATC last assigned.
        services.AddStartupModule<SayIntentions.SayIntentionsFrequencyMonitor>();
        // PTT relay (2026-10-10): a ProSim push-to-talk switch keys the SI control LVAR.
        services.AddStartupModule<SayIntentions.SayIntentionsPttRelay>();
        // Weather/CPDLC pulls are on-demand only (web Weather page) — no bootstrap Start,
        // and deliberately NOT an IVoiceFeature.
        services.AddSingleton<SayIntentions.SayIntentionsWeatherService>();
        services.AddSingleton<Core.State.IWeatherControl>(
            p => p.GetRequiredService<SayIntentions.SayIntentionsWeatherService>());
        // Arrival-gate ATC push (assignGate) — the SayIntentions half of the Core
        // arrival-gate coordinator; on-demand only, deliberately NOT an IVoiceFeature.
        services.AddSingleton<SayIntentions.SayIntentionsGateAssignService>();
        services.AddSingleton<Core.Gate.ISayIntentionsGateAssign>(
            p => p.GetRequiredService<SayIntentions.SayIntentionsGateAssignService>());
        // The reverse direction (2026-10-08): ATC's assigned_gate as the GSX arrival gate,
        // behind sayIntentions.arrivalGateFromAtc.
        services.AddStartupModule<SayIntentions.SayIntentionsArrivalGateSource>();
        // The purser's once-per-flight cruise query + reply window (2026-10-09): borrows the
        // mic like the hails; the cabin service runs it off its own tick.
        services.AddSingleton<Cabin.CabinCruiseQueryDialogue>();
        services.AddStartupModule<Cabin.CabinCrewService>();
        // Turbulence, cabin management and the captain's PA (issue #186): the cabin's
        // reactions ride the Core turbulence store; the purser's "secure the cabin?" is a
        // captain's question over the shared dialogue; the PA prompts fill the Flight Status card.
        services.AddSingleton(Cabin.CabinSecureQuestionPhrases.Table);
        services.AddStartupModule<Cabin.TurbulenceCabinService>();
        services.AddStartupModule<Cabin.PaPromptService>();
        services.AddStartupModule<Cabin.PurserPaService>();
        // The cabin's once-per-flight latches ride in the flight-state snapshot (issue #167).
        services.AddSingleton<Core.Flight.IFlightStatePart>(p => p.GetRequiredService<Cabin.CabinCrewService>());
        // Prosim2GSX-parity cabin dings (startup / final loadsheet) — plain chime playback,
        // deliberately outside the speech arbiter.
        services.AddHostedService<Cabin.CabinDingService>();
        // GSX voice control ("commence ground services", "request boarding", …) — dispatches
        // through the named-command registry so voice/web/API/StreamDeck share one seam.
        services.AddVoiceFeature<Gsx.GsxVoiceService>();
        // Pushback direction for THIS flight by voice ("push back facing north", "tail left")
        // and the FO's "tail left or tail right?" question when the automation is unsure
        // (2026-10-04). Writes the Core per-flight store the GSX pillar answers GSX's menu from.
        services.AddVoiceFeature<Gsx.PushbackDirectionVoiceFeature>();
        // The captain's questions (architecture review C2): ONE dialogue voices whatever
        // question is open on any Core captain's-question store, driven by one phrase table per
        // question — the pushback "which way?" (2026-10-04), the arrival-gate "36 Alpha or 36
        // Bravo?" (issue #156), "request de-icing?" (2026-10-09) and the turnaround "commence
        // ground services?" (issue #169). A new question is a table, not a dialogue.
        services.AddSingleton<Gsx.CaptainsQuestionPhrases>(p => Gsx.PushbackQuestionPhrases.Create(p.GetRequiredService<Gsx.PushbackDirectionVoiceFeature>()));
        services.AddSingleton(Gsx.GateChoicePhrases.Table);
        services.AddSingleton(Gsx.DeiceQuestionPhrases.Table);
        services.AddSingleton(Gsx.TurnaroundStartPhrases.Table);
        // "Any other threats, captain?" after the emergency half of the takeoff briefing
        // (issue #180): free-text answer, noted on the Core store.
        services.AddSingleton<Gsx.CaptainsQuestionPhrases>(p => Briefings.ThreatsQuestionPhrases.Create(
            p.GetRequiredService<ThreatsQuestionStore>(), p.GetRequiredService<Arbiter.ISpeechArbiter>()));
        // "Issue a revised final?" after a last-minute change (issue #181), and the FO's
        // reading of a within-tolerance change off the loadsheet store.
        services.AddSingleton(Company.LmcRevisionPhrases.Table);
        services.AddStartupModule<Company.LmcAnnouncer>();
        services.AddSingleton(Monitoring.FuelDecisionPhrases.Table);
        // Movement messages at OFF / IN and the delay-code question at a late OUT (issue #185).
        services.AddSingleton<Gsx.CaptainsQuestionPhrases>(p => Company.DelayCodePhrases.Create(p.GetRequiredService<Core.Company.DelayCodeCatalog>()));
        services.AddStartupModule<Company.MovementMessageService>();
        services.AddStartupModule<Gsx.CaptainsQuestionDialogue>();
        // The FO's packs-off / ditching-on calls and read-only checks around a GSX de-icing
        // (issue #166, opt-in gsx.deice.packsProcedure). Edges from the Core activity seam.
        services.AddStartupModule<Gsx.DeicePacksProcedure>();
        // Interphone hail dialogues ("cockpit to ground" → "go ahead, captain" → request) and
        // ground-crew upcalls on INT (ADR-0006 / issue #51).
        // The ACP transmit monitor (issue #72) feeds the hail gate — captain S_ASP_SEND_CHANNEL
        // + S_ASP_INT_SEND, subscribed once here, never S_ASP_INTRAD (that rocker is the GSX
        // smart button).
        // The captain ACP as the crew dialogues see it (campaign #85): atomic transmit
        // snapshot + the one latch-or-grace wait for INT/CAB receive.
        services.AddSingleton<Crew.AcpChannel>();
        services.AddSingleton<Crew.IAcpChannel>(p => p.GetRequiredService<Crew.AcpChannel>());
        services.AddVoiceFeature<Crew.CrewHailService>();
        services.AddStartupModule<Crew.GroundCrewUpcallService>();
        // Cold-and-dark mismatch advisory (issue #63): speaks the aircraft state check's
        // verdict, consumed from the Core diagnostics store — no GSX project reference.
        services.AddStartupModule<Crew.AircraftStateAdvisoryService>();
        // Parking-conflict guidance (issue #44): when GSX doesn't recognize the position,
        // the FO names the facility GSX offers and the actions that actually help.
        services.AddStartupModule<Crew.ParkingConflictAdvisoryService>();
        // Dropped GSX call (issue #76): the slot's retry failed too — say so, once per drop.
        services.AddStartupModule<Crew.DroppedCallAdvisoryService>();
        // "Check the aircraft state" re-check trigger (issue #92). The control seam lives in
        // Core and is implemented by the GSX pillar; resolved as optional so the feature
        // degrades to disabled (no grammar) when that pillar is absent.
        services.AddSingleton(p => new Crew.AircraftStateVoiceService(
            p.GetService<Core.State.IAircraftStateCheckControl>(),
            p.GetRequiredService<ISpeechArbiter>(),
            p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Crew.AircraftStateVoiceService>>()));
        services.AddVoiceFeature<Crew.AircraftStateVoiceService>();
        // "Disregard" / "cancel that" while idle (issue #168): silences the advisory being
        // spoken (the advisories register their line with SpokenAdvisories) and acknowledges.
        services.AddSingleton<Crew.SpokenAdvisories>();
        services.AddVoiceFeature<Crew.DisregardVoiceFeature>();
        // Accent localization (issue #53): airport-country → per-provider ground-crew voice,
        // consumed by the arbiter at render time. Registered BEFORE the arbiter resolves.
        services.AddSingleton<Crew.AccentVoiceResolver>();
        // Post-flight voice: tech-log brief + spoken debrief (deterministic template). The
        // debrief doubles as the first session-finalization step (Order 10).
        services.AddVoiceFeature<TechLog.TechLogVoiceService>();
        services.AddVoiceFeature<Logbook.LogbookVoiceService>();
        // Guided raise/rectify dialogues + the post-abnormal shutdown offer (finalizer Order 40).
        services.AddVoiceFeature<TechLog.TechLogDialogueService>();
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<TechLog.TechLogDialogueService>());
        services.AddVoiceFeature<Debrief.DebriefService>();
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<Debrief.DebriefService>());
        services.AddHostedService<PostFlightVoiceBootstrapService>();
        // "Quiet please" latch + its Low-band suppression rule (consumed by the arbiter).
        services.AddSingleton<Persona.QuietState>();
        services.AddVoiceFeature<Persona.SmallTalkService>();
        // The roster: every IVoiceFeature above in its DECLARED dispatch order — the router and
        // the "what can I say?" drawer (issue #136) consume this, never the registration order.
        services.AddSingleton<VoiceFeatureRoster>();
        services.AddSingleton<Abnormals.IDrillSource>(p => p.GetRequiredService<Abnormals.FailureMonitor>());
        services.AddSingleton<VoiceReference.IChecklistDefinitionSource, VoiceReference.ChecklistDefinitionSource>();
        services.AddSingleton<Core.VoiceReference.IVoiceReference, VoiceReference.VoiceReferenceBuilder>();
        // FO persona: phrase bank (phrases.json in the USER config tree, ADR-0007), the
        // persona itself (prompt fragment + ack variation) and the LLM restyle path for
        // advisories.
        services.AddSingleton(p => new Persona.PhraseBank(
            Core.Configuration.UserConfigPaths.Root,
            p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Persona.PhraseBank>>(),
            p.GetService<Core.State.ConfigProblemStore>()));
        services.AddSingleton<Persona.PersonaService>();
        services.AddSingleton<Persona.StyledSpeechService>();
        // ONE shared LLM client (issue #66): registering it lets every consumer's optional
        // `OpenAiChatClient? llm = null` parameter resolve to the same health-reporting
        // instance instead of each service newing up a silent private copy. The probe keeps
        // re-testing an unhealthy endpoint every five minutes so recovery is detected.
        services.AddSingleton<Llm.OpenAiChatClient>();
        // Sentence-by-sentence LLM speech for briefings and the debrief (issue #147).
        services.AddSingleton<Llm.StreamingNarrator>();
        services.AddHostedService<Llm.LlmHealthProbeService>();
        // The utterance router (CONTEXT.md, campaign #82): owns interpretation, precedence
        // and the idle grammar; the checklist engine attaches as its routing host.
        // Free-form questions to the FO (issue #149): the router's last stop before the
        // did-not-catch line. Read-only — answers are speech and a session event only.
        services.AddSingleton<Questions.FoFactSource>();
        services.AddSingleton<Questions.FoQuestionService>();
        services.AddSingleton<IFreeFormQuestionHandler>(p => p.GetRequiredService<Questions.FoQuestionService>());
        // Heard-but-not-understood bookkeeping (issue #112): every router fall-through becomes
        // a voice.unmatched event and a row on the Voice page's candidate list.
        services.AddSingleton<UnmatchedUtteranceTracker>();
        // Ambient region facts in the cruise (issue #122): the curated file lives in the USER
        // config tree (seeded from {app}\config, ADR-0007); the service shares #153's atlas
        // lookup, the narrator and the persona restyle path.
        services.AddSingleton(p => new Immersion.RegionFactBank(
            Core.Configuration.UserConfigPaths.Root,
            p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Immersion.RegionFactBank>>(),
            p.GetService<Core.State.ConfigProblemStore>()));
        services.AddStartupModule<Immersion.RegionFactService>();
        services.AddSingleton<UtteranceRouter>();
        services.AddStartupModule<SpokenChecklistEngine>();
        // WoL used to hide in the retired bootstrap (campaign #87).
        // Wake-on-LAN moved to Core (2026-10-08): one `wakeOnLan.targets` list for every PC
        // the FO depends on, sent by WakeOnLanStartup; the legacy briefing.llmWakeOnLan
        // block is still honoured there.

        return services;
    }

    /// <summary>Registers one voice feature: the concrete singleton (unless a startup-module or
    /// factory registration already provides it) and its <see cref="IVoiceFeature"/> forwarder.
    /// The order of these calls means nothing — dispatch precedence is the feature's own
    /// <see cref="VoiceFeatureDeclaration"/>, applied by <see cref="VoiceFeatureRoster"/>.</summary>
    private static IServiceCollection AddVoiceFeature<TFeature>(this IServiceCollection services)
        where TFeature : class, IVoiceFeature
    {
        services.TryAddSingleton<TFeature>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TFeature>());
        return services;
    }
}
