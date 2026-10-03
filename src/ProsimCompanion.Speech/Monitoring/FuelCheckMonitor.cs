using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The cruise fuel check (issue #148): every <see cref="FuelCheckOptions.IntervalMinutes"/>
/// in the cruise, and on "fuel check" at any time, <see cref="FuelCheckCore"/> compares the
/// live fuel with the SimBrief plan at the aircraft's position (the fallback is flow × time
/// to the ETA). Arms on Flight live; the periodic check stays silent in sterile phases and
/// outside the cruise; a shortfall beyond the margin is spoken at High. Advisory only.
/// The timing shell is thin: <see cref="ProcessTick"/> takes its clock so tests can step it.
/// </summary>
public sealed class FuelCheckMonitor : Core.Hosting.IStartupModule, IVoiceFeature, IFuelCheckRequests, IDisposable
{
    private static readonly string[] CheckPhrases = ["fuel check", "fuel check please", "check the fuel", "how is the fuel"];
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromSeconds(45);

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IFlightPhaseSource _flight;
    private readonly FlightProgressStore _progress;
    private readonly OfpStore _ofp;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FuelCheckMonitor> _logger;
    private readonly FuelCheckLogStore? _checkLog;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<double> _flow1;
    private readonly IDataRefSubscription<double> _flow2;
    private readonly object _gate = new();

    private Timer? _timer;
    private int _ticking;
    private DateTimeOffset? _cruiseSince;
    private DateTimeOffset? _lastPeriodic;
    private FlightPhase _lastPhase = FlightPhase.Unknown;

    public FuelCheckMonitor(
        IOptionsMonitor<SopOptions> sop,
        IOptionsMonitor<SpeechOptions> speech,
        IFlightPhaseSource flight,
        FlightProgressStore progress,
        OfpStore ofp,
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FuelCheckMonitor> logger,
        FuelCheckLogStore? checkLog = null)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _sop = sop;
        _speech = speech;
        _flight = flight;
        _progress = progress;
        _ofp = ofp;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _checkLog = checkLog;
        _fuelTotal = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
        _flow1 = dataRefs.Subscribe(ProsimDataRefNames.Engine1FuelFlowKgh);
        _flow2 = dataRefs.Subscribe(ProsimDataRefNames.Engine2FuelFlowKgh);
    }

    private FuelCheckOptions Options => _sop.CurrentValue.Monitoring.FuelCheck;

    public bool Enabled => Options.Enabled;

    public IEnumerable<string> Phrases => CheckPhrases;

    public bool ValueParse => false;

    public void Start() => _timer = new Timer(_ => Tick(), null, Poll, Poll);

    public void Dispose()
    {
        _timer?.Dispose();
        _fuelTotal.Dispose();
        _flow1.Dispose();
        _flow2.Dispose();
    }

    public bool TryHandle(string utterance)
    {
        if (!CheckPhrases.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        if (!_flight.IsLive)
        {
            _ = _arbiter.SpeakAsync("No flight data for a fuel check.", SpeechPriority.Normal);
            return true;
        }

        var result = Compute(DateTimeOffset.UtcNow);
        if (result is null)
        {
            _ = _arbiter.SpeakAsync("I don't have the fuel plan for a fuel check.", SpeechPriority.Normal);
            _eventLog.Record("fuel.check", new { trigger = "voice", spoken = false, reason = "no plan or no fuel figure" });
            return true;
        }

        Speak(result, "voice");
        return true;
    }

    /// <summary>The Fuel Log page's button (issue #154): the voice path without the phrase.</summary>
    public string? RequestNow(string source)
    {
        if (!_flight.IsLive)
        {
            return "No flight data for a fuel check.";
        }

        var result = Compute(DateTimeOffset.UtcNow);
        if (result is null)
        {
            _eventLog.Record("fuel.check", new { trigger = source, spoken = false, reason = "no plan or no fuel figure" });
            return "No fuel plan loaded — fetch an OFP on the INIT page.";
        }

        Speak(result, source);
        return null;
    }

    /// <summary>The periodic rule, clock supplied: in the cruise, the first check comes one
    /// interval after the cruise began and every interval after that; silent when not live,
    /// disabled, outside the cruise, sterile (never in the cruise, but the rule is explicit),
    /// or with nothing to compare against. Returns the result it spoke, for tests.</summary>
    public FuelCheckResult? ProcessTick(DateTimeOffset nowUtc)
    {
        lock (_gate)
        {
            var phase = _flight.CurrentPhase;
            if (phase != _lastPhase)
            {
                _lastPhase = phase;
                if (phase == FlightPhase.Cruise)
                {
                    _cruiseSince = nowUtc;
                    _lastPeriodic = null;
                }
                else if (phase is not (FlightPhase.Climb or FlightPhase.Cruise))
                {
                    _cruiseSince = null;
                }
            }

            var options = Options;
            if (!options.Enabled || options.IntervalMinutes <= 0 || !_flight.IsLive
                || phase != FlightPhase.Cruise || _cruiseSince is not { } since)
            {
                return null;
            }

            var snapshot = _flight.Snapshot().Data;
            var context = new SpeechContext(phase, snapshot?.AltitudeFt ?? 0, snapshot?.IsValid ?? false);
            if (SterileCockpitRule.IsSterile(_speech.CurrentValue, context))
            {
                return null;
            }

            var anchor = _lastPeriodic ?? since;
            if (nowUtc - anchor < TimeSpan.FromMinutes(options.IntervalMinutes))
            {
                return null;
            }

            _lastPeriodic = nowUtc;
            var result = Compute(nowUtc);
            if (result is null)
            {
                _eventLog.Record("fuel.check", new { trigger = "periodic", spoken = false, reason = "no plan or no fuel figure" });
                return null;
            }

            Speak(result, "periodic");
            return result;
        }
    }

    private FuelCheckResult? Compute(DateTimeOffset nowUtc)
    {
        var ofp = _ofp.Current;
        var progress = _progress.Snapshot();
        var fob = _fuelTotal.RawValue is null || _fuelTotal.IsStale ? (double?)null : _fuelTotal.Value;
        var flow = Math.Max(0, _flow1.Value) + Math.Max(0, _flow2.Value);
        return FuelCheckCore.Compute(
            new FuelCheckInputs(
                fob, flow, progress.Position, ofp?.Navlog ?? [], ofp?.FuelPlanLandingKg ?? 0, nowUtc, progress.EtaUtc),
            Options);
    }

    private void Speak(FuelCheckResult result, string trigger)
    {
        _logger.LogInformation(
            "Fuel check ({Trigger}, {Method}): FOB {FobKg:F0} kg, planned here {PlannedKg}, difference {DifferenceKg}, "
            + "estimated landing {EstimatedKg} vs planned {PlannedLandingKg} — {Priority}",
            trigger, result.Method, result.FuelOnBoardKg, result.PlannedFuelOnBoardKg, result.DifferenceKg,
            result.EstimatedLandingKg, result.PlannedLandingKg, result.Priority);
        _eventLog.Record("fuel.check", new
        {
            trigger,
            spoken = true,
            method = result.Method,
            fix = result.Fix,
            fobKg = Math.Round(result.FuelOnBoardKg),
            plannedFobKg = Round(result.PlannedFuelOnBoardKg),
            differenceKg = Round(result.DifferenceKg),
            estimatedLandingKg = Round(result.EstimatedLandingKg),
            plannedLandingKg = Round(result.PlannedLandingKg),
            shortfall = result.Shortfall,
            priority = result.Priority.ToString().ToLowerInvariant(),
            text = result.Text,
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(result.Text, result.Priority, SpokenTtl, Tag: "fuel.check"));
        _checkLog?.Add(new FuelCheckRecord(
            DateTimeOffset.UtcNow, trigger, result.Method, result.Fix, result.FuelOnBoardKg, result.PlannedFuelOnBoardKg,
            result.DifferenceKg, result.EstimatedLandingKg, result.PlannedLandingKg, result.Shortfall, result.Text));
    }

    private static double? Round(double? value) => value is { } v ? Math.Round(v) : null;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick(DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Fuel check tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
