using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The takeoff gross-error check (issue #148): once per departure cycle, when the final
/// loadsheet is out and the V-speeds are in the FMS — and on "gross error check" at any time
/// before takeoff — <see cref="GrossErrorCheckCore"/> compares the aircraft against the
/// loadsheet and the FMS PERF entries against the last performance calculation
/// (<see cref="TakeoffPerfStore"/>). Arms on Flight live. A fresh final loadsheet (a new
/// edition) re-arms the automatic check for the same cycle. Advisory only.
/// </summary>
public sealed class GrossErrorCheckMonitor : Core.Hosting.IStartupModule, IVoiceFeature, IDisposable
{
    private static readonly string[] CheckPhrases =
        ["gross error check", "gross error check please", "run the gross error check", "cross check the loadsheet"];
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SpokenTtl = TimeSpan.FromSeconds(60);

    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IFlightPhaseSource _flight;
    private readonly LoadsheetStore _loadsheet;
    private readonly TakeoffPerfStore _perf;
    private readonly GroundOpsSignals _signals;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GrossErrorCheckMonitor> _logger;
    private readonly IDataRefSubscription<double> _zfw;
    private readonly IDataRefSubscription<double> _fuelTotal;
    private readonly IDataRefSubscription<int> _flaps;
    private readonly IDataRefSubscription<int> _flex;
    private readonly IDataRefSubscription<int> _v1;
    private readonly IDataRefSubscription<int> _vr;
    private readonly IDataRefSubscription<int> _v2;
    private readonly object _gate = new();

    private Timer? _timer;
    private int _ticking;
    private int? _checkedEdition;

    public GrossErrorCheckMonitor(
        IOptionsMonitor<SopOptions> sop,
        IFlightPhaseSource flight,
        LoadsheetStore loadsheet,
        TakeoffPerfStore perf,
        GroundOpsSignals signals,
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<GrossErrorCheckMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(loadsheet);
        ArgumentNullException.ThrowIfNull(perf);
        ArgumentNullException.ThrowIfNull(signals);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _sop = sop;
        _flight = flight;
        _loadsheet = loadsheet;
        _perf = perf;
        _signals = signals;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _zfw = dataRefs.Subscribe(ProsimDataRefNames.WeightZfw);
        _fuelTotal = dataRefs.Subscribe(ProsimDataRefNames.FuelTotal);
        _flaps = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffFlaps);
        _flex = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffFlexTemp);
        _v1 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV1);
        _vr = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffVr);
        _v2 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV2);
        _signals.FlightCycleReset += OnFlightCycleReset;
        _flight.PhaseChanged += OnPhaseChanged;
    }

    private GrossErrorCheckOptions Options => _sop.CurrentValue.Monitoring.GrossErrorCheck;

    public bool Enabled => Options.Enabled;

    public IEnumerable<string> Phrases => CheckPhrases;

    public bool ValueParse => false;

    public void Start() => _timer = new Timer(_ => Tick(), null, Poll, Poll);

    public void Dispose()
    {
        _timer?.Dispose();
        _signals.FlightCycleReset -= OnFlightCycleReset;
        _flight.PhaseChanged -= OnPhaseChanged;
        _zfw.Dispose();
        _fuelTotal.Dispose();
        _flaps.Dispose();
        _flex.Dispose();
        _v1.Dispose();
        _vr.Dispose();
        _v2.Dispose();
    }

    private void OnFlightCycleReset()
    {
        lock (_gate)
        {
            _checkedEdition = null;
        }

        _perf.Clear();
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // Back at the gate after a leg: the next departure gets its own check.
        if (e.Previous is FlightPhase.Shutdown or FlightPhase.TaxiIn && e.Current.IsAtGate())
        {
            lock (_gate)
            {
                _checkedEdition = null;
            }
        }
    }

    public bool TryHandle(string utterance)
    {
        if (!CheckPhrases.Contains(CommandMatcher.Normalize(utterance)))
        {
            return false;
        }

        if (!_flight.IsLive)
        {
            _ = _arbiter.SpeakAsync("No flight data for a gross error check.", SpeechPriority.Normal);
            return true;
        }

        var report = Evaluate();
        Speak(report, "voice");
        return true;
    }

    /// <summary>The automatic rule, for the timer and tests: live, enabled, automatic, before
    /// the takeoff roll, final loadsheet Sent, V-speeds in the FMS, and not yet checked for
    /// this loadsheet edition in this cycle. Returns the report it spoke.</summary>
    public GrossErrorReport? ProcessTick()
    {
        lock (_gate)
        {
            var options = Options;
            var phase = _flight.CurrentPhase;
            var final = _loadsheet.Snapshot().Final;
            if (!options.Enabled || !options.Automatic || !_flight.IsLive
                || !(phase.IsBeforeTaxiOut() || phase == FlightPhase.TaxiOut)
                || final.Status != LoadsheetSlotStatus.Sent
                || _checkedEdition == final.EditionNumber
                || _v1.Value <= 0 || _vr.Value <= 0 || _v2.Value <= 0)
            {
                return null;
            }

            _checkedEdition = final.EditionNumber;
            var report = Evaluate();
            Speak(report, "automatic");
            return report;
        }
    }

    private GrossErrorReport Evaluate()
        => GrossErrorCheckCore.Evaluate(
            new GrossErrorInputs(
                Read(_zfw), Read(_fuelTotal), _loadsheet.Snapshot().Final, _perf.Snapshot(),
                _flaps.Value, _flex.Value, _v1.Value, _vr.Value, _v2.Value),
            Options);

    private static double? Read(IDataRefSubscription<double> subscription)
        => subscription.RawValue is null || subscription.IsStale || subscription.Value <= 0 ? null : subscription.Value;

    private void Speak(GrossErrorReport report, string trigger)
    {
        _logger.LogInformation(
            "Gross error check ({Trigger}): {Result} — mismatches: {Mismatches}; not compared: {Skipped}",
            trigger, report.Checked ? "checked" : "MISMATCH",
            report.Mismatches.Count == 0 ? "none" : string.Join(" | ", report.Mismatches),
            report.Skipped.Count == 0 ? "none" : string.Join(", ", report.Skipped));
        _eventLog.Record("fo.gross-error-check", new
        {
            trigger,
            result = report.Checked ? "checked" : "mismatch",
            mismatches = report.Mismatches,
            skipped = report.Skipped,
            text = report.Text,
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(
            report.Text, report.Checked ? SpeechPriority.Normal : SpeechPriority.High, SpokenTtl, Tag: "fo.gross-error-check"));
    }

    private void Tick()
    {
        if (Interlocked.Exchange(ref _ticking, 1) == 1)
        {
            return;
        }

        try
        {
            ProcessTick();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gross error check tick failed");
        }
        finally
        {
            Interlocked.Exchange(ref _ticking, 0);
        }
    }
}
