namespace ProsimCompanion.Core.State;

/// <summary>Drives the departure service sequence from UI surfaces (implemented by the GSX
/// automation layer). <see cref="ForceNext"/> is the INT/RAD "smart button": call the next
/// departure service now, bypassing its activation rule — including Manual entries (the classic
/// "start Boarding while Refueling runs").</summary>
public interface IGsxDepartureControl
{
    /// <summary>True once the departure sequence has been started (manually or automatically).</summary>
    bool Started { get; }

    /// <summary>True once every departure service completed or was skipped.</summary>
    bool Complete { get; }

    /// <summary>Starts the departure service sequence (idempotent). <paramref name="source"/>
    /// names the surface that started it — <c>web</c>, <c>voice</c>, <c>hail</c>,
    /// <c>command</c>, <c>api</c>, <c>streamdeck</c> — and is recorded on the start decision
    /// and the <c>gsx-departure-started</c> session event (issue #169: the 2026-10-11 start
    /// had no source in the log).</summary>
    void Start(string source);

    /// <summary>Single-shot: the next sequencing evaluation treats the current step's
    /// activation rule as satisfied. The flight-plan gate still applies.</summary>
    void ForceNext();
}
