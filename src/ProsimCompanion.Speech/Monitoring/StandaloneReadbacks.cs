using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;
using ProsimCompanion.Speech.Arbiter;
using ProsimCompanion.Speech.Briefings;
using ProsimCompanion.Speech.Recognition;

namespace ProsimCompanion.Speech.Monitoring;

/// <summary>
/// The voice side of <see cref="ReadbackCore"/> (issue #148): a value-parsing feature (it
/// needs the raw figure) that answers altimeter, V-speed, runway and minimums read-backs
/// from the captain's baro, the FMS PERF page, the resolved departure/arrival runway and the
/// briefed minima. Arms on Flight live. Reads only; it never sets anything.
/// </summary>
public sealed class StandaloneReadbacks : IVoiceFeature, IDisposable
{
    private readonly IOptionsMonitor<SopOptions> _sop;
    private readonly IFlightPhaseSource _flight;
    private readonly ProcedureSource _procedures;
    private readonly ArrivalMinimaStore _minima;
    private readonly ISpeechArbiter _arbiter;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<StandaloneReadbacks> _logger;
    private readonly IDataRefSubscription<double> _baro;
    private readonly IDataRefSubscription<int> _v1;
    private readonly IDataRefSubscription<int> _vr;
    private readonly IDataRefSubscription<int> _v2;

    public StandaloneReadbacks(
        IOptionsMonitor<SopOptions> sop,
        IFlightPhaseSource flight,
        ProcedureSource procedures,
        ArrivalMinimaStore minima,
        IProsimDataRefs dataRefs,
        ISpeechArbiter arbiter,
        JsonlEventLog eventLog,
        ILogger<StandaloneReadbacks> logger)
    {
        ArgumentNullException.ThrowIfNull(sop);
        ArgumentNullException.ThrowIfNull(flight);
        ArgumentNullException.ThrowIfNull(procedures);
        ArgumentNullException.ThrowIfNull(minima);
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(arbiter);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _sop = sop;
        _flight = flight;
        _procedures = procedures;
        _minima = minima;
        _arbiter = arbiter;
        _eventLog = eventLog;
        _logger = logger;
        _baro = dataRefs.Subscribe(ProsimDataRefNames.Efis1BaroHpa);
        _v1 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV1);
        _vr = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffVr);
        _v2 = dataRefs.Subscribe(ProsimDataRefNames.FmsPerfTakeoffV2);
    }

    private ReadbackOptions Options => _sop.CurrentValue.Monitoring.Readbacks;

    public bool Enabled => Options.Enabled;

    public IEnumerable<string> Phrases => ReadbackCore.LeadIns;

    public bool ValueParse => true;

    public bool TryHandle(string utterance)
    {
        var request = ReadbackCore.Parse(utterance);
        if (request is null)
        {
            return false;
        }

        if (!_flight.IsLive)
        {
            _ = _arbiter.SpeakAsync("No flight data to check that against.", SpeechPriority.Normal);
            return true;
        }

        var answer = ReadbackCore.Answer(request, Actuals(), Options);
        _logger.LogInformation("Read-back {Kind} \"{Utterance}\": {Outcome} — {Text}", request.Kind, utterance, answer.Outcome, answer.Text);
        _eventLog.Record("fo.readback", new
        {
            kind = request.Kind.ToString().ToLowerInvariant(),
            said = request.Runway ?? string.Join("/", request.Numbers.Select(n => n.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))),
            outcome = answer.Outcome,
            text = answer.Text,
        });
        _ = _arbiter.EnqueueAsync(new SpeechRequest(answer.Text, SpeechPriority.Normal, TimeSpan.FromSeconds(20), Tag: "fo.readback"));
        return true;
    }

    private ReadbackActuals Actuals()
    {
        // Before the takeoff roll the runway in question is the departure's; after it, the arrival's.
        var departure = _flight.CurrentPhase.IsBeforeTaxiOut()
            || _flight.CurrentPhase is FlightPhase.TaxiOut or FlightPhase.TakeoffRoll or FlightPhase.Unknown;
        string? runway = null;
        try
        {
            runway = _procedures.Resolve(departure).Runway;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Runway resolution failed for the read-back");
        }

        return new ReadbackActuals(
            _baro.RawValue is null || _baro.IsStale ? null : _baro.Value,
            _v1.Value, _vr.Value, _v2.Value, runway, _minima.Current);
    }

    public void Dispose()
    {
        _baro.Dispose();
        _v1.Dispose();
        _vr.Dispose();
        _v2.Dispose();
    }
}
