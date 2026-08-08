namespace ProsimCompanion.Core.Gate;

/// <summary>Outcome of a SayIntentions assignGate push. <see cref="Skipped"/> marks the
/// degrade path (integration disabled / no API key) — not an error, and callers must not
/// retry. <see cref="Detail"/> carries the ATC-confirmed gate name on success, or the
/// user-facing reason otherwise.</summary>
public sealed record SayIntentionsGateAssignResult(bool Ok, bool Skipped, string Detail);

/// <summary>
/// Narrow Core seam over the SayIntentions assignGate endpoint, implemented next to the
/// SayIntentions HTTP client (Speech project) so the arrival-gate coordinator can push the
/// gate to ATC without Core referencing the integration. Implementations re-resolve the API
/// key per call (it changes between SayIntentions sessions) and degrade to a Skipped result
/// instead of throwing when the integration is disabled or keyless.
/// </summary>
public interface ISayIntentionsGateAssign
{
    /// <summary>True when the integration is enabled and an API key is currently resolvable
    /// — drives the "will be skipped" status shown at Confirm time.</summary>
    bool IsActive { get; }

    /// <summary>Pushes an arrival-gate assignment to SayIntentions ATC.</summary>
    Task<SayIntentionsGateAssignResult> AssignGateAsync(
        string airportIcao, string gate, CancellationToken cancellationToken = default);
}
