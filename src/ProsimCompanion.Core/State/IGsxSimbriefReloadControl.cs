namespace ProsimCompanion.Core.State;

/// <summary>How a GSX SimBrief reload attempt ended.</summary>
public enum GsxSimbriefReloadStatus
{
    /// <summary>The "SimBrief" line was picked and GSX visibly reacted (handler data
    /// patched, or the menu moved/closed).</summary>
    Reloaded,

    /// <summary>The line was picked and acknowledged but GSX gave no signal within the
    /// verify budget — the predecessor never had a clean signal either; treat as probably
    /// done and read the VDGS.</summary>
    SentUnconfirmed,

    /// <summary>Not attempted: GSX not Ready, no "SimBrief" line on the gate menu (left for
    /// the user), aircraft not at the gate, no OFP, or another reload is running.</summary>
    NotPerformed,
}

public sealed record GsxSimbriefReloadOutcome(GsxSimbriefReloadStatus Status, string Detail);

/// <summary>
/// Asks GSX to re-read the SimBrief flight plan (its gate-menu "SimBrief" line) so the VDGS
/// shows the current flight (2026-10-09, Prosim2GSX <c>ReloadSimbrief</c> port). Implemented
/// by the GSX pillar; the Status section button and <c>gsx.reloadSimbrief</c> call it. Only
/// at the gate with GSX Ready — the implementation refuses everywhere else.
/// </summary>
public interface IGsxSimbriefReloadControl
{
    /// <summary>Runs one reload now. <paramref name="source"/> names the trigger for the
    /// decision log ("web", "command", "voice"). Never throws.</summary>
    Task<GsxSimbriefReloadOutcome> ReloadAsync(string source, CancellationToken cancellationToken = default);
}
