using Microsoft.Extensions.DependencyInjection;
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
    /// Kokoro → Google → WinRT → SAPI5 (docs/integrations/speech.md).</summary>
    public static IServiceCollection AddSpeechServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<TtsDiskCache>();
        services.AddSingleton<TtsUsageTracker>();
        services.AddSingleton<ITtsProvider, KokoroTtsProvider>();
        services.AddSingleton<ITtsProvider, GoogleTtsProvider>();
        services.AddSingleton<ITtsProvider, WinRtTtsProvider>();
        services.AddSingleton<ITtsProvider, Sapi5TtsProvider>();
        services.AddSingleton<TtsRouter>();
        services.AddSingleton<ISpeechPlayback, SpeechPlayback>();
        services.AddSingleton<IAudioDeviceCatalog, AudioDeviceCatalog>();
        services.AddSingleton<ISpeechDiagnostics, SpeechDiagnosticsService>();
        services.AddSingleton<SpeechArbiterService>();
        services.AddSingleton<ISpeechArbiter>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddSingleton<ISpeechControl>(p => p.GetRequiredService<SpeechArbiterService>());
        services.AddSingleton<CalloutsEngine>();
        services.AddSingleton<StabilizedApproachMonitor>();
        services.AddSingleton<FlowMonitor>();
        services.AddSingleton<TtsPrewarmService>();
        services.AddSingleton<PushToTalkService>();
        services.AddSingleton<IPttInputCapture>(p => p.GetRequiredService<PushToTalkService>());
        services.AddSingleton<RecognitionController>();
        // The controller's listening-window surface + the exclusive-mic seam over it (guided
        // dialogues borrow the mic; normal routing stands down while borrowed).
        services.AddSingleton<IRecognitionWindow>(p => p.GetRequiredService<RecognitionController>());
        services.AddSingleton<IMicOwnership, MicOwnership>();
        services.AddSingleton<UtteranceInterpreter>();
        services.AddSingleton<ControlMonitor>();
        services.AddSingleton<ControlSweepService>();
        services.AddSingleton<Abnormals.FailureMonitor>();
        // Web-side ECAM escape hatch (issue #56) — the Web project reaches the running
        // dialogue only through this Core seam.
        services.AddSingleton<Core.State.IAbnormalDialogueControl>(
            p => p.GetRequiredService<Abnormals.FailureMonitor>());
        // Voice features — registration order is dispatch precedence (roles first so a
        // handover is never mis-parsed as an instruction).
        services.AddSingleton<Roles.RoleManager>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Roles.RoleManager>());
        services.AddSingleton<Radios.RadioExecutor>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Radios.RadioExecutor>());
        services.AddSingleton<Fcu.FcuExecutor>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Fcu.FcuExecutor>());
        // File-driven commands.json AFTER the FCU executor: the gated FCU path keeps
        // precedence on overlapping phrases ("arm approach").
        services.AddSingleton<Commands.SpokenTokenSource>();
        services.AddSingleton<Commands.ConfiguredVoiceCommands>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Commands.ConfiguredVoiceCommands>());
        // Engine-start + flap call responses (issue #67, verbal only — no lever writes).
        // AFTER commands.json so a user-configured phrase keeps precedence.
        services.AddSingleton<IVoiceFeature, Callouts.EngineFlapCallFeature>();
        // MCDU trio (predecessor dispatch position: after fcu, before briefings). Reader is
        // read-only; tuner/arrival changer arm only via mcdu.allowActuation.
        services.AddSingleton<Mcdu.McduReader>();
        services.AddSingleton<Mcdu.IMcduReader>(p => p.GetRequiredService<Mcdu.McduReader>());
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Mcdu.McduReader>());
        services.AddSingleton<Mcdu.IMcduActuator, Mcdu.McduActuator>();
        services.AddSingleton<Mcdu.McduRadNavTuner>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Mcdu.McduRadNavTuner>());
        services.AddSingleton<Mcdu.McduArrivalChanger>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Mcdu.McduArrivalChanger>());
        services.AddSingleton<Briefings.DfdNavDataProvider>();
        // ICAO → spoken airport name (issue #70): curated short names + the DFD's
        // airport_name column. Optional everywhere it is consumed, so a missing DFD just
        // means spelled ICAOs again.
        services.AddSingleton<Briefings.DfdAirportNames>();
        services.AddSingleton<Core.Airports.IAirportNames>(
            p => p.GetRequiredService<Briefings.DfdAirportNames>());
        services.AddSingleton<Briefings.ProcedureSource>();
        services.AddSingleton<Briefings.MinimaCaptureDialogue>();
        services.AddSingleton<Briefings.MissedApproachRebrief>();
        services.AddSingleton<Briefings.BriefingService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Briefings.BriefingService>());
        // The missed-approach voice phrases dispatch through their own small feature so the
        // re-brief gate logic stays out of BriefingService.
        services.AddSingleton<IVoiceFeature, Briefings.MissedApproachVoiceFeature>();
        // Minima recall query — after BriefingService so full-briefing phrases keep precedence.
        services.AddSingleton<IVoiceFeature, Briefings.MinimaQueryVoiceFeature>();
        services.AddSingleton<Company.CompanyChannelService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Company.CompanyChannelService>());
        services.AddSingleton<Company.ICompanyChannel>(p => p.GetRequiredService<Company.CompanyChannelService>());
        // Company day mode: voice start/end, leg tracking (finalizer Order 40), turnaround +
        // end-of-day summaries. Off by default (day.enabled).
        services.AddSingleton<Day.CompanyDayService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Day.CompanyDayService>());
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<Day.CompanyDayService>());
        services.AddSingleton<Core.Day.IDayControl>(p => p.GetRequiredService<Day.CompanyDayService>());
        services.AddHostedService<Day.DayBootstrapService>();
        services.AddSingleton<SayIntentions.SayIntentionsService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<SayIntentions.SayIntentionsService>());
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
        services.AddSingleton<Cabin.CabinCrewService>();
        // Prosim2GSX-parity cabin dings (startup / final loadsheet) — plain chime playback,
        // deliberately outside the speech arbiter.
        services.AddHostedService<Cabin.CabinDingService>();
        // GSX voice control ("commence ground services", "request boarding", …) — dispatches
        // through the named-command registry so voice/web/API/StreamDeck share one seam.
        services.AddSingleton<Gsx.GsxVoiceService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Gsx.GsxVoiceService>());
        // Interphone hail dialogues ("cockpit to ground" → "go ahead, captain" → request) and
        // ground-crew upcalls on INT (ADR-0006 / issue #51). The hail feature registers AFTER
        // GsxVoiceService so single-shot phrases keep their precedence.
        services.AddSingleton<Crew.CrewHailService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Crew.CrewHailService>());
        services.AddSingleton<Crew.GroundCrewUpcallService>();
        // Accent localization (issue #53): airport-country → per-provider ground-crew voice,
        // consumed by the arbiter at render time. Registered BEFORE the arbiter resolves.
        services.AddSingleton<Crew.AccentVoiceResolver>();
        // Post-flight voice: tech-log brief + spoken debrief (deterministic template). Exact-
        // match phrases, so last in the dispatch order is fine. The debrief doubles as the
        // first session-finalization step (Order 10).
        services.AddSingleton<TechLog.TechLogVoiceService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TechLog.TechLogVoiceService>());
        // Logbook spoken queries — exact/prefix matcher, no phrase overlap with the tech log.
        services.AddSingleton<IVoiceFeature, Logbook.LogbookVoiceService>();
        // Guided raise/rectify dialogues + the post-abnormal shutdown offer (finalizer Order 40).
        services.AddSingleton<TechLog.TechLogDialogueService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TechLog.TechLogDialogueService>());
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<TechLog.TechLogDialogueService>());
        services.AddSingleton<Debrief.DebriefService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Debrief.DebriefService>());
        services.AddSingleton<Core.Sessions.ISessionFinalizationStep>(
            p => p.GetRequiredService<Debrief.DebriefService>());
        services.AddHostedService<PostFlightVoiceBootstrapService>();
        // "Quiet please" latch + its Low-band suppression rule (consumed by the arbiter);
        // the small-talk feature itself dispatches last — exact phrases only.
        services.AddSingleton<Persona.QuietState>();
        services.AddSingleton<IVoiceFeature, Persona.SmallTalkService>();
        // FO persona: phrase bank (phrases.json in the USER config tree, ADR-0007), the
        // persona itself (prompt fragment + ack variation) and the LLM restyle path for
        // advisories.
        services.AddSingleton(p => new Persona.PhraseBank(
            Core.Configuration.UserConfigPaths.Root,
            p.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Persona.PhraseBank>>()));
        services.AddSingleton<Persona.PersonaService>();
        services.AddSingleton<Persona.StyledSpeechService>();
        // ONE shared LLM client (issue #66): registering it lets every consumer's optional
        // `OpenAiChatClient? llm = null` parameter resolve to the same health-reporting
        // instance instead of each service newing up a silent private copy. The probe keeps
        // re-testing an unhealthy endpoint every five minutes so recovery is detected.
        services.AddSingleton<Llm.OpenAiChatClient>();
        services.AddHostedService<Llm.LlmHealthProbeService>();
        services.AddSingleton<SpokenChecklistEngine>();
        services.AddHostedService<SpeechBootstrapService>();

        return services;
    }
}
