namespace ProsimCompanion.Core.Diagnostics.Ecam;

/// <summary>
/// The health of one thing on an ECAM-style diagnostics page. When two states meet on a link
/// the link inherits the worse one, and "worse" is <see cref="Warning"/> over
/// <see cref="Caution"/> over <see cref="NoData"/> over <see cref="Normal"/> over
/// <see cref="NotApplicable"/> (see <see cref="DiagStates.Worse"/>).
/// </summary>
public enum DiagState
{
    /// <summary>Working as intended: green.</summary>
    Normal,

    /// <summary>Degraded, reconnecting, queued, stale, lost: amber.</summary>
    Caution,

    /// <summary>A hard failure that blocks the feature: red.</summary>
    Warning,

    /// <summary>Intentionally not in use: white "- - -", no colour.</summary>
    NotApplicable,

    /// <summary>The data source is missing or has not reported yet: amber "XX", never a
    /// stale number.</summary>
    NoData,
}

/// <summary>Severity helpers for <see cref="DiagState"/>.</summary>
public static class DiagStates
{
    /// <summary>Severity rank, higher is worse. Kept apart from the enum's declared order so
    /// the JSON names stay readable while the comparison stays explicit.</summary>
    public static int Rank(this DiagState state) => state switch
    {
        DiagState.Warning => 4,
        DiagState.Caution => 3,
        DiagState.NoData => 2,
        DiagState.Normal => 1,
        _ => 0,
    };

    /// <summary>The worse of two states (what a link between two nodes shows).</summary>
    public static DiagState Worse(DiagState a, DiagState b) => a.Rank() >= b.Rank() ? a : b;

    /// <summary>True for the two states a pilot must act on (amber or red).</summary>
    public static bool IsAbnormal(this DiagState state) => state is DiagState.Caution or DiagState.Warning;
}
