using ProsimCompanion.Core.Flight;

namespace ProsimCompanion.Core.Tests.Speech;

/// <summary>A hand-driven <see cref="ILegBoundaryEvents"/>: tests raise the "a leg began"
/// edge (ADR-0019 §1) without the live FMS / OFP / GSX plumbing behind it.</summary>
internal sealed class FakeLegBoundaryEvents : ILegBoundaryEvents
{
    public event EventHandler<LegBeganEventArgs>? LegBegan;

    public void RaiseLegBegan(string planId, string? previousPlanId = null, LegBoundaryState from = LegBoundaryState.Turnaround)
        => LegBegan?.Invoke(this, new LegBeganEventArgs(planId, previousPlanId, from));
}
