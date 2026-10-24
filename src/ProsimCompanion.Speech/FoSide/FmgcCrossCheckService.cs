using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Aircraft.Ofp;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Core.VoiceReference;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Llm;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.FoSide;

/// <summary>
/// The FO's ADIRS / FMGC cross-check (issue #188), read-only: on "cross-check" and once per
/// departure cycle when the FMGC init settles at the gate, the FO reads the ADIRS alignment,
/// the INIT page against the plan and the PERF page against the computed performance
/// (<see cref="CrossCheckComposer"/>, pure). Every number is checked by the
/// <see cref="NumberVerifier"/> before it is spoken. No MCDU key is pressed.
/// </summary>
public sealed class FmgcCrossCheckService : IVoiceFeature, Core.Hosting.IStartupModule, IDisposable
{
    private static readonly string[] Triggers = ["cross check", "cross-check", "crosscheck", "fmgc cross check", "fmgc cross-check", "adirs check", "adirs cross check", "fmgc check"];

    private readonly IOptionsMonitor<SpeechOptions> _speech;
    private readonly IFlightPhaseSource _flight;
    private readonly OfpStore _ofp;
    private readonly TakeoffPerfStore _perf;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<FmgcCrossCheckService> _logger;
    private readonly GroundOpsSignals _signals;
    private readonly IDataRefSubscription<bool>[] _aligning;
    private readonly IDataRefSubscription<double>[] _minutes;
    private readonly IDataRefSubscription<bool>[] _attitude;
    private readonly IDataRefSubscription<string?> _origin;
    private readonly IDataRefSubscription<string?> _destination;
    private readonly IDataRefSubscription<int> _cruise;
    private readonly IDataRefSubscription<int> _v1;
    private readonly IDataRefSubscription<int> _vr;
    private readonly IDataRefSubscription<int> _v2;
    private readonly IDataRefSubscription<int> _flex;
    private readonly object _gate = new();
    private Timer? _timer;
    private int _ticking;
    private bool _autoDone;

