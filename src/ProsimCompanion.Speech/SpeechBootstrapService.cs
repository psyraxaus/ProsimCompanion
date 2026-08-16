using Microsoft.Extensions.Hosting;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Checklists;
using ProsimCompanion.Speech.Monitoring;
using ProsimCompanion.Speech.Recognition;
using ProsimCompanion.Speech.Tts;

namespace ProsimCompanion.Speech;

/// <summary>
/// Activates the speech pillar at startup (resolving the arbiter starts its pump; the callouts
/// engine and stabilized-approach monitor start their sampling timers here) and silences it
/// early at host shutdown — the arbiter's Dispose is idempotent, so the container disposing it
/// again afterwards is harmless.
/// </summary>
public sealed class SpeechBootstrapService : IHostedService
{
    private readonly SpeechArbiterService _arbiter;
    private readonly CalloutsEngine _callouts;
    private readonly StabilizedApproachMonitor _stabilized;
    private readonly FlowMonitor _flow;
    private readonly TtsPrewarmService _prewarm;
    private readonly PushToTalkService _ptt;
    private readonly SpokenChecklistEngine _spokenChecklists;
    private readonly Abnormals.FailureMonitor _failures;
    private readonly SayIntentions.SayIntentionsService _sayIntentions;
    private readonly Cabin.CabinCrewService _cabin;
    private readonly Crew.GroundCrewUpcallService _groundCrew;
    private readonly Crew.AircraftStateAdvisoryService _aircraftStateAdvisory;
    private readonly Company.CompanyChannelService _company;
    private readonly Briefings.MissedApproachRebrief _missedApproach;
    private readonly Microsoft.Extensions.Options.IOptionsMonitor<Core.Configuration.BriefingOptions> _briefingOptions;
    private readonly Microsoft.Extensions.Logging.ILogger<SpeechBootstrapService> _logger;

    public SpeechBootstrapService(
        SpeechArbiterService arbiter,
        CalloutsEngine callouts,
        StabilizedApproachMonitor stabilized,
        FlowMonitor flow,
        TtsPrewarmService prewarm,
        PushToTalkService ptt,
        SpokenChecklistEngine spokenChecklists,
        Abnormals.FailureMonitor failures,
        SayIntentions.SayIntentionsService sayIntentions,
        Cabin.CabinCrewService cabin,
        Crew.GroundCrewUpcallService groundCrew,
        Crew.AircraftStateAdvisoryService aircraftStateAdvisory,
        Company.CompanyChannelService company,
        Briefings.MissedApproachRebrief missedApproach,
        Microsoft.Extensions.Options.IOptionsMonitor<Core.Configuration.BriefingOptions> briefingOptions,
        Microsoft.Extensions.Logging.ILogger<SpeechBootstrapService> logger)
    {
        ArgumentNullException.ThrowIfNull(missedApproach);
        ArgumentNullException.ThrowIfNull(briefingOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _missedApproach = missedApproach;
        _briefingOptions = briefingOptions;
        _logger = logger;
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(sayIntentions);
        ArgumentNullException.ThrowIfNull(cabin);
        ArgumentNullException.ThrowIfNull(groundCrew);
        ArgumentNullException.ThrowIfNull(aircraftStateAdvisory);
        ArgumentNullException.ThrowIfNull(company);
        _failures = failures;
        _sayIntentions = sayIntentions;
        _cabin = cabin;
        _groundCrew = groundCrew;
        _aircraftStateAdvisory = aircraftStateAdvisory;
        _company = company;
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(callouts);
        ArgumentNullException.ThrowIfNull(stabilized);
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(prewarm);
        ArgumentNullException.ThrowIfNull(ptt);
        ArgumentNullException.ThrowIfNull(spokenChecklists);

        _arbiter = arbiter;
        _callouts = callouts;
        _stabilized = stabilized;
        _flow = flow;
        _prewarm = prewarm;
        _ptt = ptt;
        _spokenChecklists = spokenChecklists;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _callouts.Start();
        _stabilized.Start();
        _flow.Start();
        _prewarm.Start();
        _ptt.Start();
        _failures.Start();
        _sayIntentions.Start();
        _cabin.Start();
        _groundCrew.Start();
        _aircraftStateAdvisory.Start();
        _company.Start();
        _spokenChecklists.Start();
        _missedApproach.Start();

        // Wake the LLM host at startup (fire-and-forget; readiness is confirmed by actually
        // reaching the endpoint, never by the send).
        var wol = _briefingOptions.CurrentValue.LlmWakeOnLan;
        if (wol.Enabled)
        {
            Llm.WakeOnLan.Send(wol.MacAddress, wol.BroadcastAddress, wol.Port, _logger);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _spokenChecklists.Dispose();
        _company.Dispose();
        _aircraftStateAdvisory.Dispose();
        _groundCrew.Dispose();
        _cabin.Dispose();
        _sayIntentions.Dispose();
        _failures.Dispose();
        _ptt.Dispose();
        _prewarm.Dispose();
        _callouts.Dispose();
        _stabilized.Dispose();
        _flow.Dispose();
        _arbiter.Dispose();
        return Task.CompletedTask;
    }
}
