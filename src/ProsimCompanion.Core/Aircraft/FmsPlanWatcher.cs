using Microsoft.Extensions.Logging;

namespace ProsimCompanion.Core.Aircraft;

/// <summary>What the FMS held when it was cleared, and what the FMGC said about itself.</summary>
public sealed class FmsClearedEventArgs(string previousOrigin, string previousDestination, string? fmgcPhase) : EventArgs
{
    public string PreviousOrigin { get; } = previousOrigin;

    public string PreviousDestination { get; } = previousDestination;

    /// <summary>The FMGC's own flight phase (<c>aircraft.fms.flightPhase</c>) at the clear —
    /// a witness only (ADR-0019 §4), never an input to anything.</summary>
    public string? FmgcPhase { get; } = fmgcPhase;
}

/// <summary>
/// Watches the FMS plan refs the app already subscribes to and raises <see cref="Cleared"/>
/// when the pilot empties the box (ADR-0019 §2, issue #211). Cached read model, never a poll:
/// three Infrequent subscriptions, the edge evaluated on each push by
/// <see cref="FmsClearDetector"/>. Whether the clear counts for the leg boundary (parked,
/// engines off) is the consumer's call — this class only reports the fact.
/// </summary>
public sealed class FmsPlanWatcher : IDisposable
{
    private readonly IDataRefSubscription<string?> _origin;
    private readonly IDataRefSubscription<string?> _destination;
    private readonly IDataRefSubscription<string?> _fmgcPhase;
    private readonly ILogger<FmsPlanWatcher> _logger;
    private readonly FmsClearDetector _detector = new();
    private readonly object _gate = new();

    public FmsPlanWatcher(IProsimDataRefs dataRefs, ILogger<FmsPlanWatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(dataRefs);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _origin = dataRefs.Subscribe(ProsimDataRefNames.FmsOrigin);
        _destination = dataRefs.Subscribe(ProsimDataRefNames.FmsDestination);
        _fmgcPhase = dataRefs.Subscribe(ProsimDataRefNames.FmsFlightPhase);
        _origin.ValueChanged += OnPlanRefChanged;
        _destination.ValueChanged += OnPlanRefChanged;
    }

    /// <summary>The pilot cleared the FMS plan (both airports went from a value to empty).</summary>
    public event EventHandler<FmsClearedEventArgs>? Cleared;

    /// <summary>The FMGC's own phase word (PREFLIGHT, TAKEOFF, … DONE), null until read.
    /// Shown on the Diagnostics SIM page and logged beside a clear; nothing else reads it.</summary>
    public string? FmgcPhase => _fmgcPhase.Value;

    /// <summary>The INIT page's origin as last read; null or empty while the box holds no plan.</summary>
    public string? Origin => _origin.Value;

    /// <summary>The INIT page's destination as last read; null or empty while the box holds no plan.</summary>
    public string? Destination => _destination.Value;

    private void OnPlanRefChanged(object? sender, EventArgs e)
    {
        (string Origin, string Destination)? cleared = null;
        lock (_gate)
        {
            var origin = _origin.Value;
            var destination = _destination.Value;
            if (_detector.Observe(origin, destination))
            {
                cleared = _detector.LastPresent;
            }
        }

        if (cleared is not { } plan)
        {
            return;
        }

        var phase = FmgcPhase;
        _logger.LogInformation(
            "FMS plan cleared: {Origin} → {Destination} is gone from the INIT page (FMGC phase {FmgcPhase})",
            plan.Origin, plan.Destination, phase ?? "unknown");
        try
        {
            Cleared?.Invoke(this, new FmsClearedEventArgs(plan.Origin, plan.Destination, phase));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "An FMS-cleared subscriber failed");
        }
    }

    public void Dispose()
    {
        _origin.ValueChanged -= OnPlanRefChanged;
        _destination.ValueChanged -= OnPlanRefChanged;
        _origin.Dispose();
        _destination.Dispose();
        _fmgcPhase.Dispose();
    }
}