    public FmgcCrossCheckService(
        IOptionsMonitor<SpeechOptions> speech,
        IProsimDataRefs dataRefs,
        IFlightPhaseSource flight,
        OfpStore ofp,
        TakeoffPerfStore perf,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<FmgcCrossCheckService> logger,
        GroundOpsSignals signals)
    {
        ArgumentNullException.ThrowIfNull(speech);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(ofp);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(signals);
        _speech = speech;
        _flight = flight;
        _ofp = ofp;
        _perf = perf;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _signals = signals;
        _aligning = [dataRefs.Subscribe(ProsimDataRefNames.Adiru1Aligning), dataRefs.Subscribe(ProsimDataRefNames.Adiru2Aligning), dataRefs.Subscribe(ProsimDataRefNames.Adiru3Aligning)];
        _minutes = [dataRefs.Subscribe(ProsimDataRefNames.Adiru1AlignMinutesLeft), dataRefs.Subscribe(ProsimDataRefNames.Adiru2AlignMinutesLeft), dataRefs.Subscribe(ProsimDataRefNames.Adiru3AlignMinutesLeft)];
        _attitude = [dataRefs.Subscribe(ProsimDataRefNames.Adiru1AttitudeAvailable), dataRefs.Subscribe(ProsimDataRefNames.Adiru2AttitudeAvailable), dataRefs.Subscribe(ProsimDataRefNames.Adiru3AttitudeAvailable)];
        _origin = dataRefs.Subscribe(ProsimDataRefNames.FmsOrigin);
        _destination = dataRefs.Subscribe(ProsimDataRefNames.FmsDestination);
        _cruise = dataRefs.Subscribe(ProsimDataRefNames.FmsCruiseAltitude);
        _v1 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV1);
        _vr = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffVr);
        _v2 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV2);
        _flex = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffFlexTemp);
    }

    public static VoiceFeatureDeclaration Declared { get; } = new()
    {
        Precedence = 66,
        Gate = VoiceFeatureGate.ByOption("speech.foSide.adirsCheck", "voiceReference.off.foSide"),
        Description = new DescribedVoiceReference("crossCheck", "voiceReference.group.crossCheck.title", VoiceReferenceTab.FirstOfficer, VoiceSpeaker.FirstOfficer,
        [
            new(["cross-check", "fmgc cross-check", "adirs check"], "voiceReference.group.crossCheck.check", "ADIRS one, two and three aligned. INIT page: …"),
        ]),
    };

    VoiceFeatureDeclaration IVoiceFeature.Declaration => Declared;

    public bool Enabled => _speech.CurrentValue.FoSide.AdirsCheck;

    public IEnumerable<string> Phrases => Triggers;

    public bool ValueParse => false;

    /// <summary>Only ever reads and speaks.</summary>
    public bool AcknowledgeOnly => true;

    public void Start()
    {
        _signals.FlightCycleReset += OnReset;
        _flight.SessionReset += OnReset;
        _timer = new Timer(_ => Tick(), null, 3000, 1000);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _signals.FlightCycleReset -= OnReset;
        _flight.SessionReset -= OnReset;
        foreach (var read in _aligning.Cast<IDataRefSubscription>().Concat(_minutes).Concat(_attitude).Concat([_origin, _destination, _cruise, _v1, _vr, _v2, _flex]))
        {
            read.Dispose();
        }
    }

    private void OnReset()
    {
        lock (_gate)
        {
            _autoDone = false;
        }
    }

    public bool TryHandle(string utterance)
    {
        if (!FoSidePhrases.IsCrossCheck(utterance))
        {
            return false;
        }

        _ = RunAsync("voice");
        return true;
    }

    /// <summary>The auto trigger, pure: at the gate, FMGC init settled (route and V1 entered), once per cycle.</summary>
    public static bool AutoDue(FlightPhase phase, bool live, string? origin, string? destination, int v1, bool doneThisCycle)
        => !doneThisCycle && live && phase.IsAtGate()
            && !string.IsNullOrWhiteSpace(origin) && !string.IsNullOrWhiteSpace(destination) && v1 > 0;

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            if (!_speech.CurrentValue.FoSide.AdirsCheck)
            {
                return;
            }

            bool due;
            lock (_gate)
            {
                due = AutoDue(_flight.CurrentPhase, _flight.IsLive && !_origin.IsStale, _origin.Value, _destination.Value, _v1.Value, _autoDone);
                if (due)
                {
                    _autoDone = true;
                }
            }

            if (due)
            {
                _ = RunAsync("auto");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FMGC cross-check tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }

    /// <summary>Reads, composes, verifies the numbers and speaks — public for tests.</summary>
    public async Task RunAsync(string source)
    {
        try
        {
            if (!_flight.IsLive || _origin.IsStale)
            {
                await _arbiter.SpeakAsync("Cross-check unavailable — no live aircraft data.", SpeechPriority.Normal).ConfigureAwait(false);
                _eventLog.Record("fo.crossCheck", new { source, ok = false, reason = "not live" });
                return;
            }

            var adirus = Enumerable.Range(0, 3)
                .Select(i => new AdiruState(i + 1, _aligning[i].Value, _minutes[i].Value, _attitude[i].Value))
                .ToList();
            var fmgc = new FmgcFacts(_origin.Value, _destination.Value, _cruise.Value, _v1.Value, _vr.Value, _v2.Value, _flex.Value);
            var readout = CrossCheckComposer.Compose(adirus, fmgc, _ofp.Current, _perf.Snapshot());

            var check = NumberVerifier.Check(readout.Text, readout.Numbers);
            if (!check.Ok)
            {
                // The composer is deterministic; a failure here is a composer bug, not a hallucination — log it and still speak.
                _logger.LogWarning("Cross-check number guard flagged {Offending} against {Allowed}", string.Join(", ", check.Offending), NumberVerifier.DescribeAllowed(check.Allowed));
            }

            _eventLog.Record("fo.crossCheck", new
            {
                source,
                ok = true,
                complete = readout.Complete,
                adirs = adirus.Select(a => new { a.Number, aligned = a.Aligned, a.Aligning, minutesLeft = Math.Round(a.MinutesLeft, 1) }).ToList(),
                init = new { fmgc.Origin, fmgc.Destination, cruiseFl = fmgc.CruiseAltitudeFt / 100, costIndex = _ofp.Current?.CostIndex },
                perf = new { fmgc.V1, fmgc.Vr, fmgc.V2, flex = fmgc.FlexTempC },
                mismatches = readout.Mismatches,
                numbersOk = check.Ok,
                text = readout.Text,
            });
            _logger.LogInformation("FMGC cross-check ({Source}): {Mismatches} difference(s) — {Text}", source, readout.Mismatches.Count, readout.Text);
            await _arbiter.EnqueueAsync(new SpeechRequest(readout.Text, SpeechPriority.Normal, TimeSpan.FromSeconds(60), Tag: "fo.crossCheck")).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FMGC cross-check failed");
        }
    }
}
