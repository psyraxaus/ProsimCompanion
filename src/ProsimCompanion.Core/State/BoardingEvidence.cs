namespace ProsimCompanion.Core.State;

/// <summary>
/// What is known about THIS departure's boarding, in one place (review 2026-11-05 — three
/// readers used three definitions after a restart: the GSX lifecycle's observed progress, the
/// gate monitor's stale-close rule and the walkaround's reading of a diagnostics-board string).
/// Written by the GSX service lifecycle tracker, which owns the per-cycle facts; read by the
/// gate monitor and the walkaround through <see cref="BoardingEvidenceStore"/>.
/// </summary>
/// <param name="Started">Boarding has begun as far as the cycle can tell: called, requested,
/// active or completed — a startup-resync seed included (the work is done in the world).</param>
/// <param name="Observed">This process saw GSX call, run or complete the boarding; a seed
/// alone is NOT observed (the "boarding has started for this plan" test of the new-plan reset).</param>
/// <param name="Completed">The cycle holds a completed boarding (observed or seeded).</param>
/// <param name="Seeded">The only evidence is a startup-resync seed.</param>
public sealed record BoardingEvidence(bool Started, bool Observed, bool Completed, bool Seeded)
{
    public static BoardingEvidence None { get; } = new(false, false, false, false);
}

/// <summary>Live boarding evidence for this departure cycle (see <see cref="BoardingEvidence"/>).</summary>
public sealed class BoardingEvidenceStore : SnapshotStore<BoardingEvidence>
{
    public BoardingEvidenceStore()
        : base(BoardingEvidence.None)
    {
    }
}
