using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Callouts;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// Step-climb advice (issue #189): the planned steps come from the navlog's altitude per fix
/// (<see cref="StepClimbPlan"/>, pure); in the cruise, within <c>speech.stepClimbLeadNm</c> of the
/// next step with the aircraft still below the planned level, the FO says once "planned step to
/// flight level … at {fix}, captain" and — with SayIntentions on — offers "say request climb". "When
/// is our next step?" is answered any time. The <see cref="StepClimbStore"/> carries the next level
/// for the request template's <c>{stepLevel}</c>. Read-and-speak only.
/// </summary>
public sealed class StepClimbAdvisor : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    private static readonly string[] QueryPhrases = ["when is our next step", "next step", "when is the step climb", "next step climb", "when do we step"];
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IOptionsMonitor<SayIntentionsOptions> _sayIntentions;
    private readonly IFlightPhaseSource _flight;
    private readonly IFlightDataSource _source;
    private readonly FlightProgressStore _progress;
    private readonly OfpStore _ofp;
    private readonly StepClimbStore _store;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<StepClimbAdvisor> _logger;
    private readonly object _gate = new();
    private readonly HashSet<int> _advised = [];
    private IReadOnlyList<StepPoint> _steps = [];
    private OfpData? _plannedFor;
    private Timer? _timer;
    private int _ticking;

    public StepClimbAdvisor(
        IOptionsMonitor<SpeechOptions> speech,
        IOptionsMonitor<SayIntentionsOptions> sayIntentions,
        IFlightPhaseSource flight,
        IFlightDataSource source,
        FlightProgressStore progress,
        OfpStore ofp,
        StepClimbStore store,
        GroundOpsSignals signals,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<StepClimbAdvisor> logger)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(sayIntentions);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _speech = speech;
        _sayIntentions = sayIntentions;
        _flight = flight;
        _source = source;
        _progress = progress;
        _ofp = ofp;
        _store = store;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
    }

    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 185,
        Gate = VoiceFeatureGate.ByOption("speech.stepClimbAdvice", VoiceReferenceTextIds.OffMonitoring),
        Description = new DescribedVoiceReference("stepClimb", "voiceReference.group.stepClimb.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["when is our next step", "next step climb"], "voiceReference.group.stepClimb.next", "Next step: flight level three seven zero at TOPLA, forty miles ahead."),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _speech.CurrentValue.StepClimbAdvice;

    public IEnumerable<string> Phrases => QueryPhrases;

    public bool ValueParse => false;

    /// <summary>Only ever speaks.</summary>
    public bool AcknowledgeOnly => true;

    public void Start()
    {
        _signals.FlightCycleReset += OnReset;
        _flight.SessionReset += OnReset;
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(5), Poll);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _signals.FlightCycleReset -= OnReset;
        _flight.SessionReset -= OnReset;
    }

    private void OnReset()
    {
        lock (_gate)
        {
            _advised.Clear();
        }

        _store.Update(_ => StepClimbSnapshot.Empty);
    }

    public bool TryHandle(string utterance)
    {
        var text = CommandMatcher.Normalize(utterance);
        if (!QueryPhrases.Any(p => text.Contains(p, StringComparison.Ordinal)))
        {
            return false;
        }

        var answer = Answer(DateTimeOffset.UtcNow);
        _eventLog.Record("step-climb.query", new { text = answer });
        _ = _arbiter.SpeakAsync(answer, SpeechPriority.Normal);
        return true;
    }

    /// <summary>The answer to "when is our next step?" — public for tests.</summary>
    public string Answer(DateTimeOffset nowUtc)
    {
        var (steps, navlog) = Plan();
        if (navlog.Count == 0)
        {
            return "No navlog loaded — I can't see the planned steps.";
        }

        var data = _source.Sample();
        var next = StepClimbPlan.Next(steps, navlog, _progress.Snapshot().Position, data.IsValid ? data.AltitudeFt : 0);
        if (next is null)
        {
            return steps.Count == 0 ? "No step climb planned — the plan holds one level." : "No further step planned.";
        }

        var distance = Distance(next);
        var where = distance is { } d ? $", {Aviation.ToDigits(((int)Math.Round(d)).ToString(CultureInfo.InvariantCulture))} miles ahead" : "";
        return $"Next step: flight level {Aviation.ToDigits(next.ToFlightLevel.ToString(CultureInfo.InvariantCulture))} at {Core.Speech.NatoPhonetics.SpeakIdentifier(next.Ident)}{where}.";
    }

    /// <summary>One evaluation — public for tests. Returns the step advised on this tick, if any.</summary>
    public StepPoint? Process(DateTimeOffset nowUtc)
    {
        if (!_speech.CurrentValue.StepClimbAdvice || !_flight.IsLive)
        {
            return null;
        }

        var (steps, navlog) = Plan();
        var data = _source.Sample();
        if (navlog.Count == 0 || !data.IsValid)
        {
            return null;
        }

        var position = _progress.Snapshot().Position;
        var next = StepClimbPlan.Next(steps, navlog, position, data.AltitudeFt);
        var distance = next is null ? null : Distance(next);
        bool advised;
        lock (_gate)
        {
            advised = next is not null && _advised.Contains(next.FixIndex);
        }

        _store.Update(_ => new StepClimbSnapshot(next?.ToFlightLevel, next?.Ident, distance is { } dd ? Math.Round(dd, 1) : null, advised));

        if (next is null || distance is not { } nm || _flight.CurrentPhase != FlightPhase.Cruise || advised
            || nm > _speech.CurrentValue.StepClimbLeadNm || data.AltitudeFt >= next.ToFt - StepClimbPlan.MinStepFt / 2)
        {
            return null;
        }

        lock (_gate)
        {
            _advised.Add(next.FixIndex);
        }

        var level = Aviation.ToDigits(next.ToFlightLevel.ToString(CultureInfo.InvariantCulture));
        var text = $"Planned step to flight level {level} at {Core.Speech.NatoPhonetics.SpeakIdentifier(next.Ident)}, captain.";
        if (_sayIntentions.CurrentValue.Enabled)
        {
            text += " Say request climb when you want it.";
        }

        _logger.LogInformation("Step-climb advice: FL{Level} at {Fix}, {Distance:F0} nm ahead, now at {Altitude:F0} ft", next.ToFlightLevel, next.Ident, nm, data.AltitudeFt);
        _eventLog.Record("step-climb.advice", new { fix = next.Ident, toFl = next.ToFlightLevel, fromFt = next.FromFt, distanceNm = Math.Round(nm, 1), altitudeFt = Math.Round(data.AltitudeFt), offeredRequest = _sayIntentions.CurrentValue.Enabled });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(text, SpeechPriority.Normal, TimeSpan.FromSeconds(45), Tag: "step-climb"));
        return next;
    }

    private (IReadOnlyList<StepPoint> Steps, IReadOnlyList<OfpFix> Navlog) Plan()
    {
        var ofp = _ofp.Current;
        lock (_gate)
        {
            if (!ReferenceEquals(ofp, _plannedFor))
            {
                _plannedFor = ofp;
                _steps = ofp is null ? [] : StepClimbPlan.Derive(ofp.Navlog);
                _advised.Clear();
            }

            return (_steps, ofp?.Navlog ?? []);
        }
    }

    private double? Distance(StepPoint step)
        => _progress.Snapshot().Position is { } p ? GreatCircle.DistanceNm(p, step.Position) : null;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            Process(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Step-climb advisor tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
