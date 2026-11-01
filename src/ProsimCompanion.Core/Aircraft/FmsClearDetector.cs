namespace ProsimCompanion.Core.Aircraft;

/// <summary>
/// The pure "the pilot cleared the FMS" rule (ADR-0019 §2, issue #211). The ProSim IOS
/// <c>Reset FMS</c> button writes <c>control.resetFMS</c>, which is write-only, so the press
/// itself is invisible; its effect is not: <c>aircraft.fms.origin</c> AND
/// <c>aircraft.fms.destination</c> go from a valid pair to empty. Two edges are deliberately
/// NOT a clear: a subscription drop (either value null — a ProSim restart, the dataref bus
/// gone), and one side alone going empty (the pilot re-typing one airport on the INIT page,
/// or ProSim publishing the two refs a tick apart). The detector arms on a present plan and
/// fires once per armed plan, so the same clear is never reported twice.
/// </summary>
public sealed class FmsClearDetector
{
    private bool _armed;

    /// <summary>The last origin/destination pair that read as a present plan, kept across the
    /// clear so the event can say which plan was cleared.</summary>
    public (string Origin, string Destination)? LastPresent { get; private set; }

    /// <summary>Feed the current pair; true exactly when a present plan has just become an
    /// empty pair (both sides read empty, neither null).</summary>
    public bool Observe(string? origin, string? destination)
    {
        if (FmsPlanRules.IsPlanPresent(origin, destination))
        {
            LastPresent = (origin!, destination!);
            _armed = true;
            return false;
        }

        if (origin is null || destination is null)
        {
            // A subscription that fell away is not the pilot clearing the box (ADR-0019 §2):
            // forget the plan so the values coming back empty after a ProSim restart cannot
            // read as a clear either.
            _armed = false;
            LastPresent = null;
            return false;
        }

        if (!_armed)
        {
            return false;
        }

        if (IsEmpty(origin) && IsEmpty(destination))
        {
            _armed = false;
            return true;
        }

        // One side changed while the other still holds — wait for the pair.
        return false;
    }

    /// <summary>ProSim's "nothing entered" spellings: empty, the dash sentinel, or "Null".</summary>
    public static bool IsEmpty(string? value)
        => string.IsNullOrWhiteSpace(value)
            || value == "----"
            || value.Equals("Null", StringComparison.OrdinalIgnoreCase);
}
