using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ProsimCompanion.Core.Configuration;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.Gate;
using ProsimCompanion.Core.Hosting;

namespace ProsimCompanion.Speech.SayIntentions;

/// <summary>
/// Takes the gate SayIntentions ATC assigns as the GSX arrival gate (2026-10-08,
/// <c>sayIntentions.arrivalGateFromAtc</c>, off by default). Listens to the 1 Hz
/// <c>assigned_gate</c> reading from <see cref="SayIntentionsService"/>, runs it through the
/// pure <see cref="AtcAssignedGateRule"/> (departure stand remembered, only a changed gate
/// from the climb onward counts, the pilot's own queue wins) and hands an accepted gate to
/// <see cref="ArrivalGateCoordinator.ConfirmFromAtc"/>. Every decision that is not "nothing
/// new" is a <c>sayintentions.arrival-gate</c> session event, so a flight where the gate was
/// never taken explains itself from the log.
/// </summary>
public sealed class SayIntentionsArrivalGateSource : IStartupModule, IDisposable
{
    private readonly SayIntentionsService _sayIntentions;
    private readonly IOptionsMonitor<SayIntentionsOptions> _options;
    private readonly IFlightPhaseSource _phase;
    private readonly ArrivalGateCoordinator _coordinator;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<SayIntentionsArrivalGateSource> _logger;
    private readonly AtcAssignedGateRule _rule = new();
    private readonly object _lock = new();

    public SayIntentionsArrivalGateSource(
        SayIntentionsService sayIntentions,
        IOptionsMonitor<SayIntentionsOptions> options,
        IFlightPhaseSource phase,
        ArrivalGateCoordinator coordinator,
        JsonlEventLog eventLog,
        ILogger<SayIntentionsArrivalGateSource> logger)
    {
        ArgumentNullException.ThrowIfNull(sayIntentions);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _sayIntentions = sayIntentions;
        _options = options;
        _phase = phase;
        _coordinator = coordinator;
        _eventLog = eventLog;
        _logger = logger;
    }

    public void Start()
    {
        _sayIntentions.AssignedGateObserved += OnAssignedGate;
        _phase.PhaseChanged += OnPhaseChanged;
    }

    public void Dispose()
    {
        _sayIntentions.AssignedGateObserved -= OnAssignedGate;
        _phase.PhaseChanged -= OnPhaseChanged;
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e)
    {
        // A new leg forgets the old departure stand — otherwise a turnaround that parks at
        // the same stand it left from would ignore ATC's identical arrival gate.
        if (e.Current is FlightPhase.ColdAndDark or FlightPhase.Shutdown)
        {
            lock (_lock)
            {
                _rule.Reset();
            }
        }
    }

    private void OnAssignedGate(string? gate)
    {
        if (!_options.CurrentValue.ArrivalGateFromAtc)
        {
            return;
        }

        var phase = _phase.CurrentPhase;
        AtcGateDecision decision;
        lock (_lock)
        {
            decision = _rule.Observe(gate, phase, _coordinator.Snapshot().PendingGate);
        }

        if (decision == AtcGateDecision.None)
        {
            return;
        }

        var normalized = ArrivalGatePlan.Normalize(gate);
        _eventLog.Record("sayintentions.arrival-gate", new { gate = normalized, phase = phase.ToString(), decision = decision.ToString() });

        switch (decision)
        {
            case AtcGateDecision.QueueArrival:
                _logger.LogInformation("SayIntentions ATC assigned gate {Gate} ({Phase}) — taken as the arrival gate", normalized, phase);
                _coordinator.ConfirmFromAtc(normalized);
                break;

            case AtcGateDecision.PilotGateWins:
                _logger.LogInformation("SayIntentions ATC assigned gate {Gate} ({Phase}) — ignored, you queued a gate yourself", normalized, phase);
                break;

            case AtcGateDecision.DepartureGateNoted:
                _logger.LogDebug("SayIntentions departure stand noted: {Gate}", normalized);
                break;

            case AtcGateDecision.SameAsDeparture:
                _logger.LogDebug("SayIntentions assigned_gate {Gate} still the departure stand ({Phase}) — waiting", normalized, phase);
                break;
        }
    }
}
