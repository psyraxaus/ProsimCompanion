namespace ProsimCompanion.Gsx.Gate;

/// <summary>
/// What the departure automation needs from <see cref="GsxGateSelectionService"/> to arm the
/// configured arrival gate at the flight phase: the request status and the request itself.
/// A seam so the automation host can be built against a fake instead of the full gate
/// selection module (which owns menus, LVAR readbacks and backoffs the host never touches).
/// </summary>
public interface IGsxArrivalGateRequester
{
    /// <summary>Where the current gate request stands.</summary>
    GsxGateRequestStatus Status { get; }

    /// <summary>Arms a gate request (a display token as typed in GSX's gate search, e.g. "B12").</summary>
    void RequestGate(string gate);
}
