namespace ProsimCompanion.Core.State;

/// <summary>Where the GSX de-icing crew is, as the Remote API lifecycle reports it.</summary>
public enum DeiceStage
{
    /// <summary>No de-icing this cycle (yet).</summary>
    None,

    /// <summary>The crew has been called (GSX service state Requested).</summary>
    Requested,

    /// <summary>The trucks are spraying (GSX service state Active).</summary>
    Active,

    /// <summary>The treatment is done (GSX service state Completed) — the holdover clock runs.</summary>
    Completed,
}

/// <summary>
/// The current departure's de-icing activity (issue #166).
/// </summary>
/// <param name="Stage">The lifecycle stage.</param>
/// <param name="StageSinceUtc">When the stage was reached; null for <see cref="DeiceStage.None"/>.</param>
/// <param name="MenuFluidType">The fluid type the app picked on GSX's "Select fluid type"
/// menu this cycle (1 = Type I … 4 = Type IV), null when the menu was not answered by the app.</param>
/// <param name="MenuConcentration">The concentration (percent) picked on "Select concentration",
/// null when not answered by the app.</param>
public sealed record DeiceActivitySnapshot(
    DeiceStage Stage,
    DateTimeOffset? StageSinceUtc,
    int? MenuFluidType,
    int? MenuConcentration)
{
    public static DeiceActivitySnapshot Empty { get; } = new(DeiceStage.None, null, null, null);

    /// <summary>True from the crew being called until the treatment completes. The abnormal
    /// monitor holds the pack-fault definitions here (packs OFF is the procedure, not a fault
    /// — EFHK 2026-10-10, issue #123 reopened), and the FO's packs/ditching procedure keys on
    /// the edges.</summary>
    public bool InProgress => Stage is DeiceStage.Requested or DeiceStage.Active;
}

/// <summary>
/// Cross-pillar seam for "is GSX de-icing us right now, and what did we tell GSX to spray"
/// (issue #166). Written by the GSX pillar (lifecycle edges, the fluid/concentration menu
/// answers); read by the Speech pillar (pack-fault hold, packs/ditching procedure), the
/// holdover card (fluid source) and the web. Resets with the flight cycle. Feature projects
/// never reference each other — this store is the Core hub between them.
/// </summary>
public sealed class DeiceActivityStore : SnapshotStore<DeiceActivitySnapshot>, IDisposable
{
    private readonly GroundOpsSignals _signals;

    public DeiceActivityStore(GroundOpsSignals signals)
        : base(DeiceActivitySnapshot.Empty)
    {
        ArgumentNullException.ThrowIfNull(signals);
        _signals = signals;
        _signals.FlightCycleReset += Reset;
    }

    public void Dispose() => _signals.FlightCycleReset -= Reset;

    /// <summary>A lifecycle edge from the GSX relay. Edges never go backwards within a cycle
    /// (a late Requested after Active is ignored) so a mirror hiccup cannot restart the
    /// procedure; a new cycle starts from <see cref="Reset"/>.</summary>
    public void SetStage(DeiceStage stage)
    {
        Update(s => stage > s.Stage
            ? s with { Stage = stage, StageSinceUtc = DateTimeOffset.UtcNow }
            : s);
    }

    /// <summary>The app answered one of GSX's two de-icing menus; either argument may be null
    /// when only the other menu was answered so far.</summary>
    public void RecordMenuAnswer(int? fluidType, int? concentration)
    {
        Update(s => s with
        {
            MenuFluidType = fluidType ?? s.MenuFluidType,
            MenuConcentration = concentration ?? s.MenuConcentration,
        });
    }

    public void Reset() => Update(_ => DeiceActivitySnapshot.Empty);
}
