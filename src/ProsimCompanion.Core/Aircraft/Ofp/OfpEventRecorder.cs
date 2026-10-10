using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProsimCompanion.Core.EventLog;

namespace ProsimCompanion.Core.Aircraft.Ofp;

/// <summary>
/// Writes an <c>ofp.loaded</c> session event every time a different OFP lands in the
/// <see cref="OfpStore"/> (issue #164). The session log had no record of the planned route
/// unless a briefing was spoken (<c>flight.route</c>), so a pilot with speech disabled got a
/// logbook, debrief and duty-day leg with no origin or destination at all. The
/// <c>airport-coordinates</c> event was not a safe stand-in: it fires only when the navdata
/// lookup succeeds. This one fires on the OFP itself.
/// <para>De-duplicated on request id + route + flight number so the SimBrief importer's
/// repeated publishes of one plan (pax randomization, reloads) do not litter the log.</para>
/// </summary>
public sealed class OfpEventRecorder : IHostedService, IDisposable
{
    private readonly OfpStore _store;
    private readonly JsonlEventLog _eventLog;
    private readonly ILogger<OfpEventRecorder> _logger;
    private readonly object _gate = new();
    private string? _lastKey;

    public OfpEventRecorder(OfpStore store, JsonlEventLog eventLog, ILogger<OfpEventRecorder> logger)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(eventLog);
        ArgumentNullException.ThrowIfNull(logger);
        _store = store;
        _eventLog = eventLog;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _store.Changed += OnChanged;
        Record(_store.Current); // an OFP may already be loaded when hosting starts
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _store.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    public void Dispose() => _store.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        try
        {
            Record(_store.Current);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OFP event record failed");
        }
    }

    /// <summary>Records the event when the OFP differs from the last one recorded. Exposed
    /// for tests; a null OFP (store cleared) records nothing.</summary>
    public void Record(OfpData? ofp)
    {
        if (ofp is null)
        {
            return;
        }

        var key = string.Join("|", ofp.RequestId, ofp.OriginIcao, ofp.DestinationIcao, ofp.FlightNumber);
        lock (_gate)
        {
            if (string.Equals(_lastKey, key, StringComparison.Ordinal))
            {
                return;
            }

            _lastKey = key;
        }

        _eventLog.Record("ofp.loaded", new
        {
            requestId = ofp.RequestId,
            source = ofp.Source.ToString().ToLowerInvariant(),
            origin = ofp.OriginIcao,
            destination = ofp.DestinationIcao,
            alternate = ofp.AlternateIcao.Length > 0 ? ofp.AlternateIcao : null,
            flightNumber = ofp.FlightNumber.Length > 0 ? ofp.FlightNumber : null,
            callsign = ofp.Callsign.Length > 0 ? ofp.Callsign : null,
        });
    }
}
