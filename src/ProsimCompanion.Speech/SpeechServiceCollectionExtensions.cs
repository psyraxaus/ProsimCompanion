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
        // Voice features — registration order is dispatch precedence (roles first so a
        // handover is never mis-parsed as an instruction).
        services.AddSingleton<Roles.RoleManager>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Roles.RoleManager>());
        services.AddSingleton<Radios.RadioExecutor>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Radios.RadioExecutor>());
        services.AddSingleton<Fcu.FcuExecutor>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Fcu.FcuExecutor>());
        services.AddSingleton<Briefings.DfdNavDataProvider>();
        services.AddSingleton<Briefings.BriefingService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Briefings.BriefingService>());
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
        // GSX voice control ("cockpit to ground", "request boarding", …) — dispatches through
        // the named-command registry so voice/web/API/StreamDeck share one seam.
        services.AddSingleton<Gsx.GsxVoiceService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<Gsx.GsxVoiceService>());
        // Post-flight voice: tech-log brief + spoken debrief (deterministic template). Exact-
        // match phrases, so last in the dispatch order is fine. The debrief doubles as the
        // first session-finalization step (Order 10).
        services.AddSingleton<TechLog.TechLogVoiceService>();
        services.AddSingleton<IVoiceFeature>(p => p.GetRequiredService<TechLog.TechLogVoiceService>());
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
        services.AddSingleton<SpokenChecklistEngine>();
        services.AddHostedService<SpeechBootstrapService>();

        return services;
    }
}
