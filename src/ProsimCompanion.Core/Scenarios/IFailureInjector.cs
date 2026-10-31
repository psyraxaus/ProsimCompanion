namespace ProsimCompanion.Core.Scenarios;

/// <summary>The SDK's own after-event triggers for a takeoff scenario (ProSim judges the event).</summary>
public enum FailureAfterEvent
{
    Takeoff,
    V1,
    Landing,
}

/// <summary>An arm condition: fire after an event, optionally delayed. Null = fire now.</summary>
public sealed record FailureArmCondition(FailureAfterEvent AfterEvent, int DelaySeconds = 0);

/// <summary>One failure ProSim reports as armed or active.</summary>
public sealed record ArmedFailureInfo(string Handle, string Name, bool IsActive);

/// <summary>
/// The failure write path (issue #197, ADR-0018): the ProSim SDK's failure objects behind the
/// Prosim pillar's <c>ProsimFailureGate</c>. Core-owned so the Speech scenario service and the
/// commands reach it without a reference to the Prosim pillar (the <c>IAcarsUplink</c> seam).
/// Nothing here takes a dataref name: a failure is named by ProSim's own failure name, and the
/// gate refuses any name that is not both in the catalogue and in ProSim's live list.
/// </summary>
public interface IFailureInjector
{
    /// <summary>True while an SDK session is attached and the failure API answered once.</summary>
    bool IsAvailable { get; }

    /// <summary>ProSim's supported failure names (<c>getFailures()</c>); empty when not connected.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Arms one failure through the gate. Returns the handle, or null when the gate
    /// refused (not in the catalogue, not in the live list) or ProSim is unreachable.</summary>
    Task<string?> ArmAsync(string failureName, FailureArmCondition? condition, CancellationToken cancellationToken = default);

    /// <summary>What is armed or active right now, ours and the IOS's alike.</summary>
    Task<IReadOnlyList<ArmedFailureInfo>> ArmedAsync(CancellationToken cancellationToken = default);

    /// <summary>Clears one armed failure by handle. False when it was not found.</summary>
    Task<bool> ClearAsync(string handle, CancellationToken cancellationToken = default);

    /// <summary>Clears EVERY armed failure ProSim reports (the IOS's included) and returns how
    /// many were reset; -1 when ProSim is unreachable.</summary>
    Task<int> ClearAllAsync(CancellationToken cancellationToken = default);
}
