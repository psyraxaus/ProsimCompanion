using ProsimCompanion.Core.State;

namespace ProsimCompanion.Core.Flight;

/// <summary>The ride right now (issue #186), for the cabin reactions and the Flight Status chip.</summary>
/// <param name="Level">The settled level.</param>
/// <param name="PeakG">The window's largest deviation from the baseline, G.</param>
/// <param name="Unit">The raw unit in force (guessed or forced).</param>
/// <param name="BaselineRaw">The learned baseline in the raw unit; null until learned.</param>
/// <param name="Calibrated">The baseline came from at-rest samples.</param>
/// <param name="EpisodeActive">From the first Light-or-worse until two minutes below Light.</param>
/// <param name="EpisodeStartedAtUtc">When the episode began.</param>
/// <param name="LastChangeAtUtc">When the level last changed.</param>
public sealed record TurbulenceSnapshot(
    TurbulenceLevel Level,
    double PeakG,
    AccelerationUnit Unit,
    double? BaselineRaw,
    bool Calibrated,
    bool EpisodeActive,
    DateTimeOffset? EpisodeStartedAtUtc,
    DateTimeOffset? LastChangeAtUtc)
{
    public static TurbulenceSnapshot Empty { get; } = new(TurbulenceLevel.None, 0, AccelerationUnit.Unknown, null, false, false, null, null);
}

/// <summary>Written by <see cref="TurbulenceMonitor"/>; read by the Speech cabin reactions and the web.</summary>
public sealed class TurbulenceStore : SnapshotStore<TurbulenceSnapshot>
{
    public TurbulenceStore()
        : base(TurbulenceSnapshot.Empty)
    {
    }

    /// <summary>Raised on the sample an episode ended (two minutes below Light).</summary>
    public event Action? EpisodeEnded;

    internal void RaiseEpisodeEnded() => EpisodeEnded?.Invoke();
}
