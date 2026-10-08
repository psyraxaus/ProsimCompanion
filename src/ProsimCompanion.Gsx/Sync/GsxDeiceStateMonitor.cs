using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.Aircraft;
using ProsimCompanion.Core.EventLog;
using ProsimCompanion.Core.State;

namespace ProsimCompanion.Gsx.Sync;

/// <summary>
/// Reads GSX's de-icing LVAR pair — <c>L:FSDT_GSX_DEICING_STATE</c> (first read in this app,
/// 2026-10-09) and <c>L:FSDT_GSX_DEICING_TYPE</c> — and publishes every transition to the GSX
/// diagnostics page, the decision log and a <c>gsx-deice-state</c> session event. Evidence
/// only: the holdover card keeps arming from the Remote API Completed edge (the relay),
/// because no recorded flight has shown this LVAR's values yet; the first de-iced departure
/// settles the table in docs/integrations/gsx.md and decides whether state 6 is the more
/// truthful arming edge. Startup module: construction subscribes; the host calls
/// <see cref="Start"/>.
/// </summary>
public sealed class GsxDeiceStateMonitor : Core.Hosting.IStartupModule, IDisposable
{
    private readonly GsxDiagnosticsStore _diagnostics;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<GsxDeiceStateMonitor> _logger;
    private readonly IDataRefSubscription<double> _state;
    private readonly IDataRefSubscription<double> _type;
    private readonly object _gate = new();
    private int _lastState = -1;
    private int _lastType = -1;

    public GsxDeiceStateMonitor(
        ISimVars simVars,
        GsxDiagnosticsStore diagnostics,
        JsonlEventLog eventLog,
        ILogger<GsxDeiceStateMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(simVars);
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);

        _diagnostics = diagnostics;
        _eventLog = eventLog;
        _logger = logger;
        _state = simVars.Subscribe(GsxLvarNames.DeicingState);
        _type = simVars.Subscribe(GsxLvarNames.DeicingType);
    }

    public void Start()
    {
        _state.ValueChanged += OnChanged;
        _type.ValueChanged += OnChanged;
        OnChanged(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _state.ValueChanged -= OnChanged;
        _type.ValueChanged -= OnChanged;
        _state.Dispose();
        _type.Dispose();
    }

    /// <summary>The per-service state convention (docs/integrations/gsx.md §2) as a label —
    /// "unverified" is deliberate wording until a flight confirms it for this LVAR.</summary>
    public static string Label(int state) => state switch
    {
        0 => "0 (not reported / none)",
        1 => "1 Callable",
        2 => "2 (unknown)",
        3 => "3 (unknown)",
        4 => "4 Requested",
        5 => "5 Active",
        6 => "6 Completed",
        _ => $"{state} (unknown)",
    };

    private void OnChanged(object? sender, EventArgs e)
    {
        try
        {
            // Nothing reported yet (SimConnect absent, GSX absent): keep the page at "—".
            if (_state.RawValue is null && _type.RawValue is null)
            {
                return;
            }

            var state = (int)_state.Value;
            var type = (int)_type.Value;
            lock (_gate)
            {
                if (state == _lastState && type == _lastType)
                {
                    return;
                }

                var previous = _lastState;
                _lastState = state;
                _lastType = type;
                _diagnostics.UpdateDeiceLvars(new GsxDeiceLvarView(DateTimeOffset.UtcNow, state, Label(state), type));
                _logger.LogInformation(
                    "GSX de-icing LVARs: state {State} ({Label}), fluid type {Type} (was state {Previous})",
                    state, Label(state), type, previous);
                _eventLog.Record("gsx-deice-state", new { state, label = Label(state), fluidType = type, previousState = previous });
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "De-icing LVAR update failed");
        }
    }
}
