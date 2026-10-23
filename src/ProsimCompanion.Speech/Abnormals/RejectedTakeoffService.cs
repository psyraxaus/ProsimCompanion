using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Abnormals;

/// <summary>
/// The rejected takeoff (issue #187): a 100 ms shell over <see cref="RejectedTakeoffCore"/>
/// and the voice feature for the captain's "stop" / "reject". Below 100 kt the word alone
/// starts the PM calls; above it the FO reads "stop" back first. After the airborne edge the FO
/// refuses — "we're flying, captain". When stopped the FO says "aircraft stopped, park brake"
/// and offers the open ECAM (or "no ECAM — your assessment, captain"). The departure cycle is
/// left alone: GSX stays as it is and a second roll re-arms. Read-and-speak only; the
/// <c>takeoff.rejected</c> event is what the logbook counts.
/// </summary>
public sealed class RejectedTakeoffService : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    public const string ReadbackText = "Stop!";
    public const string RefusedText = "We're flying, captain.";
    public const string ReverseGreenText = "Reverse green.";
    public const string DecelText = "Decel.";
    public const string SeventyKnotsText = "Seventy knots.";
    public const string StoppedText = "Aircraft stopped — park brake.";
    public const string NoEcamText = "No ECAM — your assessment, captain.";

    private static readonly string[] StopPhrases = ["stop", "stop stop stop", "stop stop", "reject", "rejecting", "rejected takeoff", "abort"];
    private static readonly HashSet<string> NormalizedStop = [.. StopPhrases.Select(CommandMatcher.Normalize)];

    private readonly IOptionsMonitor<AbnormalsOptions> _options;
    private readonly IFlightDataSource _source;
    private readonly IFlightPhaseSource _flight;
    private readonly FailureMonitor _failures;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<RejectedTakeoffService> _logger;
    private readonly IDataRefSubscription<double> _lever1;
    private readonly IDataRefSubscription<double> _lever2;
    private readonly RejectedTakeoffCore _core = new();
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;

    public RejectedTakeoffService(
        IOptionsMonitor<AbnormalsOptions> options,
        IFlightDataSource source,
        IFlightPhaseSource flight,
        IProsimDataRefs dataRefs,
        FailureMonitor failures,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<RejectedTakeoffService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(failures);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _source = source;
        _flight = flight;
        _failures = failures;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _lever1 = dataRefs.Subscribe(new DataRef<double>(ProsimDataRefNames.Throttle1Lever, DataRefTier.Frequent, double.NaN));
        _lever2 = dataRefs.Subscribe(new DataRef<double>(ProsimDataRefNames.Throttle2Lever, DataRefTier.Frequent, double.NaN));
    }

    /// <summary>Roster declaration: the captain's stop word, first in the dispatch order — a
    /// "stop" on the roll must beat every other reading of the word.</summary>
    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 5,
        Gate = VoiceFeatureGate.ByOption("abnormals.rtoEnabled", "voiceReference.off.abnormals"),
        Description = new DescribedVoiceReference("rto", "voiceReference.group.rto.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["stop", "reject"], "voiceReference.group.rto.stop", ReverseGreenText + " " + DecelText, [VoiceBadges.Phase]),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _options.CurrentValue.RtoEnabled;

    public IEnumerable<string> Phrases => StopPhrases;

    public bool ValueParse => false;

    /// <summary>Only ever speaks: the router may act on a gray-band "stop" without the
    /// clarifier — on the roll there is no time for "did you mean".</summary>
    public bool AcknowledgeOnly => true;

    public RtoState State
    {
        get
        {
            lock (_gate)
            {
                return _core.State;
            }
        }
    }

    public void Start()
    {
        _flight.SessionReset += OnSessionReset;
        _timer = new Timer(_ => Tick(), null, 1000, 100);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _flight.SessionReset -= OnSessionReset;
        _lever1.Dispose();
        _lever2.Dispose();
    }

    private void OnSessionReset()
    {
        lock (_gate)
        {
            _core.Reset();
        }
    }

    public bool TryHandle(string utterance)
    {
        if (!NormalizedStop.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        return HandleStopWord(Sample(DateTimeOffset.UtcNow));
    }

    /// <summary>The stop word against one sample — public for tests.</summary>
    public bool HandleStopWord(RtoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        StopVerdict verdict;
        lock (_gate)
        {
            verdict = _core.OnStopWord(sample);
        }

        switch (verdict)
        {
            case StopVerdict.Ignored:
                return false;

            case StopVerdict.RefusedAirborne:
                _eventLog.Record("takeoff.stop-refused", new { phase = sample.Phase.ToString(), iasKt = Math.Round(sample.IasKt) });
                _logger.LogInformation("\"stop\" after the airborne edge refused ({Phase}, {Ias:F0} kt)", sample.Phase, sample.IasKt);
                Speak(RefusedText, SpeechPriority.Critical, "rto.refused");
                return true;

            case StopVerdict.AlreadyRejecting:
                return true;

            case StopVerdict.RejectedWithReadback:
                Speak(ReadbackText, SpeechPriority.Critical, "rto.readback");
                RecordRejected(sample, confirmed: true);
                return true;

            default:
                RecordRejected(sample, confirmed: false);
                return true;
        }
    }

    /// <summary>One sample through the core — public for tests.</summary>
    public void Process(RtoSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        IReadOnlyList<RtoCallout> due;
        var wasRejecting = false;
        lock (_gate)
        {
            wasRejecting = _core.State == RtoState.Rejecting;
            due = _core.Process(sample);
            if (!wasRejecting && _core.State == RtoState.Rejecting)
            {
                // The levers rejected it (no word) — the record is the same.
                RecordRejected(sample, confirmed: false);
            }
        }

        foreach (var callout in due)
        {
            switch (callout)
            {
                case RtoCallout.ReverseGreen:
                    Callout("reverseGreen", ReverseGreenText);
                    break;
                case RtoCallout.Decel:
                    Callout("decel", DecelText);
                    break;
                case RtoCallout.SeventyKnots:
                    Callout("seventyKnots", SeventyKnotsText);
                    break;
                case RtoCallout.Stopped:
                    Callout("stopped", StoppedText);
                    break;
                case RtoCallout.EcamOffer:
                    var open = _failures.ActiveAbnormalTitles;
                    var text = open.Count > 0
                        ? "ECAM — " + string.Join(", ", open) + ". Your call, captain."
                        : NoEcamText;
                    _eventLog.Record("takeoff.rto-complete", new { ecam = open, offered = open.Count > 0 });
                    Callout("ecamOffer", text, SpeechPriority.High);
                    break;
            }
        }
    }

    private void RecordRejected(RtoSample sample, bool confirmed)
    {
        string? trigger;
        double speed;
        lock (_gate)
        {
            trigger = _core.Trigger;
            speed = _core.RejectSpeedKt;
        }

        _logger.LogWarning("TAKEOFF REJECTED at {Speed:F0} kt by {Trigger}{Readback}", speed, trigger, confirmed ? " (read back)" : "");
        _eventLog.Record("takeoff.rejected", new
        {
            speedKt = Math.Round(speed),
            trigger,
            confirmed,
            phase = sample.Phase.ToString(),
            aboveReadbackSpeed = speed > RejectedTakeoffCore.ReadbackSpeedKt,
        });
    }

    private void Callout(string id, string text, SpeechPriority priority = SpeechPriority.Critical)
    {
        _eventLog.Record("takeoff.rto-callout", new { id, text });
        Speak(text, priority, "rto." + id);
    }

    private void Speak(string text, SpeechPriority priority, string tag)
        => _ = _arbiter.EnqueueAsync(new SpeechRequest(text, priority, TimeSpan.FromSeconds(6), Tag: tag));

    private RtoSample Sample(DateTimeOffset nowUtc)
    {
        var data = _source.Sample();
        return new RtoSample(
            _flight.CurrentPhase, data.IndicatedAirspeedKt, data.GroundSpeedKt, data.OnGround,
            data.ReversersMaxBoth, data.ParkBrakeSet, Lever(_lever1), Lever(_lever2), nowUtc);
    }

    private static double? Lever(IDataRefSubscription<double> lever)
        => lever.RawValue is null || !double.IsFinite(lever.Value) ? null : lever.Value;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_options.CurrentValue.RtoEnabled || !_flight.IsLive || !_source.Sample().IsValid)
            {
                return;
            }

            Process(Sample(DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rejected-takeoff tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
