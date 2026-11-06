using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Crew;

/// <summary>
/// The pushback crew on the interphone (owner request 2026-11-07, plan V2 — "one stop shop":
/// our engine voices the ramp agent, so SayIntentions' ramp crew and GSX's own crew audio can
/// be switched off on the rig). The shell around <see cref="PushbackCrewCore"/>: one line per
/// <see cref="PushbackStage"/> from <see cref="GroundOpsSignals.PushbackProgress"/> — bridge
/// off and doors checked, tug connected with the brakes-release request, the clearance to push
/// with this flight's direction (from the <see cref="PushbackChoiceStore"/>), clear to start
/// engines, push complete, good start confirmed, pin out and clear — and the two captain's
/// phrases it answers: "brakes released" (while the question stands) and "ready for pushback".
/// Delivery is the upcalls' shape: <see cref="InterphoneRing"/> (MECH press only on the lines
/// that open a call), INT latch or grace, the GroundCrew role (voice, accent, intercom filter).
/// Lines are queued on one lane so they come in order; each is recorded as
/// <c>ground.pushback.crew</c>. Read-and-speak only beyond the allow-listed MECH press.
/// </summary>
public sealed class PushbackCrewService : Core.Hosting.IStartupModule, IVoiceFeature, IDisposable
{
    public const string EventName = "ground.pushback.crew";

    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly IOptionsMonitor<GroundCrewOptions> _options;
    private readonly PushbackChoiceStore _pushback;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<PushbackCrewService> _logger;
    private readonly IAcpChannel? _acp;
    private readonly IProsimDataRefs? _dataRefs;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private PushbackCrewCore.CrewState _state = PushbackCrewCore.CrewState.Initial;

    public PushbackCrewService(
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        IOptionsMonitor<GroundCrewOptions> options,
        PushbackChoiceStore pushback,
        JsonlEventLog eventLog,
        ILogger<PushbackCrewService> logger,
        IAcpChannel? acp = null,
        IProsimDataRefs? dataRefs = null)
    {
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(pushback);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _signals = signals;
        _arbiter = arbiter;
        _options = options;
        _pushback = pushback;
        _eventLog = eventLog;
        _logger = logger;
        _acp = acp;
        _dataRefs = dataRefs;
    }

    /// <summary>Roster declaration: between the GSX requests and the pushback direction — the
    /// captain's "brakes released" and "ready for pushback" are ground words.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 215,
        Gate = VoiceFeatureGate.ByOption("groundCrew.pushbackCrew.enabled", CrewHailVoiceReference.OffGroundCrew),
        Description = new DescribedVoiceReference("pushbackCrew", "voiceReference.group.pushbackCrew.title", VoiceReferenceTab.Ground, VoiceSpeaker.GroundCrew,
        [
            new(PushbackCrewCore.ReadyPhrases, "voiceReference.group.pushbackCrew.ready", "Copied — stand by for the tug.", [VoiceBadges.Interphone]),
            new(PushbackCrewCore.BrakesReleasedPhrases, "voiceReference.group.pushbackCrew.brakes", "Brakes released, copied. Cleared to push, tail left. Clear to start engines.", [VoiceBadges.Interphone]),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled
    {
        get
        {
            var options = _options.CurrentValue;
            return options.Enabled && options.PushbackCrew.Enabled;
        }
    }

    public IEnumerable<string> Phrases => PushbackCrewCore.ReadyPhrases.Concat(PushbackCrewCore.BrakesReleasedPhrases);

    public bool ValueParse => false;

    public void Start()
    {
        _signals.PushbackProgress += OnStage;
        _signals.FlightCycleReset += OnFlightCycleReset;
    }

    public void Dispose()
    {
        _signals.PushbackProgress -= OnStage;
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _shutdown.Cancel();
        _shutdown.Dispose();
        _lane.Dispose();
    }

    public bool TryHandle(string utterance)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(utterance))
        {
            return false;
        }

        var text = utterance.Trim();
        var options = _options.CurrentValue.PushbackCrew;
        if (PushbackCrewCore.ReadyPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            Speak(PushbackCrewCore.OnReady(options), options, heard: text);
            return true;
        }

        if (!PushbackCrewCore.BrakesReleasedPhrases.Any(p => string.Equals(p, text, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        PushbackCrewCore.CrewLine? line;
        lock (_gate)
        {
            // Nobody asked: the words belong to no one, the router says so ("released" on its
            // own especially must never be swallowed here).
            if (!_state.BrakesQuestionOpen)
            {
                return false;
            }

            (_state, line) = PushbackCrewCore.OnBrakesReleased(_state, options, Direction());
        }

        Speak(line, options, heard: text);
        return true;
    }

    // Arrival and new-plan resets alike: the push is per leg.
    private void OnFlightCycleReset(FlightCycleResetKind _)
    {
        lock (_gate)
        {
            _state = PushbackCrewCore.CrewState.Initial;
        }
    }

    private void OnStage(PushbackStage stage)
    {
        try
        {
            if (!Enabled)
            {
                return;
            }

            var options = _options.CurrentValue.PushbackCrew;
            PushbackCrewCore.CrewLine? line;
            lock (_gate)
            {
                (_state, line) = PushbackCrewCore.OnStage(_state, stage, options, Direction());
                if (stage == PushbackStage.Clear)
                {
                    // The push is over: a second push on the same leg (a tug called back after
                    // an interrupted one) starts the script again.
                    _state = PushbackCrewCore.CrewState.Initial with { ClearSpoken = false };
                }
            }

            Speak(line, options, heard: null, stage);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pushback crew stage {Stage} failed", stage);
        }
    }

    private string? Direction()
    {
        try
        {
            return _pushback.Snapshot().Effective?.Spoken;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pushback crew: direction unavailable");
            return null;
        }
    }

    private void Speak(PushbackCrewCore.CrewLine? line, PushbackCrewOptions options, string? heard, PushbackStage? stage = null)
    {
        if (line is null)
        {
            return;
        }

        var ground = _options.CurrentValue;
        _ = SpeakAsync(line, ground, heard, stage);
    }

    private async Task SpeakAsync(PushbackCrewCore.CrewLine line, GroundCrewOptions ground, string? heard, PushbackStage? stage)
    {
        try
        {
            await _lane.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await InterphoneRing.RingAsync(
                        _dataRefs, _acp, ground.MechCall && line.OpensCall, ground.RequireIntChannel, ground.IntChannelGraceSeconds,
                        _logger, "pushback crew", _shutdown.Token)
                    .ConfigureAwait(false);

                var tag = $"ground.pushback.{line.Tag}";
                await _arbiter.EnqueueAsync(new SpeechRequest(
                    line.Text, SpeechPriority.Normal, Ttl: TimeSpan.FromSeconds(90), Tag: tag,
                    Role: SpeechRole.GroundCrew), _shutdown.Token).ConfigureAwait(false);
                _eventLog.Record(EventName, new { line = line.Tag, stage = stage?.ToString(), heard, text = line.Text });
            }
            finally
            {
                _lane.Release();
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown — fine
        }
        catch (ObjectDisposedException)
        {
            // shutdown raced the lane — fine
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Pushback crew line {Line} failed", line.Tag);
        }
    }
}
