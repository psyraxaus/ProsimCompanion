using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Flight;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Web.Components;

/// <summary>
/// Logs every change of the Flight Phase card's two headline surfaces — the highlighted
/// progress-strip block ("pill") and the phase text — with the inputs and trigger that moved
/// them. The two come from different state machines (phase engine vs GSX departure sequence)
/// and can read as contradictory (issue #110); without this log the 2026-08 flight could not
/// reconstruct what the pilot actually saw and when. Shares
/// <see cref="FlightStatusPresentation"/> with the dashboard so the log is exactly the
/// rendered value, browser open or not. Registered as a startup module — construction wires
/// the observers.
/// </summary>
public sealed class FlightStatusChangeLog : IDisposable
{
    private readonly IFlightPhaseSource _flightState;
    private readonly IGsxDepartureControl _departureControl;
    private readonly ILogger<FlightStatusChangeLog> _logger;
    private readonly IDisposable _diagnosticsSubscription;
    private readonly object _gate = new();

    private string? _lastPill;
    private string? _lastText;

    public FlightStatusChangeLog(
        IFlightPhaseSource flightState,
        GsxDiagnosticsStore diagnostics,
        IGsxDepartureControl departureControl,
        ILogger<FlightStatusChangeLog> logger)
    {
        ArgumentNullException.ThrowIfNull(flightState);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(departureControl);
        ArgumentNullException.ThrowIfNull(logger);

        _flightState = flightState;
        _departureControl = departureControl;
        _logger = logger;

        // The pill's departure-sequence input has no event of its own; every departure edge is
        // accompanied by a diagnostics snapshot change (board rows / automation phase), which is
        // also exactly what re-renders the dashboard — so observing the store keeps the log in
        // lockstep with the page.
        _diagnosticsSubscription = diagnostics.Observe(_ => Evaluate("GSX diagnostics"));
        _flightState.PhaseChanged += OnPhaseChanged;

        Evaluate("startup");
    }

    private void OnPhaseChanged(object? sender, FlightPhaseChangedEventArgs e) => Evaluate("phase engine");

    private void Evaluate(string trigger)
    {
        var phase = _flightState.CurrentPhase;
        var started = _departureControl.Started;
        var complete = _departureControl.Complete;
        var pill = FlightStatusPresentation.ActiveBlockLabel(phase, started, complete);
        var text = FlightStatusPresentation.PhaseDisplay(phase);

        string? previousPill;
        string? previousText;
        lock (_gate)
        {
            if (pill == _lastPill && text == _lastText)
            {
                return;
            }

            previousPill = _lastPill;
            previousText = _lastText;
            _lastPill = pill;
            _lastText = text;
        }

        _logger.LogInformation(
            "Flight Status display: pill {Pill} (was {PreviousPill}), phase text {PhaseText} (was {PreviousText}) — trigger {Trigger}, departure started={DepartureStarted} complete={DepartureComplete}",
            pill, previousPill ?? "—", text, previousText ?? "—", trigger, started, complete);
    }

    public void Dispose()
    {
        _flightState.PhaseChanged -= OnPhaseChanged;
        _diagnosticsSubscription.Dispose();
    }
}
