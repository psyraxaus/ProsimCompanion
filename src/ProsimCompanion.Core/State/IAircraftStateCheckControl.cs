namespace ProsimCompanion.Core.State;

/// <summary>Re-runs the session-start cold-and-dark check on demand (implemented by the GSX
/// sync layer; verdict lands in <see cref="GsxDiagnosticsStore"/>). The check is once-per-
/// session by design, so without this seam a pilot who fixed the switches — or hit the #59
/// false-airborne latch — could only get a fresh verdict by restarting the app (issue #92).</summary>
public interface IAircraftStateCheckControl
{
    /// <summary>Clears the once-per-session latch and re-assesses immediately. The fresh
    /// verdict is always announced (the pilot explicitly asked, so even a Pass or a Skipped
    /// gets a spoken answer). Returns false when the assessment could not run yet — no sim
    /// session, or the settle guards still holding — so the caller can answer honestly
    /// instead of leaving the request hanging.</summary>
    bool RequestRecheck(string source);
}
