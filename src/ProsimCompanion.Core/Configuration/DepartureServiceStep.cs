namespace ProsimCompanion.Core.Configuration;

/// <summary>
/// When a departure service is called relative to the service before it in the configured
/// order — the Prosim2GSX activation model, where concurrency is expressed per service as a
/// dependency on the previous entry's observed GSX state rather than as a global mode.
/// The first non-skipped entry has no previous service and is always immediately eligible.
/// </summary>
public enum GsxServiceActivation
{
    /// <summary>Never called and never monitored — the entry is inert this leg.</summary>
    Skip = 0,

    /// <summary>Called externally (INT/RAD, web button, GSX menu, or another app). The entry
    /// still gates the sequence: services after it wait, and it must complete (or be skipped)
    /// for the departure sequence to finish.</summary>
    Manual = 1,

    /// <summary>Called as soon as the previous service's call is confirmed — services run
    /// side by side (GSX arbitrates real apron parallelism).</summary>
    AfterCalled = 2,

    /// <summary>Called once the previous service is reported requested by GSX.</summary>
    AfterRequested = 3,

    /// <summary>Called once the previous service is reported active by GSX.</summary>
    AfterActive = 4,

    /// <summary>Called only after the previous service completed — a strict serial chain.</summary>
    AfterPrevCompleted = 5,

    /// <summary>Called only after every earlier service completed or was skipped — the barrier
    /// used for the classic board-last.</summary>
    AfterAllCompleted = 6,

    /// <summary>Called on the pilot's voice request ("request refueling", hail dialogue) —
    /// sequencer semantics are <see cref="Manual"/> (never auto-called; still gates later
    /// steps), but the status board says what it is waiting for. Any other trigger (INT/RAD
    /// force-next, web, GSX menu) advances it too — ADR-0006.</summary>
    Voice = 7,
}

/// <summary>Which legs of a session a departure service runs on. The first leg lasts until the
/// first arrival of the session; every later departure is a turnaround.</summary>
public enum GsxServiceConstraint
{
    Always = 0,

    /// <summary>Only on the session's first departure (e.g. the cabin is already clean).</summary>
    FirstLeg = 1,

    /// <summary>Only on turnarounds (e.g. Cleaning/Lavatory after a completed leg).</summary>
    TurnAround = 2,

    /// <summary>Only at company-hub airports (<c>gsx.companyHubs</c> ICAO prefixes) — e.g.
    /// full catering at base only. Unknown airport counts as non-hub.</summary>
    CompanyHub = 3,

    /// <summary>Only away from company hubs (e.g. outstation-only services).</summary>
    NonCompanyHub = 4,
}

/// <summary>
/// One entry of the ordered departure-service queue (<c>gsx.departureServices</c>): which
/// service, when it activates relative to the previous entry, and on which legs it runs.
/// List position is the order — there is no separate priority field.
/// </summary>
public sealed class DepartureServiceStep
{
    public DepartureServiceStep()
    {
    }

    public DepartureServiceStep(
        string service,
        GsxServiceActivation activation,
        GsxServiceConstraint constraint = GsxServiceConstraint.Always)
    {
        Service = service;
        Activation = activation;
        Constraint = constraint;
    }

    /// <summary>Canonical Remote API service id (e.g. "Refueling", "Boarding") — matched
    /// case-insensitively against the GSX state mirror.</summary>
    public string Service { get; set; } = "";

    public GsxServiceActivation Activation { get; set; } = GsxServiceActivation.AfterCalled;

    public GsxServiceConstraint Constraint { get; set; } = GsxServiceConstraint.Always;

    /// <summary>Skip this service when the planned flight is shorter than this many minutes
    /// (Prosim2GSX "Min. Flight Time" — e.g. no catering on a 30-minute hop). 0 = no
    /// constraint. Compared against the OFP's estimated enroute time; an unknown duration
    /// never skips (no data, no skip).</summary>
    public int MinimumFlightMinutes { get; set; }
}
