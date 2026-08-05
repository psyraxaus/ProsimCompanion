namespace ProsimCompanion.Core.Aircraft.Ofp;

/// <summary>
/// Typed SimBrief OFP snapshot — the fields the loadsheet pipeline, FMS sync and EFB pages
/// consume. All weights/fuel are kilograms (lbs OFPs are converted at parse time); estimates
/// that SimBrief omitted are 0. Held by <see cref="OfpStore"/> after each successful fetch.
/// </summary>
public sealed record OfpData
{
    /// <summary>SimBrief <c>params.request_id</c> — the flight-plan identity. A different id
    /// means a genuinely new OFP (used as the new-plan sentinel, predecessor rule).</summary>
    public string RequestId { get; init; } = "";

    /// <summary>The loadsheet "Ident": the leading 4 characters of the request id (what
    /// ProSim's own loadsheet generator prints).</summary>
    public string Ident { get; init; } = "";

    public string Callsign { get; init; } = "";
    public string OriginIcao { get; init; } = "";
    public string OriginIata { get; init; } = "";
    public string DestinationIcao { get; init; } = "";
    public string DestinationIata { get; init; } = "";

    /// <summary>Empty when the OFP has no alternate. The SimBrief <c>alternate</c> node is
    /// polymorphic (object / array / empty string); the first alternate wins.</summary>
    public string AlternateIcao { get; init; } = "";

    public string AircraftReg { get; init; } = "";
    public string AircraftIcaoType { get; init; } = "";

    public int PaxCount { get; init; }
    public double CargoKg { get; init; }

    /// <summary>Block (ramp) fuel, rounded UP to the next 100 kg (fuel-order increments).</summary>
    public double FuelPlanRampKg { get; init; }
    public double FuelPlanLandingKg { get; init; }
    public double FuelTaxiKg { get; init; }

    public double EstZfwKg { get; init; }
    public double EstTowKg { get; init; }
    public double EstLdwKg { get; init; }
    public double MaxZfwKg { get; init; }
    public double MaxTowKg { get; init; }
    public double MaxLdwKg { get; init; }

    /// <summary>Scheduled off-block time (SimBrief <c>times.sched_out</c>), null if absent.</summary>
    public DateTimeOffset? ScheduledOutUtc { get; init; }

    public DateTimeOffset FetchedAtUtc { get; init; }

    /// <summary>Average flight time (SimBrief <c>times.est_time_enroute</c> seconds), null if
    /// absent — future per-service minimum-duration constraints read this.</summary>
    public TimeSpan? EstimatedEnroute { get; init; }
}

/// <summary>
/// Holds the most recently fetched OFP for every consumer (loadsheets, FMS sync, web pages).
/// Thread-safe; <see cref="Changed"/> fires on the setter's thread — UI consumers marshal.
/// </summary>
public sealed class OfpStore
{
    private readonly object _lock = new();
    private OfpData? _current;

    public event EventHandler? Changed;

    public OfpData? Current
    {
        get
        {
            lock (_lock)
            {
                return _current;
            }
        }
    }

    public void Set(OfpData ofp)
    {
        ArgumentNullException.ThrowIfNull(ofp);
        lock (_lock)
        {
            _current = ofp;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _current = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
